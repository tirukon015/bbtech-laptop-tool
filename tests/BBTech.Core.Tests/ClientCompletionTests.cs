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
    /// <summary>Software checks, managed installation, configurable QC rules, master configuration and server authority (client side).</summary>
    public sealed class ClientCompletionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-client-" + Guid.NewGuid().ToString("N"));
        public ClientCompletionTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        // ------------------------------------------------------------------ drivers

        [Fact]
        public void Drivers_ProblemDevicesAndBasicDisplay_AreReported()
        {
            var report = DriverHealth.Evaluate(new[]
            {
                new PnpDeviceInfo { Name = "Microsoft Basic Display Adapter", PnpClass = "Display", ErrorCode = 0 },
                new PnpDeviceInfo { Name = "Unknown device", PnpClass = null, ErrorCode = 28 },
                new PnpDeviceInfo { Name = "Realtek Audio", PnpClass = "MEDIA", ErrorCode = 0 },
            });
            Assert.False(report.Ok);
            Assert.True(report.BasicDisplayDriverOnly);
            Assert.Contains(report.Problems, p => p.Contains("no driver installed"));
        }

        [Fact]
        public void Drivers_AllWorking_IsOk()
        {
            var report = DriverHealth.Evaluate(new[]
            {
                new PnpDeviceInfo { Name = "Intel(R) Iris(R) Xe Graphics", PnpClass = "Display" },
                new PnpDeviceInfo { Name = "Microsoft Basic Render Driver", PnpClass = "System" },
            });
            Assert.True(report.Ok);
        }

        // ------------------------------------------------------------------ configurable QC rules

        private static TestResultRecord R(string dept, string id, string status) =>
            new TestResultRecord { TestId = id, TestName = id, Status = status, Department = dept, OperatorName = "X", CompletedAt = DateTime.Now };

        private static List<TestResultRecord> AllPass(QcRequirements req, string dept) => req.Required(dept).Select(id => R(dept, id, "PASS")).ToList();

        [Fact]
        public void Skip_IsAccepted_OnlyWhereAllowed()
        {
            var req = QcRequirements.Default();
            var hw = AllPass(req, Departments.Hardware);
            hw.First(r => r.TestId == "Bluetooth").Status = "SKIPPED";     // allowed: some laptops have no Bluetooth
            var ok = QcGate.Evaluate(hw, AllPass(req, Departments.Software), AllPass(req, Departments.Qc), req);
            Assert.True(ok.CanPass);

            hw.First(r => r.TestId == "Battery").Status = "SKIPPED";       // not allowed: cannot bypass a required test
            var blocked = QcGate.Evaluate(hw, AllPass(req, Departments.Software), AllPass(req, Departments.Qc), req);
            Assert.False(blocked.CanPass);
            Assert.Contains("Battery", blocked.Hardware.SkippedNotAllowed);
            Assert.Contains(blocked.BlockingReasons, r => r.Contains("skipped but required"));
        }

        [Fact]
        public void WindowsActivation_IsRequired_OfficeIsNot()
        {
            var req = QcRequirements.Default();
            Assert.Contains("SoftwareWindowsActivation", req.Required(Departments.Software));
            Assert.DoesNotContain("SoftwareOfficeActivation", req.Required(Departments.Software));

            var sw = AllPass(req, Departments.Software);
            sw.First(r => r.TestId == "SoftwareWindowsActivation").Status = "FAIL";
            sw.Add(R(Departments.Software, "SoftwareOfficeActivation", "FAIL")); // optional: visible, not blocking
            var report = QcGate.Evaluate(AllPass(req, Departments.Hardware), sw, AllPass(req, Departments.Qc), req);
            Assert.Single(report.BlockingReasons);
            Assert.Contains("SoftwareWindowsActivation", report.BlockingReasons[0]);
            Assert.Equal(2, report.Software.Failed.Count);
        }

        [Fact]
        public void ServerRequirements_ReplaceDefaults_InvalidOnesAreIgnored()
        {
            var custom = QcRequirements.Default();
            custom.Tests.First(t => t.TestId == "SoftwareOfficeActivation").Required = true; // server makes Office mandatory
            var sw = AllPass(QcRequirements.Default(), Departments.Software);
            var hw = AllPass(custom, Departments.Hardware); var qc = AllPass(custom, Departments.Qc);
            Assert.False(QcGate.Evaluate(hw, sw, qc, custom).CanPass);

            var broken = new QcRequirements { Tests = new List<TestRequirement>() };
            Assert.False(broken.IsValid);
            Assert.True(QcGate.Evaluate(hw, sw, qc, broken).CanPass); // falls back to the defaults
        }

        // ------------------------------------------------------------------ managed installation

        [Fact]
        public void Package_WithoutChecksumOrPublisher_IsRefused()
        {
            var p = SoftwarePackage.DefaultOffice();
            Assert.Null(p.Validate());
            p.Publisher = "";
            Assert.Contains("cannot be verified", p.Validate());
        }

        [Theory]
        [InlineData("setup.exe & calc.exe")]
        [InlineData("..\\..\\Windows\\System32\\cmd.exe")]
        [InlineData("installer.bat")]
        [InlineData("run.ps1")]
        public void Package_UnsafeInstallerNames_AreRefused(string name)
        {
            var p = SoftwarePackage.DefaultOffice();
            p.OnlineInstallerName = name;
            Assert.NotNull(p.Validate());
        }

        [Theory]
        [InlineData("/configure config.xml", true)]
        [InlineData("/quiet & del C:\\*", false)]
        [InlineData("/configure ..\\..\\x.xml", false)]
        [InlineData("| powershell -enc AAA", false)]
        public void Package_InstallArguments_AreRestricted(string args, bool ok)
        {
            var p = SoftwarePackage.DefaultOffice();
            p.InstallArguments = args;
            Assert.Equal(ok, p.Validate() == null);
        }

        [Fact]
        public void Package_DownloadMustBeHttps_AndChecksumWellFormed()
        {
            var p = SoftwarePackage.DefaultOffice();
            p.OnlineDownloadUrl = "http://example.com/OfficeSetup.exe";
            Assert.NotNull(p.Validate());
            p.OnlineDownloadUrl = "https://example.com/OfficeSetup.exe";
            p.Sha256Checksum = "not-a-hash";
            Assert.NotNull(p.Validate());
        }

        [Fact]
        public void Package_ChecksumNeverPassesWhenMissing_AndMustMatch()
        {
            string file = Path.Combine(_dir, "OfficeSetup.exe");
            File.WriteAllText(file, "installer bytes");
            var p = SoftwarePackage.DefaultOffice();
            Assert.False(p.VerifyFileIntegrity(file));                    // no checksum configured: not "verified"
            p.Sha256Checksum = SoftwarePackage.ComputeSha256(file);
            Assert.True(p.VerifyFileIntegrity(file));
            File.AppendAllText(file, "tampered");
            Assert.False(p.VerifyFileIntegrity(file));
        }

        [Fact]
        public void Package_InstallerIsOnlyTakenFromTheApprovedFolder()
        {
            string root = Path.Combine(_dir, "installers");
            Directory.CreateDirectory(root);
            var p = SoftwarePackage.DefaultOffice();
            Assert.Null(p.ResolveInstaller(root));                        // missing

            File.WriteAllText(Path.Combine(root, "OfficeSetup.exe"), "x");
            Assert.Equal(Path.Combine(root, "OfficeSetup.exe"), p.ResolveInstaller(root));

            var offline = new SoftwarePackage { SourceMode = PackageSourceMode.Offline, OfflineFolderPath = "..\\..\\elsewhere", OfflineExecutableName = "Setup.exe", Publisher = "Microsoft Corporation" };
            Assert.Null(offline.ResolveInstaller(root));
        }

        [Fact]
        public void Catalog_DropsInvalidPackages_AndAlwaysOffersOffice()
        {
            var bad = new SoftwarePackage { PackageId = "evil", Name = "Evil", OnlineInstallerName = "x.exe", Publisher = "", Sha256Checksum = "" };
            var list = SoftwareCatalog.Effective(new[] { bad });
            Assert.DoesNotContain(list, p => p.PackageId == "evil");
            Assert.Contains(list, p => p.DetectRule == "office");
        }

        [Theory]
        [InlineData(0, true, true)]
        [InlineData(3010, true, true)]
        [InlineData(0, false, false)]     // "exit 0" but nothing installed = failure
        [InlineData(1603, false, false)]
        [InlineData(-1223, false, false)] // UAC declined
        [InlineData(-2, false, false)]    // timed out
        public void InstallOutcome_RequiresExitCodeAndDetection(int exitCode, bool detected, bool success)
        {
            var o = InstallOutcome.From(exitCode, detected, "Microsoft Office", "16.0", 60);
            Assert.Equal(success, o.Success);
            Assert.False(string.IsNullOrWhiteSpace(o.Message));
            if (exitCode == 3010) Assert.True(o.RestartRequired);
        }

        // ------------------------------------------------------------------ master configuration

        private static MasterConfigResponse Config(int version) => new MasterConfigResponse
        {
            ConfigVersion = version,
            UpdatedAt = DateTime.UtcNow,
            Operators = new List<OperatorRecord>
            {
                new OperatorRecord { OperatorId = "srv-admin", Name = "Touhidul", Role = "Super Admin", Active = true, Permissions = new List<string> { AppPermissions.All } },
                new OperatorRecord { OperatorId = "srv-kamal", Name = "Kamal", Role = "Technician", Active = true, Departments = new List<string> { "Hardware" }, Permissions = new List<string> { AppPermissions.HardwareTest } },
            },
            Packages = new List<SoftwarePackage> { SoftwarePackage.DefaultOffice() },
            Requirements = QcRequirements.Default(),
        };

        [Fact]
        public void MasterConfig_RoundTripsEncrypted_AndRefusesRollback()
        {
            string path = Path.Combine(_dir, "config", "master.dat");
            Assert.Null(MasterConfigCache.TrySave(path, Config(5)));
            Assert.DoesNotContain("Kamal", File.ReadAllText(path));     // not readable with ordinary tools
            Assert.Equal(5, MasterConfigCache.Load(path).ConfigVersion);

            Assert.NotNull(MasterConfigCache.TrySave(path, Config(4))); // older: ignored
            Assert.Equal(5, MasterConfigCache.Load(path).ConfigVersion);

            var bytes = File.ReadAllBytes(path); bytes[bytes.Length - 5] ^= 0xFF; File.WriteAllBytes(path, bytes);
            Assert.Null(MasterConfigCache.Load(path));                  // tampered: rejected
        }

        [Fact]
        public void MasterConfig_WithoutAdmin_IsRejected()
        {
            var cfg = Config(1);
            cfg.Operators.RemoveAt(0);
            Assert.NotNull(cfg.Validate());
            Assert.NotNull(MasterConfigCache.TrySave(Path.Combine(_dir, "m.dat"), cfg));
        }

        [Fact]
        public void ServerOperators_AreAuthoritative_AndEndRemovedSessions()
        {
            var s = SettingsStore.Load(Path.Combine(_dir, "config", "settings.dat"));
            var localOnly = s.AddOperator("Ghost", "Hardware");
            s.SelectOperator(localOnly.OperatorId);
            Assert.NotNull(s.CurrentOperator);

            s.ApplyServerOperators(Config(1).Operators);

            Assert.Null(s.CurrentOperator);                               // the session of a removed operator ends
            Assert.False(s.FindOperator(localOnly.OperatorId).Active);   // kept for history, inactive
            Assert.True(s.FindOperator("srv-kamal").Active);
            Assert.Throws<ArgumentException>(() => s.ApplyServerOperators(new List<OperatorRecord>()));
        }

        // ------------------------------------------------------------------ whole workflow on the local store

        [Fact]
        public async Task Workflow_Hardware_Software_Qc_Itms_OnTheEncryptedStore()
        {
            var open = QcRepository.Open(_dir, new FakeMachine(7), QcStoreTests.LaptopA);
            Assert.True(open.Ok, open.Message);
            var repo = open.Repository;
            var device = repo.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs { Cpu = "i5" });
            var req = QcRequirements.Default();
            var hardware = req.Required(Departments.Hardware);
            var software = req.Required(Departments.Software);
            var qc = req.Required(Departments.Qc);

            QcGateReport Gate() => QcGate.Evaluate(repo.GetLatestDepartmentResults(device.DeviceId, Departments.Hardware),
                repo.GetLatestDepartmentResults(device.DeviceId, Departments.Software), repo.GetLatestDepartmentResults(device.DeviceId, Departments.Qc), req);

            // Hardware (Kamal): one test fails first, then passes after repair: two records, latest counts.
            foreach (var t in hardware) repo.RecordTest(device.DeviceId, t, t, t == "Keyboard" ? "FAIL" : "PASS", "Kamal", Departments.Hardware, operatorId: "kamal");
            Assert.False(Gate().CanPass);
            System.Threading.Thread.Sleep(15);
            repo.RecordTest(device.DeviceId, "Keyboard", "Keyboard", "PASS", "Kamal", Departments.Hardware, operatorId: "kamal");
            Assert.Equal(2, repo.GetTestHistory(device.DeviceId, "Keyboard", Departments.Hardware).Count);
            System.Threading.Thread.Sleep(15);
            repo.RecordTest(device.DeviceId, LifecycleEvents.HardwareHandover, "Handover", "PASS", "Kamal", Departments.Hardware, operatorId: "kamal");
            System.Threading.Thread.Sleep(15);

            // Software (Shadir) and QC checklist (Jimmy).
            foreach (var t in software) repo.RecordTest(device.DeviceId, t, t, "PASS", "Shadir", Departments.Software, operatorId: "shadir");
            System.Threading.Thread.Sleep(15);
            repo.RecordTest(device.DeviceId, LifecycleEvents.SoftwareHandover, "Handover", "PASS", "Shadir", Departments.Software, operatorId: "shadir");
            System.Threading.Thread.Sleep(15);
            Assert.False(Gate().CanPass);                                       // QC checklist not done yet
            foreach (var t in qc) repo.RecordTest(device.DeviceId, t, t, "PASS", "Jimmy", Departments.Qc, operatorId: "jimmy");
            System.Threading.Thread.Sleep(15);
            var report = Gate();
            Assert.True(report.CanPass, string.Join("; ", report.BlockingReasons));
            Assert.Equal(new[] { "Kamal" }, report.Hardware.Operators);
            Assert.Equal(new[] { "Shadir" }, report.Software.Operators);

            // ITMS refuses until QC PASS is recorded, then accepts the gate.
            var itms = new Integration.ItmsTransmissionService(repo, new Integration.DisabledItmsConnector());
            Assert.NotNull(itms.QcGateProblem(device));
            repo.RecordTest(device.DeviceId, QcGate.QcDecisionTestId, "Final QC Decision", "PASS", "Jimmy", Departments.Qc, details: "snapshot", operatorId: "jimmy");
            Assert.Null(itms.QcGateProblem(device));

            // Everything survives a restart (reopen the encrypted journal).
            var reopened = QcRepository.Open(_dir, new FakeMachine(7), QcStoreTests.LaptopA).Repository;
            Assert.Equal("PASS", reopened.GetLatestTestResult(device.DeviceId, QcGate.QcDecisionTestId, Departments.Qc).Status);
            Assert.Equal("shadir", reopened.GetLatestTestResult(device.DeviceId, "SoftwareWindowsActivation", Departments.Software).OperatorId);
            await Task.CompletedTask;
        }

        // ------------------------------------------------------------------ server rejections

        [Fact]
        public async Task Sync_RejectedRecords_StayOnUsbWithTheReason()
        {
            var queue = new UsbSyncQueue(Path.Combine(_dir, "q.dat"));
            queue.Enqueue(SyncQueueItem.FromTestResult("DEV", "kamal", "Kamal", "QC", "QC_FINAL", "Final", "PASS", "", "1.4.0", "REC-QC"));
            queue.Enqueue(SyncQueueItem.FromTestResult("DEV", "kamal", "Kamal", "Hardware", "Battery", "Battery", "PASS", "", "1.4.0", "REC-HW"));
            var api = new ProductionHardeningTests.FakeApi
            {
                Answer = b => new SyncUploadResult
                {
                    Success = true, ServerReceiptId = "R1", VerifiedRecordIds = new List<string> { "REC-HW" },
                    Rejected = new List<RejectedRecord> { new RejectedRecord { RecordId = "REC-QC", Reason = "operator lacks qc.approve" } },
                },
            };
            var outcome = await new SyncCoordinator(api, queue, new AuditStore(Path.Combine(_dir, "a.dat"))).RunOnceAsync(null);

            Assert.Equal(1, outcome.Rejected);
            var left = Assert.Single(queue.GetAllItems());
            Assert.Equal("REC-QC", left.RecordId);
            Assert.Equal(SyncState.Failed, left.State);
            Assert.Contains("qc.approve", left.LastError);
        }
    }
}
