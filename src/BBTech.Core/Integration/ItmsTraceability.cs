using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BBTech.Core.Export;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using BBTech.Core.Testing;

namespace BBTech.Core.Integration
{
    /// <summary>
    /// The "bbtech_traceability" section sent to ITMS with every laptop:
    ///
    ///   identity, motherboard, bios, cpu, memory{summary, modules}, storage[{device, health, raw_metrics}], battery,
    ///   gpu{internal, external}, display{internal, external}, network{physical, external, virtual}, audio, camera,
    ///   usb{controllers, port_tests}, bluetooth, tpm, security, windows, office, drivers, hardware_tests, repairs,
    ///   software, qc, lifecycle, hardware_changes, snapshots
    ///
    /// Every technical value is { value, unit, source, confidence[, calculation][, reason] }. Built only from
    /// stored records: nothing is invented. Missing = UNKNOWN (with reason), absent hardware = NOT_PRESENT.
    /// </summary>
    public static class ItmsTraceability
    {
        public const string Schema = "bbtech-traceability/2";
        private static string Iso(DateTime? t) => t.HasValue ? t.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : null;
        private static string Or(string v) => string.IsNullOrWhiteSpace(v) ? InventoryItem.UnknownValue : v;

        public static List<TestResultRecord> Snapshots(IEnumerable<TestResultRecord> history) =>
            (history ?? Enumerable.Empty<TestResultRecord>()).Where(r => r != null && !r.Deleted && r.TestId == LifecycleEvents.DeviceSnapshot)
                .OrderBy(r => r.CompletedAt ?? r.StartedAt ?? DateTime.MinValue).ToList();

        /// <summary>The latest stored technical snapshot of the laptop, or null when none was captured yet.</summary>
        public static TechnicalInventory LatestInventory(IEnumerable<TestResultRecord> history) =>
            Snapshots(history).Select(r => TechnicalInventory.FromJson(r.Details)).LastOrDefault(i => i != null);

        /// <summary>SHA-256 of the compact JSON: sent with the payload so ITMS can confirm it stored exactly this.</summary>
        public static string Sha256(JsonObject traceability)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonWriter.Serialize(traceability, false))).Select(b => b.ToString("x2")));
        }

        // ------------------------------------------------------------------ value / item rendering

        private static JsonObject Value(InventoryItem item, InventoryValue v)
        {
            var o = new JsonObject();
            o.Add("value", v.Value);
            if (!string.IsNullOrEmpty(v.Unit)) o.Add("unit", v.Unit);
            o.Add("source", v.Value == InventoryItem.UnknownValue ? (v.Reason ?? UnknownReason.NotReported) : (v.Source ?? item.Source ?? ""));
            o.Add("confidence", v.Value == InventoryItem.UnknownValue ? DataConfidence.Unknown : (v.Confidence ?? item.Confidence ?? DataConfidence.Detected));
            if (!string.IsNullOrEmpty(v.Calculation)) o.Add("calculation", v.Calculation);
            if (!string.IsNullOrEmpty(v.Reason)) o.Add("reason", v.Reason);
            else if (v.Value == InventoryItem.UnknownValue) o.Add("reason", UnknownReason.NotReported);     // snapshots taken before 1.6.0
            return o;
        }

        private static JsonObject Item(InventoryItem i, Func<InventoryValue, bool> filter = null)
        {
            var o = new JsonObject();
            o.Add("name", i.Name);
            if (string.IsNullOrEmpty(i.IdentityKey))
            {
                var unknownId = new JsonObject();
                unknownId.Add("value", InventoryItem.UnknownValue); unknownId.Add("reason", UnknownReason.NoUniqueId);
                o.Add("identity", unknownId);
            }
            else o.Add("identity", i.IdentityKey);
            o.Add("source", i.Source ?? "");
            foreach (var v in i.Values.Where(filter ?? (_ => true))) o.Add(v.Key.StartsWith("raw.", StringComparison.Ordinal) ? v.Key.Substring(4) : v.Key, Value(i, v));
            return o;
        }

        /// <summary>One item → object; several → list; none → NOT_PRESENT (or UNKNOWN when no snapshot exists).</summary>
        private static object Section(TechnicalInventory inv, params string[] categories)
        {
            if (inv == null) return InventoryItem.UnknownValue;
            var items = categories.SelectMany(inv.Of).ToList();
            if (items.Count == 0) return "NOT_PRESENT";
            return items.Count == 1 ? (object)Item(items[0]) : items.Select(i => (object)Item(i)).ToList();
        }

        private static object List(TechnicalInventory inv, string category)
        {
            if (inv == null) return InventoryItem.UnknownValue;
            var items = inv.Of(category).ToList();
            return items.Count == 0 ? (object)"NOT_PRESENT" : items.Select(i => (object)Item(i)).ToList();
        }

        private static object Storage(TechnicalInventory inv)
        {
            if (inv == null) return InventoryItem.UnknownValue;
            var drives = inv.Of("storage").ToList();
            if (drives.Count == 0) return "NOT_PRESENT";
            string[] healthKeys = { "health_status", "derived_remaining_life_percent", "percentage_used", "power_on_hours", "temperature_c", "engine_health_percent",
                                    "windows_health_status", "raw_metrics_available", "raw_metrics_source", "raw_metrics" };
            return drives.Select(d =>
            {
                var o = new JsonObject();
                o.Add("device", Item(d, v => !v.Key.StartsWith("raw.", StringComparison.Ordinal) && !healthKeys.Contains(v.Key)));
                o.Add("health", Item(d, v => healthKeys.Contains(v.Key)));
                var raw = new JsonObject();
                foreach (var v in d.Values.Where(v => v.Key.StartsWith("raw.", StringComparison.Ordinal))) raw.Add(v.Key.Substring(4), Value(d, v));
                o.Add("raw_metrics", raw.Any() ? (object)raw : d.Get("raw_metrics") == null ? (object)InventoryItem.UnknownValue : Value(d, d.Values.First(v => v.Key == "raw_metrics")));
                return (object)o;
            }).ToList();
        }

        // ------------------------------------------------------------------ build

        public static JsonObject Build(DeviceRecord device, IEnumerable<TestResultRecord> history, QcRequirements requirements = null)
        {
            var all = (history ?? Enumerable.Empty<TestResultRecord>()).Where(r => r != null && !r.Deleted)
                        .OrderBy(r => r.CompletedAt ?? r.StartedAt ?? DateTime.MinValue).ToList();
            var state = LaptopLifecycle.Evaluate(all, requirements);
            var snapshots = Snapshots(all);
            var inv = snapshots.Select(r => TechnicalInventory.FromJson(r.Details)).LastOrDefault(i => i != null);
            var root = new JsonObject();
            root.Add("schema", Schema);
            root.Add("generated_at", Iso(DateTime.UtcNow));
            root.Add("technical_inventory_available", inv != null);
            root.Add("inventory_captured_at", Iso(inv?.CapturedAt));
            root.Add("inventory_stage", inv?.Stage ?? InventoryItem.UnknownValue);

            // ---- one permanent record: BBTech identity vs the manufacturer's identifiers
            var id = new JsonObject();
            id.Add("bbtech_device_id", Or(device?.DeviceId));
            id.Add("hardware_fingerprint", Or(device?.HardwareFingerprint));
            id.Add("identity_hashes", new JsonObject { { "serial", Or(device?.SerialHash) }, { "uuid", Or(device?.UuidHash) }, { "baseboard_serial", Or(device?.BoardSerialHash) } });
            id.Add("first_seen", Iso(device?.CreatedAt));
            if (!string.IsNullOrWhiteSpace(device?.IdentityNote)) id.Add("identity_note", device.IdentityNote);
            id.Add("system", Section(inv, "identity"));
            id.Add("chassis", Section(inv, "chassis"));
            root.Add("identity", id);

            root.Add("motherboard", Section(inv, "baseboard"));
            root.Add("bios", Section(inv, "bios"));
            root.Add("cpu", Section(inv, "cpu"));
            root.Add("memory", new JsonObject { { "summary", Section(inv, "memory") }, { "modules", List(inv, "ram_module") } });
            root.Add("storage", Storage(inv));
            root.Add("battery", Section(inv, "battery"));
            root.Add("gpu", new JsonObject { { "internal", List(inv, "gpu") }, { "external", List(inv, "external_display_adapter") } });
            root.Add("display", new JsonObject { { "internal", List(inv, "display") }, { "external", List(inv, "display_external") } });
            root.Add("network", new JsonObject { { "physical", List(inv, "network") }, { "external", List(inv, "network_external") }, { "virtual", Section(inv, "network_virtual") } });
            root.Add("audio", new JsonObject { { "controllers", List(inv, "audio") }, { "endpoints", List(inv, "audio_endpoint") } });
            root.Add("camera", List(inv, "camera"));
            root.Add("bluetooth", List(inv, "bluetooth"));
            root.Add("tpm", Section(inv, "tpm"));
            root.Add("security", new JsonObject
            {
                { "firmware", Section(inv, "firmware") },
                { "virtualization", inv == null ? (object)InventoryItem.UnknownValue : inv.Of("cpu").Select(c => (object)Item(c, v => v.Key.Contains("virtualization") || v.Key == "hypervisor_running" || v.Key == "slat_supported")).ToList() },
            });
            root.Add("windows", Section(inv, "windows"));
            root.Add("office", Section(inv, "office"));
            root.Add("drivers", new JsonObject { { "key_drivers", List(inv, "driver") }, { "problems", Section(inv, "driver_health") } });

            // ---- what people did, in order (failures stay; a retest is a new entry)
            JsonObject Event(TestResultRecord r) => new JsonObject
            {
                { "record_id", r.TestResultId },
                { "at", Iso(r.CompletedAt ?? r.StartedAt) },
                { "department", r.Department ?? "" },
                { "test_id", r.TestId },
                { "test_name", r.TestName ?? QcGate.Label(r.TestId) },
                { "result", r.Status },
                { "operator_id", Or(r.OperatorId) },
                { "operator_name", Or(r.OperatorName) },
                { "notes", r.Comment ?? "" },
                { "evidence", r.Details ?? "" },
                { "source", IsManual(r.TestId) ? "manual_test" : "interactive_test" },
                { "confidence", IsManual(r.TestId) ? DataConfidence.Manual : DataConfidence.Verified },
            };
            List<object> Events(Func<TestResultRecord, bool> filter) => all.Where(filter).Select(r => (object)Event(r)).ToList();

            root.Add("usb", new JsonObject
            {
                { "controllers", List(inv, "usb_controller") },
                // Physical ports are proven only by the port test (a controller existing proves nothing).
                { "port_tests", Events(r => r.TestId == "Usb" && LifecycleEvents.IsPlainTest(r.TestId)) },
            });
            root.Add("hardware_tests", Events(r => r.Department == Departments.Hardware && LifecycleEvents.IsPlainTest(r.TestId)));
            root.Add("repairs", Events(r => LifecycleEvents.IsRepair(r.TestId)));
            root.Add("software", new JsonObject
            {
                { "windows_installation", Events(r => r.TestId == LifecycleEvents.WindowsInstall) },
                { "installations", Events(r => r.Department == Departments.Software && r.TestId != null && r.TestId.Contains("Install") && r.TestId != LifecycleEvents.WindowsInstall) },
                { "activation", Events(r => r.Department == Departments.Software && r.TestId != null && r.TestId.Contains("Activation")) },
                { "checks", Events(r => r.Department == Departments.Software && LifecycleEvents.IsPlainTest(r.TestId) && !r.TestId.Contains("Install") && !r.TestId.Contains("Activation")) },
            });

            var qc = new JsonObject();
            qc.Add("checklist", Events(r => r.Department == Departments.Qc && LifecycleEvents.IsPlainTest(r.TestId)));
            qc.Add("decisions", Events(r => r.TestId == LifecycleEvents.QcDecision));
            qc.Add("current_decision", state.QcDecision?.Status ?? "NONE");
            qc.Add("decided_by", Or(state.QcDecision?.OperatorName));
            qc.Add("decided_at", Iso(state.QcDecision?.CompletedAt));
            qc.Add("basis", state.QcDecision?.Details ?? "");
            qc.Add("reworked_items", state.PreviousIssues.Select(i => (object)new JsonObject
            {
                { "department", i.Department }, { "test_id", i.TestId }, { "test_name", i.TestName }, { "fail_count", i.FailCount },
                { "failed_at", Iso(i.FailedAt) }, { "failed_by", Or(i.FailedBy) }, { "failure", i.FailureNote ?? "" },
                { "repair", string.IsNullOrWhiteSpace(i.RepairNote) ? InventoryItem.UnknownValue : i.RepairNote },
                { "passed_at", Iso(i.PassedAt) }, { "passed_by", Or(i.PassedBy) },
            }).ToList());
            qc.Add("delivery_check", new JsonObject
            {
                { "items", Events(r => LifecycleEvents.IsDeliveryItem(r.TestId)) },
                { "decision", state.DeliveryDecision == null ? "NONE" : state.DeliveryDecision.Status == "PASS" ? "READY_FOR_DELIVERY" : "HOLD" },
                { "order_reference", Or(state.DeliveryDecision?.Details) },
                { "decided_by", Or(state.DeliveryDecision?.OperatorName) },
                { "decided_at", Iso(state.DeliveryDecision?.CompletedAt) },
                { "reason", state.DeliveryDecision?.Comment ?? "" },
            });
            root.Add("qc", qc);

            root.Add("lifecycle", new JsonObject
            {
                { "status", state.Status },
                { "hardware_ready", state.HardwareReady },
                { "software_ready", state.SoftwareReady },
                { "ready_for_itms", state.ReadyForItms },
                { "qc_returns", state.ReturnCount },
                { "hardware_changed_after_qc_pass", state.HardwareChangedAfterQc != null },
                { "events", Events(r => LifecycleEvents.IsStageEvent(r.TestId)) },
            });

            // ---- replaced parts: component / old / new / reason / who / when (old parts stay in history)
            root.Add("hardware_changes", all.Where(r => r.TestId == LifecycleEvents.HardwareChanged).Select(r =>
            {
                List<HardwareChange> parts = null;
                try { if (!string.IsNullOrEmpty(r.Details)) parts = Json.FromBytes<List<HardwareChange>>(Encoding.UTF8.GetBytes(r.Details)); } catch { parts = null; }
                return (object)new JsonObject
                {
                    { "at", Iso(r.CompletedAt ?? r.StartedAt) }, { "detected_by", Or(r.OperatorName) }, { "operator_id", Or(r.OperatorId) },
                    { "summary", r.Comment ?? "" },
                    { "changes", (parts ?? new List<HardwareChange>()).Select(c => (object)new JsonObject
                        { { "component", c.Component }, { "change", c.Change }, { "old", c.Old }, { "new", c.New }, { "reason", c.Reason ?? "" } }).ToList() },
                };
            }).ToList());

            // ---- every snapshot (when, at which lifecycle moment, which hardware) + QC PASS vs now
            root.Add("snapshots", snapshots.Select(r =>
            {
                var i = TechnicalInventory.FromJson(r.Details);
                return (object)new JsonObject
                {
                    { "record_id", r.TestResultId }, { "captured_at", Iso(r.CompletedAt ?? r.StartedAt) }, { "stage", i?.Stage ?? InventoryItem.UnknownValue },
                    { "hardware_signature", i?.HardwareSignature() ?? r.Comment ?? "" }, { "captured_by", Or(r.OperatorName) },
                };
            }).ToList());
            var qcPass = state.QcDecision?.Status == "PASS" ? state.QcDecision : all.LastOrDefault(r => r.TestId == LifecycleEvents.QcDecision && r.Status == "PASS");
            var atQc = qcPass == null ? null : snapshots.Where(s => (s.CompletedAt ?? s.StartedAt) <= (qcPass.CompletedAt ?? qcPass.StartedAt)).Select(s => TechnicalInventory.FromJson(s.Details)).LastOrDefault(x => x != null);
            var afterQc = qcPass == null ? null : snapshots.Where(s => (s.CompletedAt ?? s.StartedAt) > (qcPass.CompletedAt ?? qcPass.StartedAt)).Select(s => TechnicalInventory.FromJson(s.Details)).LastOrDefault(x => x != null);
            var cmp = new JsonObject();
            cmp.Add("qc_pass_at", Iso(qcPass?.CompletedAt));
            cmp.Add("hardware_at_qc_pass", atQc?.HardwareSignature() ?? InventoryItem.UnknownValue);
            cmp.Add("hardware_now", inv?.HardwareSignature() ?? InventoryItem.UnknownValue);
            cmp.Add("changed_since_qc_pass", atQc != null && inv != null ? (object)(TechnicalInventory.StructuredChanges(atQc, inv).Count > 0) : InventoryItem.UnknownValue);
            cmp.Add("changes_since_qc_pass", atQc != null && inv != null ? TechnicalInventory.DescribeChanges(atQc, inv).Cast<object>().ToList() : new List<object>());
            cmp.Add("rechecked_after_qc_pass", afterQc != null);
            root.Add("qc_pass_vs_current_hardware", cmp);
            return root;
        }

        /// <summary>Checks a person performs by looking (not a machine measurement).</summary>
        private static bool IsManual(string testId) =>
            testId == "PhysicalInspection" || testId == "OtherInspection" || (testId != null && (testId.StartsWith("Qc", StringComparison.Ordinal) || testId.StartsWith("Delivery", StringComparison.Ordinal)));
    }
}
