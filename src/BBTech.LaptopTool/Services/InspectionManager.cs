using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBTech.Core.Identity;
using BBTech.Core.Models;
using BBTech.Core.Settings;
using BBTech.Core.Storage;
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

            return await Task.Run(() =>
            {
                Match = Repo.FindDevice(Identity);
                switch (Match.Status)
                {
                    case MatchStatus.NoIdentifiers:
                        return InspectionReadiness.NoIdentifiers;
                    case MatchStatus.Conflict:
                        return InspectionReadiness.NeedsConflictDecision;
                    case MatchStatus.NotFound:
                        Device = Repo.RegisterDevice(Identity, IdentityFactory.SpecsFrom(Detection));
                        AppLog.Info("QC: new device record created");
                        return InspectionReadiness.Ready;
                    default:
                        Device = Repo.UpdateDevice(Match.Device.DeviceId, Identity, IdentityFactory.SpecsFrom(Detection));
                        PendingOpen = Repo.GetOpenInspection(Device.DeviceId);
                        return PendingOpen != null ? InspectionReadiness.NeedsResumeDecision : InspectionReadiness.Ready;
                }
            });
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
    }
}
