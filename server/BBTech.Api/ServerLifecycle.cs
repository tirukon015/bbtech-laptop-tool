using BBTech.Core.Storage;
using BBTech.Core.Testing;
using Npgsql;

namespace BBTech.Api;

/// <summary>
/// The laptop lifecycle on the server. It uses the SAME engine as the desktop tool (BBTech.Core
/// LaptopLifecycle), fed with the server's own rows, so the rules cannot drift apart and the server never
/// has to trust a status claimed by a client.
/// </summary>
public static class ServerLifecycle
{
    public static async Task<QcRequirements> RequirementsAsync(NpgsqlConnection conn, NpgsqlTransaction tx = null)
    {
        var req = new QcRequirements();
        await using (var cmd = new NpgsqlCommand("SELECT department_code, test_id, label, required, skip_allowed FROM test_definitions WHERE active ORDER BY department_code, sort_order", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                req.Tests.Add(new TestRequirement { Department = r.GetString(0), TestId = r.GetString(1), Label = r.GetString(2), Required = r.GetBoolean(3), SkipAllowed = r.GetBoolean(4) });
        await using (var cmd = new NpgsqlCommand("SELECT value FROM system_configurations WHERE key = 'allow_out_of_order_work'", conn, tx))
            req.AllowOutOfOrderWork = string.Equals((string)await cmd.ExecuteScalarAsync(), "true", StringComparison.OrdinalIgnoreCase);
        return req;
    }

    /// <summary>Every non-deleted row of the laptop, oldest first, as Core records (snapshots without their large JSON).</summary>
    public static async Task<List<TestResultRecord>> HistoryAsync(NpgsqlConnection conn, string deviceId, NpgsqlTransaction tx = null, bool withEvidence = false)
    {
        var list = new List<TestResultRecord>();
        await using var cmd = new NpgsqlCommand(
            @"SELECT record_id, user_id, operator_name, department_code, test_id, test_name, status, notes,
                     CASE WHEN test_id = 'DEVICE_SNAPSHOT' AND NOT @ev THEN '' ELSE details END, tested_at
              FROM test_results WHERE device_id = @d AND deleted_at IS NULL ORDER BY tested_at, received_at", conn, tx);
        cmd.Parameters.AddWithValue("d", deviceId);
        cmd.Parameters.AddWithValue("ev", withEvidence);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var at = r.GetDateTime(9);
            string status = r.GetString(6);
            list.Add(new TestResultRecord
            {
                TestResultId = r.GetString(0), DeviceId = deviceId, OperatorId = r.GetString(1), OperatorName = r.GetString(2), Department = r.GetString(3),
                TestId = r.GetString(4), TestName = r.GetString(5), Status = status, Comment = r.GetString(7), Details = r.GetString(8),
                StartedAt = at, CompletedAt = status == "TESTING" || status == "NOT TESTED" ? null : at, Required = true,
            });
        }
        return list;
    }

    public static async Task<LifecycleState> StateAsync(NpgsqlConnection conn, string deviceId, QcRequirements req, NpgsqlTransaction tx = null) =>
        LaptopLifecycle.Evaluate(await HistoryAsync(conn, deviceId, tx), req);

    /// <summary>After a record was stored: recompute the status; when it changed, record the transition and update the device.</summary>
    public static async Task ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string deviceId, QcRequirements req, SyncQueueItemDto record, string userId, string department)
    {
        var state = await StateAsync(conn, deviceId, req, tx);
        string previous;
        await using (var cmd = new NpgsqlCommand("SELECT lifecycle_status FROM devices WHERE device_id = @d FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("d", deviceId);
            previous = (string)await cmd.ExecuteScalarAsync() ?? LifecycleStatus.Received;
        }
        await using (var cmd = new NpgsqlCommand("UPDATE devices SET lifecycle_status = @s, qc_return_count = @n, lifecycle_updated_at = CASE WHEN lifecycle_status <> @s THEN @at ELSE lifecycle_updated_at END WHERE device_id = @d", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", state.Status);
            cmd.Parameters.AddWithValue("n", state.ReturnCount);
            cmd.Parameters.AddWithValue("at", record.Timestamp.ToUniversalTime());
            cmd.Parameters.AddWithValue("d", deviceId);
            await cmd.ExecuteNonQueryAsync();
        }
        if (previous == state.Status) return;
        await using (var cmd = new NpgsqlCommand(
            @"INSERT INTO lifecycle_events (device_id, from_status, to_status, record_id, user_id, department_code, reason, occurred_at)
              VALUES (@d, @f, @t, @r, @u, @dep, @why, @at)", conn, tx))
        {
            cmd.Parameters.AddWithValue("d", deviceId);
            cmd.Parameters.AddWithValue("f", previous);
            cmd.Parameters.AddWithValue("t", state.Status);
            cmd.Parameters.AddWithValue("r", record.RecordId);
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("dep", department);
            cmd.Parameters.AddWithValue("why", record.TestId == LifecycleEvents.QcReturn || record.TestId == LifecycleEvents.DeliveryDecision || record.TestId == LifecycleEvents.QcDecision ? record.Notes ?? "" : "");
            cmd.Parameters.AddWithValue("at", record.Timestamp.ToUniversalTime());
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Management view: where laptops are, how long they stay, rework and QC failure reasons.</summary>
    public static async Task<object> ReportAsync(NpgsqlConnection conn)
    {
        var byStatus = new Dictionary<string, long>();
        await using (var cmd = new NpgsqlCommand("SELECT lifecycle_status, count(*) FROM devices GROUP BY 1 ORDER BY 1", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) byStatus[r.GetString(0)] = r.GetInt64(1);

        var hoursInStage = new Dictionary<string, double>();
        await using (var cmd = new NpgsqlCommand(
            @"SELECT from_status, round(avg(extract(epoch FROM (occurred_at - prev_at)) / 3600.0)::numeric, 2)
              FROM (SELECT from_status, occurred_at, lag(occurred_at) OVER (PARTITION BY device_id ORDER BY occurred_at, lifecycle_event_id) AS prev_at FROM lifecycle_events) x
              WHERE prev_at IS NOT NULL GROUP BY 1 ORDER BY 1", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) hoursInStage[r.GetString(0)] = (double)r.GetDecimal(1);

        var returns = new List<object>();
        await using (var cmd = new NpgsqlCommand(
            @"SELECT details AS target, notes AS reason, count(*) FROM test_results WHERE test_id = 'LC_QC_RETURN' AND deleted_at IS NULL GROUP BY 1, 2 ORDER BY 3 DESC LIMIT 50", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) returns.Add(new { ReturnedTo = r.GetString(0), Reason = r.GetString(1), Count = r.GetInt64(2) });

        var operators = new List<object>();
        await using (var cmd = new NpgsqlCommand(
            @"SELECT operator_name, department_code, count(*) FILTER (WHERE status = 'PASS'), count(*) FILTER (WHERE status = 'FAIL'), count(DISTINCT device_id)
              FROM test_results WHERE deleted_at IS NULL AND test_id NOT IN ('DEVICE_SNAPSHOT', 'DEVICE_HARDWARE_CHANGED') GROUP BY 1, 2 ORDER BY 1, 2", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) operators.Add(new { Operator = r.GetString(0), Department = r.GetString(1), Pass = r.GetInt64(2), Fail = r.GetInt64(3), Laptops = r.GetInt64(4) });

        long Count(params string[] s) => s.Sum(x => byStatus.TryGetValue(x, out var n) ? n : 0);
        long total, reworked, qcFailed;
        await using (var cmd = new NpgsqlCommand(
            @"SELECT (SELECT count(*) FROM devices), (SELECT count(*) FROM devices WHERE qc_return_count > 0),
                     (SELECT count(DISTINCT device_id) FROM qc_decisions WHERE decision = 'FAIL')", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        { await r.ReadAsync(); total = r.GetInt64(0); reworked = r.GetInt64(1); qcFailed = r.GetInt64(2); }

        return new
        {
            GeneratedAt = DateTime.UtcNow,
            TotalLaptops = total,
            ByStatus = byStatus,
            InHardware = Count(LifecycleStatus.Received, LifecycleStatus.HardwareInProgress, LifecycleStatus.HardwareRework),
            WaitingForSoftware = Count(LifecycleStatus.ReadyForSoftware),
            InSoftware = Count(LifecycleStatus.SoftwareInProgress, LifecycleStatus.SoftwareRework),
            InQc = Count(LifecycleStatus.ReadyForQc, LifecycleStatus.QcInProgress, LifecycleStatus.QcRework),
            ReadyForEcommerce = Count(LifecycleStatus.ReadyForEcommerce),
            WaitingForDeliveryCheck = Count(LifecycleStatus.ReadyForEcommerce, LifecycleStatus.FinalCheck, LifecycleStatus.DeliveryHold),
            ReadyForDelivery = Count(LifecycleStatus.ReadyForDelivery),
            LaptopsReturnedByQc = reworked,
            LaptopsThatFailedQc = qcFailed,
            AverageHoursInStage = hoursInStage,
            QcReturnReasons = returns,
            WorkByOperator = operators,
        };
    }
}
