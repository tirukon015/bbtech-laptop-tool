using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace BBTech.Api.Data;

/// <summary>PostgreSQL access (Npgsql) and the migration runner. Only the API talks to the database.</summary>
public sealed class Database
{
    public NpgsqlDataSource Source { get; }

    public Database(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("No database connection string. Set ConnectionStrings__Postgres (environment variable) or ConnectionStrings:Postgres.");
        Source = NpgsqlDataSource.Create(connectionString);
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default) => await Source.OpenConnectionAsync(ct);

    /// <summary>Applies every embedded Migrations/NNN_*.sql not yet applied, each in its own transaction, in order.</summary>
    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", conn))
            await cmd.ExecuteNonQueryAsync(ct);

        // Serialise concurrent API instances starting together.
        await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(727001)", conn)) await cmd.ExecuteNonQueryAsync(ct);
        var applied = new List<string>();
        try
        {
            var done = new HashSet<string>();
            await using (var cmd = new NpgsqlCommand("SELECT name FROM schema_migrations", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) done.Add(r.GetString(0));

            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.Contains(".Migrations.") && n.EndsWith(".sql")).OrderBy(n => n, StringComparer.Ordinal))
            {
                string name = res.Substring(res.IndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length);
                if (done.Contains(name)) continue;
                string sql;
                using (var s = asm.GetManifestResourceStream(res)!)
                using (var reader = new StreamReader(s, Encoding.UTF8)) sql = await reader.ReadToEndAsync(ct);

                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(sql, conn, tx)) await cmd.ExecuteNonQueryAsync(ct);
                await using (var cmd = new NpgsqlCommand("INSERT INTO schema_migrations (name) VALUES (@n)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("n", name);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                applied.Add(name);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(727001)", conn);
            await unlock.ExecuteNonQueryAsync(ct);
        }
        return applied;
    }

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Issues a new random API key for a workstation; only its hash is stored. Returns the key (shown once).</summary>
    public async Task<(Guid id, string key)> CreateWorkstationAsync(string name, bool isAdmin, CancellationToken ct = default)
    {
        string key = "bbt_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var id = Guid.NewGuid();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("INSERT INTO workstations (workstation_id, name, key_hash, is_admin) VALUES (@id, @n, @h, @a)", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("h", Sha256Hex(key));
        cmd.Parameters.AddWithValue("a", isAdmin);
        await cmd.ExecuteNonQueryAsync(ct);
        return (id, key);
    }
}
