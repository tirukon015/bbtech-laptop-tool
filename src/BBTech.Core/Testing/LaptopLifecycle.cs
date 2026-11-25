using System;
using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Storage;

namespace BBTech.Core.Testing
{
    /// <summary>
    /// Lifecycle events are ordinary append-only records (same journal, same USB queue, same server table)
    /// with reserved test ids, so the whole history of a laptop is one stream that is never overwritten.
    /// </summary>
    public static class LifecycleEvents
    {
        /// <summary>Hardware declares the laptop READY_FOR_SOFTWARE (department Hardware, PASS).</summary>
        public const string HardwareHandover = "LC_HARDWARE_HANDOVER";
        /// <summary>Software declares the laptop READY_FOR_QC (department Software, PASS).</summary>
        public const string SoftwareHandover = "LC_SOFTWARE_HANDOVER";
        /// <summary>QC sends the laptop back (department QC, FAIL). Details = target department, Comment = reason.</summary>
        public const string QcReturn = "LC_QC_RETURN";
        /// <summary>Final QC decision of the preparation (PASS = ready for E-Commerce / ITMS).</summary>
        public const string QcDecision = QcGate.QcDecisionTestId;
        /// <summary>Final check before delivery (department QC): PASS = READY_FOR_DELIVERY, FAIL = HOLD. Details = order reference.</summary>
        public const string DeliveryDecision = "QC_DELIVERY_FINAL";
        /// <summary>"Repair:&lt;TestId&gt;" records what was fixed between a FAIL and its retest.</summary>
        public const string RepairPrefix = "Repair:";
        /// <summary>Windows installation tracking (department Software): TESTING = started, PASS = verified, FAIL.</summary>
        public const string WindowsInstall = "SoftwareWindowsInstall";
        /// <summary>Department used only to group the delivery checklist definitions (records are QC records).</summary>
        public const string DeliveryGroup = "Delivery";
        /// <summary>Technical identity + inventory + measured health at one moment (Details = TechnicalInventory JSON, Comment = hardware signature).</summary>
        public const string DeviceSnapshot = "DEVICE_SNAPSHOT";
        /// <summary>A part was replaced between two snapshots (Comment = what changed).</summary>
        public const string HardwareChanged = "DEVICE_HARDWARE_CHANGED";

        /// <summary>Evidence records: never a test result, never deletable, allowed at any stage.</summary>
        public static bool IsEvidence(string testId) => testId == DeviceSnapshot || testId == HardwareChanged;

        public static bool IsRepair(string testId) => testId != null && testId.StartsWith(RepairPrefix, StringComparison.Ordinal);
        public static bool IsDeliveryItem(string testId) => testId != null && testId.StartsWith("Delivery", StringComparison.Ordinal);
        public static bool IsStageEvent(string testId) =>
            testId == HardwareHandover || testId == SoftwareHandover || testId == QcReturn || testId == QcDecision || testId == DeliveryDecision;
        /// <summary>True for an ordinary test/check result (not a lifecycle event, repair note or delivery item).</summary>
        public static bool IsPlainTest(string testId) => !IsStageEvent(testId) && !IsRepair(testId) && !IsDeliveryItem(testId) && !IsEvidence(testId);
    }

    public static class LifecycleStatus
    {
        public const string Received = "RECEIVED";
        public const string HardwareInProgress = "HARDWARE_IN_PROGRESS";
        public const string HardwareRework = "HARDWARE_REWORK";
        public const string ReadyForSoftware = "READY_FOR_SOFTWARE";
        public const string SoftwareInProgress = "SOFTWARE_IN_PROGRESS";
        public const string SoftwareRework = "SOFTWARE_REWORK";
        public const string ReadyForQc = "READY_FOR_QC";
        public const string QcInProgress = "QC_IN_PROGRESS";
        public const string QcRework = "QC_REWORK";
        public const string ReadyForEcommerce = "READY_FOR_ECOMMERCE";
        public const string FinalCheck = "FINAL_CHECK";
        public const string DeliveryHold = "DELIVERY_HOLD";
        public const string ReadyForDelivery = "READY_FOR_DELIVERY";

        /// <summary>Short label for the technician (few words, no jargon).</summary>
        public static string Label(string status)
        {
            switch (status)
            {
                case Received: return "Received";
                case HardwareInProgress: return "In Hardware";
                case HardwareRework: return "Back in Hardware";
                case ReadyForSoftware: return "Ready for Software";
                case SoftwareInProgress: return "In Software";
                case SoftwareRework: return "Back in Software";
                case ReadyForQc: return "Ready for QC";
                case QcInProgress: return "In QC";
                case QcRework: return "QC failed";
                case ReadyForEcommerce: return "QC passed";
                case FinalCheck: return "Final check";
                case DeliveryHold: return "On hold";
                case ReadyForDelivery: return "Ready for delivery";
                default: return status ?? "";
            }
        }

        /// <summary>Which department has to act now.</summary>
        public static string Owner(string status)
        {
            switch (status)
            {
                case Received: case HardwareInProgress: case HardwareRework: return Departments.Hardware;
                case ReadyForSoftware: case SoftwareInProgress: case SoftwareRework: return Departments.Software;
                case ReadyForEcommerce: case ReadyForDelivery: return Departments.Ecommerce;
                default: return Departments.Qc;
            }
        }
    }

    /// <summary>A test that failed earlier and passed later: QC sees that the laptop was not "always perfect".</summary>
    public sealed class ReworkedItem
    {
        public string Department { get; set; }
        public string TestId { get; set; }
        public string TestName { get; set; }
        public DateTime? FailedAt { get; set; }
        public string FailedBy { get; set; }
        public string FailureNote { get; set; }
        public string RepairNote { get; set; }
        public DateTime? PassedAt { get; set; }
        public string PassedBy { get; set; }
        public int FailCount { get; set; }
    }

    public sealed class TimelineEntry
    {
        public DateTime? At { get; set; }
        public string Department { get; set; }
        /// <summary>TEST, REPAIR, HANDOVER, RETURN, QC_DECISION, DELIVERY</summary>
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Result { get; set; }
        public string Operator { get; set; }
        public string Notes { get; set; }
        public string Evidence { get; set; }
    }

    /// <summary>Where one physical laptop is in the BBTech preparation lifecycle, derived from its history.</summary>
    public sealed class LifecycleState
    {
        public string Status { get; set; } = LifecycleStatus.Received;
        public string Label => LifecycleStatus.Label(Status);
        public string Owner => LifecycleStatus.Owner(Status);
        public bool HardwareReady { get; set; }
        public bool SoftwareReady { get; set; }
        public TestResultRecord HardwareHandover { get; set; }
        public TestResultRecord SoftwareHandover { get; set; }
        /// <summary>A QC return not yet answered by the target department's new handover.</summary>
        public TestResultRecord PendingReturn { get; set; }
        public string PendingReturnTarget => PendingReturn?.Details;
        /// <summary>The QC decision that currently counts (null when superseded by rework).</summary>
        public TestResultRecord QcDecision { get; set; }
        public TestResultRecord DeliveryDecision { get; set; }
        /// <summary>A part changed after the QC PASS: that PASS no longer counts and QC must verify again.</summary>
        public TestResultRecord HardwareChangedAfterQc { get; set; }
        /// <summary>The hardware snapshot taken after the counting QC PASS (required before READY FOR DELIVERY).</summary>
        public TestResultRecord DeliverySnapshot { get; set; }
        public QcGateReport Gate { get; set; }
        public DepartmentStatus DeliveryGate { get; set; }
        public List<ReworkedItem> PreviousIssues { get; set; } = new List<ReworkedItem>();
        /// <summary>How many times QC sent the laptop back.</summary>
        public int ReturnCount { get; set; }
        /// <summary>The single question QC needs answered.</summary>
        public bool ReadyForItms => Status == LifecycleStatus.ReadyForEcommerce || Status == LifecycleStatus.FinalCheck ||
                                    Status == LifecycleStatus.DeliveryHold || Status == LifecycleStatus.ReadyForDelivery;
        public bool AtQcStage => HardwareReady && SoftwareReady;
    }

    /// <summary>
    /// The BBTech laptop lifecycle:
    ///   RECEIVED → Hardware (test / fix / retest) → READY_FOR_SOFTWARE → Software → READY_FOR_QC → QC →
    ///   READY_FOR_ECOMMERCE (ITMS) → final delivery check → READY_FOR_DELIVERY,
    /// with rework loops: QC can return the laptop to Hardware or Software; that department fixes, hands
    /// over again, and the laptop comes back to QC. Nothing is ever "permanently finished": the status is
    /// recomputed from the full event history, so an earlier PASS never hides later problems.
    /// </summary>
    public static class LaptopLifecycle
    {
        private static DateTime T(TestResultRecord r) => r.CompletedAt ?? r.StartedAt ?? DateTime.MinValue;

        public static LifecycleState Evaluate(IEnumerable<TestResultRecord> history, QcRequirements requirements = null)
        {
            var req = requirements != null && requirements.IsValid ? requirements : QcRequirements.Default();
            var all = (history ?? Enumerable.Empty<TestResultRecord>()).Where(r => r != null && !r.Deleted).OrderBy(T).ToList();
            var state = new LifecycleState();

            List<TestResultRecord> Tests(string dept) =>
                all.Where(r => string.Equals(r.Department, dept, StringComparison.OrdinalIgnoreCase) && LifecycleEvents.IsPlainTest(r.TestId)).ToList();
            TestResultRecord Last(string id, Func<TestResultRecord, bool> filter = null) =>
                all.LastOrDefault(r => r.TestId == id && (filter == null || filter(r)));

            var hw = Tests(Departments.Hardware); var sw = Tests(Departments.Software); var qc = Tests(Departments.Qc);
            state.Gate = QcGate.Evaluate(hw, sw, qc, req);
            state.PreviousIssues = Reworked(all);

            var returns = all.Where(r => r.TestId == LifecycleEvents.QcReturn).ToList();
            state.ReturnCount = returns.Count;
            var lastReturnHw = returns.LastOrDefault(r => string.Equals(r.Details, Departments.Hardware, StringComparison.OrdinalIgnoreCase));
            var lastReturnSw = returns.LastOrDefault(r => string.Equals(r.Details, Departments.Software, StringComparison.OrdinalIgnoreCase));
            var lastReturn = returns.LastOrDefault();

            state.HardwareHandover = Last(LifecycleEvents.HardwareHandover);
            state.SoftwareHandover = Last(LifecycleEvents.SoftwareHandover);
            bool hwGateOk = !Blocks(state.Gate.Hardware);
            bool swGateOk = !Blocks(state.Gate.Software);
            bool hwHandoverCurrent = state.HardwareHandover != null && (lastReturnHw == null || T(state.HardwareHandover) > T(lastReturnHw));
            bool swHandoverCurrent = state.SoftwareHandover != null && (lastReturnSw == null || T(state.SoftwareHandover) > T(lastReturnSw));
            state.HardwareReady = hwHandoverCurrent && hwGateOk;
            state.SoftwareReady = state.HardwareReady && swHandoverCurrent && swGateOk;

            if (!hwHandoverCurrent && lastReturnHw != null) state.PendingReturn = lastReturnHw;
            else if (!swHandoverCurrent && lastReturnSw != null) state.PendingReturn = lastReturnSw;

            if (!state.HardwareReady) state.Gate.BlockingReasons.Insert(0, state.HardwareHandover == null ? "Hardware has not handed the laptop over" : "Hardware rework is not finished");
            else if (!state.SoftwareReady) state.Gate.BlockingReasons.Insert(0, state.SoftwareHandover == null ? "Software has not handed the laptop over" : "Software rework is not finished");

            // ---- current status
            if (!state.HardwareReady)
            {
                state.Status = state.HardwareHandover != null ? LifecycleStatus.HardwareRework
                             : hw.Count > 0 ? LifecycleStatus.HardwareInProgress : LifecycleStatus.Received;
                return state;
            }
            if (!state.SoftwareReady)
            {
                state.Status = state.SoftwareHandover != null ? LifecycleStatus.SoftwareRework
                             : sw.Any(r => T(r) >= T(state.HardwareHandover)) || sw.Count > 0 ? LifecycleStatus.SoftwareInProgress : LifecycleStatus.ReadyForSoftware;
                return state;
            }

            // At QC. A decision counts only if it was made after the latest handover and the latest return.
            // A decision counts only if it came after the latest handover, the latest return AND the latest hardware
            // change (a part swapped after QC PASS means QC has not seen this hardware).
            DateTime anchorNoChange = new[] { T(state.HardwareHandover), T(state.SoftwareHandover), lastReturn == null ? DateTime.MinValue : T(lastReturn) }.Max();
            var lastChange = Last(LifecycleEvents.HardwareChanged);
            var passBeforeChange = Last(LifecycleEvents.QcDecision, r => T(r) > anchorNoChange);
            if (lastChange != null && passBeforeChange != null && passBeforeChange.Status == "PASS" && T(lastChange) > T(passBeforeChange))
            {
                state.HardwareChangedAfterQc = lastChange;
                state.Gate.BlockingReasons.Insert(0, "Hardware changed after QC PASS: " + lastChange.Comment);
            }
            DateTime anchor = lastChange != null && T(lastChange) > anchorNoChange && state.HardwareChangedAfterQc != null ? T(lastChange) : anchorNoChange;
            var decision = Last(LifecycleEvents.QcDecision, r => T(r) > anchor);
            state.QcDecision = decision;
            if (decision == null)
            {
                state.Status = qc.Any(r => T(r) > anchor) ? LifecycleStatus.QcInProgress : LifecycleStatus.ReadyForQc;
                return state;
            }
            if (decision.Status != "PASS") { state.Status = LifecycleStatus.QcRework; return state; }

            // QC passed: ready for E-Commerce / ITMS; the delivery check is a separate, later event.
            var deliveryItems = all.Where(r => LifecycleEvents.IsDeliveryItem(r.TestId) && T(r) > T(decision)).ToList();
            state.DeliveryGate = QcGate.Evaluate(LifecycleEvents.DeliveryGroup, deliveryItems, req.Required(LifecycleEvents.DeliveryGroup),
                                                 id => req.SkipAllowed(LifecycleEvents.DeliveryGroup, id));
            state.DeliveryDecision = Last(LifecycleEvents.DeliveryDecision, r => T(r) > T(decision));
            state.DeliverySnapshot = Last(LifecycleEvents.DeviceSnapshot, r => T(r) > T(decision));
            state.Status = state.DeliveryDecision != null
                ? (state.DeliveryDecision.Status == "PASS" ? LifecycleStatus.ReadyForDelivery : LifecycleStatus.DeliveryHold)
                : deliveryItems.Count > 0 ? LifecycleStatus.FinalCheck : LifecycleStatus.ReadyForEcommerce;
            return state;
        }

        /// <summary>A department blocks when a required test is missing, failed, or skipped where skipping is not allowed.</summary>
        private static bool Blocks(DepartmentStatus d) => d.Missing.Count > 0 || d.Failed.Any(f => d.Required.Contains(f.TestId));

        /// <summary>
        /// May this record be written now? Null = yes; otherwise the reason (shown to the technician and returned
        /// by the server). This is the stage-order rule set, enforced in the client service AND on the server.
        /// </summary>
        public static string CheckEvent(LifecycleState state, string department, string testId, string status, QcRequirements requirements = null)
        {
            var req = requirements != null && requirements.IsValid ? requirements : QcRequirements.Default();
            if (LifecycleEvents.IsEvidence(testId)) return null;                          // evidence can be captured at any stage
            if (state == null) return "The laptop's history could not be read.";
            bool ordered = !req.AllowOutOfOrderWork;

            switch (testId)
            {
                case LifecycleEvents.HardwareHandover:
                    return Blocks(state.Gate.Hardware) ? "Hardware is not finished: " + Describe(state.Gate.Hardware) : null;
                case LifecycleEvents.SoftwareHandover:
                    if (!state.HardwareReady) return "Hardware has not handed this laptop over yet.";
                    return Blocks(state.Gate.Software) ? "Software is not finished: " + Describe(state.Gate.Software) : null;
                case LifecycleEvents.QcReturn:
                    return state.AtQcStage ? null : "The laptop is not at QC.";
                case LifecycleEvents.QcDecision:
                    if (status != "PASS") return null;                                   // QC FAIL can always be recorded
                    return state.Gate.CanPass ? null : "QC PASS is not possible yet: " + string.Join("; ", state.Gate.BlockingReasons);
                case LifecycleEvents.DeliveryDecision:
                    if (!state.ReadyForItms) return "The final delivery check needs a QC PASS first.";
                    if (status != "PASS") return null;                                   // HOLD can always be recorded
                    if (state.DeliverySnapshot == null)
                        return "The laptop's hardware must be checked again before delivery (wait for the hardware check to finish).";
                    return state.DeliveryGate != null && !Blocks(state.DeliveryGate) ? null
                         : "The delivery checklist is not finished: " + (state.DeliveryGate == null ? "not started" : Describe(state.DeliveryGate));
            }
            if (LifecycleEvents.IsDeliveryItem(testId)) return state.ReadyForItms ? null : "The final delivery check needs a QC PASS first.";
            if (!ordered) return null;
            if (string.Equals(department, Departments.Software, StringComparison.OrdinalIgnoreCase) && !state.HardwareReady)
                return "Hardware has not handed this laptop over yet.";
            if (string.Equals(department, Departments.Qc, StringComparison.OrdinalIgnoreCase) && !state.AtQcStage)
                return !state.HardwareReady ? "This laptop is still with Hardware." : "This laptop is still with Software.";
            return null;
        }

        private static string Describe(DepartmentStatus d)
        {
            var parts = new List<string>();
            var failed = d.Failed.Where(f => d.Required.Contains(f.TestId)).Select(f => f.TestName ?? QcGate.Label(f.TestId)).ToList();
            if (failed.Count > 0) parts.Add("failed: " + string.Join(", ", failed));
            var missing = d.Missing.Except(d.SkippedNotAllowed).Select(QcGate.Label).ToList();
            if (missing.Count > 0) parts.Add("not done: " + string.Join(", ", missing));
            if (d.SkippedNotAllowed.Count > 0) parts.Add("cannot be skipped: " + string.Join(", ", d.SkippedNotAllowed.Select(QcGate.Label)));
            return string.Join("; ", parts);
        }

        /// <summary>Tests that failed at least once and whose latest result is PASS (with the repair note in between).</summary>
        public static List<ReworkedItem> Reworked(IEnumerable<TestResultRecord> history)
        {
            var all = (history ?? Enumerable.Empty<TestResultRecord>()).Where(r => r != null && !r.Deleted).OrderBy(T).ToList();
            var list = new List<ReworkedItem>();
            foreach (var g in all.Where(r => LifecycleEvents.IsPlainTest(r.TestId)).GroupBy(r => new { D = r.Department ?? "", r.TestId }))
            {
                var runs = g.ToList();
                var fails = runs.Where(r => r.Status == "FAIL").ToList();
                var last = runs[runs.Count - 1];
                if (fails.Count == 0 || last.Status != "PASS") continue;
                var lastFail = fails[fails.Count - 1];
                var repair = all.LastOrDefault(r => r.TestId == LifecycleEvents.RepairPrefix + g.Key.TestId &&
                                                    string.Equals(r.Department, g.Key.D, StringComparison.OrdinalIgnoreCase) && T(r) >= T(lastFail));
                list.Add(new ReworkedItem
                {
                    Department = g.Key.D, TestId = g.Key.TestId, TestName = last.TestName ?? QcGate.Label(g.Key.TestId),
                    FailedAt = lastFail.CompletedAt ?? lastFail.StartedAt, FailedBy = lastFail.OperatorName, FailureNote = lastFail.Comment,
                    RepairNote = repair?.Comment, PassedAt = last.CompletedAt ?? last.StartedAt, PassedBy = last.OperatorName, FailCount = fails.Count,
                });
            }
            return list.OrderBy(i => i.FailedAt).ToList();
        }

        /// <summary>The complete chronological history of the laptop: who did what, when, with what result.</summary>
        public static List<TimelineEntry> Timeline(IEnumerable<TestResultRecord> history)
        {
            var all = (history ?? Enumerable.Empty<TestResultRecord>()).Where(r => r != null && !r.Deleted && r.Status != "NOT TESTED").OrderBy(T).ToList();
            var list = new List<TimelineEntry>();
            foreach (var r in all)
            {
                var e = new TimelineEntry { At = r.CompletedAt ?? r.StartedAt, Department = r.Department, Operator = r.OperatorName, Notes = r.Comment, Result = r.Status, Evidence = r.Details };
                switch (r.TestId)
                {
                    case LifecycleEvents.HardwareHandover: e.Kind = "HANDOVER"; e.Title = "Hardware finished → Software"; e.Result = "READY"; break;
                    case LifecycleEvents.SoftwareHandover: e.Kind = "HANDOVER"; e.Title = "Software finished → QC"; e.Result = "READY"; break;
                    case LifecycleEvents.QcReturn: e.Kind = "RETURN"; e.Title = "QC returned the laptop to " + r.Details; e.Result = "RETURNED"; e.Evidence = null; break;
                    case LifecycleEvents.QcDecision: e.Kind = "QC_DECISION"; e.Title = "QC decision"; break;
                    case LifecycleEvents.DeliveryDecision:
                        e.Kind = "DELIVERY"; e.Title = "Final delivery check" + (string.IsNullOrWhiteSpace(r.Details) ? "" : " (order " + r.Details + ")");
                        e.Result = r.Status == "PASS" ? "READY FOR DELIVERY" : "HOLD"; e.Evidence = null; break;
                    default:
                        if (r.TestId == LifecycleEvents.DeviceSnapshot) continue;        // shown in the evidence view, not as an event
                        if (r.TestId == LifecycleEvents.HardwareChanged) { e.Kind = "HARDWARE_CHANGE"; e.Title = "Hardware changed"; e.Result = "CHANGED"; e.Evidence = null; }
                        else if (LifecycleEvents.IsRepair(r.TestId)) { e.Kind = "REPAIR"; e.Title = "Fixed: " + (r.TestName ?? r.TestId.Substring(LifecycleEvents.RepairPrefix.Length)); e.Result = "FIXED"; }
                        else if (LifecycleEvents.IsDeliveryItem(r.TestId)) { e.Kind = "DELIVERY"; e.Title = "Delivery check: " + (r.TestName ?? r.TestId); }
                        else { e.Kind = "TEST"; e.Title = r.TestName ?? QcGate.Label(r.TestId); }
                        break;
                }
                list.Add(e);
            }
            return list;
        }
    }
}
