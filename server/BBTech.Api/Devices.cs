using BBTech.Core.Testing;
using Npgsql;

namespace BBTech.Api;

/// <summary>Device readiness and results as the server knows them (lifecycle status from the server's own history).</summary>
public static class Devices
{
    /// <summary>Statuses in which the preparation QC has passed and still counts (ITMS may use the laptop).</summary>
    public static readonly string[] ReadyStatuses =
        { LifecycleStatus.ReadyForEcommerce, LifecycleStatus.FinalCheck, LifecycleStatus.DeliveryHold, LifecycleStatus.ReadyForDelivery };

    public static async Task<DeviceStatusDto> StatusAsync(NpgsqlConnection conn, string deviceId)
    {
        var dto = new DeviceStatusDto { DeviceId = deviceId, Readiness = "UNKNOWN", LifecycleStatus = "UNKNOWN" };
        await using (var cmd = new NpgsqlCommand("SELECT lifecycle_status, qc_return_count FROM devices WHERE device_id = @d", conn))
        {
            cmd.Parameters.AddWithValue("d", deviceId);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return dto;
            dto.LifecycleStatus = r.GetString(0);
            dto.QcReturnCount = r.GetInt32(1);
        }
        await using (var cmd = new NpgsqlCommand(
            @"SELECT q.decision, u.name, q.decided_at FROM qc_decisions q JOIN users u ON u.user_id = q.user_id
              WHERE q.device_id = @d ORDER BY q.decided_at DESC, q.qc_decision_id DESC LIMIT 1", conn))
        {
            cmd.Parameters.AddWithValue("d", deviceId);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync()) { dto.QcDecision = r.GetString(0); dto.QcDecidedBy = r.GetString(1); dto.QcDecidedAt = r.GetDateTime(2); }
        }
        // READY only while the lifecycle says the preparation QC passed and still counts (a later return undoes it).
        dto.Readiness = ReadyStatuses.Contains(dto.LifecycleStatus) ? "READY"
                      : dto.QcDecision == "FAIL" || dto.LifecycleStatus.EndsWith("_REWORK") ? "NOT_READY"
                      : dto.LifecycleStatus == LifecycleStatus.Received ? "UNKNOWN" : "IN_PROGRESS";
        return dto;
    }

    public static async Task<List<TestResultDto>> LatestResultsAsync(NpgsqlConnection conn, string deviceId)
    {
        var list = new List<TestResultDto>();
        await using var cmd = new NpgsqlCommand(
            @"SELECT DISTINCT ON (department_code, test_id) record_id, device_id, user_id, operator_name, department_code, test_id, test_name, status, notes, tested_at
              FROM test_results WHERE device_id = @d AND deleted_at IS NULL AND test_id <> 'DEVICE_SNAPSHOT'
              ORDER BY department_code, test_id, tested_at DESC, received_at DESC", conn);
        cmd.Parameters.AddWithValue("d", deviceId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new TestResultDto
            {
                RecordId = r.GetString(0), DeviceId = r.GetString(1), OperatorId = r.GetString(2), OperatorName = r.GetString(3),
                Department = r.GetString(4), TestId = r.GetString(5), TestName = r.GetString(6), Status = r.GetString(7), Notes = r.GetString(8), TestedAt = r.GetDateTime(9),
            });
        return list;
    }

    /// <summary>ITMS: laptops whose preparation QC passed and still counts.</summary>
    public static async Task<List<DeviceStatusDto>> ReadyAsync(NpgsqlConnection conn)
    {
        var ids = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT device_id FROM devices WHERE lifecycle_status = ANY(@s) ORDER BY lifecycle_updated_at DESC", conn))
        {
            cmd.Parameters.AddWithValue("s", ReadyStatuses);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) ids.Add(r.GetString(0));
        }
        var list = new List<DeviceStatusDto>();
        foreach (var id in ids) list.Add(await StatusAsync(conn, id));
        return list;
    }
}
