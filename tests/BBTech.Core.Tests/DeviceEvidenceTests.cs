using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BBTech.Core.Export;
using BBTech.Core.Identity;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Software;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Technical evidence: inventory with sources, no invented values, part-replacement history, ITMS payload, history that survives a Windows reinstall.</summary>
    public sealed class DeviceEvidenceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-evidence-" + Guid.NewGuid().ToString("N"));
        public DeviceEvidenceTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static TechnicalInventory Inventory(string ssdModel = "Samsung PM981 512GB", string ssdSerial = "S4EWNX0M123", string temp = "39")
        {
            var inv = new TechnicalInventory { CapturedAt = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc), ToolVersion = "1.5.0", DeviceId = "bbt-1" };
            inv.Add("baseboard", "LENOVO 20W0", "SMBIOS", DataConfidence.Detected, "L1HF123").SetId("serial_number", "L1HF123");
            inv.Add("ram_module", "Samsung M471A2G44AM0-CWE", "SMBIOS", DataConfidence.Detected, "39AB12CD").Set("capacity_gb", 16).Set("configured_speed_mts", 3200).SetId("serial_number", "39AB12CD");
            inv.Add("storage", ssdModel, "NVMe Health Log", DataConfidence.Measured, ssdSerial)
               .SetId("serial_number", ssdSerial).Set("percentage_used", 7).Set("media_and_data_integrity_errors", 0).Set("temperature_c", temp).Set("power_on_hours", 3421);
            inv.Add("battery", "5B10W51829", "Battery report", DataConfidence.Measured, "1234").Set("design_capacity_mwh", 50000).Set("full_charge_capacity_mwh", 42000).Set("cycle_count", null);
            return inv;
        }

        // ------------------------------------------------------------------ inventory rules

        [Fact]
        public void MissingValues_AreUnknown_NeverInvented_AndZeroStaysZero()
        {
            var inv = Inventory();
            Assert.Equal("UNKNOWN", inv.Of("battery").Single().Get("cycle_count"));           // not exposed: UNKNOWN, not 0
            Assert.Equal("0", inv.Of("storage").Single().Get("media_and_data_integrity_errors")); // a measured zero is a real value

            var item = new InventoryItem().SetId("baseboard_serial", "To be filled by O.E.M.").SetId("asset_tag", "Default string").SetId("serial", "PF2ABCDE");
            Assert.Equal("UNKNOWN", item.Get("baseboard_serial"));
            Assert.Equal("UNKNOWN", item.Get("asset_tag"));
            Assert.Equal("PF2ABCDE", item.Get("serial"));
        }

        [Fact]
        public void HardwareSignature_IgnoresMeasurements_ButDetectsAReplacedPart()
        {
            var a = Inventory(temp: "39");
            var hotter = Inventory(temp: "55");
            Assert.Equal(a.HardwareSignature(), hotter.HardwareSignature());                    // temperature is not identity

            var swapped = Inventory("WD SN740 1TB", "22461A800999");
            Assert.NotEqual(a.HardwareSignature(), swapped.HardwareSignature());
            var change = Assert.Single(TechnicalInventory.DescribeChanges(a, swapped));
            Assert.Equal("Storage replaced: Samsung PM981 512GB (S4EWNX0M123) → WD SN740 1TB (22461A800999)", change);
        }

        [Fact]
        public void Inventory_RoundTripsThroughJson_WithSourcesAndConfidence()
        {
            var back = TechnicalInventory.FromJson(Inventory().ToJson());
            Assert.Equal(4, back.Items.Count);
            var ssd = back.Of("storage").Single();
            Assert.Equal("NVMe Health Log", ssd.Source);
            Assert.Equal(DataConfidence.Measured, ssd.Confidence);
            Assert.Equal("3421", ssd.Get("power_on_hours"));
            Assert.Null(TechnicalInventory.FromJson("not json"));
        }

        // ------------------------------------------------------------------ ITMS payload

        private (QcRepository repo, DeviceRecord device) Store(string folder = null)
        {
            var open = QcRepository.Open(folder ?? _dir, new FakeMachine(5), QcStoreTests.LaptopA);
            Assert.True(open.Ok, open.Message);
            var repo = open.Repository;
            var match = repo.FindDevice(QcStoreTests.LaptopA);
            return (repo, match.Status == MatchStatus.Matched ? match.Device : repo.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs()));
        }

        private static void FullPass(QcRepository repo, DeviceRecord d)
        {
            var req = QcRequirements.Default();
            foreach (var t in req.Required(Departments.Hardware)) repo.RecordTest(d.DeviceId, t, t, "PASS", "Kamal", Departments.Hardware, operatorId: "kamal");
            repo.RecordTest(d.DeviceId, LifecycleEvents.HardwareHandover, "Handover", "PASS", "Kamal", Departments.Hardware, operatorId: "kamal");
            System.Threading.Thread.Sleep(15);
            foreach (var t in req.Required(Departments.Software)) repo.RecordTest(d.DeviceId, t, t, "PASS", "Shadir", Departments.Software, operatorId: "shadir");
            repo.RecordTest(d.DeviceId, LifecycleEvents.SoftwareHandover, "Handover", "PASS", "Shadir", Departments.Software, operatorId: "shadir");
            System.Threading.Thread.Sleep(15);
            foreach (var t in req.Required(Departments.Qc)) repo.RecordTest(d.DeviceId, t, t, "PASS", "Jimmy", Departments.Qc, operatorId: "jimmy");
            System.Threading.Thread.Sleep(15);
            repo.RecordTest(d.DeviceId, LifecycleEvents.QcDecision, "Final QC Decision", "PASS", "Jimmy", Departments.Qc, details: "basis", operatorId: "jimmy");
        }

        [Fact]
        public void Traceability_ContainsIdentityInventoryHistoryAndDecision()
        {
            var (repo, d) = Store();
            repo.RecordTest(d.DeviceId, "Battery", "Battery", "FAIL", "Kamal", Departments.Hardware, "Health 54%", operatorId: "kamal");
            System.Threading.Thread.Sleep(15);
            repo.RecordTest(d.DeviceId, "Repair:Battery", "Battery", "PASS", "Kamal", Departments.Hardware, "Battery replaced", operatorId: "kamal");
            System.Threading.Thread.Sleep(15);
            repo.RecordTest(d.DeviceId, LifecycleEvents.DeviceSnapshot, "Technical snapshot", "PASS", "Kamal", Departments.Hardware, "sig", Inventory().ToJson(), operatorId: "kamal");
            FullPass(repo, d);

            string json = JsonWriter.Serialize(ItmsTraceability.Build(d, repo.GetDeviceHistory(d.DeviceId)), false);

            Assert.Contains("\"bbtech_device_id\":\"" + d.DeviceId + "\"", json);
            Assert.Contains("\"status\":\"READY_FOR_ECOMMERCE\"", json);
            Assert.Contains("\"ready_for_itms\":true", json);
            Assert.Contains("\"percentage_used\":{\"value\":\"7\",\"unit\":\"percent\",\"source\":\"NVMe Health Log\",\"confidence\":\"MEASURED\"}", json); // raw value + unit + source
            Assert.Contains("\"cycle_count\":{\"value\":\"UNKNOWN\",\"source\":\"NOT_REPORTED (the source returned no value)\",\"confidence\":\"UNKNOWN\",\"reason\":\"NOT_REPORTED (the source returned no value)\"}", json);   // never fabricated, always with a reason
            Assert.Contains("\"capacity_gb\":{\"value\":\"16\",\"unit\":\"GB\"", json);
            Assert.Contains("Battery replaced", json);                             // repair history
            Assert.Contains("\"fail_count\":1", json);
            Assert.Contains("\"decided_by\":\"Jimmy\"", json);
            Assert.Contains("\"operator_id\":\"shadir\"", json);
            Assert.DoesNotContain("\"test_id\":\"DEVICE_SNAPSHOT\"", json.Substring(json.IndexOf("\"hardware_tests\"")).Split(new[] { "\"repairs\"" }, StringSplitOptions.None)[0]);
        }

        [Fact]
        public void Traceability_WithoutSnapshot_SaysUnknown()
        {
            var (repo, d) = Store();
            string json = JsonWriter.Serialize(ItmsTraceability.Build(d, repo.GetDeviceHistory(d.DeviceId)), false);
            Assert.Contains("\"technical_inventory_available\":false", json);
            Assert.Contains("\"cpu\":\"UNKNOWN\"", json);                       // no snapshot: UNKNOWN, not empty
            Assert.Contains("\"current_decision\":\"NONE\"", json);
        }

        [Fact]
        public async Task ItmsPayload_CarriesTheTraceabilityRecord()
        {
            var (repo, d) = Store();
            repo.RecordTest(d.DeviceId, LifecycleEvents.DeviceSnapshot, "Technical snapshot", "PASS", "Kamal", Departments.Hardware, "sig", Inventory().ToJson());
            FullPass(repo, d);
            var handler = new MockHttpMessageHandler();
            var service = new ItmsTransmissionService(repo, new HttpItmsConnector(() => "http://localhost:8000", () => "key", handler));

            var outcome = await service.SendOrContinueAsync(d);

            Assert.True(outcome.Confirmed, outcome.Message);
            Assert.Contains("bbtech_traceability", handler.LastRequestBody);
            Assert.Contains("S4EWNX0M123", handler.LastRequestBody);              // the exact physical SSD
            Assert.Contains("READY_FOR_ECOMMERCE", handler.LastRequestBody);
        }

        // ------------------------------------------------------------------ history survives a Windows reinstall

        [Fact]
        public void History_IsRestoredFromTheUsb_AfterTheLaptopStoreWasWiped_WithTheSameDeviceId()
        {
            string laptopDisk = Path.Combine(_dir, "laptop"), usb = Path.Combine(_dir, "usb", "carry");
            var (repo, d) = Store(laptopDisk);
            Assert.StartsWith("bbt-", d.DeviceId);                                 // stable id from the hardware identity
            Assert.Equal(d.DeviceId, QcRepository.StableDeviceId(QcStoreTests.LaptopA));
            foreach (var t in QcRequirements.Default().Required(Departments.Hardware)) repo.RecordTest(d.DeviceId, t, t, "PASS", "Kamal", Departments.Hardware, operatorId: "kamal");
            repo.RecordTest(d.DeviceId, LifecycleEvents.HardwareHandover, "Handover", "PASS", "Kamal", Departments.Hardware, operatorId: "kamal");
            HistoryCarrier.Save(usb, QcStoreTests.LaptopA, d.DeviceId, repo.GetDeviceHistory(d.DeviceId));
            int count = repo.GetDeviceHistory(d.DeviceId).Count;
            Assert.DoesNotContain("Kamal", File.ReadAllText(HistoryCarrier.FileFor(usb, QcStoreTests.LaptopA)));  // encrypted on the USB

            // Clean Windows install: the laptop's local store is gone.
            Directory.Delete(laptopDisk, true);
            var (fresh, _) = (QcRepository.Open(laptopDisk, new FakeMachine(9), QcStoreTests.LaptopA).Repository, 0);
            Assert.Equal(MatchStatus.NotFound, fresh.FindDevice(QcStoreTests.LaptopA).Status);

            var carried = HistoryCarrier.Load(usb, QcStoreTests.LaptopA);
            Assert.NotNull(carried);
            var again = fresh.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs(), null, carried.DeviceId);
            Assert.Equal(d.DeviceId, again.DeviceId);
            Assert.Equal(count, fresh.ImportHistory(again.DeviceId, carried.Records));
            Assert.Equal(0, fresh.ImportHistory(again.DeviceId, carried.Records));   // idempotent
            var state = LaptopLifecycle.Evaluate(fresh.GetDeviceHistory(again.DeviceId));
            Assert.Equal(LifecycleStatus.ReadyForSoftware, state.Status);           // Hardware's work is still there
            Assert.Equal("kamal", fresh.GetLatestTestResult(again.DeviceId, "Battery", Departments.Hardware).OperatorId);
        }

        [Fact]
        public void CarryFile_OfAnotherLaptop_OrTampered_IsNotUsed()
        {
            string usb = Path.Combine(_dir, "carry");
            HistoryCarrier.Save(usb, QcStoreTests.LaptopA, "bbt-a", new List<TestResultRecord> { new TestResultRecord { TestResultId = "1", TestId = "Battery", Status = "PASS" } });
            var other = new DeviceIdentity("Dell", "Latitude", "5420", "OTHER999", "99999999-2222-3333-4444-555555555555", "BRD-Z");
            Assert.Null(HistoryCarrier.Load(usb, other));

            string file = HistoryCarrier.FileFor(usb, QcStoreTests.LaptopA);
            var bytes = File.ReadAllBytes(file); bytes[bytes.Length - 3] ^= 0x55; File.WriteAllBytes(file, bytes);
            Assert.Null(HistoryCarrier.Load(usb, QcStoreTests.LaptopA));
        }

        // ------------------------------------------------------------------ Windows installation verdict

        [Fact]
        public void WindowsInstall_IsVerifiedFromTheInstallDate_NotFromHavingStarted()
        {
            var started = new DateTime(2026, 11, 25, 10, 0, 0);
            var oldWindows = new WindowsInfo { Edition = "Windows 11 Pro", Version = "10.0.26200", Build = "26200", Architecture = "64-bit", InstallDate = new DateTime(2026, 8, 18, 13, 0, 0) };
            var newWindows = new WindowsInfo { Edition = "Windows 11 Pro", Version = "10.0.26200", Build = "26200", Architecture = "64-bit", InstallDate = new DateTime(2026, 11, 25, 10, 40, 0) };

            Assert.Equal("FAIL", WindowsInstallCheck.Evaluate(started, oldWindows).Status);                // started, but Windows was never reinstalled
            Assert.Contains("not reinstalled", WindowsInstallCheck.Evaluate(started, oldWindows).Note);
            Assert.Equal("PASS", WindowsInstallCheck.Evaluate(started, newWindows).Status);
            Assert.Contains("build 26200", WindowsInstallCheck.Evaluate(started, newWindows).Note);
            Assert.Equal("PASS", WindowsInstallCheck.Evaluate(null, oldWindows).Status);                   // no install started: existing Windows kept, and said so
            Assert.Contains("Existing Windows kept", WindowsInstallCheck.Evaluate(null, oldWindows).Note);
            Assert.Equal("FAIL", WindowsInstallCheck.Evaluate(started, new WindowsInfo()).Status);         // unreadable = not verified
        }

        [Fact]
        public void Drivers_PairedBluetoothAccessories_AreNotLaptopProblems()
        {
            var report = DriverHealth.Evaluate(new[]
            {
                new PnpDeviceInfo { Name = "Bluetooth HID Device", ErrorCode = 10, PnpDeviceId = @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D\8&1" },
                new PnpDeviceInfo { Name = "Intel(R) Wi-Fi 6 AX201", ErrorCode = 0, PnpDeviceId = @"PCI\VEN_8086&DEV_A0F0" },
            });
            Assert.True(report.Ok);
        }
    }
}
