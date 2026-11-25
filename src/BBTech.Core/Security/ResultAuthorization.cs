using System;
using BBTech.Core.Settings;
using BBTech.Core.Testing;

namespace BBTech.Core.Security
{
    /// <summary>
    /// Who may write or delete which result. Fail-closed: no operator, an inactive operator or an
    /// unknown department is always denied. Used by the service layer for every write, so hiding a
    /// button is never the only protection.
    /// </summary>
    public static class ResultAuthorization
    {
        /// <summary>Null when allowed; otherwise the reason (shown to the technician).</summary>
        public static string CheckRecord(OperatorRecord op, string department, string testId)
        {
            if (op == null) return "Select your name before recording results.";
            if (!op.Active) return "Your profile is disabled. Contact an administrator.";

            if (testId == LifecycleEvents.QcDecision || testId == LifecycleEvents.QcReturn || testId == LifecycleEvents.DeliveryDecision)
                return op.CanPerform(AppPermissions.QcApprove) ? null : "Only QC approvers can record a QC decision.";

            switch (Normalize(department))
            {
                case Departments.Hardware: return op.CanAccessHardware ? null : "Your profile is not allowed to record Hardware results.";
                case Departments.Software: return op.CanAccessSoftware ? null : "Your profile is not allowed to record Software results.";
                case Departments.Qc: return op.CanAccessQc ? null : "Your profile is not allowed to record QC results.";
                default: return "Unknown department.";
            }
        }

        public static string CheckDelete(OperatorRecord op, string testId)
        {
            if (op == null || !op.Active) return "Only an identified administrator can delete records.";
            if (LifecycleEvents.IsEvidence(testId)) return "Device evidence (snapshots, hardware changes) cannot be deleted.";
            if (LifecycleEvents.IsStageEvent(testId))
                return "Lifecycle events (handovers, returns, QC decisions) cannot be deleted. Record a new one instead (history is kept).";
            return op.CanDeleteRecords ? null : "Only administrators can delete records.";
        }

        public static void EnsureRecord(OperatorRecord op, string department, string testId)
        {
            string why = CheckRecord(op, department, testId);
            if (why != null) throw new UnauthorizedAccessException(why);
        }

        public static void EnsureDelete(OperatorRecord op, string testId)
        {
            string why = CheckDelete(op, testId);
            if (why != null) throw new UnauthorizedAccessException(why);
        }

        public static string Normalize(string department)
        {
            if (string.Equals(department, Departments.Hardware, StringComparison.OrdinalIgnoreCase)) return Departments.Hardware;
            if (string.Equals(department, Departments.Software, StringComparison.OrdinalIgnoreCase)) return Departments.Software;
            if (string.Equals(department, Departments.Qc, StringComparison.OrdinalIgnoreCase)) return Departments.Qc;
            if (string.Equals(department, Departments.Ecommerce, StringComparison.OrdinalIgnoreCase)) return Departments.Ecommerce;
            return null;
        }
    }
}
