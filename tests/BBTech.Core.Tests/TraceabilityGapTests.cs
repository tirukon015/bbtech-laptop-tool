using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BBTech.Core.Export;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using BBTech.Core.Sync;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Gaps closed in the ITMS traceability audit: measured health, sources/units, part changes, QC-vs-delivery hardware, verified ITMS transfer.</summary>
    public sealed class TraceabilityGapTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-gap-" + Guid.NewGuid().ToString("N"));
        public TraceabilityGapTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        // ------------------------------------------------------------------ SSD health: raw first, verdict by rule

        [Fact]
        public void StorageHealth_WithoutRawValues_IsUnknown_NeverGood()
        {
            var v = StorageHealthRules.Evaluate(new Dictionary<string, string>());
            Assert.Equal("UNKNOWN", v.Status);
        }

        [Theory]
        [InlineData("4", "0", "0", "100", "10", "HEALTHY")]
        [InlineData("84", "0", "0", "100", "10", "WARNING")]       // wear >= 80
        [InlineData("4", "0", "3", "100", "10", "WARNING")]        // media errors
        [InlineData("4", "4", "0", "100", "10", "CRITICAL")]       // NVMe critical warning
        [InlineData("4", "0", "0", "5", "10", "CRITICAL")]         // spare below threshold
        [InlineData("100", "0", "0", "100", "10", "CRITICAL")]     // worn out
        public void StorageHealth_NvmeRules(string used, string crit, string media, string spare, string thr, string expected)
        {
            var raw = new Dictionary<string, string>
            {
                ["percentage_used"] = used, ["critical_warning"] = crit, ["media_and_data_integrity_errors"] = media,
                ["available_spare_percent"] = spare, ["available_spare_threshold_percent"] = thr, ["composite_temperature_k"] = "312",
            };
            var v = StorageHealthRules.Evaluate(raw);
            Assert.Equal(expected, v.Status);
            Assert.Contains(v.Reasons, r => r.Length > 0);
            Assert.Equal(StorageHealthRules.RuleDescription, v.Rule);
        }

        [Fact]
        public void StorageHealth_SataPendingSectors_AreAWarning_LifeRemainingBecomesWear()
        {
            var v = StorageHealthRules.Evaluate(new Dictionary<string, string> { ["current_pending_sector_count"] = "2", ["life_remaining_percent"] = "95" });
            Assert.Equal("WARNING", v.Status);
            Assert.Equal("CRITICAL", StorageHealthRules.Evaluate(new Dictionary<string, string> { ["life_remaining_percent"] = "0" }).Status);
        }

        // ------------------------------------------------------------------ units, sources, derived values, reasons

        [Fact]
        public void Values_CarryUnits_Sources_Calculations_AndReasons()
        {
            var item = new InventoryItem { Category = "storage", Source = "WMI", Confidence = DataConfidence.Detected }
                .Set("capacity_bytes", 512110190592L)
                .SetFrom("raw.percentage_used", 4, "NVMe SMART / Health Information log", DataConfidence.Measured)
                .SetCalculated("derived_remaining_life_percent", 96, "100 - percentage_used", "percent")
                .SetUnknown("raw.unsafe_shutdowns", UnknownReason.AdminRequired)
                .Set("battery_design_capacity_mwh", 51000);

            Assert.Equal("bytes", item.Values.Single(v => v.Key == "capacity_bytes").Unit);
            var used = item.Values.Single(v => v.Key == "raw.percentage_used");
            Assert.Equal("percent", used.Unit);
            Assert.Equal(DataConfidence.Measured, used.Confidence);
            var derived = item.Values.Single(v => v.Key == "derived_remaining_life_percent");
            Assert.Equal(DataConfidence.Calculated, derived.Confidence);
            Assert.Equal("100 - percentage_used", derived.Calculation);
            var unknown = item.Values.Single(v => v.Key == "raw.unsafe_shutdowns");
            Assert.Equal("UNKNOWN", unknown.Value);
            Assert.Equal(UnknownReason.AdminRequired, unknown.Reason);
            Assert.Equal("mWh", item.Values.Single(v => v.Key == "battery_design_capacity_mwh").Unit);
        }

        // ------------------------------------------------------------------ store + lifecycle helpers

        private (QcRepository repo, DeviceRecord d) Store()
        {
            var open = QcRepository.Open(_dir, new FakeMachine(11), QcStoreTests.LaptopA);
            Assert.True(open.Ok, open.Message);
            return (open.Repository, open.Repository.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs()));
        }

        private static TechnicalInventory Snapshot(string ssdSerial, string stage)
        {
            var inv = new TechnicalInventory { CapturedAt = DateTime.UtcNow, ToolVersion = "1.6.0", Stage = stage };
            inv.Add("baseboard", "LENOVO 20W0", "WMI Win32_BaseBoard", DataConfidence.Detected, "L1HF0000").SetId("serial_number", "L1HF0000").SetId("version", "SDK0J40697 WIN");
            inv.Add("storage", "Micron MTFDKBA512TFK", "WMI", DataConfidence.Detected, ssdSerial).SetId("serial_number", ssdSerial)
               .Set("raw_metrics_available", "true").SetFrom("raw.percentage_used", 4, "NVMe SMART / Health Information log (Windows IOCTL)", DataConfidence.Measured)
               .SetFrom("raw.media_and_data_integrity_errors", 0, "NVMe SMART / Health Information log (Windows IOCTL)", DataConfidence.Measured)
               .SetCalculated("health_status", "HEALTHY", "StorageHealthRules: no threshold reached");
            inv.Add("display", "AUO 403D", "EDID", DataConfidence.Detected, "AUO403D|0").Set("connection_type", "internal (laptop panel)");
            return inv;
        }

        private static void Rec(QcRepository repo, DeviceRecord d, string dept, string id, string status, string by, string notes = null, string details = null)
        {
            System.Threading.Thread.Sleep(12);
            repo.RecordTest(d.DeviceId, id, id, status, by, dept, notes, details, operatorId: by.ToLowerInvariant());
        }

        private static void ToQcPass(QcRepository repo, DeviceRecord d)
        {
            var req = QcRequirements.Default();
            foreach (var t in req.Required(Departments.Hardware)) Rec(repo, d, Departments.Hardware, t, "PASS", "Kamal");
            Rec(repo, d, Departments.Hardware, LifecycleEvents.HardwareHandover, "PASS", "Kamal");
            foreach (var t in req.Required(Departments.Software)) Rec(repo, d, Departments.Software, t, "PASS", "Shadir");
            Rec(repo, d, Departments.Software, LifecycleEvents.SoftwareHandover, "PASS", "Shadir");
            foreach (var t in req.Required(Departments.Qc)) Rec(repo, d, Departments.Qc, t, "PASS", "Jimmy");
            Rec(repo, d, Departments.Qc, LifecycleEvents.QcDecision, "PASS", "Jimmy");
        }

        // ------------------------------------------------------------------ part swapped after QC PASS

        [Fact]
        public void PartSwappedAfterQcPass_CancelsThePass_AndIsShownWithOldAndNew()
        {
            var (repo, d) = Store();
            var before = Snapshot("SSD-A-111", "QC_START");
            Rec(repo, d, Departments.Hardware, LifecycleEvents.DeviceSnapshot, "PASS", "Kamal", before.HardwareSignature(), before.ToJson());
            ToQcPass(repo, d);
            Assert.True(LaptopLifecycle.Evaluate(repo.GetDeviceHistory(d.DeviceId)).ReadyForItms);

            // Later the SSD is different.
            var after = Snapshot("SSD-B-222", "FINAL_CHECK");
            var changes = TechnicalInventory.StructuredChanges(before, after);
            var change = Assert.Single(changes);
            Assert.Equal("Storage", change.Component);
            Assert.Equal("replaced", change.Change);
            Assert.Contains("SSD-A-111", change.Old);
            Assert.Contains("SSD-B-222", change.New);
            change.Reason = "detected automatically between snapshots";
            Rec(repo, d, Departments.Qc, LifecycleEvents.HardwareChanged, "PASS", "Jimmy", change.Describe(),
                System.Text.Encoding.UTF8.GetString(Json.ToBytes(changes)));
            Rec(repo, d, Departments.Qc, LifecycleEvents.DeviceSnapshot, "PASS", "Jimmy", after.HardwareSignature(), after.ToJson());

            var state = LaptopLifecycle.Evaluate(repo.GetDeviceHistory(d.DeviceId));
            Assert.False(state.ReadyForItms);                                          // QC must verify again
            Assert.NotNull(state.HardwareChangedAfterQc);
            Assert.Contains("Hardware changed after QC PASS", state.Gate.BlockingReasons[0]);
            Assert.Contains("needs a QC PASS", LaptopLifecycle.CheckEvent(state, Departments.Qc, LifecycleEvents.DeliveryDecision, "PASS"));

            // The payload shows old and new, and QC-time vs current hardware.
            string json = JsonWriter.Serialize(ItmsTraceability.Build(d, repo.GetDeviceHistory(d.DeviceId)), false);
            Assert.Contains("\"component\":\"Storage\",\"change\":\"replaced\"", json);
            Assert.Contains("SSD-A-111", json);
            Assert.Contains("\"changed_since_qc_pass\":true", json);
            Assert.Contains("\"hardware_changed_after_qc_pass\":true", json);

            // QC re-verifies the new hardware and passes again: ready again, and the old SSD stays in history.
            Rec(repo, d, Departments.Qc, "Storage", "PASS", "Jimmy", "New SSD checked");
            Rec(repo, d, Departments.Qc, LifecycleEvents.QcDecision, "PASS", "Jimmy");
            Assert.True(LaptopLifecycle.Evaluate(repo.GetDeviceHistory(d.DeviceId)).ReadyForItms);
            Assert.Equal(2, ItmsTraceability.Snapshots(repo.GetDeviceHistory(d.DeviceId)).Count);
        }

        [Fact]
        public void Payload_HasTheRequestedTree_WithStorageSplitIntoDeviceHealthAndRawMetrics()
        {
            var (repo, d) = Store();
            var inv = Snapshot("SSD-A-111", "QC_DECISION");
            Rec(repo, d, Departments.Hardware, LifecycleEvents.DeviceSnapshot, "PASS", "Kamal", inv.HardwareSignature(), inv.ToJson());
            string json = JsonWriter.Serialize(ItmsTraceability.Build(d, repo.GetDeviceHistory(d.DeviceId)), false);

            foreach (var section in new[] { "identity", "motherboard", "bios", "cpu", "memory", "storage", "battery", "gpu", "display", "network", "audio", "camera",
                                            "usb", "bluetooth", "tpm", "security", "windows", "office", "drivers", "hardware_tests", "software", "qc", "lifecycle",
                                            "hardware_changes", "snapshots", "qc_pass_vs_current_hardware" })
                Assert.Contains("\"" + section + "\":", json);
            Assert.Contains("\"device\":{\"name\":\"Micron MTFDKBA512TFK\"", json);
            Assert.Contains("\"raw_metrics\":{\"percentage_used\":{\"value\":\"4\",\"unit\":\"percent\",\"source\":\"NVMe SMART / Health Information log (Windows IOCTL)\",\"confidence\":\"MEASURED\"}", json);
            Assert.Contains("\"health_status\":{\"value\":\"HEALTHY\",\"source\":\"calculated\",\"confidence\":\"CALCULATED\",\"calculation\":\"StorageHealthRules", json);
            Assert.Contains("\"motherboard\":{\"name\":\"LENOVO 20W0\"", json);
            Assert.Contains("\"bios\":\"NOT_PRESENT\"", json);                   // not in this test snapshot: NOT_PRESENT, not invented
            Assert.Contains("\"stage\":\"QC_DECISION\"", json);
        }

        // ------------------------------------------------------------------ ITMS transfer is verified by content hash

        private sealed class EchoHandler : HttpMessageHandler
        {
            public Func<string, string> Reply;
            public string Body;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
            {
                Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(Reply(Body)) };
            }
        }

        private static string Sent(string body) =>
            System.Text.RegularExpressions.Regex.Match(body, "\"traceability_sha256\"\\s*:\\s*\"([a-f0-9]{64})\"").Groups[1].Value;

        [Theory]
        [InlineData("echo", "VERIFIED", true)]
        [InlineData("none", "UNCONFIRMED", true)]
        [InlineData("other", "MISMATCH", false)]
        public async Task ItmsTransfer_IsVerifiedAgainstTheTraceabilityHash(string mode, string expected, bool confirmed)
        {
            var (repo, d) = Store();
            ToQcPass(repo, d);
            var handler = new EchoHandler
            {
                Reply = body =>
                {
                    string echo = mode == "echo" ? Sent(body) : mode == "other" ? new string('a', 64) : null;
                    return "{\"success\":true,\"status\":\"queued\",\"request_no\":3001" + (echo == null ? "" : ",\"traceability_sha256\":\"" + echo + "\"") + "}";
                },
            };
            var service = new ItmsTransmissionService(repo, new HttpItmsConnector(() => "http://localhost:8000", () => "key", handler));

            var outcome = await service.SendOrContinueAsync(d);

            Assert.Equal(64, Sent(handler.Body).Length);                                // the hash is always sent
            Assert.Equal(expected, outcome.TraceabilityVerification);
            Assert.Equal(confirmed, outcome.Confirmed);
            if (mode == "other") Assert.Contains("hash mismatch", outcome.Record.LastError);
            if (mode == "echo") Assert.Contains("traceability VERIFIED", outcome.Record.RemoteId);
        }

        // ------------------------------------------------------------------ evidence on the USB is tamper-evident

        [Fact]
        public void EditedSnapshotOnTheUsb_IsDetected()
        {
            var item = SyncQueueItem.FromTestResult("DEV", "kamal", "Kamal", "Hardware", LifecycleEvents.DeviceSnapshot, "Snapshot", "PASS", "sig", "1.6.0", "R1");
            item.Details = Snapshot("SSD-A-111", "PERIODIC").ToJson();
            item.Checksum = item.ComputeChecksum();
            Assert.True(item.VerifyIntegrity());

            item.Details = item.Details.Replace("SSD-A-111", "SSD-FAKE-9");             // someone edits the evidence
            Assert.False(item.VerifyIntegrity());
        }

        [Fact]
        public void TechnicianStoragePass_NeverChangesTheMeasuredSsdHealth()
        {
            var (repo, d) = Store();
            var inv = Snapshot("SSD-A-111", "QC_START");
            inv.Items.Single(i => i.Category == "storage").Values.RemoveAll(v => v.Key == "health_status");
            inv.Items.Single(i => i.Category == "storage").SetCalculated("health_status", "CRITICAL", "StorageHealthRules: NVMe critical warning bits set (4)");
            Rec(repo, d, Departments.Hardware, LifecycleEvents.DeviceSnapshot, "PASS", "Kamal", inv.HardwareSignature(), inv.ToJson());
            Rec(repo, d, Departments.Hardware, "Storage", "PASS", "Kamal", "SSD looks GOOD");          // a person's opinion

            string json = JsonWriter.Serialize(ItmsTraceability.Build(d, repo.GetDeviceHistory(d.DeviceId)), false);
            Assert.Contains("\"health_status\":{\"value\":\"CRITICAL\",\"source\":\"calculated\",\"confidence\":\"CALCULATED\"", json);
            Assert.DoesNotContain("\"health_status\":{\"value\":\"GOOD\"", json);
        }

        [Fact]
        public void WindowsReinstall_RestoresHistory_AppendsNewEvents_AndNeverReplacesOldOnes()
        {
            string laptopDisk = Path.Combine(_dir, "laptop"), usb = Path.Combine(_dir, "usb", "carry");
            var first = QcRepository.Open(laptopDisk, new FakeMachine(11), QcStoreTests.LaptopA).Repository;
            var d = first.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs());
            var inv = Snapshot("SSD-A-111", "HARDWARE_HANDOVER");
            Rec(first, d, Departments.Hardware, LifecycleEvents.DeviceSnapshot, "PASS", "Kamal", inv.HardwareSignature(), inv.ToJson());
            foreach (var t in QcRequirements.Default().Required(Departments.Hardware)) Rec(first, d, Departments.Hardware, t, "PASS", "Kamal");
            Rec(first, d, Departments.Hardware, LifecycleEvents.HardwareHandover, "PASS", "Kamal");
            var before = first.GetDeviceHistory(d.DeviceId).Select(r => r.TestResultId + "|" + r.TestId + "|" + r.Status + "|" + r.OperatorId + "|" + r.Details).OrderBy(x => x).ToList();
            HistoryCarrier.Save(usb, QcStoreTests.LaptopA, d.DeviceId, first.GetDeviceHistory(d.DeviceId));

            // Windows reinstalled: new machine key, empty local store; the same physical laptop is recognised.
            Directory.Delete(laptopDisk, true);
            var fresh = QcRepository.Open(laptopDisk, new FakeMachine(12), QcStoreTests.LaptopA).Repository;
            var carried = HistoryCarrier.Load(usb, QcStoreTests.LaptopA);
            var again = fresh.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs(), null, carried.DeviceId);
            Assert.Equal(d.DeviceId, again.DeviceId);
            Assert.Equal(before.Count, fresh.ImportHistory(again.DeviceId, carried.Records));

            // New Windows / software work is appended.
            Rec(fresh, again, Departments.Software, "SoftwareWindowsInstall", "PASS", "Shadir", "Windows 11 Pro build 26200 installed");
            var sw = QcRequirements.Default().Required(Departments.Software).ToList();
            foreach (var t in sw) Rec(fresh, again, Departments.Software, t, "PASS", "Shadir");
            Rec(fresh, again, Departments.Software, LifecycleEvents.SoftwareHandover, "PASS", "Shadir");

            // The old carry file is offered again, and a forged copy of an old record too: nothing is replaced.
            Assert.Equal(0, fresh.ImportHistory(again.DeviceId, carried.Records));
            var forged = carried.Records.Where(r => r.TestId == "Battery").Select(r => { var c = Json.FromBytes<TestResultRecord>(Json.ToBytes(r)); c.Status = "FAIL"; c.OperatorId = "someone"; return c; }).ToList();
            Assert.Equal(0, fresh.ImportHistory(again.DeviceId, forged));

            var after = fresh.GetDeviceHistory(again.DeviceId);
            var oldPart = after.Where(r => r.Department == Departments.Hardware).Select(r => r.TestResultId + "|" + r.TestId + "|" + r.Status + "|" + r.OperatorId + "|" + r.Details).OrderBy(x => x).ToList();
            Assert.Equal(before, oldPart);                                                         // old history byte-for-byte the same
            Assert.Equal(before.Count + sw.Count + 2, after.Count);
            Assert.Equal(LifecycleStatus.ReadyForQc, LaptopLifecycle.Evaluate(after).Status);
            Assert.Single(ItmsTraceability.Snapshots(after));                                      // hardware evidence survived
        }

        [Fact]
        public void BuiltInPanelOff_IsNotAHardwareChange_ButADifferentPanelIs()
        {
            var open = Snapshot("SSD-A-111", "QC_DECISION");
            var docked = Snapshot("SSD-A-111", "FINAL_CHECK");
            docked.Items.RemoveAll(i => i.Category == "display");
            docked.Add("display", "Built-in display", "EDID", DataConfidence.Unknown).SetUnknown("panel_id", "INTERNAL_PANEL_NOT_ACTIVE");

            Assert.Empty(TechnicalInventory.StructuredChanges(open, docked));          // lid closed: nothing was replaced
            Assert.Equal(open.HardwareSignature(), docked.HardwareSignature());

            var newPanel = Snapshot("SSD-A-111", "FINAL_CHECK");
            newPanel.Items.RemoveAll(i => i.Category == "display");
            newPanel.Add("display", "BOE 0A2B", "EDID", DataConfidence.Detected, "BOE0A2B|0");
            var change = Assert.Single(TechnicalInventory.StructuredChanges(open, newPanel));
            Assert.Equal("Display", change.Component);
            Assert.Equal("replaced", change.Change);
        }

        [Theory]
        [InlineData("raw.data_units_read", "NVMe data unit (1 = 512,000 bytes)")]
        [InlineData("raw.unsafe_shutdowns", "count")]
        [InlineData("raw.composite_temperature_k", "K")]
        [InlineData("raw.power_on_hours", "hours")]
        [InlineData("raw.percentage_used", "percent")]
        public void RawDriveValues_HaveTheirUnits(string key, string unit) => Assert.Equal(unit, InventoryUnits.For(key));

        [Fact]
        public void BatteryHealth_IsCalculatedWithTwoDecimals_FromRealCapacities()
        {
            // 45,160 / 51,000 x 100 = 88.549... -> 88.55 (the formula is stored with the value)
            double health = Math.Round(45160 * 100.0 / 51000, 2);
            Assert.Equal(88.55, health);
            var item = new InventoryItem().SetCalculated("health_percent", health, "full_charge_capacity_mwh / design_capacity_mwh x 100", "percent");
            Assert.Equal("88.55", item.Get("health_percent"));
            Assert.Equal(DataConfidence.Calculated, item.Values[0].Confidence);
        }
    }
}
