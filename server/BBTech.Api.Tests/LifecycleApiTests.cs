using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BBTech.Core.Audit;
using BBTech.Core.Models;
using BBTech.Core.Sync;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Api.Tests;

/// <summary>
/// The real BBTech lifecycle through the real API and PostgreSQL, driven by the desktop tool's own sync client:
/// Hardware (fail → repair → retest) → handover → Software → handover → QC → return to Hardware → fix → QC PASS →
/// READY for ITMS → final delivery check. The server enforces every rule with its own data.
/// </summary>
public sealed class LifecycleApiTests : IClassFixture<ApiFixture>, IDisposable
{
    private readonly ApiFixture _f;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-lc-api-" + Guid.NewGuid().ToString("N"));
    private DateTime _t = DateTime.UtcNow.AddHours(-3);

    public LifecycleApiTests(ApiFixture f) { _f = f; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly JsonSerializerOptions Json = Opts();
    private static JsonSerializerOptions Opts() { var o = new JsonSerializerOptions(); ApiJson.Configure(o); return o; }

    private SyncQueueItem Rec(string dev, string op, string dept, string test, string result, string notes = "", string details = null)
    {
        _t = _t.AddSeconds(10);
        var i = SyncQueueItem.FromTestResult(dev, op, op, dept, test, test, result, notes, "1.5.0");
        i.Timestamp = _t;
        i.Details = details; i.DeviceSerial = "SN-" + dev; i.DeviceManufacturer = "Lenovo"; i.DeviceModel = "ThinkPad T14 Gen 2";
        i.Checksum = i.ComputeChecksum();
        return i;
    }

    /// <summary>Uploads records (in order) and returns the rejection reasons by test id.</summary>
    private async Task<Dictionary<string, string>> Upload(params SyncQueueItem[] items)
    {
        var queue = new UsbSyncQueue(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dat"));
        foreach (var i in items) queue.Enqueue(i);
        var outcome = await new SyncCoordinator(_f.DesktopClient(), queue, new AuditStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".a"))).RunOnceAsync(null);
        Assert.True(outcome.Success, outcome.Message);
        return queue.GetAllItems().ToDictionary(i => i.TestId, i => i.LastError);
    }

    private async Task<string> Status(string dev) => (await _f.DesktopClient().GetDeviceStatusAsync(dev)).LifecycleStatus;

    private SyncQueueItem[] All(string dev, string dept, string op, Func<string, string> result = null) =>
        BBTech.Core.Testing.QcRequirements.Default().Tests
            .Where(t => t.Required && t.Department == dept)
            .Select(t => Rec(dev, op, dept, t.TestId, result?.Invoke(t.TestId) ?? "PASS")).ToArray();

    [Fact]
    public async Task Acceptance_LifecycleWithRework_ThroughTheServer()
    {
        string dev = "DEV-LC-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        // Hardware finds a failure; the stage cannot be handed over and Software cannot start.
        Assert.Empty(await Upload(All(dev, "Hardware", "kamal", t => t == "Battery" ? "FAIL" : "PASS")));
        Assert.Equal(LifecycleStatus.HardwareInProgress, await Status(dev));
        var refused = await Upload(Rec(dev, "kamal", "Hardware", LifecycleEvents.HardwareHandover, "PASS"),
                                   Rec(dev, "shadir", "Software", "SoftwareDrivers", "PASS"));
        Assert.Contains("Battery", refused[LifecycleEvents.HardwareHandover]);
        Assert.Contains("Hardware has not handed", refused["SoftwareDrivers"]);

        // Fixed, retested, handed over.
        Assert.Empty(await Upload(Rec(dev, "kamal", "Hardware", "Repair:Battery", "PASS", "Battery replaced"),
                                  Rec(dev, "kamal", "Hardware", "Battery", "PASS", "Health 96%"),
                                  Rec(dev, "kamal", "Hardware", LifecycleEvents.HardwareHandover, "PASS")));
        Assert.Equal(LifecycleStatus.ReadyForSoftware, await Status(dev));

        // Software: Windows verified, checks, handover.
        Assert.Empty(await Upload(All(dev, "Software", "shadir").Append(Rec(dev, "shadir", "Software", LifecycleEvents.SoftwareHandover, "PASS")).ToArray()));
        Assert.Equal(LifecycleStatus.ReadyForQc, await Status(dev));

        // QC finds a problem: a QC PASS is refused, QC returns the laptop to Hardware.
        var qc = All(dev, "QC", "jimmy", t => t == "Speaker" ? "FAIL" : "PASS");
        var passTry = Rec(dev, "jimmy", "QC", LifecycleEvents.QcDecision, "PASS");
        var refusedPass = await Upload(qc.Append(passTry).ToArray());
        Assert.Contains("QC PASS not accepted", refusedPass[LifecycleEvents.QcDecision]);
        Assert.Empty(await Upload(Rec(dev, "jimmy", "QC", LifecycleEvents.QcDecision, "FAIL", "Speaker crackles"),
                                  Rec(dev, "jimmy", "QC", LifecycleEvents.QcReturn, "FAIL", "Speaker crackles", "Hardware")));
        Assert.Equal(LifecycleStatus.HardwareRework, await Status(dev));
        Assert.Equal(1, (await _f.DesktopClient().GetDeviceStatusAsync(dev)).QcReturnCount);
        Assert.Equal("NOT_READY", (await _f.DesktopClient().GetDeviceStatusAsync(dev)).Readiness);

        // A QC inspector without approval rights cannot send laptops back.
        Assert.Contains("qc.approve", (await Upload(Rec(dev, "helper", "QC", LifecycleEvents.QcReturn, "FAIL", "x", "Software")))[LifecycleEvents.QcReturn]);

        // Hardware fixes, hands over; straight back to QC.
        Assert.Empty(await Upload(Rec(dev, "kamal", "Hardware", "Speaker", "FAIL", "Confirmed"),
                                  Rec(dev, "kamal", "Hardware", "Repair:Speaker", "PASS", "Speaker replaced"),
                                  Rec(dev, "kamal", "Hardware", "Speaker", "PASS"),
                                  Rec(dev, "kamal", "Hardware", LifecycleEvents.HardwareHandover, "PASS")));
        Assert.Equal(LifecycleStatus.ReadyForQc, await Status(dev));

        // QC rechecks and passes: READY for E-Commerce / ITMS.
        Assert.Empty(await Upload(Rec(dev, "jimmy", "QC", "Speaker", "PASS"), Rec(dev, "jimmy", "QC", LifecycleEvents.QcDecision, "PASS")));
        var ready = await _f.DesktopClient().GetDeviceStatusAsync(dev);
        Assert.Equal(LifecycleStatus.ReadyForEcommerce, ready.LifecycleStatus);
        Assert.Equal("READY", ready.Readiness);
        Assert.Contains(await _f.Client(_f.WorkstationKey).GetFromJsonAsync<List<DeviceStatusDto>>("/v1/devices/ready", Json), d => d.DeviceId == dev);

        // Final delivery check with the ITMS order reference: HOLD, then READY FOR DELIVERY.
        Assert.Contains("needs a reason", (await Upload(Rec(dev, "jimmy", "QC", LifecycleEvents.DeliveryDecision, "FAIL", "", "ORD-123456")))[LifecycleEvents.DeliveryDecision]);
        Assert.Empty(await Upload(Rec(dev, "jimmy", "QC", "DeliveryCharger", "FAIL", "Charger missing"),
                                  Rec(dev, "jimmy", "QC", LifecycleEvents.DeliveryDecision, "FAIL", "Charger missing", "ORD-123456")));
        Assert.Equal(LifecycleStatus.DeliveryHold, await Status(dev));
        var delivery = BBTech.Core.Testing.QcRequirements.Default().Required(LifecycleEvents.DeliveryGroup).Select(t => Rec(dev, "jimmy", "QC", t, "PASS")).ToList();
        // Before READY FOR DELIVERY the hardware is checked again (a part swapped since QC PASS would cancel it).
        var recheck = new TechnicalInventory { CapturedAt = DateTime.UtcNow, ToolVersion = "1.6.0", DeviceId = dev, Stage = "FINAL_CHECK" };
        recheck.Add("storage", "Micron MTFDKBA512TFK", "WMI", DataConfidence.Detected, "SSD-1").SetId("serial_number", "SSD-1");
        Assert.Contains("hardware must be checked again", (await Upload(Rec(dev, "jimmy", "QC", LifecycleEvents.DeliveryDecision, "PASS", "", "ORD-123456")))[LifecycleEvents.DeliveryDecision]);
        delivery.Add(Rec(dev, "jimmy", "QC", LifecycleEvents.DeviceSnapshot, "PASS", recheck.HardwareSignature(), recheck.ToJson()));
        delivery.Add(Rec(dev, "jimmy", "QC", LifecycleEvents.DeliveryDecision, "PASS", "", "ORD-123456"));
        Assert.Empty(await Upload(delivery.ToArray()));
        Assert.Equal(LifecycleStatus.ReadyForDelivery, await Status(dev));

        // Traceability in the database: every status change with from/to/who/why; nothing overwritten.
        long transitions = await _f.ScalarAsync<long>("SELECT count(*) FROM lifecycle_events WHERE device_id = @d", ("d", dev));
        Assert.True(transitions >= 9, "transitions: " + transitions);
        Assert.Equal("Speaker crackles", await _f.ScalarAsync<string>("SELECT reason FROM lifecycle_events WHERE device_id = @d AND to_status = 'HARDWARE_REWORK'", ("d", dev)));
        Assert.Equal(2L, await _f.ScalarAsync<long>("SELECT count(*) FROM test_results WHERE device_id = @d AND test_id = 'Battery' AND department_code = 'Hardware'", ("d", dev)));

        // The timeline endpoint tells the story in order.
        var timeline = await _f.Client(_f.WorkstationKey).GetFromJsonAsync<List<TimelineEntry>>("/v1/devices/" + dev + "/timeline", Json);
        Assert.Contains(timeline, e => e.Kind == "RETURN" && e.Title.Contains("Hardware"));
        Assert.Contains(timeline, e => e.Kind == "DELIVERY" && e.Result == "READY FOR DELIVERY" && e.Title.Contains("ORD-123456"));
        Assert.Equal(2, timeline.Count(e => e.Kind == "REPAIR"));

        // Management report.
        var report = await _f.Client(_f.AdminKey, "touhidul").GetAsync("/v1/admin/reports/lifecycle");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        string body = await report.Content.ReadAsStringAsync();
        Assert.Contains("ReadyForDelivery", body);
        Assert.Contains("Speaker crackles", body);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.Client(_f.WorkstationKey, "touhidul").GetAsync("/v1/admin/reports/lifecycle")).StatusCode);
    }

    [Fact]
    public async Task Snapshot_IsStored_Queryable_AndHistoryRestoresToTheClient()
    {
        string dev = "DEV-SNAP-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var inv = new TechnicalInventory { CapturedAt = DateTime.UtcNow, ToolVersion = "1.5.0", DeviceId = dev };
        inv.Add("storage", "Micron MTFDKBA512TFK", "Windows Native NVMe Protocol IOCTL", DataConfidence.Measured, "SSD-SERIAL-1")
           .SetId("serial_number", "SSD-SERIAL-1").Set("percentage_used", 4).Set("media_and_data_integrity_errors", 0).Set("unsafe_shutdowns", 31);
        Assert.Empty(await Upload(Rec(dev, "kamal", "Hardware", LifecycleEvents.DeviceSnapshot, "PASS", inv.HardwareSignature(), inv.ToJson()),
                                  Rec(dev, "kamal", "Hardware", "Battery", "FAIL", "Health 54%")));

        // Queryable JSON on the server (e.g. ITMS/admin: "which laptops have this SSD?").
        Assert.Equal("4", await _f.ScalarAsync<string>(
            "SELECT v->>'Value' FROM device_snapshots s, jsonb_array_elements(s.inventory->'Items') i, jsonb_array_elements(i->'Values') v WHERE s.device_id = @d AND v->>'Key' = 'percentage_used'", ("d", dev)));
        var snapshot = await _f.Client(_f.WorkstationKey).GetStringAsync("/v1/devices/" + dev + "/snapshot");
        Assert.Contains("SSD-SERIAL-1", snapshot);

        // The same snapshot record re-sent with altered evidence is a conflict, not a duplicate.
        var original = Rec(dev, "kamal", "Hardware", LifecycleEvents.DeviceSnapshot, "PASS", inv.HardwareSignature(), inv.ToJson());
        Assert.Empty(await Upload(original));
        var forged = Rec(dev, "kamal", "Hardware", LifecycleEvents.DeviceSnapshot, "PASS", inv.HardwareSignature(), inv.ToJson().Replace("SSD-SERIAL-1", "SSD-FAKE-999"));
        forged.RecordId = original.RecordId; forged.Timestamp = original.Timestamp; forged.Checksum = forged.ComputeChecksum();
        Assert.Contains("already stored", (await Upload(forged))[LifecycleEvents.DeviceSnapshot]);

        // A garbled snapshot is refused (evidence must be readable).
        Assert.Contains("not readable", (await Upload(Rec(dev, "kamal", "Hardware", LifecycleEvents.DeviceSnapshot, "PASS", "sig", "{broken")))[LifecycleEvents.DeviceSnapshot]);

        // The desktop client reads the full history back (used to restore a laptop wiped by a Windows install).
        var history = await _f.DesktopClient().GetDeviceHistoryAsync(dev);
        Assert.Equal(3, history.Count);
        var records = history.Select(h => h.ToRecord()).ToList();
        Assert.Contains(records, r => r.TestId == "Battery" && r.Status == "FAIL" && r.Comment == "Health 54%" && r.OperatorId == "kamal");
        Assert.Contains(records, r => r.TestId == LifecycleEvents.DeviceSnapshot && TechnicalInventory.FromJson(r.Details) != null);

        // Evidence can never be deleted, even by an admin.
        string snapId = await _f.ScalarAsync<string>("SELECT record_id FROM test_results WHERE device_id = @d AND test_id = 'DEVICE_SNAPSHOT'", ("d", dev));
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Client(_f.AdminKey, "touhidul").DeleteAsync("/v1/admin/results/" + snapId)).StatusCode);
    }

    /// <summary>Real-hardware leg (runs only with BBTECH_REAL_INVENTORY = a snapshot captured by the tool on a real laptop).</summary>
    [Fact]
    public async Task RealLaptopSnapshot_IsStoredOnTheServer_WithItsRawDriveValues()
    {
        string path = Environment.GetEnvironmentVariable("BBTECH_REAL_INVENTORY");
        if (string.IsNullOrEmpty(path)) return;
        var inv = TechnicalInventory.FromJson(File.ReadAllText(path));
        Assert.NotNull(inv);
        string dev = "DEV-REAL-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        inv.DeviceId = dev;
        Assert.Empty(await Upload(Rec(dev, "kamal", "Hardware", LifecycleEvents.DeviceSnapshot, "PASS", inv.HardwareSignature(), inv.ToJson())));

        var report = new System.Text.StringBuilder();
        foreach (var key in new[] { "raw.critical_warning", "raw.available_spare_percent", "raw.available_spare_threshold_percent", "raw.percentage_used",
                                    "raw.data_units_read", "raw.data_units_written", "raw.power_cycles", "raw.power_on_hours", "raw.unsafe_shutdowns",
                                    "raw.media_and_data_integrity_errors", "raw.error_information_log_entries", "raw.composite_temperature_k", "health_status" })
        {
            string onServer = await _f.ScalarAsync<string>(
                "SELECT v->>'Value' FROM device_snapshots s, jsonb_array_elements(s.inventory->'Items') i, jsonb_array_elements(i->'Values') v " +
                "WHERE s.device_id = @d AND i->>'Category' = 'storage' AND v->>'Key' = @k", ("d", dev), ("k", key));
            string local = inv.Of("storage").First().Get(key);
            Assert.Equal(local, onServer);
            report.AppendLine(key + " = " + onServer);
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path), "server_real_snapshot.txt"), report.ToString());
    }

    [Fact]
    public async Task ClientAuditEntries_AreKept_ButOnlyConfirmedActorsCountAsVerified()
    {
        string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
        AuditRecordDto A(string id, string actor, string action, DateTime at) => new AuditRecordDto
            { AuditId = id + "-" + tag, ActorUserId = actor, ActorName = actor, Action = action, Timestamp = at, Checksum = "c-" + id, Target = "DEV-" + tag };
        var batch = new SyncUploadBatchDto
        {
            BatchId = "B-" + tag, DeviceId = "DEV-" + tag, SentAt = DateTime.UtcNow,
            AuditRecords =
            {
                A("real", "kamal", "HARDWARE_TEST", DateTime.UtcNow.AddMinutes(-5)),
                A("forged", "the-ceo", "QC_DECISION_CREATED", DateTime.UtcNow.AddMinutes(-5)),        // not a user
                A("admin", "kamal", "ROLE_CHANGED", DateTime.UtcNow.AddMinutes(-5)),                  // server-only action
                A("future", "kamal", "HARDWARE_TEST", DateTime.UtcNow.AddDays(30)),                    // impossible time
                A("before", "kamal", "HARDWARE_TEST", new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)),   // before the user existed
                A("case", "kamal", "hardware_test", DateTime.UtcNow.AddMinutes(-5)),                  // not a known client action
                A("named", "kamal", "HARDWARE_TEST", DateTime.UtcNow.AddMinutes(-4)),
            },
        };
        var resp = await _f.Client(_f.WorkstationKey).PostAsJsonAsync("/v1/sync/upload", batch, Json);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

        async Task<bool?> Verified(string id) => await _f.ScalarAsync<bool?>("SELECT actor_verified FROM audit_logs WHERE audit_id = @id", ("id", id + "-" + tag));
        Assert.True(await Verified("real"));
        Assert.False(await Verified("forged"));       // kept as evidence, but not proof of who did it
        Assert.False(await Verified("admin"));
        Assert.False(await Verified("future"));
        Assert.False(await Verified("before"));
        Assert.False(await Verified("case"));
        Assert.Equal("Kamal", await _f.ScalarAsync<string>("SELECT actor_name FROM audit_logs WHERE audit_id = @id", ("id", "named-" + tag)));   // not the client's text

        var listed = await _f.Client(_f.AdminKey, "touhidul").GetStringAsync("/v1/admin/audit?limit=500");
        Assert.Contains("forged-" + tag, listed);
    }
}
