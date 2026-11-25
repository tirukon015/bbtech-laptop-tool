using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BBTech.Core.Audit;
using BBTech.Core.Settings;
using BBTech.Core.Sync;
using Npgsql;
using Xunit;

namespace BBTech.Api.Tests;

public sealed class ApiTests : IClassFixture<ApiFixture>, IDisposable
{
    private readonly ApiFixture _f;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-api-tests-" + Guid.NewGuid().ToString("N"));

    public ApiTests(ApiFixture f) { _f = f; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly JsonSerializerOptions Json = Opts();
    private static JsonSerializerOptions Opts() { var o = new JsonSerializerOptions(); ApiJson.Configure(o); return o; }

    private UsbSyncQueue NewQueue() => new UsbSyncQueue(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dat"));
    private AuditStore NewAudit() => new AuditStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-audit.dat"));
    private static string NewDevice() => "DEV-" + Guid.NewGuid().ToString("N").Substring(0, 10);

    private static SyncQueueItem Item(string device, string op, string dept, string test, string result, DateTime? at = null)
    {
        var i = SyncQueueItem.FromTestResult(device, op, op, dept, test, test, result, "", "1.4.0");
        if (at.HasValue) { i.Timestamp = at.Value; i.Checksum = i.ComputeChecksum(); }
        i.DeviceSerial = "SN-" + device; i.DeviceManufacturer = "Lenovo"; i.DeviceModel = "ThinkPad T14";
        return i;
    }

    private async Task<SyncRunOutcome> Sync(UsbSyncQueue queue, AuditStore audit = null) =>
        await new SyncCoordinator(_f.DesktopClient(), queue, audit ?? NewAudit()).RunOnceAsync(null);

    // ------------------------------------------------------------------ authentication

    [Fact]
    public async Task Health_IsPublic_ButV1NeedsAValidWorkstationKey()
    {
        Assert.Equal(HttpStatusCode.OK, (await _f.Client().GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client().GetAsync("/v1/config/master")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client("bbt_wrong").GetAsync("/v1/config/master")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.Client(_f.WorkstationKey).GetAsync("/v1/config/master")).StatusCode);
    }

    [Fact]
    public async Task DisabledWorkstation_IsRefused()
    {
        var (id, key) = await _f.Db.CreateWorkstationAsync("lost USB", false);
        await using (var conn = await _f.Db.OpenAsync())
        await using (var cmd = new NpgsqlCommand("UPDATE workstations SET active = false WHERE workstation_id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client(key).GetAsync("/v1/config/master")).StatusCode);
    }

    // ------------------------------------------------------------------ master configuration

    [Fact]
    public async Task MasterConfig_IsReadByTheDesktopClient_AndValid()
    {
        var cfg = await _f.DesktopClient().FetchMasterConfigAsync();
        Assert.NotNull(cfg);
        Assert.Null(cfg.Validate());
        Assert.True(cfg.ConfigVersion >= 1);
        Assert.Contains(cfg.Operators, o => o.OperatorId == "touhidul" && o.IsAdmin && o.Active);
        var kamal = cfg.Operators.Single(o => o.OperatorId == "kamal");
        Assert.True(kamal.CanAccessHardware);
        Assert.False(kamal.CanAccessQc);
        Assert.True(cfg.Requirements.IsValid);
        Assert.Contains("SoftwareWindowsActivation", cfg.Requirements.Required("Software"));
        var office = Assert.Single(cfg.Packages, p => p.PackageId == "office-default");
        Assert.Null(office.Validate());
        Assert.Equal("Microsoft Corporation", office.Publisher);

        // The client's encrypted cache accepts it.
        Assert.Null(MasterConfigCache.TrySave(Path.Combine(_dir, "master.dat"), cfg));
    }

    // ------------------------------------------------------------------ sync (desktop client <-> server)

    [Fact]
    public async Task Sync_EndToEnd_UploadsVerifiesAndClearsTheUsbQueue()
    {
        string dev = NewDevice();
        var queue = NewQueue();
        var audit = NewAudit();
        queue.Enqueue(Item(dev, "kamal", "Hardware", "Battery", "PASS"));
        queue.Enqueue(Item(dev, "kamal", "Hardware", "Keyboard", "FAIL"));
        audit.Log(new OperatorRecord { OperatorId = "kamal", Name = "Kamal", Active = true }, AuditActions.TestSubmitted, "Battery", null, "PASS");

        var outcome = await Sync(queue, audit);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(2, outcome.Verified);
        Assert.Empty(queue.GetAllItems());                                  // verified -> removed from USB
        // The uploaded audit event is marked synced; the sync's own SYNC_COMPLETED / USB_CLEANUP events go next time.
        Assert.DoesNotContain(audit.GetPendingSyncRecords(), a => a.Action == AuditActions.TestSubmitted);
        Assert.All(audit.GetPendingSyncRecords(), a => Assert.Contains(a.Action, new[] { AuditActions.SyncCompleted, AuditActions.UsbCleanup }));
        Assert.Equal(1L, await _f.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE source = 'client' AND action = 'TEST_RESULT_RECORDED' AND target = 'Battery'"));
        Assert.Equal(2L, await _f.ScalarAsync<long>("SELECT count(*) FROM test_results WHERE device_id = @d", ("d", dev)));
        Assert.Equal("Kamal", await _f.ScalarAsync<string>("SELECT operator_name FROM test_results WHERE device_id = @d AND test_id = 'Battery'", ("d", dev)));
        Assert.Equal("SN-" + dev, await _f.ScalarAsync<string>("SELECT serial_number FROM devices WHERE device_id = @d", ("d", dev)));
    }

    [Fact]
    public async Task Sync_IsIdempotent_RetriedRecordIsNotDuplicated()
    {
        string dev = NewDevice();
        var item = Item(dev, "kamal", "Hardware", "Ram", "PASS");
        var q1 = NewQueue(); q1.Enqueue(item);
        var q2 = NewQueue(); q2.Enqueue(Item(dev, "kamal", "Hardware", "Ram", "PASS")); // different record id
        var q3 = NewQueue();
        var copy = Item(dev, "kamal", "Hardware", "Ram", "PASS"); copy.RecordId = item.RecordId; copy.Timestamp = item.Timestamp; copy.Checksum = copy.ComputeChecksum();
        q3.Enqueue(copy);                                                   // same record re-sent (e.g. crash before purge)

        await Sync(q1);
        var again = await Sync(q3);

        Assert.Equal(1, again.Verified);                                    // the server confirms it already has it
        Assert.Empty(q3.GetAllItems());
        Assert.Equal(1L, await _f.ScalarAsync<long>("SELECT count(*) FROM test_results WHERE record_id = @r", ("r", item.RecordId)));
        await Sync(q2);
        Assert.Equal(2L, await _f.ScalarAsync<long>("SELECT count(*) FROM test_results WHERE device_id = @d", ("d", dev))); // re-test = new row
    }

    [Fact]
    public async Task Sync_ConflictingRecordWithSameId_IsRejected_AndKeptOnUsb()
    {
        string dev = NewDevice();
        var original = Item(dev, "kamal", "Hardware", "Display", "FAIL");
        var q1 = NewQueue(); q1.Enqueue(original);
        await Sync(q1);

        var forged = Item(dev, "kamal", "Hardware", "Display", "PASS");      // same id, FAIL -> PASS
        forged.RecordId = original.RecordId; forged.Checksum = forged.ComputeChecksum();
        var q2 = NewQueue(); q2.Enqueue(forged);
        var outcome = await Sync(q2);

        Assert.Equal(1, outcome.Rejected);
        Assert.Single(q2.GetAllItems());
        Assert.Equal("FAIL", await _f.ScalarAsync<string>("SELECT status FROM test_results WHERE record_id = @r", ("r", original.RecordId)));
    }

    [Theory]
    [InlineData("kamal", "QC", "Display", "PASS", "not allowed")]          // Hardware technician cannot record QC
    [InlineData("kamal", "Software", "SoftwareDisplay", "PASS", "not allowed")]
    [InlineData("nobody", "Hardware", "Battery", "PASS", "unknown operator")]
    [InlineData("helper", "QC", "QC_FINAL", "PASS", "qc.approve")]          // inspector without approval right
    [InlineData("kamal", "Hardware", "Battery", "GREAT", "invalid result")]
    public async Task Sync_ServerRejectsUnauthorisedOrInvalidRecords(string op, string dept, string test, string result, string reason)
    {
        var queue = NewQueue();
        queue.Enqueue(Item(NewDevice(), op, dept, test, result));
        var outcome = await Sync(queue);

        Assert.Equal(1, outcome.Rejected);
        var kept = Assert.Single(queue.GetAllItems());
        Assert.Contains(reason, kept.LastError);
    }

    [Fact]
    public async Task Sync_ServerUnavailable_KeepsEverything()
    {
        var queue = NewQueue();
        queue.Enqueue(Item(NewDevice(), "kamal", "Hardware", "Battery", "PASS"));
        var offline = new BBTechApiClient("http://127.0.0.1:1", _f.WorkstationKey); // nothing listens there
        var outcome = await new SyncCoordinator(offline, queue, NewAudit()).RunOnceAsync(null);
        Assert.False(outcome.Success);
        Assert.Single(queue.GetPendingItems());
    }

    // ------------------------------------------------------------------ QC authority + ITMS readiness

    private async Task UploadAllRequired(string dev, DateTime at, string failTest = null)
    {
        // The real lifecycle order: Hardware tests → Hardware handover → Software → Software handover → QC checklist.
        // A department with a failure cannot hand over, so the later stages are not uploaded.
        var cfg = await _f.DesktopClient().FetchMasterConfigAsync();
        var queue = NewQueue();
        int step = 0;
        foreach (var (dept, op, handover) in new[] { ("Hardware", "kamal", "LC_HARDWARE_HANDOVER"), ("Software", "shadir", "LC_SOFTWARE_HANDOVER"), ("QC", "jimmy", (string)null) })
        {
            bool failed = false;
            foreach (var t in cfg.Requirements.Tests.Where(t => t.Required && t.Department == dept))
            {
                failed |= t.TestId == failTest;
                queue.Enqueue(Item(dev, op, dept, t.TestId, t.TestId == failTest ? "FAIL" : "PASS", at.AddSeconds(++step)));
            }
            if (failed) break;
            if (handover != null) queue.Enqueue(Item(dev, op, dept, handover, "PASS", at.AddSeconds(++step)));
        }
        var outcome = await Sync(queue);
        Assert.True(outcome.Success, outcome.Message);
        Assert.True(outcome.Rejected == 0, string.Join(" | ", queue.GetAllItems().Select(i => i.TestId + ": " + i.LastError)));
    }

    [Fact]
    public async Task QcPass_IsRejectedByTheServer_WhenItsOwnDataIsIncomplete()
    {
        string dev = NewDevice();
        var queue = NewQueue();
        queue.Enqueue(Item(dev, "kamal", "Hardware", "Battery", "PASS"));
        queue.Enqueue(Item(dev, "jimmy", "QC", "QC_FINAL", "PASS", DateTime.UtcNow.AddSeconds(1)));
        var outcome = await Sync(queue);

        Assert.Equal(1, outcome.Rejected);
        Assert.Contains("QC PASS not accepted", Assert.Single(queue.GetAllItems()).LastError);
        var status = await _f.DesktopClient().GetDeviceStatusAsync(dev);
        Assert.Equal("IN_PROGRESS", status.Readiness);
    }

    [Fact]
    public async Task QcPass_WithAllRequiredResults_MakesTheLaptopReady_AndALaterFailSupersedesIt()
    {
        string dev = NewDevice();
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        await UploadAllRequired(dev, t0);

        var pass = NewQueue();
        pass.Enqueue(Item(dev, "jimmy", "QC", "QC_FINAL", "PASS", t0.AddMinutes(5)));
        Assert.Equal(1, (await Sync(pass)).Verified);
        var ready = await _f.DesktopClient().GetDeviceStatusAsync(dev);
        Assert.Equal("READY", ready.Readiness);
        Assert.Equal("Jimmy", ready.QcDecidedBy);

        var list = await _f.Client(_f.WorkstationKey).GetFromJsonAsync<List<DeviceStatusDto>>("/v1/devices/ready", Json);
        Assert.Contains(list, d => d.DeviceId == dev);

        var fail = NewQueue();
        fail.Enqueue(Item(dev, "jimmy", "QC", "QC_FINAL", "FAIL", t0.AddMinutes(10)));
        await Sync(fail);
        Assert.Equal("NOT_READY", (await _f.DesktopClient().GetDeviceStatusAsync(dev)).Readiness);
        Assert.Equal(2L, await _f.ScalarAsync<long>("SELECT count(*) FROM qc_decisions WHERE device_id = @d", ("d", dev))); // history kept
    }

    [Fact]
    public async Task QcPass_IsRejected_WhenARequiredTestFailed()
    {
        string dev = NewDevice();
        var t0 = DateTime.UtcNow.AddMinutes(-20);
        await UploadAllRequired(dev, t0, failTest: "SoftwareWindowsActivation");
        var pass = NewQueue();
        pass.Enqueue(Item(dev, "jimmy", "QC", "QC_FINAL", "PASS", t0.AddMinutes(1)));
        var outcome = await Sync(pass);
        Assert.Equal(1, outcome.Rejected);
        Assert.Contains("SoftwareWindowsActivation", Assert.Single(pass.GetAllItems()).LastError);
    }

    // ------------------------------------------------------------------ database integrity

    [Fact]
    public async Task Database_AuditAndQcDecisionsAreAppendOnly_ResultsImmutable()
    {
        string dev = NewDevice();
        var queue = NewQueue();
        queue.Enqueue(Item(dev, "kamal", "Hardware", "Battery", "FAIL"));
        await Sync(queue);

        await using var conn = await _f.Db.OpenAsync();
        async Task<PostgresException> Fails(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("d", dev);
            return await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }
        Assert.Contains("append-only", (await Fails("UPDATE audit_logs SET action = 'X'")).MessageText);
        Assert.Contains("append-only", (await Fails("DELETE FROM audit_logs")).MessageText);
        Assert.Contains("immutable", (await Fails("UPDATE test_results SET status = 'PASS' WHERE device_id = @d")).MessageText);
        Assert.Contains("never deleted", (await Fails("DELETE FROM test_results WHERE device_id = @d")).MessageText);
        // An installer must always be verifiable (checksum or publisher).
        Assert.Equal("23514", (await Fails("INSERT INTO installers (package_id, source_mode, file_name) VALUES ('office-default', 0, 'x.exe')")).SqlState);
    }

    [Fact]
    public async Task RestrictedWorkstation_AcceptsOnlyItsListedOperators()
    {
        var (wsId, key) = await _f.Db.CreateWorkstationAsync("Kamal's USB", false);
        var put = await _f.Client(_f.AdminKey, "touhidul").PutAsJsonAsync("/v1/admin/workstations/" + wsId + "/operators", new List<string> { "kamal" }, Json);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        string dev = NewDevice();
        var queue = NewQueue();
        queue.Enqueue(Item(dev, "kamal", "Hardware", "Battery", "PASS"));
        queue.Enqueue(Item(dev, "jimmy", "QC", "Display", "PASS"));   // valid operator, but not on this USB
        var client = new BBTechApiClient("http://localhost", key, _f.Factory.CreateClient());
        var outcome = await new SyncCoordinator(client, queue, NewAudit()).RunOnceAsync(null);

        Assert.Equal(1, outcome.Verified);
        Assert.Equal(1, outcome.Rejected);
        Assert.Contains("not allowed on this workstation", Assert.Single(queue.GetAllItems()).LastError);
    }

    [Fact]
    public async Task QcDecisions_FailClosed_OnAWorkstationWithoutRegisteredOperators()
    {
        var (_, key) = await _f.Db.CreateWorkstationAsync("Unregistered USB", false);
        string dev = NewDevice();
        var queue = NewQueue();
        queue.Enqueue(Item(dev, "kamal", "Hardware", "Battery", "PASS"));                     // ordinary result: accepted
        queue.Enqueue(Item(dev, "jimmy", "QC", "QC_FINAL", "FAIL"));                          // decision: refused here
        var ret = Item(dev, "jimmy", "QC", "LC_QC_RETURN", "FAIL");
        ret.Notes = "Speaker crackles"; ret.Details = "Hardware"; ret.Checksum = ret.ComputeChecksum();
        queue.Enqueue(ret);
        var client = new BBTechApiClient("http://localhost", key, _f.Factory.CreateClient());
        var outcome = await new SyncCoordinator(client, queue, NewAudit()).RunOnceAsync(null);

        Assert.Equal(1, outcome.Verified);
        Assert.Equal(2, outcome.Rejected);
        Assert.All(queue.GetAllItems(), i => Assert.Contains("registered for this QC approver", i.LastError));
    }

    // ------------------------------------------------------------------ administration

    [Fact]
    public async Task Admin_RequiresAdminKeyAndAnIdentifiedAdminUser()
    {
        var body = new UserUpsertRequest { UserId = "riyad", Name = "Riyad", Role = "E-Commerce Specialist", Departments = { "E-Commerce" }, Permissions = { "ecommerce" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.Client(_f.WorkstationKey, "touhidul").PostAsJsonAsync("/v1/admin/users", body, Json)).StatusCode); // technician USB key
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.Client(_f.AdminKey).PostAsJsonAsync("/v1/admin/users", body, Json)).StatusCode);               // no actor
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.Client(_f.AdminKey, "kamal").PostAsJsonAsync("/v1/admin/users", body, Json)).StatusCode);      // not an admin

        int before = (await _f.DesktopClient().FetchMasterConfigAsync()).ConfigVersion;
        var ok = await _f.Client(_f.AdminKey, "touhidul").PostAsJsonAsync("/v1/admin/users", body, Json);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var cfg = await _f.DesktopClient().FetchMasterConfigAsync();
        Assert.True(cfg.ConfigVersion > before);
        Assert.Contains(cfg.Operators, o => o.OperatorId == "riyad" && o.CanAccessEcommerce);
        Assert.True(await _f.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE action = 'USER_CREATED' AND target = 'user:riyad'") >= 1);
    }

    [Theory]
    [InlineData("", "", "https://x/OfficeSetup.exe", "neither")]                // no checksum, no publisher
    [InlineData("", "Microsoft Corporation", "http://x/OfficeSetup.exe", "HTTPS")]
    [InlineData("", "Microsoft Corporation", "", "args")]
    public async Task Admin_PackageValidation_BlocksUnsafeDefinitions(string sha, string publisher, string url, string kind)
    {
        var pkg = new SoftwarePackageDto
        {
            PackageId = "pkg-" + kind.ToLowerInvariant(), Name = "Test", Version = "1", SourceMode = 0, OnlineInstallerName = "OfficeSetup.exe",
            Sha256Checksum = sha, Publisher = publisher, OnlineDownloadUrl = url, Approved = true,
            InstallArguments = kind == "args" ? "/quiet & del C:\\" : "",
        };
        var resp = await _f.Client(_f.AdminKey, "touhidul").PostAsJsonAsync("/v1/admin/packages", pkg, Json);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_CanPublishAnApprovedPackage_ThatTheClientAccepts()
    {
        var pkg = new SoftwarePackageDto
        {
            PackageId = "office-ltsc-offline", Name = "Microsoft Office LTSC", Version = "2024", SourceMode = 1, OfflineFolderPath = "OfficeLTSC",
            OfflineExecutableName = "setup.exe", InstallArguments = "/configure configuration.xml", Publisher = "Microsoft Corporation",
            Sha256Checksum = new string('a', 64), Required = true, DetectRule = "office", Approved = true, TimeoutMinutes = 120,
        };
        Assert.Equal(HttpStatusCode.OK, (await _f.Client(_f.AdminKey, "touhidul").PostAsJsonAsync("/v1/admin/packages", pkg, Json)).StatusCode);
        var cfg = await _f.DesktopClient().FetchMasterConfigAsync();
        var p = Assert.Single(cfg.Packages, x => x.PackageId == "office-ltsc-offline");
        Assert.Null(p.Validate());
        Assert.Equal(BBTech.Core.Software.PackageSourceMode.Offline, p.SourceMode);
        Assert.Equal(120, p.TimeoutMinutes);
    }

    [Fact]
    public async Task Admin_TestDefinitionsWithoutRequiredTests_AreRefused()
    {
        var bad = new List<TestRequirementDto> { new() { Department = "Hardware", TestId = "Battery", Required = true } };
        var resp = await _f.Client(_f.AdminKey, "touhidul").PutAsJsonAsync("/v1/admin/test-definitions", bad, Json);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_CannotDeleteAFinalQcDecision_ButCanTombstoneOtherResults()
    {
        string dev = NewDevice();
        var t0 = DateTime.UtcNow.AddMinutes(-40);
        await UploadAllRequired(dev, t0);
        var q = NewQueue();
        var decision = Item(dev, "jimmy", "QC", "QC_FINAL", "PASS", t0.AddMinutes(1));
        q.Enqueue(decision);
        await Sync(q);
        var http = _f.Client(_f.AdminKey, "touhidul");

        Assert.Equal(HttpStatusCode.BadRequest, (await http.DeleteAsync("/v1/admin/results/" + decision.RecordId)).StatusCode);
        string battery = await _f.ScalarAsync<string>("SELECT record_id FROM test_results WHERE device_id = @d AND test_id = 'Battery'", ("d", dev));
        Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync("/v1/admin/results/" + battery)).StatusCode);
        Assert.NotNull(await _f.ScalarAsync<DateTime?>("SELECT deleted_at FROM test_results WHERE record_id = @r", ("r", battery)));
    }
}
