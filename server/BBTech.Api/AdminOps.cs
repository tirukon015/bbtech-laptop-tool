using Npgsql;

namespace BBTech.Api;

/// <summary>Admin changes to master data. Each change is validated, audited server-side and bumps the config version.</summary>
public static class AdminOps
{
    public static async Task<string> UpsertUserAsync(NpgsqlConnection conn, UserUpsertRequest req, string by)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Role))
            return "UserId, Name and Role are required.";
        if (req.UserId.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(req.UserId, "^[A-Za-z0-9._-]+$")) return "UserId: letters, digits, '.', '_' or '-'.";

        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM roles WHERE code = @r)", conn, tx))
        {
            cmd.Parameters.AddWithValue("r", req.Role);
            if (!(bool)(await cmd.ExecuteScalarAsync())!) return "Unknown role: " + req.Role;
        }
        string oldValue = "";
        bool exists;
        await using (var cmd = new NpgsqlCommand("SELECT name || ' / ' || role_code || CASE WHEN active THEN '' ELSE ' (inactive)' END FROM users WHERE user_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", req.UserId);
            oldValue = (string)await cmd.ExecuteScalarAsync() ?? "";
            exists = oldValue != "";
        }
        try
        {
            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO users (user_id, name, role_code, active, created_by, updated_by) VALUES (@id, @n, @r, @a, @by, @by)
                  ON CONFLICT (user_id) DO UPDATE SET name = EXCLUDED.name, role_code = EXCLUDED.role_code, active = EXCLUDED.active,
                      updated_at = now(), updated_by = EXCLUDED.updated_by, deleted_at = NULL", conn, tx);
            cmd.Parameters.AddWithValue("id", req.UserId);
            cmd.Parameters.AddWithValue("n", req.Name.Trim());
            cmd.Parameters.AddWithValue("r", req.Role);
            cmd.Parameters.AddWithValue("a", req.Active);
            cmd.Parameters.AddWithValue("by", by ?? "");
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return "An active user with this name already exists.";
        }

        await using (var cmd = new NpgsqlCommand("DELETE FROM user_departments WHERE user_id = @id; DELETE FROM user_permissions WHERE user_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", req.UserId);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var d in (req.Departments ?? new()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO user_departments (user_id, department_code) SELECT @id, code FROM departments WHERE lower(code) = lower(@d)", conn, tx);
            cmd.Parameters.AddWithValue("id", req.UserId);
            cmd.Parameters.AddWithValue("d", d);
            if (await cmd.ExecuteNonQueryAsync() == 0) return "Unknown department: " + d;
        }
        foreach (var p in (req.Permissions ?? new()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO user_permissions (user_id, permission_code) SELECT @id, code FROM permissions WHERE lower(code) = lower(@p)", conn, tx);
            cmd.Parameters.AddWithValue("id", req.UserId);
            cmd.Parameters.AddWithValue("p", p);
            if (await cmd.ExecuteNonQueryAsync() == 0) return "Unknown permission: " + p;
        }

        string newValue = req.Name.Trim() + " / " + req.Role + (req.Active ? "" : " (inactive)");
        await ServerAudit.WriteAsync(conn, tx, by, by, exists ? "USER_CHANGED" : "USER_CREATED", "user:" + req.UserId, oldValue, newValue,
            "departments: " + string.Join(", ", req.Departments ?? new()) + "; permissions: " + string.Join(", ", req.Permissions ?? new()), null);
        if (exists)
            await ServerAudit.WriteAsync(conn, tx, by, by, "PERMISSION_CHANGED", "user:" + req.UserId, "", string.Join(", ", req.Permissions ?? new()), "", null);
        await MasterData.BumpVersionAsync(conn, tx, by);
        await tx.CommitAsync();
        return null;
    }

    public static async Task UpsertPackageAsync(NpgsqlConnection conn, SoftwarePackageDto p, string by)
    {
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
            @"INSERT INTO software_packages (package_id, name, version, required, detect_rule, approved, created_by)
              VALUES (@id, @n, @v, @req, @rule, @ap, @by)
              ON CONFLICT (package_id) DO UPDATE SET name = EXCLUDED.name, version = EXCLUDED.version, required = EXCLUDED.required,
                  detect_rule = EXCLUDED.detect_rule, approved = EXCLUDED.approved, updated_at = now(), deleted_at = NULL", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", p.PackageId);
            cmd.Parameters.AddWithValue("n", p.Name);
            cmd.Parameters.AddWithValue("v", p.Version ?? "");
            cmd.Parameters.AddWithValue("req", p.Required);
            cmd.Parameters.AddWithValue("rule", p.DetectRule ?? "office");
            cmd.Parameters.AddWithValue("ap", p.Approved);
            cmd.Parameters.AddWithValue("by", by ?? "");
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand("UPDATE installers SET active = false WHERE package_id = @id AND active", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", p.PackageId);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand(
            @"INSERT INTO installers (package_id, source_mode, file_name, download_url, offline_folder, sha256, publisher, install_arguments, requires_elevation, timeout_minutes)
              VALUES (@id, @m, @f, @u, @o, @h, @pub, @args, @el, @to)", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", p.PackageId);
            cmd.Parameters.AddWithValue("m", (short)p.SourceMode);
            cmd.Parameters.AddWithValue("f", p.SourceMode == 0 ? p.OnlineInstallerName : p.OfflineExecutableName);
            cmd.Parameters.AddWithValue("u", p.OnlineDownloadUrl ?? "");
            cmd.Parameters.AddWithValue("o", p.OfflineFolderPath ?? "");
            cmd.Parameters.AddWithValue("h", (p.Sha256Checksum ?? "").ToLowerInvariant());
            cmd.Parameters.AddWithValue("pub", p.Publisher ?? "");
            cmd.Parameters.AddWithValue("args", p.InstallArguments ?? "");
            cmd.Parameters.AddWithValue("el", p.RequiresElevation);
            cmd.Parameters.AddWithValue("to", p.TimeoutMinutes);
            await cmd.ExecuteNonQueryAsync();
        }
        await ServerAudit.WriteAsync(conn, tx, by, by, "SOFTWARE_PACKAGE_CHANGED", "package:" + p.PackageId, "", p.Name + " " + p.Version,
            (p.SourceMode == 0 ? "online " : "offline ") + (p.Sha256Checksum != "" ? "sha256 " : "") + (p.Publisher != "" ? "publisher=" + p.Publisher : ""), null);
        await MasterData.BumpVersionAsync(conn, tx, by);
        await tx.CommitAsync();
    }

    public static async Task<string> ReplaceTestDefinitionsAsync(NpgsqlConnection conn, List<TestRequirementDto> tests, string by)
    {
        if (tests == null || tests.Count == 0) return "No test definitions.";
        foreach (var dept in Rules.WorkDepartments)
            if (!tests.Any(t => t.Required && string.Equals(t.Department, dept, StringComparison.OrdinalIgnoreCase)))
                return "Every department needs at least one required test (" + dept + " has none).";
        string Group(string d) => Rules.Normalize(d) ?? (string.Equals(d, "Delivery", StringComparison.OrdinalIgnoreCase) ? "Delivery" : null);
        if (tests.Any(t => string.IsNullOrWhiteSpace(t.TestId) || BBTech.Core.Testing.LifecycleEvents.IsStageEvent(t.TestId) || BBTech.Core.Testing.LifecycleEvents.IsEvidence(t.TestId) || Group(t.Department) == null))
            return "Each test needs a TestId and a group (Hardware, Software, QC or Delivery); lifecycle events are not tests.";

        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("UPDATE test_definitions SET active = false", conn, tx)) await cmd.ExecuteNonQueryAsync();
        int order = 0;
        foreach (var t in tests)
        {
            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO test_definitions (department_code, test_id, label, required, skip_allowed, sort_order, active)
                  VALUES (@d, @t, @l, @r, @s, @o, true)
                  ON CONFLICT (department_code, test_id) DO UPDATE SET label = EXCLUDED.label, required = EXCLUDED.required,
                      skip_allowed = EXCLUDED.skip_allowed, sort_order = EXCLUDED.sort_order, active = true", conn, tx);
            cmd.Parameters.AddWithValue("d", Group(t.Department));
            cmd.Parameters.AddWithValue("t", t.TestId);
            cmd.Parameters.AddWithValue("l", string.IsNullOrWhiteSpace(t.Label) ? t.TestId : t.Label);
            cmd.Parameters.AddWithValue("r", t.Required);
            cmd.Parameters.AddWithValue("s", t.SkipAllowed);
            cmd.Parameters.AddWithValue("o", ++order);
            await cmd.ExecuteNonQueryAsync();
        }
        await ServerAudit.WriteAsync(conn, tx, by, by, "CONFIG_CHANGED", "test_definitions", "", tests.Count + " definitions", "", null);
        await MasterData.BumpVersionAsync(conn, tx, by);
        await tx.CommitAsync();
        return null;
    }

    public static async Task<string> UpsertLaptopModelAsync(NpgsqlConnection conn, LaptopModelDto m, string by)
    {
        if (string.IsNullOrWhiteSpace(m?.Manufacturer) || string.IsNullOrWhiteSpace(m.Model)) return "Manufacturer and Model are required.";
        await using var tx = await conn.BeginTransactionAsync();
        int id;
        await using (var cmd = new NpgsqlCommand(
            @"INSERT INTO laptop_models (manufacturer, model, notes) VALUES (@m, @mo, @n)
              ON CONFLICT (manufacturer, model) DO UPDATE SET notes = EXCLUDED.notes, deleted_at = NULL RETURNING laptop_model_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("m", m.Manufacturer.Trim());
            cmd.Parameters.AddWithValue("mo", m.Model.Trim());
            cmd.Parameters.AddWithValue("n", m.Notes ?? "");
            id = (int)(await cmd.ExecuteScalarAsync())!;
        }
        await using (var cmd = new NpgsqlCommand("DELETE FROM laptop_model_packages WHERE laptop_model_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var p in (m.RequiredPackageIds ?? new()).Distinct())
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO laptop_model_packages (laptop_model_id, package_id) SELECT @id, package_id FROM software_packages WHERE package_id = @p AND deleted_at IS NULL", conn, tx);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("p", p);
            if (await cmd.ExecuteNonQueryAsync() == 0) return "Unknown package: " + p;
        }
        await ServerAudit.WriteAsync(conn, tx, by, by, "CONFIG_CHANGED", "laptop_model:" + m.Manufacturer + " " + m.Model, "", string.Join(", ", m.RequiredPackageIds ?? new()), "", null);
        await MasterData.BumpVersionAsync(conn, tx, by);
        await tx.CommitAsync();
        return null;
    }

    /// <summary>Admin delete of a test result = tombstone (history kept). Final QC decisions can never be deleted.</summary>
    public static async Task<string> TombstoneResultAsync(NpgsqlConnection conn, string recordId, ServerUser actor)
    {
        if (!actor.Can("records.delete")) return "records.delete required.";
        await using var tx = await conn.BeginTransactionAsync();
        string testId;
        await using (var cmd = new NpgsqlCommand("SELECT test_id FROM test_results WHERE record_id = @id AND deleted_at IS NULL", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", recordId);
            testId = (string)await cmd.ExecuteScalarAsync();
        }
        if (testId == null) return "No such record.";
        if (BBTech.Core.Testing.LifecycleEvents.IsStageEvent(testId) || BBTech.Core.Testing.LifecycleEvents.IsEvidence(testId))
            return "Lifecycle events, QC decisions and device evidence cannot be deleted.";
        await using (var cmd = new NpgsqlCommand("UPDATE test_results SET deleted_at = now(), deleted_by = @by WHERE record_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", recordId);
            cmd.Parameters.AddWithValue("by", actor.UserId);
            await cmd.ExecuteNonQueryAsync();
        }
        await ServerAudit.WriteAsync(conn, tx, actor.UserId, actor.Name, "RECORD_DELETED", "record:" + recordId, testId, "DELETED", "tombstone", null);
        await tx.CommitAsync();
        return null;
    }

    public static async Task<List<AuditRecordDto>> AuditAsync(NpgsqlConnection conn, int limit)
    {
        var list = new List<AuditRecordDto>();
        await using var cmd = new NpgsqlCommand(
            "SELECT audit_id, occurred_at, actor_user_id, actor_name, action, target, old_value, new_value, details, client_checksum, previous_checksum FROM audit_logs ORDER BY occurred_at DESC LIMIT @l", conn);
        cmd.Parameters.AddWithValue("l", limit);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new AuditRecordDto
            {
                AuditId = r.GetString(0), Timestamp = r.GetDateTime(1), ActorUserId = r.GetString(2), ActorName = r.GetString(3), Action = r.GetString(4),
                Target = r.GetString(5), OldValue = r.GetString(6), NewValue = r.GetString(7), Details = r.GetString(8), Checksum = r.GetString(9), PreviousChecksum = r.GetString(10), Synced = true,
            });
        return list;
    }
}
