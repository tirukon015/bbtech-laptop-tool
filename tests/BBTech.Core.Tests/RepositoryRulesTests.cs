using System;
using System.IO;
using System.Linq;
using BBTech.Core.Identity;
using BBTech.Core.Storage;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Input rules of the laptop's QC repository (invalid calls fail clearly and write nothing).</summary>
    public class RepositoryRulesTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-reporules-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly OperatorSnapshot Op = new OperatorSnapshot { OperatorId = "op-1", Name = "Operator One", Role = "QC" };
        private static readonly DeviceSpecs Specs = new DeviceSpecs { Cpu = "i5", Ram = "16 GB" };

        private QcRepository Open()
        {
            var r = QcRepository.Open(_dir, new FakeMachine(7), QcStoreTests.LaptopA);
            Assert.True(r.Ok, r.Message);
            return r.Repository;
        }

        [Fact]
        public void Updating_an_unknown_device_fails_with_a_clear_message()
        {
            var repo = Open();
            var ex = Assert.Throws<InvalidOperationException>(() => repo.UpdateDevice("missing", QcStoreTests.LaptopA, Specs));
            Assert.Equal("Unknown device.", ex.Message);
            Assert.Throws<InvalidOperationException>(() => repo.UpdateDevice(null, QcStoreTests.LaptopA, Specs));
        }

        [Fact]
        public void Results_with_an_unknown_status_or_missing_test_id_are_rejected_and_not_stored()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(QcStoreTests.LaptopA, Specs);
            var insp = repo.StartInspection(dev.DeviceId, Op, "1.0.0", Specs);
            Assert.Throws<ArgumentException>(() => repo.SaveResult(insp.InspectionId, "Display", "Display", "OK", true, ""));
            Assert.Throws<ArgumentException>(() => repo.SaveResult(insp.InspectionId, "Display", "Display", "pass", true, ""));
            Assert.Throws<ArgumentException>(() => repo.SaveResult(insp.InspectionId, "", "Display", "PASS", true, ""));
            Assert.Empty(repo.GetResults(insp.InspectionId));
            foreach (var s in QcRepository.ResultStatuses) repo.SaveResult(insp.InspectionId, "Display", "Display", s, true, "");
            Assert.Equal("SKIPPED", repo.GetResults(insp.InspectionId).Single().Status);
        }

        [Fact]
        public void Completing_with_an_unknown_overall_result_is_rejected_and_the_inspection_stays_open()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(QcStoreTests.LaptopA, Specs);
            var insp = repo.StartInspection(dev.DeviceId, Op, "1.0.0", Specs);
            Assert.Throws<ArgumentException>(() => repo.CompleteInspection(insp.InspectionId, "OK"));
            Assert.Throws<ArgumentException>(() => repo.CompleteInspection(insp.InspectionId, null));
            Assert.True(repo.GetInspection(insp.InspectionId).IsOpen);
            Assert.Empty(repo.SyncRecords);
        }

        [Fact]
        public void Null_ids_give_clear_errors_or_empty_results_instead_of_crashing()
        {
            var repo = Open();
            Assert.Equal("Unknown device.", Assert.Throws<InvalidOperationException>(() => repo.StartInspection(null, Op, "1.0.0", Specs)).Message);
            Assert.Equal("Unknown inspection.", Assert.Throws<InvalidOperationException>(() => repo.SaveResult(null, "Display", "Display", "PASS", true, "")).Message);
            Assert.Throws<InvalidOperationException>(() => repo.CompleteInspection(null, "PASS"));
            Assert.Throws<InvalidOperationException>(() => repo.AbandonInspection(null));
            Assert.Null(repo.GetInspection(null));
            Assert.Empty(repo.GetResults(null));
        }

        [Fact]
        public void Wifi_state_needs_an_ssid_and_round_trips_without_a_password()
        {
            var repo = Open();
            Assert.Null(repo.GetWifiState(null));
            Assert.Throws<ArgumentNullException>(() => repo.SetWifiState(null));
            Assert.Throws<ArgumentException>(() => repo.SetWifiState(new WifiProvisionRecord { Ssid = "" }));
            repo.SetWifiState(new WifiProvisionRecord { Ssid = "BBTECH_EXAMPLE", SettingsFingerprint = "fp-1", AllUsers = true });
            var again = Open().GetWifiState("BBTECH_EXAMPLE");
            Assert.Equal("fp-1", again.SettingsFingerprint);
            Assert.True(again.AllUsers);
        }
    }
}
