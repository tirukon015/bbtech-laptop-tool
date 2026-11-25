using BBTech.Api;
using BBTech.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace BBTech.Api.Tests;

/// <summary>
/// Runs the real API in-process against a fresh, throw-away PostgreSQL database (created per test class,
/// dropped afterwards). Connection: BBTECH_TEST_PG (a connection string to any database on the server),
/// otherwise the local dev container from server/docker-compose.yml using the password in server/.env.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public WebApplicationFactory<Program> Factory { get; private set; }
    public Database Db { get; private set; }
    public string WorkstationKey { get; private set; }
    public string AdminKey { get; private set; }
    public string ConnectionString { get; private set; }
    private string _adminConnection;
    private string _dbName;

    private static string BaseConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("BBTECH_TEST_PG");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "docker-compose.yml"))) dir = Path.GetDirectoryName(dir);
        string envFile = dir == null ? null : Path.Combine(dir, ".env");
        if (envFile == null || !File.Exists(envFile))
            throw new InvalidOperationException("No test database: set BBTECH_TEST_PG, or start server/docker-compose.yml with a server/.env file.");
        string pw = File.ReadAllLines(envFile).Select(l => l.Trim()).First(l => l.StartsWith("BBTECH_DB_PASSWORD=")).Substring("BBTECH_DB_PASSWORD=".Length);
        return new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 5433, Username = "bbtech", Password = pw, Database = "bbtech" }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        _adminConnection = BaseConnectionString();
        _dbName = "bbtech_test_" + Guid.NewGuid().ToString("N").Substring(0, 12);
        await using (var conn = new NpgsqlConnection(_adminConnection))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("CREATE DATABASE " + _dbName, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _dbName, Pooling = false }.ConnectionString;

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", ConnectionString);
            b.UseEnvironment("Development");
        });
        _ = Factory.Server; // starts the app: migrations run here
        Db = new Database(ConnectionString);

        Guid wsId;
        (wsId, WorkstationKey) = await Db.CreateWorkstationAsync("USB-01", isAdmin: false);
        (_, AdminKey) = await Db.CreateWorkstationAsync("Admin console", isAdmin: true);
        await using var c = await Db.OpenAsync();
        await AdminOps.UpsertUserAsync(c, new UserUpsertRequest { UserId = "touhidul", Name = "Touhidul", Role = "Super Admin" }, "test");
        await AdminOps.UpsertUserAsync(c, new UserUpsertRequest { UserId = "kamal", Name = "Kamal", Role = "Technician", Departments = { "Hardware" }, Permissions = { "hardware.test" } }, "test");
        await AdminOps.UpsertUserAsync(c, new UserUpsertRequest { UserId = "shadir", Name = "Shadir", Role = "Technician", Departments = { "Hardware", "Software" },
            Permissions = { "hardware.test", "software.test", "software.install", "software.activation.check" } }, "test");
        await AdminOps.UpsertUserAsync(c, new UserUpsertRequest { UserId = "jimmy", Name = "Jimmy", Role = "QC Inspector", Departments = { "QC" }, Permissions = { "qc.inspect", "qc.approve" } }, "test");
        await AdminOps.UpsertUserAsync(c, new UserUpsertRequest { UserId = "helper", Name = "Helper", Role = "QC Inspector", Departments = { "QC" }, Permissions = { "qc.inspect" } }, "test");
        // The test USB is registered for every test operator (QC decisions need an explicit registration).
        foreach (var u in new[] { "touhidul", "kamal", "shadir", "jimmy", "helper" })
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO workstation_operators (workstation_id, user_id) VALUES (@w, @u)", c);
            cmd.Parameters.AddWithValue("w", wsId);
            cmd.Parameters.AddWithValue("u", u);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public HttpClient Client(string key = null, string actor = null)
    {
        var http = Factory.CreateClient();
        if (key != null) http.DefaultRequestHeaders.Add("X-BBTech-Api-Key", key);
        if (actor != null) http.DefaultRequestHeaders.Add("X-BBTech-Actor", actor);
        return http;
    }

    /// <summary>The desktop tool's real API client, talking to the in-process server.</summary>
    public BBTech.Core.Sync.BBTechApiClient DesktopClient(string key = null) =>
        new BBTech.Core.Sync.BBTechApiClient("http://localhost", key ?? WorkstationKey, Factory.CreateClient());

    public async Task<T> ScalarAsync<T>(string sql, params (string, object)[] p)
    {
        await using var conn = await Db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return (T)await cmd.ExecuteScalarAsync();
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        Db?.Source.Dispose();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_adminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DROP DATABASE IF EXISTS " + _dbName + " WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
