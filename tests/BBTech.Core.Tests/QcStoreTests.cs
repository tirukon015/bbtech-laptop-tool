using System;
using System.IO;
using System.Linq;
using System.Text;
using BBTech.Core.Identity;
using BBTech.Core.Security;
using BBTech.Core.Storage;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Simulates a Windows installation's DPAPI machine key (changes on reinstall).</summary>
    internal sealed class FakeMachine : IMachineKeyProtector
    {
        private readonly EnvelopeCrypto _c;
        public FakeMachine(int seed) { var k = new byte[64]; new Random(seed).NextBytes(k); _c = new EnvelopeCrypto(k); }
        public byte[] Protect(byte[] data) => _c.Encrypt(data);
        public byte[] Unprotect(byte[] data) => _c.TryDecrypt(data);
    }

    public class QcStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-qc-tests-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        internal static readonly DeviceIdentity LaptopA = new DeviceIdentity("LENOVO", "ThinkPad", "T14 Gen 2", "SERIALEX01", "6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1", "L1HF0001");
        internal static readonly DeviceIdentity LaptopB = new DeviceIdentity("Dell", "Latitude", "5420", "ABC12345", "4C4C4544-0042-3510-8051-B8C04F4E3532", "BRD-B");
        private static readonly OperatorSnapshot Jimmy = new OperatorSnapshot { OperatorId = "op-jimmy", Name = "Jimmy", Role = "QC" };
        private static readonly OperatorSnapshot Rahim = new OperatorSnapshot { OperatorId = "op-rahim", Name = "Rahim", Role = "QC" };
        private static readonly DeviceSpecs Specs = new DeviceSpecs { Cpu = "i5", Generation = "11th Gen", Ram = "32 GB", Storage = "512 GB SSD", BatteryHealth = "87%" };

        private QcRepository Open(int machine = 1, DeviceIdentity id = null)
        {
            var r = QcRepository.Open(_dir, new FakeMachine(machine), id ?? LaptopA);
            Assert.True(r.Ok, r.Message);
            return r.Repository;
        }

        [Fact]
        public void Create_device_inspection_and_results_survive_reopen()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(LaptopA, Specs);
            var insp = repo.StartInspection(dev.DeviceId, Jimmy, "1.0.0", Specs);
            repo.SaveResult(insp.InspectionId, "Keyboard", "Keyboard", "PASS", true, "Keys 79/79");
            repo.SaveResult(insp.InspectionId, "Display", "Display", "PASS", true, "");
            repo.CompleteInspection(insp.InspectionId, "PASS");

            var again = Open(); // e.g. a different USB / app restart
            var match = again.FindDevice(LaptopA);
            Assert.Equal(MatchStatus.Matched, match.Status);
            var history = again.GetHistory(match.Device.DeviceId);
            Assert.Single(history);
            Assert.Equal("Jimmy", history[0].Inspection.OperatorNameSnapshot);
            Assert.Equal("QC", history[0].Inspection.OperatorRoleSnapshot);
            Assert.Equal("PASS", history[0].Inspection.OverallStatus);
            Assert.Equal(2, history[0].Results.Count);
            Assert.Equal(InspectionStatus.Completed, history[0].Inspection.Status);
            Assert.Single(again.SyncRecords, s => s.SyncStatus == SyncStatus.LocalOnly);
        }

        [Fact]
        public void Each_result_is_persisted_immediately_and_unfinished_inspection_is_recoverable()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(LaptopA, Specs);
            var insp = repo.StartInspection(dev.DeviceId, Jimmy, "1.0.0", Specs);
            repo.SaveResult(insp.InspectionId, "Keyboard", "Keyboard", "PASS", true, "");
            // "Crash": no Complete; reopen from disk.
            var after = Open();
            var open = after.GetOpenInspection(dev.DeviceId);
            Assert.NotNull(open);
            Assert.Equal(insp.InspectionId, open.InspectionId);
            Assert.Equal("PASS", after.GetResults(open.InspectionId).Single().Status);
        }

        [Fact]
        public void Completed_inspections_are_immutable_and_history_is_preserved()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(LaptopA, Specs);
            var i1 = repo.StartInspection(dev.DeviceId, Jimmy, "1.0.0", Specs);
            repo.SaveResult(i1.InspectionId, "Battery", "Battery", "PASS", true, "");
            repo.CompleteInspection(i1.InspectionId, "PASS");

            Assert.Throws<InvalidOperationException>(() => repo.SaveResult(i1.InspectionId, "Battery", "Battery", "FAIL", true, ""));
            Assert.Throws<InvalidOperationException>(() => repo.CompleteInspection(i1.InspectionId, "FAIL"));

            var i2 = repo.StartInspection(dev.DeviceId, Rahim, "1.0.0", Specs);
            repo.CompleteInspection(i2.InspectionId, "FAIL");
            var i3 = repo.StartInspection(dev.DeviceId, Jimmy, "1.0.0", Specs);
            repo.CompleteInspection(i3.InspectionId, "PASS");

            var h = Open().GetHistory(dev.DeviceId);
            Assert.Equal(3, h.Count);
            Assert.Equal(new[] { 3, 2, 1 }, h.Select(x => x.Inspection.Number).ToArray());
            Assert.Equal(new[] { "Jimmy", "Rahim", "Jimmy" }, h.Select(x => x.Inspection.OperatorNameSnapshot).ToArray());
            Assert.Equal(new[] { "PASS", "FAIL", "PASS" }, h.Select(x => x.Inspection.OverallStatus).ToArray());
            Assert.Equal("PASS", h[2].Results.Single().Status); // inspection 1 unchanged
        }

        [Fact]
        public void Abandoned_inspection_is_kept_not_deleted()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(LaptopA, Specs);
            var i1 = repo.StartInspection(dev.DeviceId, Jimmy, "1.0.0", Specs);
            repo.AbandonInspection(i1.InspectionId);
            var h = Open().GetHistory(dev.DeviceId);
            Assert.Single(h);
            Assert.Equal(InspectionStatus.Abandoned, h[0].Inspection.Status);
            Assert.Equal("INCOMPLETE", h[0].Inspection.OverallStatus);
        }

        [Fact]
        public void Other_laptop_history_is_never_shown()
        {
            // The store holds A's history (e.g. a disk image cloned from laptop A onto laptop B).
            var repo = Open();
            var dev = repo.RegisterDevice(LaptopA, Specs);
            repo.CompleteInspection(repo.StartInspection(dev.DeviceId, Jimmy, "1.0.0", Specs).InspectionId, "PASS");

            var onB = QcRepository.Open(_dir, new FakeMachine(1), LaptopB).Repository;
            Assert.Equal(MatchStatus.NotFound, onB.FindDevice(LaptopB).Status);
        }

        [Fact]
        public void Store_files_contain_no_plaintext()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(LaptopA, Specs);
            var i = repo.StartInspection(dev.DeviceId, Jimmy, "1.0.0", Specs);
            repo.SaveResult(i.InspectionId, "Keyboard", "Keyboard", "PASS", true, "All keys OK");
            repo.CompleteInspection(i.InspectionId, "PASS");

            foreach (var f in Directory.GetFiles(_dir))
            {
                string text = Encoding.UTF8.GetString(File.ReadAllBytes(f)) + Encoding.Unicode.GetString(File.ReadAllBytes(f));
                foreach (var word in new[] { "Jimmy", "Keyboard", "PASS", "SERIALEX01", "ThinkPad", "All keys", "Tested", "inspection" })
                    Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void Windows_reinstall_with_data_kept_recovers_via_hardware_lock()
        {
            var repo = Open(machine: 1);
            var dev = repo.RegisterDevice(LaptopA, Specs);
            repo.CompleteInspection(repo.StartInspection(dev.DeviceId, Jimmy, "1.0.0", Specs).InspectionId, "PASS");

            // New Windows installation (different DPAPI machine key), same physical laptop.
            var r = QcRepository.Open(_dir, new FakeMachine(2), LaptopA);
            Assert.True(r.Ok);
            Assert.Equal(KeyState.UnlockedRecovery, r.KeyState);
            Assert.Single(r.Repository.GetHistory(dev.DeviceId));

            // The machine lock was re-created for the new Windows: next open uses it directly.
            Assert.Equal(KeyState.UnlockedMachine, QcRepository.Open(_dir, new FakeMachine(2), LaptopA).KeyState);
        }

        [Fact]
        public void Recovery_works_if_one_identifier_survives()
        {
            var repo = Open(machine: 1);
            repo.RegisterDevice(LaptopA, Specs);
            // New Windows AND motherboard replaced (new UUID + board serial), BIOS serial re-entered.
            var repaired = new DeviceIdentity("LENOVO", "ThinkPad", "T14 Gen 2", "SERIALEX01", "11111111-2222-3333-4444-555555555555", "NEWBOARD");
            Assert.Equal(KeyState.UnlockedRecovery, QcRepository.Open(_dir, new FakeMachine(3), repaired).KeyState);
        }

        [Fact]
        public void Different_laptop_and_different_windows_cannot_open_the_store()
        {
            Open(machine: 1).RegisterDevice(LaptopA, Specs);
            var r = QcRepository.Open(_dir, new FakeMachine(9), LaptopB);
            Assert.False(r.Ok);
            Assert.Equal(KeyState.Locked, r.KeyState);
            Assert.True(File.Exists(Path.Combine(_dir, EncryptedJournal.FileName))); // data is never deleted
        }

        [Fact]
        public void Missing_key_file_never_overwrites_existing_history()
        {
            Open().RegisterDevice(LaptopA, Specs);
            long size = new FileInfo(Path.Combine(_dir, EncryptedJournal.FileName)).Length;
            File.Delete(Path.Combine(_dir, KeyVault.KeyFileName));
            File.Delete(Path.Combine(_dir, KeyVault.KeyFileName + ".bak"));
            var r = QcRepository.Open(_dir, new FakeMachine(1), LaptopA);
            Assert.False(r.Ok);
            Assert.Equal(KeyState.KeyMissing, r.KeyState);
            Assert.Equal(size, new FileInfo(Path.Combine(_dir, EncryptedJournal.FileName)).Length);
        }

        [Fact]
        public void Power_loss_during_write_keeps_earlier_records()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(LaptopA, Specs);
            var i = repo.StartInspection(dev.DeviceId, Jimmy, "1.0.0", Specs);
            repo.SaveResult(i.InspectionId, "Keyboard", "Keyboard", "PASS", true, "");

            // Simulate a half-written final record.
            string j = Path.Combine(_dir, EncryptedJournal.FileName);
            using (var f = new FileStream(j, FileMode.Append)) { f.Write(Encoding.ASCII.GetBytes("BQR1"), 0, 4); f.Write(BitConverter.GetBytes(5000), 0, 4); f.Write(new byte[100], 0, 100); }

            var r = QcRepository.Open(_dir, new FakeMachine(1), LaptopA);
            Assert.True(r.Ok);
            Assert.Equal(108, r.Stats.TruncatedTailBytes);
            Assert.Equal("PASS", r.Repository.GetResults(i.InspectionId).Single().Status);
            // and new writes work after the repair
            r.Repository.SaveResult(i.InspectionId, "Display", "Display", "FAIL", true, "");
            Assert.Equal(2, Open().GetResults(i.InspectionId).Count);
        }

        [Fact]
        public void Corrupted_record_is_skipped_and_later_records_survive()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(LaptopA, Specs);
            var i = repo.StartInspection(dev.DeviceId, Jimmy, "1.0.0", Specs);
            repo.SaveResult(i.InspectionId, "Keyboard", "Keyboard", "PASS", true, "");
            string j = Path.Combine(_dir, EncryptedJournal.FileName);
            long cut = new FileInfo(j).Length;
            repo.SaveResult(i.InspectionId, "Display", "Display", "PASS", true, "");
            repo.SaveResult(i.InspectionId, "Camera", "Camera", "FAIL", true, "");

            // Damage a byte inside the "Display" record.
            var bytes = File.ReadAllBytes(j);
            bytes[cut + 40] ^= 0xFF;
            File.WriteAllBytes(j, bytes);

            var r = QcRepository.Open(_dir, new FakeMachine(1), LaptopA);
            Assert.True(r.Ok);
            Assert.Equal(1, r.Stats.Corrupted);
            var results = r.Repository.GetResults(i.InspectionId).Select(x => x.TestId).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "Camera", "Keyboard" }, results);
        }

        [Fact]
        public void Garbage_file_is_handled_without_crash()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(Path.Combine(_dir, EncryptedJournal.FileName), Encoding.ASCII.GetBytes("this is not a journal at all ... BQR1 garbage"));
            File.WriteAllBytes(Path.Combine(_dir, KeyVault.KeyFileName), new byte[] { 1, 2, 3 });
            var r = QcRepository.Open(_dir, new FakeMachine(1), LaptopA);
            Assert.False(r.Ok); // key unreadable -> reported, nothing overwritten
            Assert.Equal(KeyState.KeyMissing, r.KeyState);
        }

        [Fact]
        public void Envelope_detects_tampering()
        {
            var c = new EnvelopeCrypto(EnvelopeCrypto.NewKey());
            var data = c.Encrypt(Encoding.UTF8.GetBytes("hello"));
            Assert.Equal("hello", Encoding.UTF8.GetString(c.TryDecrypt(data)));
            data[20] ^= 1;
            Assert.Null(c.TryDecrypt(data));
            Assert.Null(new EnvelopeCrypto(EnvelopeCrypto.NewKey()).TryDecrypt(c.Encrypt(new byte[10])));
        }

        [Fact]
        public void Real_dpapi_protector_round_trips()
        {
            var p = new DpapiMachineKeyProtector();
            var key = EnvelopeCrypto.NewKey();
            Assert.Equal(key, p.Unprotect(p.Protect(key)));
            Assert.Null(p.Unprotect(new byte[] { 1, 2, 3 }));
        }
    }
}
