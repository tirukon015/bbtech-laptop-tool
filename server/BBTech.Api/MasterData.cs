using Npgsql;

namespace BBTech.Api;

/// <summary>Builds the master configuration the desktop tool downloads, and the admin changes to it.</summary>
public static class MasterData
{
    public static async Task<int> ConfigVersionAsync(NpgsqlConnection conn, NpgsqlTransaction tx = null)
    {
        await using var cmd = new NpgsqlCommand("SELECT value FROM system_configurations WHERE key = 'config_version'", conn, tx);
        return int.TryParse((string)await cmd.ExecuteScalarAsync(), out var v) ? v : 1;
    }

    /// <summary>Every master-data change bumps the version, so workstations replace their cached copy.</summary>
    public static async Task BumpVersionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string by)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE system_configurations SET value = (value::int + 1)::text, updated_at = now(), updated_by = @by WHERE key = 'config_version'", conn, tx);
        cmd.Parameters.AddWithValue("by", by ?? "");
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<List<OperatorDto>> OperatorsAsync(NpgsqlConnection conn)
    {
        var ops = new Dictionary<string, OperatorDto>();
        await using (var cmd = new NpgsqlCommand("SELECT user_id, name, role_code, active, created_at, updated_at FROM users WHERE deleted_at IS NULL ORDER BY name", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                ops[r.GetString(0)] = new OperatorDto
                {
                    OperatorId = r.GetString(0), UserId = r.GetString(0), Name = r.GetString(1), Role = r.GetString(2), Active = r.GetBoolean(3),
                    CreatedAt = r.GetDateTime(4), UpdatedAt = r.GetDateTime(5),
                };
        await using (var cmd = new NpgsqlCommand("SELECT user_id, department_code FROM user_departments ORDER BY department_code", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) if (ops.TryGetValue(r.GetString(0), out var o)) o.Departments.Add(r.GetString(1));
        // Role permissions are expanded so the client needs no role table; admin roles carry "ALL".
        await using (var cmd = new NpgsqlCommand(
            @"SELECT u.user_id, p FROM users u JOIN roles ro ON ro.code = u.role_code
              CROSS JOIN LATERAL (SELECT permission_code AS p FROM user_permissions up WHERE up.user_id = u.user_id
                                  UNION SELECT permission_code FROM role_permissions rp WHERE rp.role_code = u.role_code
                                  UNION SELECT 'ALL' WHERE ro.is_admin) x
              WHERE u.deleted_at IS NULL ORDER BY p", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) if (ops.TryGetValue(r.GetString(0), out var o)) o.Permissions.Add(r.GetString(1));
        return ops.Values.ToList();
    }

    public static async Task<MasterConfigDto> BuildAsync(NpgsqlConnection conn)
    {
        var cfg = new MasterConfigDto
        {
            ConfigVersion = await ConfigVersionAsync(conn),
            Operators = await OperatorsAsync(conn),
            Requirements = new QcRequirementsDto(),
            SupportedWindowsVersions = new List<string> { "Windows 10", "Windows 11" },
        };
        await using (var cmd = new NpgsqlCommand("SELECT updated_at FROM system_configurations WHERE key = 'config_version'", conn))
            cfg.UpdatedAt = (DateTime)(await cmd.ExecuteScalarAsync())!;
        cfg.Requirements.Version = cfg.ConfigVersion;

        await using (var cmd = new NpgsqlCommand("SELECT department_code, test_id, label, required, skip_allowed FROM test_definitions WHERE active ORDER BY department_code, sort_order", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                cfg.Requirements.Tests.Add(new TestRequirementDto { Department = r.GetString(0), TestId = r.GetString(1), Label = r.GetString(2), Required = r.GetBoolean(3), SkipAllowed = r.GetBoolean(4) });

        await using (var cmd = new NpgsqlCommand(
            @"SELECT p.package_id, p.name, p.version, p.required, p.detect_rule, p.created_by, p.created_at, p.updated_at,
                     i.source_mode, i.file_name, i.download_url, i.offline_folder, i.sha256, i.publisher, i.install_arguments, i.requires_elevation, i.timeout_minutes
              FROM software_packages p JOIN installers i ON i.package_id = p.package_id AND i.active
              WHERE p.approved AND p.deleted_at IS NULL ORDER BY p.name", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                int mode = r.GetInt16(8);
                cfg.Packages.Add(new SoftwarePackageDto
                {
                    PackageId = r.GetString(0), Name = r.GetString(1), Version = r.GetString(2), Required = r.GetBoolean(3), DetectRule = r.GetString(4),
                    CreatedBy = r.IsDBNull(5) ? "" : r.GetString(5), CreatedAt = r.GetDateTime(6), UpdatedAt = r.GetDateTime(7),
                    SourceMode = mode,
                    OnlineInstallerName = mode == 0 ? r.GetString(9) : "", OfflineExecutableName = mode == 1 ? r.GetString(9) : "",
                    OnlineDownloadUrl = r.GetString(10), OfflineFolderPath = r.GetString(11), Sha256Checksum = r.GetString(12), Publisher = r.GetString(13),
                    InstallArguments = r.GetString(14), RequiresElevation = r.GetBoolean(15), TimeoutMinutes = r.GetInt32(16), Approved = true,
                });
            }

        await using (var cmd = new NpgsqlCommand("SELECT value FROM system_configurations WHERE key = 'allow_out_of_order_work'", conn))
            cfg.Requirements.AllowOutOfOrderWork = string.Equals((string)await cmd.ExecuteScalarAsync(), "true", StringComparison.OrdinalIgnoreCase);

        var models = new Dictionary<int, LaptopModelDto>();
        await using (var cmd = new NpgsqlCommand("SELECT laptop_model_id, manufacturer, model, notes FROM laptop_models WHERE deleted_at IS NULL ORDER BY manufacturer, model", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) models[r.GetInt32(0)] = new LaptopModelDto { Manufacturer = r.GetString(1), Model = r.GetString(2), Notes = r.GetString(3) };
        await using (var cmd = new NpgsqlCommand("SELECT laptop_model_id, package_id FROM laptop_model_packages", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) if (models.TryGetValue(r.GetInt32(0), out var m)) m.RequiredPackageIds.Add(r.GetString(1));
        cfg.LaptopModels = models.Values.ToList();
        return cfg;
    }

    /// <summary>Validates a package definition with the same rules as the desktop tool (no arbitrary EXE, always verifiable).</summary>
    public static string ValidatePackage(SoftwarePackageDto p)
    {
        if (p == null || string.IsNullOrWhiteSpace(p.PackageId) || string.IsNullOrWhiteSpace(p.Name)) return "PackageId and Name are required.";
        if (!System.Text.RegularExpressions.Regex.IsMatch(p.PackageId, "^[a-z0-9][a-z0-9._-]{1,60}$")) return "PackageId: lowercase letters, digits, '.', '_' or '-'.";
        if (p.SourceMode != 0 && p.SourceMode != 1) return "SourceMode must be 0 (online) or 1 (offline).";
        string exe = p.SourceMode == 0 ? p.OnlineInstallerName : p.OfflineExecutableName;
        if (string.IsNullOrWhiteSpace(exe) || !System.Text.RegularExpressions.Regex.IsMatch(exe, @"^[A-Za-z0-9 ._()\-]{1,120}\.(exe|msi)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "The installer must be a plain .exe or .msi file name.";
        if (string.IsNullOrWhiteSpace(p.Sha256Checksum) && string.IsNullOrWhiteSpace(p.Publisher)) return "A SHA-256 checksum or a trusted publisher is required.";
        if (!string.IsNullOrWhiteSpace(p.Sha256Checksum) && !System.Text.RegularExpressions.Regex.IsMatch(p.Sha256Checksum, "^[A-Fa-f0-9]{64}$")) return "Invalid SHA-256.";
        if (!System.Text.RegularExpressions.Regex.IsMatch(p.InstallArguments ?? "", @"^[A-Za-z0-9 ._:/=\-\\""]{0,400}$") || (p.InstallArguments ?? "").Contains("..")) return "Install arguments contain characters that are not allowed.";
        if (!string.IsNullOrWhiteSpace(p.OnlineDownloadUrl) && !p.OnlineDownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "The download URL must be HTTPS.";
        if ((p.OfflineFolderPath ?? "").Contains("..")) return "The offline folder must not contain '..'.";
        if (p.TimeoutMinutes < 1 || p.TimeoutMinutes > 240) return "TimeoutMinutes must be 1-240.";
        if (p.DetectRule != "office" && !(p.DetectRule ?? "").StartsWith("uninstall:", StringComparison.OrdinalIgnoreCase)) return "DetectRule must be 'office' or 'uninstall:<name>'.";
        return null;
    }
}
