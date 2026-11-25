using Npgsql;

namespace BBTech.Api;

/// <summary>A user as the SERVER knows it (from the database), never as a client claims it.</summary>
public sealed class ServerUser
{
    public string UserId { get; init; }
    public string Name { get; init; }
    public string Role { get; init; }
    public bool RoleIsAdmin { get; init; }
    public bool Active { get; init; }
    public HashSet<string> Departments { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Permissions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAdmin => RoleIsAdmin || Permissions.Contains("ALL");
    public bool Can(string permission) => Active && (IsAdmin || Permissions.Contains(permission));

    public bool CanAccess(string department) => Active && (IsAdmin || Departments.Contains(department) || department switch
    {
        "Hardware" => Permissions.Contains("hardware.test"),
        "Software" => Permissions.Contains("software.test"),
        "QC" => Permissions.Contains("qc.inspect"),
        "E-Commerce" => Permissions.Contains("ecommerce"),
        _ => false,
    });

    public static async Task<ServerUser> LoadAsync(NpgsqlConnection conn, string userId, NpgsqlTransaction tx = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        ServerUser u;
        await using (var cmd = new NpgsqlCommand(
            "SELECT u.user_id, u.name, u.role_code, r.is_admin, u.active FROM users u JOIN roles r ON r.code = u.role_code WHERE u.user_id = @id AND u.deleted_at IS NULL", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", userId);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            u = new ServerUser { UserId = r.GetString(0), Name = r.GetString(1), Role = r.GetString(2), RoleIsAdmin = r.GetBoolean(3), Active = r.GetBoolean(4) };
        }
        await using (var cmd = new NpgsqlCommand("SELECT department_code FROM user_departments WHERE user_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", userId);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) u.Departments.Add(r.GetString(0));
        }
        await using (var cmd = new NpgsqlCommand(
            "SELECT permission_code FROM user_permissions WHERE user_id = @id UNION SELECT permission_code FROM role_permissions WHERE role_code = @role", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", userId);
            cmd.Parameters.AddWithValue("role", u.Role);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) u.Permissions.Add(r.GetString(0));
        }
        return u;
    }
}

public static class Rules
{
    public const string QcDecisionTestId = "QC_FINAL";
    public static readonly string[] Statuses = { "NOT TESTED", "TESTING", "PASS", "FAIL", "SKIPPED" };
    public static readonly string[] WorkDepartments = { "Hardware", "Software", "QC" };

    /// <summary>Same rules as the desktop tool's ResultAuthorization, evaluated with SERVER data. Null = allowed.</summary>
    public static string CheckRecord(ServerUser user, string department, string testId)
    {
        if (user == null) return "unknown operator";
        if (!user.Active) return "operator is inactive";
        if (testId == QcDecisionTestId || testId == BBTech.Core.Testing.LifecycleEvents.QcReturn || testId == BBTech.Core.Testing.LifecycleEvents.DeliveryDecision)
            return user.Can("qc.approve") ? null : "operator lacks qc.approve";
        return department switch
        {
            "Hardware" or "Software" or "QC" => user.CanAccess(department) ? null : "operator is not allowed to record " + department + " results",
            _ => "unknown department",
        };
    }

    public static string Normalize(string department) =>
        WorkDepartments.FirstOrDefault(d => string.Equals(d, department, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Server-side QC gate: the same rules as the client (latest result per test; skip only where allowed).</summary>
public static class ServerQcGate
{
    public sealed record Requirement(string Department, string TestId, bool Required, bool SkipAllowed);

    public static async Task<List<Requirement>> LoadRequirementsAsync(NpgsqlConnection conn, NpgsqlTransaction tx = null)
    {
        var list = new List<Requirement>();
        await using var cmd = new NpgsqlCommand("SELECT department_code, test_id, required, skip_allowed FROM test_definitions WHERE active", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new Requirement(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3)));
        return list;
    }

    /// <summary>Latest (non-deleted) status per (department, test) for the device.</summary>
    public static async Task<Dictionary<(string, string), string>> LoadLatestAsync(NpgsqlConnection conn, string deviceId, NpgsqlTransaction tx = null)
    {
        var map = new Dictionary<(string, string), string>();
        await using var cmd = new NpgsqlCommand(
            @"SELECT DISTINCT ON (department_code, test_id) department_code, test_id, status
              FROM test_results WHERE device_id = @d AND deleted_at IS NULL
              ORDER BY department_code, test_id, tested_at DESC, received_at DESC", conn, tx);
        cmd.Parameters.AddWithValue("d", deviceId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) map[(r.GetString(0), r.GetString(1))] = r.GetString(2);
        return map;
    }

    /// <summary>Reasons QC PASS is not allowed (empty = allowed).</summary>
    public static List<string> BlockingReasons(IEnumerable<Requirement> requirements, IReadOnlyDictionary<(string, string), string> latest)
    {
        var reasons = new List<string>();
        foreach (var req in requirements.Where(q => q.Required))
        {
            latest.TryGetValue((req.Department, req.TestId), out var status);
            if (status == "PASS") continue;
            if (status == "SKIPPED" && req.SkipAllowed) continue;
            reasons.Add(req.Department + "/" + req.TestId + ": " + (status == null ? "not done" : status == "SKIPPED" ? "skipped but required" : status));
        }
        return reasons;
    }
}
