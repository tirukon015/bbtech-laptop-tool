using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BBTech.Core.Audit;
using BBTech.Core.Security;
using BBTech.Core.Settings;
using BBTech.Core.Software;
using BBTech.Core.Storage;
using BBTech.Core.Sync;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>
    /// Tests for the 2026-11-25 audit P0 fixes (see docs/AUDIT-2026-11-25.md): operator session
    /// tampering, fail-closed authorization, never-lose-data local files, USB sync safety and the QC gate.
    /// </summary>
    public sealed class ProductionHardeningTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-hardening-" + Guid.NewGuid().ToString("N"));
        private string SettingsPath => Path.Combine(_dir, "config", "settings.dat");

        public ProductionHardeningTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static OperatorRecord Op(string name, string[] depts, params string[] perms) => new OperatorRecord
        {
            OperatorId = name.ToLowerInvariant(),
            Name = name,
            Role = "Technician",
            Active = true,
            Departments = depts.ToList(),
            Permissions = perms.ToList(),
        };

        private static readonly OperatorRecord Kamal = Op("Kamal", new[] { "Hardware" }, AppPermissions.HardwareTest);
        private static readonly OperatorRecord Jimmy = Op("Jimmy", new[] { "QC" }, AppPermissions.QcInspect, AppPermissions.QcApprove);
        private static readonly OperatorRecord QcHelper = Op("Helper", new[] { "QC" }, AppPermissions.QcInspect);
        private static readonly OperatorRecord Admin = new OperatorRecord { OperatorId = "t", Name = "Touhidul", Role = "Super Admin", Active = true, Permissions = new List<string> { AppPermissions.All } };

        // ------------------------------------------------------------------ operator session

        [Fact]
        public void Session_ForgedWorkstationFile_IsIgnored()
        {
            var s = SettingsStore.Load(SettingsPath);
            var admin = s.AddOperator("Touhidul", "Super Admin");
            s.AddOperator("Kamal", "Hardware");
            s.ResetSession();

            // Attacker writes a session file naming the admin, valid for a day, without a valid signature.
            var forged = new OperatorSession { OperatorId = admin.OperatorId, OperatorName = admin.Name, SelectedAt = DateTime.Now, SessionExpiresAt = DateTime.Now.AddDays(1) };
            File.WriteAllBytes(SettingsPath + ".session.json", Json.ToBytes(forged));

            Assert.Null(SettingsStore.Load(SettingsPath).CurrentOperator);
        }

        [Fact]
        public void Session_SignedButEditedToLastLonger_IsRejected()
        {
            var op = new OperatorRecord { OperatorId = "x", Name = "Kamal", Active = true };
            var session = OperatorSession.Create(op, new DateTime(2026, 11, 25, 10, 0, 0));
            Assert.True(session.HasValidSignature());

            session.SessionExpiresAt = session.SessionExpiresAt.AddHours(8);
            Assert.False(session.HasValidSignature());
            Assert.True(session.IsExpired(new DateTime(2026, 11, 25, 10, 30, 0))); // longer than the 1-hour maximum
        }

        [Fact]
        public void Session_DurationIsCappedAtOneHour()
        {
            var op = new OperatorRecord { OperatorId = "x", Name = "Kamal", Active = true };
            var now = new DateTime(2026, 11, 25, 10, 0, 0);
            var session = OperatorSession.Create(op, now, TimeSpan.FromDays(3));
            Assert.Equal(now.AddHours(1), session.SessionExpiresAt);
        }

        [Fact]
        public void Session_ClockMovedBack_Expires()
        {
            var op = new OperatorRecord { OperatorId = "x", Name = "Kamal", Active = true };
            var session = OperatorSession.Create(op, new DateTime(2026, 11, 25, 10, 0, 0));
            Assert.True(session.IsExpired(new DateTime(2026, 11, 25, 9, 0, 0)));
        }

        [Fact]
        public void Session_LegacyCurrentOperatorIdAlone_DoesNotSignIn()
        {
            var s = SettingsStore.Load(SettingsPath);
            var k = s.AddOperator("Kamal", "Hardware");
            s.ResetSession();
            s.Settings.CurrentOperatorId = k.OperatorId; // old-style selection with no session/expiry
            s.Save();
            Assert.Null(SettingsStore.Load(SettingsPath).CurrentOperator);
        }

        [Fact]
        public void BbtechApiUrl_MustBeHttps()
        {
            var s = SettingsStore.Load(SettingsPath);
            Assert.Throws<ArgumentException>(() => s.SetBbtechApiConfig("http://example.com", "k"));
            s.SetBbtechApiConfig("https://api.example.com/", "k");
            Assert.Equal("https://api.example.com", SettingsStore.Load(SettingsPath).EffectiveBbtechApiUrl);
            Assert.True(BBTechApiClient.IsUsableUrl("http://localhost:5000"));
            Assert.False(new BBTechApiClient(null).IsConfigured);
        }

        // ------------------------------------------------------------------ authorization (fail closed)

        [Fact]
        public void Authorization_NoOperator_IsAlwaysDenied()
        {
            Assert.NotNull(ResultAuthorization.CheckRecord(null, Departments.Hardware, "Battery"));
            Assert.NotNull(ResultAuthorization.CheckDelete(null, "Battery"));
            Assert.Throws<UnauthorizedAccessException>(() => ResultAuthorization.EnsureRecord(null, Departments.Qc, "Display"));
        }

        [Fact]
        public void Authorization_DepartmentBoundaries()
        {
            Assert.Null(ResultAuthorization.CheckRecord(Kamal, Departments.Hardware, "Battery"));
            Assert.NotNull(ResultAuthorization.CheckRecord(Kamal, Departments.Qc, "Display"));
            Assert.NotNull(ResultAuthorization.CheckRecord(Kamal, Departments.Software, "SoftwareDisplay"));
            Assert.NotNull(ResultAuthorization.CheckRecord(Kamal, "Unknown", "Battery"));
            var disabled = Op("Old", new[] { "Hardware" }, AppPermissions.HardwareTest); disabled.Active = false;
            Assert.NotNull(ResultAuthorization.CheckRecord(disabled, Departments.Hardware, "Battery"));
        }

        [Fact]
        public void Authorization_FinalQcDecision_NeedsQcApprove()
        {
            Assert.Null(ResultAuthorization.CheckRecord(Jimmy, Departments.Qc, QcGate.QcDecisionTestId));
            Assert.NotNull(ResultAuthorization.CheckRecord(QcHelper, Departments.Qc, QcGate.QcDecisionTestId));
            Assert.Null(ResultAuthorization.CheckRecord(QcHelper, Departments.Qc, "Display"));
            Assert.NotNull(ResultAuthorization.CheckRecord(Kamal, Departments.Qc, QcGate.QcDecisionTestId));
        }

        [Fact]
        public void Authorization_Delete_AdminOnly_AndNeverTheQcDecision()
        {
            Assert.NotNull(ResultAuthorization.CheckDelete(Kamal, "Battery"));
            Assert.Null(ResultAuthorization.CheckDelete(Admin, "Battery"));
            Assert.NotNull(ResultAuthorization.CheckDelete(Admin, QcGate.QcDecisionTestId));
        }

        [Fact]
        public void Repository_RefusesToDeleteQcDecision_ButDeletesOtherRecordsAsTombstones()
        {
            var r = QcRepository.Open(_dir, new FakeMachine(1), QcStoreTests.LaptopA);
            Assert.True(r.Ok, r.Message);
            var repo = r.Repository;
            var dev = repo.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs());
            var battery = repo.RecordTest(dev.DeviceId, "Battery", "Battery", "PASS", "Kamal", "Hardware", operatorId: "kamal");
            var decision = repo.RecordTest(dev.DeviceId, QcGate.QcDecisionTestId, "Final QC Decision", "PASS", "Jimmy", "QC");

            Assert.Equal("kamal", battery.OperatorId);
            Assert.False(repo.DeleteTestResult(decision.TestResultId));
            Assert.NotNull(repo.GetLatestTestResult(dev.DeviceId, QcGate.QcDecisionTestId, "QC"));
            Assert.True(repo.DeleteTestResult(battery.TestResultId));
            Assert.Null(repo.GetLatestTestResult(dev.DeviceId, "Battery", "Hardware"));
        }

        // ------------------------------------------------------------------ never lose local data

        [Fact]
        public void JsonListFile_DamagedFile_IsSetAside_AndBackupRestored()
        {
            string path = Path.Combine(_dir, "q.dat");
            JsonListFile.Write(path, new List<string> { "a" });
            JsonListFile.Write(path, new List<string> { "a", "b" }); // .bak now holds ["a"]
            File.WriteAllText(path, "{ this is not json");

            var items = JsonListFile.Read<string>(path, out string aside);

            Assert.NotNull(aside);
            Assert.True(File.Exists(aside));
            Assert.Equal("{ this is not json", File.ReadAllText(aside)); // damaged data kept for recovery
            Assert.Equal(new[] { "a" }, items);
        }

        [Fact]
        public void AuditStore_DamagedLog_IsNotOverwritten()
        {
            string path = Path.Combine(_dir, "audit", "audit_log.dat");
            var store = new AuditStore(path);
            store.Log(Kamal, AuditActions.TestSubmitted, "Battery");
            File.WriteAllText(path, "garbage");

            store.Log(Kamal, AuditActions.TestSubmitted, "Display");

            Assert.NotNull(store.LastQuarantinedFile);
            Assert.Equal("garbage", File.ReadAllText(store.LastQuarantinedFile));
            Assert.Contains(store.GetRecords(Admin), r => r.Action == AuditActions.AuditLogRecovered);
        }

        [Fact]
        public void AuditStore_EditedOrRemovedRecords_AreDetected()
        {
            string path = Path.Combine(_dir, "audit.dat");
            var store = new AuditStore(path);
            store.Log(Jimmy, AuditActions.QcDecision, "Device:1", null, "FAIL");
            store.Log(Jimmy, AuditActions.QcDecision, "Device:1", null, "PASS");
            store.Log(Kamal, AuditActions.TestSubmitted, "Device:1 Battery", null, "PASS");
            Assert.True(store.VerifyChain().Intact);

            // Edit: FAIL -> PASS, even with the old unkeyed SHA-256 recomputed, is detected.
            var list = Json.FromBytes<List<AuditRecord>>(File.ReadAllBytes(path));
            list[0].NewValue = "PASS";
            File.WriteAllBytes(path, Json.ToBytes(list));
            Assert.Contains(list[0].AuditId, store.VerifyChain().TamperedIds);

            // Removal of a record breaks the chain.
            list = Json.FromBytes<List<AuditRecord>>(File.ReadAllBytes(path));
            list[0].NewValue = "FAIL";
            list.RemoveAt(1);
            File.WriteAllBytes(path, Json.ToBytes(list));
            var report = store.VerifyChain();
            Assert.Empty(report.TamperedIds);
            Assert.NotEmpty(report.BrokenLinkIds);
        }

        [Fact]
        public void AuditStore_ViewingRequiresIdentifiedAuditor()
        {
            var store = new AuditStore(Path.Combine(_dir, "a.dat"));
            Assert.Throws<UnauthorizedAccessException>(() => store.GetRecords(null));
            Assert.Throws<UnauthorizedAccessException>(() => store.GetRecords(Kamal));
            Assert.NotNull(store.GetRecords(Admin));
        }

        // ------------------------------------------------------------------ USB sync safety

        internal sealed class FakeApi : IBBTechApiClient
        {
            public bool Configured = true, Online = true;
            public Func<SyncUploadBatch, SyncUploadResult> Answer;
            public Exception Throw;
            public int Uploads;
            public string BaseUrl => "https://api.test";
            public bool IsConfigured => Configured;
            public Task<bool> CheckConnectivityAsync() => Task.FromResult(Online);
            public Task<SyncUploadResult> UploadRecordsAsync(SyncUploadBatch batch)
            {
                Uploads++;
                if (Throw != null) throw Throw;
                return Task.FromResult(Answer(batch));
            }
            public Task<MasterConfigResponse> FetchMasterConfigAsync() => Task.FromResult<MasterConfigResponse>(null);
            public Task<DeviceStatusResponse> GetDeviceStatusAsync(string deviceId) => Task.FromResult<DeviceStatusResponse>(null);
            public Task<List<DeviceHistoryRecord>> GetDeviceHistoryAsync(string deviceId) => Task.FromResult<List<DeviceHistoryRecord>>(null);
        }

        private (UsbSyncQueue queue, AuditStore audit) NewQueue(int records)
        {
            var queue = new UsbSyncQueue(Path.Combine(_dir, "sync", "q.dat"));
            for (int i = 1; i <= records; i++)
                queue.Enqueue(SyncQueueItem.FromTestResult("DEV", "kamal", "Kamal", "Hardware", "T" + i, "Test " + i, "PASS", "", "1.2.0", "REC-" + i));
            return (queue, new AuditStore(Path.Combine(_dir, "audit", "a.dat")));
        }

        [Fact]
        public async Task Sync_NotConfiguredOrOffline_KeepsEverything()
        {
            var (queue, audit) = NewQueue(2);
            var api = new FakeApi { Configured = false };
            await new SyncCoordinator(api, queue, audit).RunOnceAsync(Kamal);
            api.Configured = true; api.Online = false;
            await new SyncCoordinator(api, queue, audit).RunOnceAsync(Kamal);

            Assert.Equal(0, api.Uploads);
            Assert.Equal(2, queue.GetAllItems().Count);
        }

        [Fact]
        public async Task Sync_ServerSuccessWithoutReceipt_DeletesNothing()
        {
            var (queue, audit) = NewQueue(2);
            var api = new FakeApi { Answer = b => new SyncUploadResult { Success = true, VerifiedRecordIds = b.Records.Select(r => r.RecordId).ToList() } };
            var outcome = await new SyncCoordinator(api, queue, audit).RunOnceAsync(Kamal);

            Assert.False(outcome.Success);
            Assert.Equal(2, queue.GetAllItems().Count);
            Assert.All(queue.GetAllItems(), i => Assert.Equal(SyncState.Failed, i.State));
        }

        [Fact]
        public async Task Sync_ServerError_OrException_KeepsEverything()
        {
            var (queue, audit) = NewQueue(2);
            await new SyncCoordinator(new FakeApi { Answer = b => SyncUploadResult.Fail("500") }, queue, audit).RunOnceAsync(Kamal);
            await new SyncCoordinator(new FakeApi { Throw = new IOException("network dropped") }, queue, audit).RunOnceAsync(Kamal);
            Assert.Equal(2, queue.GetAllItems().Count);
            Assert.Equal(2, queue.GetPendingItems().Count); // FAILED records are retried next time
        }

        [Fact]
        public async Task Sync_OnlyRecordsTheServerVerified_AreRemoved()
        {
            var (queue, audit) = NewQueue(3);
            var api = new FakeApi
            {
                // Server confirms REC-1 only, and also names an id that was never in the batch.
                Answer = b => SyncUploadResult.Ok("RCPT-1", new[] { "REC-1", "NOT-IN-BATCH" }),
            };
            var outcome = await new SyncCoordinator(api, queue, audit).RunOnceAsync(Kamal);

            Assert.True(outcome.Success);
            Assert.Equal(1, outcome.Purged);
            var left = queue.GetAllItems().Select(i => i.RecordId).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "REC-2", "REC-3" }, left);
            Assert.All(queue.GetAllItems(), i => Assert.Equal(SyncState.Pending, i.State));
        }

        [Fact]
        public async Task Sync_AllVerified_QueueEmptied_AndAudited()
        {
            var (queue, audit) = NewQueue(2);
            var api = new FakeApi { Answer = b => SyncUploadResult.Ok("RCPT-9", b.Records.Select(r => r.RecordId)) };
            await new SyncCoordinator(api, queue, audit).RunOnceAsync(Kamal);

            Assert.Empty(queue.GetAllItems());
            var log = audit.GetRecords(Admin);
            Assert.Contains(log, r => r.Action == AuditActions.UsbCleanup && r.Details.Contains("RCPT-9"));
        }

        [Fact]
        public void Queue_InterruptedUpload_IsRecovered_NotLost_NotDeleted()
        {
            var (queue, _) = NewQueue(2);
            queue.TransitionState(new[] { "REC-1", "REC-2" }, SyncState.Uploading);
            // App closed / Windows restarted here.
            Assert.Empty(queue.GetPendingItems());
            Assert.Equal(0, queue.PurgeVerifiedItems());

            Assert.Equal(2, queue.RecoverInterrupted());
            Assert.Equal(2, queue.GetPendingItems().Count);
        }

        [Fact]
        public void Queue_CannotSkipStraightToDeletable()
        {
            var (queue, _) = NewQueue(1);
            Assert.Equal(0, queue.TransitionState(new[] { "REC-1" }, SyncState.Verified, "R"));
            Assert.Equal(0, queue.TransitionState(new[] { "REC-1" }, SyncState.DeleteAllowed));
            Assert.Equal(0, queue.PurgeVerifiedItems());
            Assert.Single(queue.GetAllItems());
        }

        [Fact]
        public void Queue_TamperedRecord_IsNeitherUploadedNorDeleted()
        {
            var (queue, _) = NewQueue(1);
            string path = Path.Combine(_dir, "sync", "q.dat");
            var items = Json.FromBytes<List<SyncQueueItem>>(File.ReadAllBytes(path));
            items[0].Result = "FAIL"; // edited on the USB
            items[0].Checksum = PlainSha256(items[0].RecordId + "|" + items[0].Result); // what an attacker can compute
            File.WriteAllBytes(path, Json.ToBytes(items));

            Assert.Empty(queue.GetPendingItems());
            Assert.Single(queue.GetTamperedItems());
        }

        private static string PlainSha256(string text)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        // ------------------------------------------------------------------ QC gate

        private static TestResultRecord R(string dept, string id, string status, string by = "Kamal") =>
            new TestResultRecord { TestId = id, TestName = id, Status = status, Department = dept, OperatorName = by, CompletedAt = DateTime.Now };

        private static List<TestResultRecord> All(string dept, IEnumerable<string> ids, string status, string by) =>
            ids.Select(i => R(dept, i, status, by)).ToList();

        [Fact]
        public void QcGate_AllPassed_AllowsPass()
        {
            var report = QcGate.Evaluate(
                All("Hardware", DepartmentRequirements.Hardware, "PASS", "Kamal"),
                All("Software", DepartmentRequirements.Software, "PASS", "Shadir"),
                All("QC", DepartmentRequirements.QcChecklist, "PASS", "Jimmy"));
            Assert.True(report.CanPass);
            Assert.Equal(new[] { "Kamal" }, report.Hardware.Operators);
            Assert.Equal("All passed", report.Software.Summary);
        }

        [Fact]
        public void QcGate_MissingSoftware_BlocksPass()
        {
            var report = QcGate.Evaluate(
                All("Hardware", DepartmentRequirements.Hardware, "PASS", "Kamal"),
                new List<TestResultRecord>(),
                All("QC", DepartmentRequirements.QcChecklist, "PASS", "Jimmy"));
            Assert.False(report.CanPass);
            Assert.Contains(report.BlockingReasons, r => r.StartsWith("Software"));
            Assert.Equal("Not started", report.Software.Summary);
        }

        [Fact]
        public void QcGate_LatestResultWins_ANewerFailHidesAnOlderPass()
        {
            var hw = All("Hardware", DepartmentRequirements.Hardware, "PASS", "Kamal");
            hw.Add(new TestResultRecord { TestId = "Battery", TestName = "Battery", Status = "FAIL", Department = "Hardware", OperatorName = "Kamal", CompletedAt = DateTime.Now.AddMinutes(5) });
            var report = QcGate.Evaluate(hw,
                All("Software", DepartmentRequirements.Software, "PASS", "Shadir"),
                All("QC", DepartmentRequirements.QcChecklist, "PASS", "Jimmy"));
            Assert.False(report.CanPass);
            Assert.Single(report.Hardware.Failed);

            // ...and a newer PASS after repair clears it again.
            hw.Add(new TestResultRecord { TestId = "Battery", TestName = "Battery", Status = "PASS", Department = "Hardware", OperatorName = "Kamal", CompletedAt = DateTime.Now.AddMinutes(10) });
            Assert.True(QcGate.Evaluate(hw, All("Software", DepartmentRequirements.Software, "PASS", "Shadir"), All("QC", DepartmentRequirements.QcChecklist, "PASS", "Jimmy")).CanPass);
        }

        [Fact]
        public void QcGate_SkippedCounts_NotTestedDoesNot_DeletedIgnored()
        {
            var hw = All("Hardware", DepartmentRequirements.Hardware, "PASS", "Kamal");
            hw.First(r => r.TestId == "Bluetooth").Status = "SKIPPED";
            hw.First(r => r.TestId == "Camera").Status = "NOT TESTED";
            var d = QcGate.Evaluate("Hardware", hw, DepartmentRequirements.Hardware);
            Assert.Equal(new[] { "Camera" }, d.Missing);

            hw.First(r => r.TestId == "Camera").Status = "PASS";
            hw.First(r => r.TestId == "Camera").Deleted = true;
            Assert.Equal(new[] { "Camera" }, QcGate.Evaluate("Hardware", hw, DepartmentRequirements.Hardware).Missing);
        }

        // ------------------------------------------------------------------ activation / installer inputs

        [Theory]
        [InlineData("ABCDE-12345-FGHIJ-67890-KLMNO", true)]
        [InlineData("ABCDE-12345-FGHIJ-67890-KLMNO & calc", false)]
        [InlineData("ABCDE-12345-FGHIJ-67890", false)]
        [InlineData("\"; del /q *", false)]
        public void ProductKey_OnlyWellFormedKeysAccepted(string key, bool ok)
        {
            Assert.Equal(ok, ActivationProcessBuilder.IsValidProductKey(key));
            if (!ok) Assert.Throws<ArgumentException>(() => ActivationProcessBuilder.BuildWindowsActivationProcess(key, visible: false));
        }
    }
}
