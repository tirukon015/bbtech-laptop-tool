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

        [Fact]
        public void A_second_record_for_the_same_laptop_is_refused_unless_a_conflict_is_confirmed()
        {
            var repo = Open();
            repo.RegisterDevice(QcStoreTests.LaptopA, Specs);
            Assert.Throws<InvalidOperationException>(() => repo.RegisterDevice(QcStoreTests.LaptopA, Specs));
            Assert.Throws<InvalidOperationException>(() => repo.RegisterDevice(QcStoreTests.LaptopA, Specs, "note"));
            Assert.Single(repo.Devices);

            // Same serial, different motherboard: a conflict that staff may override with a note.
            var swapped = new DeviceIdentity("LENOVO", "ThinkPad", "T14 Gen 2", "SERIALEX01", "11111111-2222-3333-4444-555555555555", "BRD-NEW");
            Assert.Throws<InvalidOperationException>(() => repo.RegisterDevice(swapped, Specs));
            var flagged = repo.RegisterDevice(swapped, Specs, "Created despite identity conflict");
            Assert.Equal("Created despite identity conflict", flagged.IdentityNote);
            Assert.Equal(2, repo.Devices.Count);
        }

        [Fact]
        public void Inspection_numbers_count_up_per_laptop()
        {
            var repo = Open();
            var a = repo.RegisterDevice(QcStoreTests.LaptopA, Specs);
            var b = repo.RegisterDevice(QcStoreTests.LaptopB, Specs);
            var a1 = repo.StartInspection(a.DeviceId, Op, "1.0.0", Specs);
            repo.CompleteInspection(a1.InspectionId, "PASS");
            var b1 = repo.StartInspection(b.DeviceId, Op, "1.0.0", Specs);
            var a2 = repo.StartInspection(a.DeviceId, Op, "1.0.0", Specs);
            Assert.Equal(1, a1.Number);
            Assert.Equal(1, b1.Number);
            Assert.Equal(2, a2.Number);
        }

        [Fact]
        public void Starting_an_inspection_without_an_operator_name_fails()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(QcStoreTests.LaptopA, Specs);
            Assert.Throws<InvalidOperationException>(() => repo.StartInspection(dev.DeviceId, null, "1.0.0", Specs));
            Assert.Throws<InvalidOperationException>(() => repo.StartInspection(dev.DeviceId, new OperatorSnapshot { Name = "  " }, "1.0.0", Specs));
            Assert.Empty(repo.GetHistory(dev.DeviceId));
        }

    }
}
