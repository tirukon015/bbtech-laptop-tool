using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBTech.Core.Audit;
using BBTech.Core.Storage;
using BBTech.Core.Identity;
using BBTech.Core.Models;
using BBTech.Core.Settings;
using BBTech.Core.Storage;
using BBTech.Core.Sync;
using BBTech.Core.Testing;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Services
{
    public enum InspectionReadiness
    {
        /// <summary>Device resolved; results will be saved to the laptop's history.</summary>
        Ready,
        /// <summary>An unfinished inspection exists: ask Resume / Start new.</summary>
        NeedsResumeDecision,
        /// <summary>Identifiers conflict with stored records: ask before creating a separate record.</summary>
        NeedsConflictDecision,
        /// <summary>No serial/UUID/board serial: testing works but cannot be saved to history.</summary>
        NoIdentifiers,
        /// <summary>The protected store could not be opened.</summary>
        StoreUnavailable,
    }

    /// <summary>
    /// QC workflow on top of the laptop's encrypted store: resolves the physical device, starts an
    /// inspection for the selected operator, persists every test result immediately, recovers
    /// unfinished inspections and completes inspections (which then become immutable history).
    /// </summary>
    public sealed class InspectionManager
    {
        public DetectionResult Detection { get; private set; }
        public DeviceIdentity Identity { get; private set; }
        public MatchResult Match { get; private set; }
        public DeviceRecord Device { get; private set; }
        public InspectionRecord Current { get; private set; }
        public InspectionRecord PendingOpen { get; private set; }
        public string StoreMessage { get; private set; } = "";

        private QcRepository Repo => QcStoreService.Current;
        public bool CanSave => Repo != null && Device != null;

        public async Task<InspectionReadiness> InitializeAsync()
        {
            Detection = await DetectionSession.GetOrDetectAsync();
            Identity = IdentityFactory.From(Detection);
            var open = await QcStoreService.OpenAsync(Identity);
            if (!open.Ok) { StoreMessage = open.Message; return InspectionReadiness.StoreUnavailable; }
            StoreMessage = open.Message;

            var readiness = await Task.Run(() => ResolveDevice());
            if (Device != null) await RestoreHistoryAsync();
            return readiness;
        }

        private CarriedHistory _carried;
        /// <summary>How many records were restored for this laptop at start (0 = nothing was missing).</summary>
        public int RestoredRecords { get; private set; }

        private InspectionReadiness ResolveDevice()
        {
            {
                Match = Repo.FindDevice(Identity);
                switch (Match.Status)
                {
                    case MatchStatus.NoIdentifiers:
                        return InspectionReadiness.NoIdentifiers;
                    case MatchStatus.Conflict:
                        return InspectionReadiness.NeedsConflictDecision;
                    case MatchStatus.NotFound:
                        // Unknown here: maybe new, maybe its local store was wiped by a Windows install.
                        // If the USB carries its history, keep the id it already had.
                        _carried = HistoryCarrier.Load(AppPaths.CarryDir, Identity);
                        Device = Repo.RegisterDevice(Identity, IdentityFactory.SpecsFrom(Detection), null, _carried?.DeviceId);
                        AppLog.Info(_carried != null ? "Device record re-created; its history will be restored from the USB" : "New device record created");
                        return InspectionReadiness.Ready;
                    default:
                        Device = Repo.UpdateDevice(Match.Device.DeviceId, Identity, IdentityFactory.SpecsFrom(Detection));
                        PendingOpen = Repo.GetOpenInspection(Device.DeviceId);
                        return PendingOpen != null ? InspectionReadiness.NeedsResumeDecision : InspectionReadiness.Ready;
                }
            }
        }

        /// <summary>
        /// Brings back history the laptop lost (a clean Windows install wipes its local store): first from the
        /// USB carry file, then from the BBTech server when reachable. Only missing records are added.
        /// </summary>
        private async Task RestoreHistoryAsync()
        {
            try
            {
                var carried = _carried ?? HistoryCarrier.Load(AppPaths.CarryDir, Identity);
                int n = carried == null ? 0 : Repo.ImportHistory(Device.DeviceId, carried.Records);

                var api = UsbSyncService.CreateClient();
                if (api.IsConfigured)
                {
                    var remote = await api.GetDeviceHistoryAsync(Device.DeviceId);
                    if (remote != null && remote.Count > 0) n += Repo.ImportHistory(Device.DeviceId, remote.Select(r => r.ToRecord()));
                }
                RestoredRecords = n;
                if (n > 0)
                {
                    AppLog.Info("History restored for this laptop: " + n + " record(s)");
                    new AuditStore(AppPaths.AuditLogPath).Log(SettingsService.Store.CurrentOperator, AuditActions.DeviceDetected,
                        "Device:" + Device.DeviceId, null, "HISTORY_RESTORED", n + " record(s) restored after the local store was missing");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("History restore failed: " + ex.Message);
            }
        }

        /// <summary>Staff confirmed: create a separate, clearly flagged device record despite the conflict.</summary>
        public void CreateDeviceDespiteConflict()
        {
            Device = Repo.RegisterDevice(Identity, IdentityFactory.SpecsFrom(Detection), "Created despite identity conflict: " + Match.Reason);
            AppLog.Warn("QC: device record created despite identity conflict");
        }

        /// <summary>Continue the unfinished inspection; returns its saved results.</summary>
        public List<TestResultRecord> ResumePending()
        {
            Current = PendingOpen;
            PendingOpen = null;
            AppLog.Info("QC: unfinished inspection resumed");
            return Repo.GetResults(Current.InspectionId);
        }

        /// <summary>Do not resume: the unfinished inspection is kept in history as ABANDONED.</summary>
        public void AbandonPending()
        {
            if (PendingOpen != null) Repo.AbandonInspection(PendingOpen.InspectionId);
            PendingOpen = null;
            AppLog.Info("QC: unfinished inspection kept as abandoned");
        }

        /// <summary>"Start over": the current inspection is kept in history as ABANDONED; the next result starts a new one.</summary>
        public void AbandonCurrent()
        {
            if (Current == null) return;
            Repo.AbandonInspection(Current.InspectionId);
            Current = null;
            AppLog.Info("QC: inspection closed as abandoned (start over)");
        }

        /// <summary>Starts the inspection on the first recorded result (snapshot of the current operator).</summary>
        private void EnsureInspection(OperatorRecord op, TestPlan plan)
        {
            if (Current != null) return;
            if (op == null) throw new InvalidOperationException("Select who is testing first.");
            Current = Repo.StartInspection(Device.DeviceId,
                new OperatorSnapshot { OperatorId = op.OperatorId, Name = op.Name, Role = op.Role },
                AppPaths.Version, IdentityFactory.SpecsFrom(Detection), plan.ModeName, plan.SelectedNames);
            AppLog.Info("QC: inspection #" + Current.Number + " started (" + plan.ModeLabel + ", " + plan.Selected.Count + " tests)");
        }

        /// <summary>Persists one test's state immediately (crash/power-loss safe).</summary>
        public void Record(TestItem item, OperatorRecord op, TestPlan plan)
        {
            if (!CanSave) return;
            EnsureInspection(op, plan);
            Repo.SaveResult(Current.InspectionId, item.Kind.ToString(), item.Name, item.StateLabel, item.Required, item.Notes);
            AppLog.Info("QC: result saved (" + item.Name + ")"); // status deliberately not logged
        }

        /// <summary>Records a change of test mode / selected tests on the open inspection (no-op before it starts).</summary>
        public void UpdateSelection(TestPlan plan)
        {
            if (!CanSave || Current == null) return;
            Current = Repo.UpdateTestSelection(Current.InspectionId, plan.ModeName, plan.SelectedNames);
            AppLog.Info("QC: test selection updated (" + plan.ModeLabel + ", " + plan.Selected.Count + " tests)");
        }

        /// <summary>Saves that a test was removed from the inspection: it is no longer counted.</summary>
        public void RecordDeselected(TestItem item)
        {
            if (!CanSave || Current == null) return;
            if (Repo.GetResults(Current.InspectionId).Any(r => r.TestId == item.Kind.ToString()))
                Repo.SaveResult(Current.InspectionId, item.Kind.ToString(), item.Name, "NOT TESTED", false, "Removed from this inspection");
        }

        public bool HasResults => Current != null && Repo.GetResults(Current.InspectionId).Any(r => r.Status != "NOT TESTED");

        /// <summary>
        /// Completes the current inspection; returns it (immutable from now on). The selected tests are
        /// recorded exactly and the overall result is calculated from them only.
        /// </summary>
        public InspectionRecord Complete(IEnumerable<TestItem> items, TestPlan plan)
        {
            if (Current == null) throw new InvalidOperationException("No inspection in progress.");
            var selected = items.Where(i => plan.IsSelected(i.Kind)).ToList();
            Repo.UpdateTestSelection(Current.InspectionId, plan.ModeName, plan.SelectedNames);
            foreach (var i in selected) Repo.SaveResult(Current.InspectionId, i.Kind.ToString(), i.Name, i.StateLabel, true, i.Notes);
            string overall = TestText.Label(OverallResultCalculator.Calculate(selected));
            var done = Repo.CompleteInspection(Current.InspectionId, overall);
            Current = null;
            AppLog.Info("QC: inspection #" + done.Number + " completed");
            return done;
        }

        public InspectionView View(InspectionRecord insp) =>
            new InspectionView { Inspection = insp, Results = Repo?.GetResults(insp.InspectionId) ?? new List<TestResultRecord>() };

        /// <summary>
        /// Plain-language reason why results cannot be saved for this laptop, or null when they can.
        /// Dashboards show it instead of letting tests run and silently not saving them.
        /// </summary>
        public string SaveBlockedReason(InspectionReadiness readiness)
        {
            switch (readiness)
            {
                case InspectionReadiness.Ready:
                case InspectionReadiness.NeedsResumeDecision:
                    return CanSave ? null : "This laptop's record could not be opened.";
                case InspectionReadiness.NoIdentifiers:
                    return "This laptop has no serial number, UUID or board serial, so its results cannot be stored safely. Ask an administrator.";
                case InspectionReadiness.NeedsConflictDecision:
                    return "This laptop's identifiers match more than one stored laptop. Ask an administrator before testing. (" + (Match?.Reason ?? "") + ")";
                case InspectionReadiness.StoreUnavailable:
                    return "The BBTech data store could not be opened, so results cannot be saved. " + StoreMessage;
                default:
                    return "Results cannot be saved for this laptop.";
            }
        }

        /// <summary>
        /// Records one test result for a department, as the signed-in operator. Enforced here (not only in
        /// the UI): no signed-in operator, no permission for the department, or no stored laptop record
        /// throws, so a result is never silently dropped or attributed to someone else.
        /// Each call creates a new timestamped record (history is never overwritten), writes an audit
        /// entry, queues the record for USB sync and starts a background sync pass.
        /// </summary>
        public TestResultRecord RecordDepartmentTest(string testId, string testName, string status, string department, string notes = null, string details = null)
        {
            var op = SettingsService.Store.CurrentOperator;
            BBTech.Core.Security.ResultAuthorization.EnsureRecord(op, department, testId);
            if (!CanSave) throw new InvalidOperationException("Results cannot be saved for this laptop. " + StoreMessage);
            department = BBTech.Core.Security.ResultAuthorization.Normalize(department);

            // Lifecycle order (Hardware → Software → QC, handovers, QC gate): enforced here, not only in the UI.
            string blocked = LaptopLifecycle.CheckEvent(Lifecycle(), department, testId, status, MasterDataService.Requirements);
            if (blocked != null) throw new InvalidOperationException(blocked);

            // FAIL → fix → retest PASS: the repair is recorded as its own event so the history never looks
            // as if the laptop was always fine. (Notes of the passing retest describe what was fixed.)
            var previous = Repo.GetLatestTestResult(Device.DeviceId, testId, department);
            bool isRetest = previous != null && previous.Status != "NOT TESTED" && previous.Status != "TESTING";
            if (LifecycleEvents.IsPlainTest(testId) && status == "PASS" && previous != null && previous.Status == "FAIL")
                Save(op, LifecycleEvents.RepairPrefix + testId, testName, "PASS", department,
                     string.IsNullOrWhiteSpace(notes) ? "Fixed and retested" : notes, "Previous failure: " + previous.Comment, false);

            return Save(op, testId, testName, status, department, notes, details, isRetest);
        }

        private TestResultRecord Save(OperatorRecord op, string testId, string testName, string status, string department, string notes, string details, bool isRetest)
        {
            var rec = Repo.RecordTest(Device.DeviceId, testId, testName, status, op.Name, department, notes, details, Current?.InspectionId, op.OperatorId);
            AppLog.Info(string.Format("{0} saved: {1} -> {2} by {3}", department, testName, status, op.Name));
            try { HistoryCarrier.Save(AppPaths.CarryDir, Identity, Device.DeviceId, History()); }
            catch (Exception ex) { AppLog.Warn("USB history copy not updated: " + ex.Message); }

            try
            {
                var item = SyncQueueItem.FromTestResult(
                    Device.DeviceId, op.OperatorId, op.Name, department, testId, testName, status, notes, AppPaths.Version, rec.TestResultId);
                item.Timestamp = (rec.CompletedAt ?? rec.StartedAt)?.ToUniversalTime() ?? item.Timestamp; // same moment as the journal record, in UTC
                item.DeviceSerial = Device.SerialNumber;
                item.DeviceManufacturer = Device.Manufacturer;
                item.DeviceModel = (Device.Model + " " + Device.ModelNumber).Trim();
                item.Details = details;
                item.Checksum = item.ComputeChecksum();   // after Details: the evidence is covered by the tag
                new UsbSyncQueue(AppPaths.SyncQueuePath).Enqueue(item);

                new AuditStore(AppPaths.AuditLogPath).Log(op, AuditActions.ForRecord(department, testId, status, isRetest),
                    "Device:" + Device.DeviceId + " " + testName, null, status, (department + (string.IsNullOrEmpty(notes) ? "" : " | " + notes)));
            }
            catch (Exception ex)
            {
                // The result itself is stored in the encrypted journal; the queue/audit failure is logged.
                AppLog.Warn("Saved, but queueing for sync / audit failed: " + ex.Message);
            }

            UsbSyncService.TriggerInBackground(op);
            return rec;
        }

        // ------------------------------------------------------------------ technical evidence

        /// <summary>The latest stored technical snapshot of this laptop (null when none yet).</summary>
        public BBTech.Core.Models.TechnicalInventory LatestInventory() => BBTech.Core.Integration.ItmsTraceability.LatestInventory(History());

        /// <summary>
        /// Captures the laptop's technical record (identity, inventory, measured health, Windows/Office state)
        /// and stores it as a history record when it is the first one, when a part changed since the last one,
        /// or when the last one is older than 12 hours. A changed part also gets its own DEVICE_HARDWARE_CHANGED
        /// event ("Storage replaced: old → new"); the old snapshot stays in history. Runs in the background.
        /// </summary>
        private static readonly System.Threading.SemaphoreSlim SnapshotGate = new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>
        /// Captures the laptop's technical record (identity, inventory, measured health, Windows/Office state).
        /// Stored when <paramref name="force"/> (a lifecycle moment: handover, QC decision, final check), when it is the
        /// first one, when a part changed, or when the last one is older than 12 hours. A changed part gets its own
        /// DEVICE_HARDWARE_CHANGED event with component / old / new / reason; the old snapshot stays in history.
        /// </summary>
        public async Task<BBTech.Core.Models.TechnicalInventory> CaptureSnapshotAsync(bool force = false, string stage = "PERIODIC")
        {
            var op = SettingsService.Store.CurrentOperator;
            if (!CanSave || op == null || Detection == null) return LatestInventory();
            string dept = BBTech.Core.Security.ResultAuthorization.Normalize(SettingsService.Store.CurrentDepartment);
            if (dept == null || BBTech.Core.Security.ResultAuthorization.CheckRecord(op, dept, LifecycleEvents.DeviceSnapshot) != null) return LatestInventory();
            await SnapshotGate.WaitAsync();
            try
            {
                var device = Device; var detection = Detection;
                var now = await Task.Run(() => BBTech.LaptopTool.Detect.InventoryCollector.Collect(detection, device));
                now.Stage = stage;
                var history = History();
                var lastRecord = history.LastOrDefault(r => r.TestId == LifecycleEvents.DeviceSnapshot);
                var last = lastRecord == null ? null : BBTech.Core.Models.TechnicalInventory.FromJson(lastRecord.Details);
                string signature = now.HardwareSignature();
                bool changed = last != null && BBTech.Core.Models.TechnicalInventory.StructuredChanges(last, now).Count > 0;
                bool stale = lastRecord == null || last == null || (DateTime.Now - (lastRecord.CompletedAt ?? DateTime.MinValue)).TotalHours >= 12;
                if (!force && !changed && !stale) return last;

                if (changed)
                {
                    var changes = BBTech.Core.Models.TechnicalInventory.StructuredChanges(last, now);
                    // Why: the repair note(s) recorded since the previous snapshot, else "detected automatically".
                    var since = lastRecord.CompletedAt ?? DateTime.MinValue;
                    string reason = string.Join("; ", history.Where(r => LifecycleEvents.IsRepair(r.TestId) && (r.CompletedAt ?? DateTime.MinValue) >= since)
                                                             .Select(r => r.Comment).Where(c => !string.IsNullOrWhiteSpace(c)));
                    foreach (var c in changes) c.Reason = string.IsNullOrEmpty(reason) ? "detected automatically between snapshots" : reason;
                    string text = string.Join("; ", changes.Select(c => c.Describe()));
                    Save(op, LifecycleEvents.HardwareChanged, "Hardware changed", "PASS", dept, text,
                         System.Text.Encoding.UTF8.GetString(Json.ToBytes(changes)), false);
                    AppLog.Info("Hardware change detected: " + text);
                }
                Save(op, LifecycleEvents.DeviceSnapshot, "Technical snapshot (" + stage + ")", "PASS", dept, signature, now.ToJson(), false);
                return now;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Technical snapshot failed: " + ex.Message);
                return LatestInventory();
            }
            finally
            {
                SnapshotGate.Release();
            }
        }

        /// <summary>Evidence of what the hardware was at a lifecycle moment; runs in the background.</summary>
        private void SnapshotInBackground(string stage)
        {
            Task.Run(async () =>
            {
                try { await CaptureSnapshotAsync(true, stage); }
                catch (Exception ex) { AppLog.Warn("Stage snapshot failed: " + ex.Message); }
            });
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Every event recorded for this laptop, oldest first.</summary>
        public List<TestResultRecord> History() =>
            Repo == null || Device == null ? new List<TestResultRecord>() : Repo.GetDeviceHistory(Device.DeviceId);

        /// <summary>Where this laptop is in the BBTech lifecycle (recomputed from its full history).</summary>
        public LifecycleState Lifecycle() => LaptopLifecycle.Evaluate(History(), MasterDataService.Requirements);

        public List<TimelineEntry> Timeline() => LaptopLifecycle.Timeline(History());

        /// <summary>The gate report QC uses (latest Hardware, Software and QC checklist results + handovers).</summary>
        public QcGateReport EvaluateQcGate() => Lifecycle().Gate;

        /// <summary>Hardware → READY_FOR_SOFTWARE, or Software → READY_FOR_QC. Refused while required work is open.</summary>
        public TestResultRecord HandOver(string department)
        {
            bool hw = string.Equals(department, Departments.Hardware, StringComparison.OrdinalIgnoreCase);
            var s = Lifecycle();
            var d = hw ? s.Gate.Hardware : s.Gate.Software;
            string snapshot = string.Format("{0} passed, {1} skipped, {2} reworked; by {3}", d.Passed.Count, d.Skipped.Count,
                s.PreviousIssues.Count(i => string.Equals(i.Department, department, StringComparison.OrdinalIgnoreCase)), string.Join(", ", d.Operators));
            SnapshotInBackground(hw ? "HARDWARE_HANDOVER" : "SOFTWARE_HANDOVER");
            return RecordDepartmentTest(hw ? LifecycleEvents.HardwareHandover : LifecycleEvents.SoftwareHandover,
                hw ? "Hardware ready for Software" : "Software ready for QC", "PASS", hw ? Departments.Hardware : Departments.Software,
                s.PendingReturn != null ? "After QC return: " + s.PendingReturn.Comment : "", snapshot);
        }

        /// <summary>QC FAIL: records the decision and sends the laptop back to the department that must fix it.</summary>
        public TestResultRecord ReturnFromQc(string targetDepartment, string reason)
        {
            string target = BBTech.Core.Security.ResultAuthorization.Normalize(targetDepartment);
            if (target != Departments.Hardware && target != Departments.Software) throw new ArgumentException("Return to Hardware or Software.");
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Say what the problem is.");
            RecordQcDecision("FAIL", reason);
            return RecordDepartmentTest(LifecycleEvents.QcReturn, "Returned to " + target, "FAIL", Departments.Qc, reason, target);
        }

        /// <summary>Final check before delivery: READY FOR DELIVERY (PASS) or HOLD (FAIL, with reason). Linked to the ITMS order reference if given.</summary>
        public TestResultRecord RecordDeliveryDecision(bool ready, string orderReference, string reason)
        {
            if (!ready && string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Say why the laptop is on hold.");
            return RecordDepartmentTest(LifecycleEvents.DeliveryDecision, "Final delivery check", ready ? "PASS" : "FAIL", Departments.Qc,
                ready ? "" : reason, (orderReference ?? "").Trim());
        }

        /// <summary>
        /// Records the final QC decision. PASS is refused unless the gate allows it; FAIL is always allowed.
        /// The record carries a snapshot of what QC saw (Details), so the decision stays explainable later.
        /// </summary>
        public TestResultRecord RecordQcDecision(string outcome, string notes)
        {
            if (outcome != "PASS" && outcome != "FAIL") throw new ArgumentException("The decision must be PASS or FAIL.");
            var report = EvaluateQcGate();
            if (outcome == "PASS" && !report.CanPass)
            {
                try { new AuditStore(AppPaths.AuditLogPath).Log(SettingsService.Store.CurrentOperator, AuditActions.QcDecisionRejected, "Device:" + Device?.DeviceId, null, "PASS", string.Join("; ", report.BlockingReasons)); } catch { }
                throw new InvalidOperationException("QC PASS is not possible yet:\n" + string.Join("\n", report.BlockingReasons));
            }
            string snapshot = string.Format("Hardware: {0} (by {1}); Software: {2} (by {3}); QC checklist: {4}",
                report.Hardware.Summary, string.Join(", ", report.Hardware.Operators),
                report.Software.Summary, string.Join(", ", report.Software.Operators),
                report.QcChecklist.Summary);
            var saved = RecordDepartmentTest(QcGate.QcDecisionTestId, "Final QC Decision", outcome, Departments.Qc, notes, snapshot);
            SnapshotInBackground("QC_DECISION");   // what hardware the laptop had when QC decided
            return saved;
        }
        public TestResultRecord GetLatestTestResult(string testId, string department = null)
        {
            if (Repo == null || Device == null) return null;
            return Repo.GetLatestTestResult(Device.DeviceId, testId, department);
        }

        public List<TestResultRecord> GetTestHistory(string testId, string department = null)
        {
            if (Repo == null || Device == null) return new List<TestResultRecord>();
            return Repo.GetTestHistory(Device.DeviceId, testId, department);
        }

        public List<TestResultRecord> GetLatestDepartmentResults(string department = null)
        {
            if (Repo == null || Device == null) return new List<TestResultRecord>();
            return Repo.GetLatestDepartmentResults(Device.DeviceId, department);
        }

        /// <summary>Admin delete (tombstone; history keeps it). Requires an identified operator with records.delete.</summary>
        public bool DeleteTestResult(TestResultRecord record, OperatorRecord actor)
        {
            if (record == null) return false;
            BBTech.Core.Security.ResultAuthorization.EnsureDelete(actor, record.TestId);
            if (Repo == null) return false;
            bool ok = Repo.DeleteTestResult(record.TestResultId);
            if (ok)
            {
                try
                {
                    new AuditStore(AppPaths.AuditLogPath).Log(actor, AuditActions.RecordDeleted, "TestResult:" + record.TestResultId,
                        record.Status, "DELETED", record.Department + " / " + record.TestName + " by " + record.OperatorName);
                }
                catch (Exception ex) { AppLog.Warn("Delete audit failed: " + ex.Message); }
            }
            return ok;
        }
    }
}
