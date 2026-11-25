using System.Globalization;
using BBTech.Api.Data;
using BBTech.Core.Models;
using BBTech.Core.Testing;
using Npgsql;

namespace BBTech.Api;

/// <summary>
/// Receives one USB upload batch. For every record:
///   validate → authorise with SERVER data (user, permissions, QC gate for QC PASS) → insert idempotently
///   (record_id is the primary key) → read back and compare the server-computed content hash → VERIFIED.
/// A record already stored with the same content is VERIFIED again (safe retry). A record stored with
/// DIFFERENT content, or refused by a rule, is REJECTED with a reason and stays on the USB.
/// Only VERIFIED ids are returned in VerifiedRecordIds, so the client deletes nothing else.
/// </summary>
public sealed class SyncProcessor
{
    public const int MaxRecordsPerBatch = 5000;
    private readonly Database _db;

    public SyncProcessor(Database db) { _db = db; }

    public static string ContentHash(SyncQueueItemDto r) => Database.Sha256Hex(string.Join("|",
        r.RecordId, r.DeviceId, r.OperatorId, Rules.Normalize(r.Department), r.TestId, r.Result, r.Notes ?? "",
        r.Timestamp.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), r.AppVersion ?? "")
        // The evidence (snapshot JSON etc.) is part of what is verified: a changed snapshot is a conflict, not a duplicate.
        + (string.IsNullOrEmpty(r.Details) ? "" : "|details:" + Database.Sha256Hex(r.Details)));

    public async Task<SyncUploadResultDto> ProcessAsync(SyncUploadBatchDto batch, Guid workstationId, CancellationToken ct = default)
    {
        var result = new SyncUploadResultDto { Success = true };
        if (batch == null || string.IsNullOrWhiteSpace(batch.BatchId) || batch.BatchId.Length > 100)
            return new SyncUploadResultDto { Success = false, ErrorMessage = "Missing or invalid batch id." };
        batch.Records ??= new(); batch.AuditRecords ??= new();
        if (batch.Records.Count + batch.AuditRecords.Count > MaxRecordsPerBatch)
            return new SyncUploadResultDto { Success = false, ErrorMessage = "Batch too large (max " + MaxRecordsPerBatch + ")." };

        await using var conn = await _db.OpenAsync(ct);
        var outcomes = new List<(string id, string outcome, string reason)>();

        // Oldest first: a QC PASS is evaluated after the Hardware/Software results uploaded in the same batch.
        foreach (var rec in batch.Records.Where(r => r != null).OrderBy(r => r.Timestamp))
        {
            string outcome, reason;
            try { (outcome, reason) = await ProcessRecordAsync(conn, rec, batch.BatchId, workstationId, ct); }
            catch (PostgresException ex)
            {
                // e.g. the same record arriving from two workstations at once: not verified now, retried next sync.
                (outcome, reason) = ("REJECTED", "temporary server error (" + ex.SqlState + "); it will be retried");
            }
            outcomes.Add((rec.RecordId, outcome, reason));
            if (outcome == "REJECTED") result.Rejected.Add(new RejectedRecordDto { RecordId = rec.RecordId, Reason = reason });
            else
            {
                result.VerifiedRecordIds.Add(rec.RecordId);
                if (outcome == "DUPLICATE") result.DuplicateCount++;
            }
        }

        foreach (var a in batch.AuditRecords.Where(a => a != null))
            if (await StoreAuditAsync(conn, a, workstationId, ct)) result.VerifiedAuditIds.Add(a.AuditId);

        result.ProcessedCount = result.VerifiedRecordIds.Count;

        // Batch bookkeeping (idempotent on the batch id; the same batch always gets the same receipt).
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            string receipt;
            await using (var cmd = new NpgsqlCommand(
                @"INSERT INTO sync_batches (batch_id, receipt_id, workstation_id, record_count, verified_count, rejected_count)
                  VALUES (@b, @r, @w, @n, @v, @x)
                  ON CONFLICT (batch_id) DO UPDATE SET verified_count = EXCLUDED.verified_count, rejected_count = EXCLUDED.rejected_count
                  RETURNING receipt_id", conn, tx))
            {
                cmd.Parameters.AddWithValue("b", batch.BatchId);
                cmd.Parameters.AddWithValue("r", "RCPT-" + Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("w", workstationId);
                cmd.Parameters.AddWithValue("n", batch.Records.Count);
                cmd.Parameters.AddWithValue("v", result.VerifiedRecordIds.Count);
                cmd.Parameters.AddWithValue("x", result.Rejected.Count);
                receipt = (string)(await cmd.ExecuteScalarAsync(ct))!;
            }
            foreach (var (id, outcome, reason) in outcomes.Where(o => !string.IsNullOrEmpty(o.id)))
            {
                await using var cmd = new NpgsqlCommand(
                    @"INSERT INTO sync_records (batch_id, record_id, outcome, reason) VALUES (@b, @id, @o, @r)
                      ON CONFLICT (batch_id, record_id) DO UPDATE SET outcome = EXCLUDED.outcome, reason = EXCLUDED.reason", conn, tx);
                cmd.Parameters.AddWithValue("b", batch.BatchId);
                cmd.Parameters.AddWithValue("id", id.Length > 100 ? id.Substring(0, 100) : id);
                cmd.Parameters.AddWithValue("o", outcome);
                cmd.Parameters.AddWithValue("r", reason ?? "");
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await ServerAudit.WriteAsync(conn, tx, "server", "BBTech API", "SYNC_COMPLETED", "batch:" + batch.BatchId, "", receipt,
                $"{result.VerifiedRecordIds.Count} verified ({result.DuplicateCount} already stored), {result.Rejected.Count} rejected, {result.VerifiedAuditIds.Count} audit", workstationId, ct);
            await tx.CommitAsync(ct);
            result.ServerReceiptId = receipt;
        }
        return result;
    }

    private static string Validate(SyncQueueItemDto r)
    {
        if (string.IsNullOrWhiteSpace(r.RecordId) || r.RecordId.Length > 100) return "invalid record id";
        if (string.IsNullOrWhiteSpace(r.DeviceId) || r.DeviceId.Length > 200) return "invalid device id";
        if (string.IsNullOrWhiteSpace(r.TestId) || r.TestId.Length > 100) return "invalid test id";
        if (Rules.Normalize(r.Department) == null) return "unknown department";
        if (!Rules.Statuses.Contains(r.Result)) return "invalid result";
        if (r.TestId == Rules.QcDecisionTestId && r.Result != "PASS" && r.Result != "FAIL") return "a final QC decision must be PASS or FAIL";
        if (r.TestId == Rules.QcDecisionTestId && Rules.Normalize(r.Department) != "QC") return "a final QC decision must come from QC";
        if (r.Timestamp == default || r.Timestamp > DateTime.UtcNow.AddDays(1)) return "invalid timestamp";
        bool snapshot = r.TestId == LifecycleEvents.DeviceSnapshot;
        if ((r.Notes?.Length ?? 0) > 4000 || (r.Details?.Length ?? 0) > (snapshot ? 600000 : 8000)) return "notes too long";
        if (snapshot && TechnicalInventory.FromJson(r.Details) == null) return "the technical snapshot is not readable";
        if ((r.TestId == LifecycleEvents.HardwareHandover || r.TestId == LifecycleEvents.SoftwareHandover) && r.Result != "PASS") return "a handover must be PASS";
        if (r.TestId == LifecycleEvents.HardwareHandover && Rules.Normalize(r.Department) != "Hardware") return "a Hardware handover must come from Hardware";
        if (r.TestId == LifecycleEvents.SoftwareHandover && Rules.Normalize(r.Department) != "Software") return "a Software handover must come from Software";
        if (r.TestId == LifecycleEvents.QcReturn && (Rules.Normalize(r.Department) != "QC" || (r.Details != "Hardware" && r.Details != "Software") || string.IsNullOrWhiteSpace(r.Notes)))
            return "a QC return needs a target department (Hardware or Software) and a reason";
        if (r.TestId == LifecycleEvents.DeliveryDecision && (Rules.Normalize(r.Department) != "QC" || (r.Result != "PASS" && r.Result != "FAIL")))
            return "a delivery decision must be PASS or FAIL from QC";
        if (r.TestId == LifecycleEvents.DeliveryDecision && r.Result == "FAIL" && string.IsNullOrWhiteSpace(r.Notes)) return "a delivery hold needs a reason";
        return null;
    }

    private async Task<(string outcome, string reason)> ProcessRecordAsync(NpgsqlConnection conn, SyncQueueItemDto r, string batchId, Guid workstationId, CancellationToken ct)
    {
        string invalid = Validate(r);
        if (invalid != null) return ("REJECTED", invalid);
        string department = Rules.Normalize(r.Department);
        string hash = ContentHash(r);

        await using var tx = await conn.BeginTransactionAsync(ct);
        // Already stored? Same content = verified again (idempotent retry); different content = conflict.
        await using (var cmd = new NpgsqlCommand("SELECT content_hash FROM test_results WHERE record_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", r.RecordId);
            var existing = (string)await cmd.ExecuteScalarAsync(ct);
            if (existing != null)
                return existing == hash ? ("DUPLICATE", "") : ("REJECTED", "a different record with this id is already stored");
        }

        var user = await ServerUser.LoadAsync(conn, r.OperatorId, tx);
        string denied = Rules.CheckRecord(user, department, r.TestId);
        if (denied != null) return ("REJECTED", denied);

        // The operator id is asserted by the workstation. If an admin restricted this workstation to named
        // operators, records for anyone else are refused (limits what a lost/copied USB key can claim).
        // Decisions (QC PASS/FAIL, QC return, delivery release/hold) FAIL CLOSED: the operator must be explicitly
        // registered on this workstation key, because without a technician PIN the operator id is only asserted.
        bool hasList, listed;
        await using (var cmd = new NpgsqlCommand(
            @"SELECT EXISTS (SELECT 1 FROM workstation_operators WHERE workstation_id = @w),
                     EXISTS (SELECT 1 FROM workstation_operators WHERE workstation_id = @w AND user_id = @u)", conn, tx))
        {
            cmd.Parameters.AddWithValue("w", workstationId);
            cmd.Parameters.AddWithValue("u", user.UserId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            await rd.ReadAsync(ct);
            hasList = rd.GetBoolean(0); listed = rd.GetBoolean(1);
        }
        if (hasList && !listed) return ("REJECTED", "operator is not allowed on this workstation");
        bool decision = r.TestId == LifecycleEvents.QcDecision || r.TestId == LifecycleEvents.QcReturn || r.TestId == LifecycleEvents.DeliveryDecision;
        if (decision && !listed)
            return ("REJECTED", "QC decisions are accepted only from a workstation registered for this QC approver (admin: assign the operator to this USB key)");

        await using (var cmd = new NpgsqlCommand(
            @"INSERT INTO devices (device_id, serial_number, manufacturer, model) VALUES (@d, @s, @m, @mo)
              ON CONFLICT (device_id) DO UPDATE SET last_seen_at = now(),
                  serial_number = CASE WHEN EXCLUDED.serial_number <> '' THEN EXCLUDED.serial_number ELSE devices.serial_number END,
                  manufacturer  = CASE WHEN EXCLUDED.manufacturer  <> '' THEN EXCLUDED.manufacturer  ELSE devices.manufacturer END,
                  model         = CASE WHEN EXCLUDED.model         <> '' THEN EXCLUDED.model         ELSE devices.model END", conn, tx))
        {
            cmd.Parameters.AddWithValue("d", r.DeviceId);
            cmd.Parameters.AddWithValue("s", r.DeviceSerial ?? "");
            cmd.Parameters.AddWithValue("m", r.DeviceManufacturer ?? "");
            cmd.Parameters.AddWithValue("mo", r.DeviceModel ?? "");
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Lifecycle rules, evaluated with the SERVER's own history of this laptop (same engine as the tool):
        // stage order, handovers only when the stage is complete, QC PASS only when everything required passed,
        // delivery check only after QC PASS. Nothing a client claims is taken on trust.
        var requirements = await ServerLifecycle.RequirementsAsync(conn, tx);
        string blocked = LaptopLifecycle.CheckEvent(await ServerLifecycle.StateAsync(conn, r.DeviceId, requirements, tx), department, r.TestId, r.Result, requirements);
        if (blocked != null)
            return ("REJECTED", (r.TestId == Rules.QcDecisionTestId ? "QC PASS not accepted by the server: " : "not accepted by the server: ") + (blocked.Length > 600 ? blocked.Substring(0, 600) + "..." : blocked));

        await using (var cmd = new NpgsqlCommand(
            @"INSERT INTO test_results (record_id, device_id, user_id, operator_name, department_code, test_id, test_name, status, notes, details,
                                        tested_at, app_version, content_hash, batch_id, workstation_id)
              VALUES (@id, @d, @u, @on, @dep, @t, @tn, @s, @n, @det, @at, @v, @h, @b, @w)", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", r.RecordId);
            cmd.Parameters.AddWithValue("d", r.DeviceId);
            cmd.Parameters.AddWithValue("u", user.UserId);
            cmd.Parameters.AddWithValue("on", user.Name);           // the server's name for the user, not the client's claim
            cmd.Parameters.AddWithValue("dep", department);
            cmd.Parameters.AddWithValue("t", r.TestId);
            cmd.Parameters.AddWithValue("tn", r.TestName ?? "");
            cmd.Parameters.AddWithValue("s", r.Result);
            cmd.Parameters.AddWithValue("n", r.Notes ?? "");
            cmd.Parameters.AddWithValue("det", r.Details ?? "");
            cmd.Parameters.AddWithValue("at", r.Timestamp.ToUniversalTime());
            cmd.Parameters.AddWithValue("v", r.AppVersion ?? "");
            cmd.Parameters.AddWithValue("h", hash);
            cmd.Parameters.AddWithValue("b", batchId);
            cmd.Parameters.AddWithValue("w", workstationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (r.TestId == Rules.QcDecisionTestId)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO qc_decisions (record_id, device_id, user_id, decision, snapshot, decided_at) VALUES (@id, @d, @u, @x, @s, @at)", conn, tx);
            cmd.Parameters.AddWithValue("id", r.RecordId);
            cmd.Parameters.AddWithValue("d", r.DeviceId);
            cmd.Parameters.AddWithValue("u", user.UserId);
            cmd.Parameters.AddWithValue("x", r.Result);
            cmd.Parameters.AddWithValue("s", r.Details ?? "");
            cmd.Parameters.AddWithValue("at", r.Timestamp.ToUniversalTime());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (r.TestId == LifecycleEvents.DeviceSnapshot)
        {
            var inventory = TechnicalInventory.FromJson(r.Details);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO device_snapshots (record_id, device_id, hardware_signature, captured_at, inventory) VALUES (@id, @d, @sig, @at, @inv::jsonb)", conn, tx);
            cmd.Parameters.AddWithValue("id", r.RecordId);
            cmd.Parameters.AddWithValue("d", r.DeviceId);
            cmd.Parameters.AddWithValue("sig", inventory.HardwareSignature());
            cmd.Parameters.AddWithValue("at", r.Timestamp.ToUniversalTime());
            cmd.Parameters.AddWithValue("inv", r.Details);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Recompute where the laptop is now; record the transition (previous stage → next stage, who, when).
        await ServerLifecycle.ApplyAsync(conn, tx, r.DeviceId, requirements, r, user.UserId, department);
        await tx.CommitAsync(ct);

        // Verify: read back what is stored and compare with what was sent.
        await using (var cmd = new NpgsqlCommand("SELECT content_hash FROM test_results WHERE record_id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", r.RecordId);
            var stored = (string)await cmd.ExecuteScalarAsync(ct);
            return stored == hash ? ("INSERTED", "") : ("REJECTED", "stored record could not be verified");
        }
    }

    /// <summary>Actions only the server performs (user/role/permission/package/workstation administration): a client
    /// entry claiming one is kept, but never counted as verified.</summary>
    private static readonly HashSet<string> ServerOnlyAuditActions = new(StringComparer.Ordinal)
    {
        "USER_CREATED", "USER_DISABLED", "USER_ENABLED", "USER_CHANGED", "ROLE_CHANGED", "PERMISSION_CHANGED",
        "SOFTWARE_PACKAGE_CHANGED", "WORKSTATION_CREATED", "RECORD_DELETED",
    };

    private static async Task<bool> StoreAuditAsync(NpgsqlConnection conn, AuditRecordDto a, Guid workstationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(a.AuditId) || a.AuditId.Length > 100 || string.IsNullOrWhiteSpace(a.Action) || a.Timestamp == default) return false;

        // The client names the actor itself, so the server records whether it can confirm that claim. Nothing is dropped
        // (it stays evidence), but only confirmed entries count as proof of who did something.
        bool verified;
        await using (var cmd = new NpgsqlCommand(
            @"SELECT EXISTS (SELECT 1 FROM users WHERE user_id = @u AND active AND deleted_at IS NULL)
                 AND (NOT EXISTS (SELECT 1 FROM workstation_operators WHERE workstation_id = @w)
                      OR EXISTS (SELECT 1 FROM workstation_operators WHERE workstation_id = @w AND user_id = @u))", conn))
        {
            cmd.Parameters.AddWithValue("u", a.ActorUserId ?? "");
            cmd.Parameters.AddWithValue("w", workstationId);
            verified = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        }
        verified = verified && a.Timestamp.ToUniversalTime() <= DateTime.UtcNow.AddDays(1) && !ServerOnlyAuditActions.Contains(a.Action);

        await using (var cmd = new NpgsqlCommand(
            @"INSERT INTO audit_logs (audit_id, occurred_at, actor_user_id, actor_name, action, target, old_value, new_value, details,
                                      client_checksum, previous_checksum, source, workstation_id, actor_verified)
              VALUES (@id, @t, @u, @n, @a, @tg, @o, @nv, @d, @c, @p, 'client', @w, @v)
              ON CONFLICT (audit_id) DO NOTHING", conn))
        {
            cmd.Parameters.AddWithValue("v", verified);
            cmd.Parameters.AddWithValue("id", a.AuditId);
            cmd.Parameters.AddWithValue("t", a.Timestamp.ToUniversalTime());
            cmd.Parameters.AddWithValue("u", a.ActorUserId ?? "");
            cmd.Parameters.AddWithValue("n", a.ActorName ?? "");
            cmd.Parameters.AddWithValue("a", a.Action);
            cmd.Parameters.AddWithValue("tg", a.Target ?? "");
            cmd.Parameters.AddWithValue("o", a.OldValue ?? "");
            cmd.Parameters.AddWithValue("nv", a.NewValue ?? "");
            cmd.Parameters.AddWithValue("d", a.Details ?? "");
            cmd.Parameters.AddWithValue("c", a.Checksum ?? "");
            cmd.Parameters.AddWithValue("p", a.PreviousChecksum ?? "");
            cmd.Parameters.AddWithValue("w", workstationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("SELECT client_checksum FROM audit_logs WHERE audit_id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", a.AuditId);
            return (string)await cmd.ExecuteScalarAsync(ct) == (a.Checksum ?? "");
        }
    }
}

/// <summary>Server-side audit events (admin changes, sync receipts). Append-only table.</summary>
public static class ServerAudit
{
    public static async Task WriteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string actorId, string actorName, string action, string target,
        string oldValue, string newValue, string details, Guid? workstationId, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO audit_logs (audit_id, occurred_at, actor_user_id, actor_name, action, target, old_value, new_value, details, source, workstation_id)
              VALUES (@id, now(), @u, @n, @a, @t, @o, @nv, @d, 'server', @w)", conn, tx);
        cmd.Parameters.AddWithValue("id", "srv-" + Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("u", actorId ?? "server");
        cmd.Parameters.AddWithValue("n", actorName ?? "BBTech API");
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("t", target ?? "");
        cmd.Parameters.AddWithValue("o", oldValue ?? "");
        cmd.Parameters.AddWithValue("nv", newValue ?? "");
        cmd.Parameters.AddWithValue("d", details ?? "");
        cmd.Parameters.AddWithValue("w", (object)workstationId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
