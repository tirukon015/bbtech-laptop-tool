namespace BBTech.Core.Audit
{
    public static class AuditActions
    {
        public const string UserCreated = "USER_CREATED";
        public const string UserDisabled = "USER_DISABLED";
        public const string UserEnabled = "USER_ENABLED";
        public const string RoleChanged = "ROLE_CHANGED";
        public const string PermissionChanged = "PERMISSION_CHANGED";
        public const string ConfigChanged = "CONFIG_CHANGED";
        public const string SoftwarePackageChanged = "SOFTWARE_PACKAGE_CHANGED";
        public const string TestSubmitted = "TEST_RESULT_RECORDED";
        public const string QcSubmitted = "QC_RESULT_RECORDED";
        public const string RecordDeleted = "RECORD_DELETED";
        public const string SyncStarted = "SYNC_STARTED";
        public const string SyncCompleted = "SYNC_COMPLETED";
        public const string SyncFailed = "SYNC_FAILED";
        public const string UsbCleanup = "USB_CLEANUP";
        public const string ActivationExecuted = "ACTIVATION_EXECUTED";
        public const string ActivationChecked = "ACTIVATION_CHECKED";
        public const string OperatorSelected = "OPERATOR_SELECTED";
        public const string OperatorSignedOut = "OPERATOR_SIGNED_OUT";
        public const string AdminUnlocked = "ADMIN_UNLOCKED";
        public const string AdminUnlockFailed = "ADMIN_UNLOCK_FAILED";
        public const string QcDecision = "QC_DECISION_CREATED";
        public const string QcDecisionRejected = "QC_DECISION_REJECTED";
        public const string InstallStarted = "INSTALL_STARTED";
        public const string InstallCompleted = "SOFTWARE_INSTALLED";
        public const string InstallRejected = "INSTALL_REJECTED";
        public const string AuditLogRecovered = "AUDIT_LOG_RECOVERED";
        public const string SyncQueueRecovered = "SYNC_QUEUE_RECOVERED";
        public const string PermissionDenied = "PERMISSION_DENIED";
        public const string SessionStarted = "SESSION_STARTED";
        public const string TestExecuted = "TEST_EXECUTED";
        public const string QcStarted = "QC_STARTED";
        public const string UserChanged = "USER_CHANGED";
        public const string ItmsSubmission = "ITMS_SUBMISSION";

        // Lifecycle (1.5)
        public const string DeviceDetected = "DEVICE_DETECTED";
        public const string HardwareTest = "HARDWARE_TEST";
        public const string HardwareFail = "HARDWARE_FAIL";
        public const string HardwareRetest = "HARDWARE_RETEST";
        public const string HardwarePass = "HARDWARE_PASS";
        public const string HardwareRepair = "HARDWARE_REPAIR";
        public const string HardwareHandover = "HARDWARE_HANDOVER";
        public const string SoftwareStarted = "SOFTWARE_STARTED";
        public const string WindowsInstalled = "WINDOWS_INSTALLED";
        public const string SoftwareCheck = "SOFTWARE_CHECK";
        public const string ActivationCheck = "ACTIVATION_CHECK";
        public const string SoftwarePass = "SOFTWARE_PASS";
        public const string QcItemChecked = "QC_ITEM_CHECKED";
        public const string QcFail = "QC_FAIL";
        public const string QcRework = "QC_REWORK";
        public const string QcPass = "QC_PASS";
        public const string FinalQcStarted = "FINAL_QC_STARTED";
        public const string FinalQcPass = "FINAL_QC_PASS";
        public const string FinalQcFail = "FINAL_QC_FAIL";
        public const string DeviceSnapshot = "DEVICE_SNAPSHOT";
        public const string HardwareChanged = "HARDWARE_CHANGED";

        /// <summary>The audit action for a saved record (one place, so client and reports agree).</summary>
        public static string ForRecord(string department, string testId, string status, bool isRetest)
        {
            switch (testId)
            {
                case Testing.LifecycleEvents.HardwareHandover: return HardwareHandover;
                case Testing.LifecycleEvents.SoftwareHandover: return SoftwarePass;
                case Testing.LifecycleEvents.QcReturn: return QcRework;
                case Testing.LifecycleEvents.QcDecision: return status == "PASS" ? QcPass : QcFail;
                case Testing.LifecycleEvents.DeliveryDecision: return status == "PASS" ? FinalQcPass : FinalQcFail;
                case Testing.LifecycleEvents.WindowsInstall: return status == "PASS" ? WindowsInstalled : status == "TESTING" ? SoftwareStarted : SoftwareCheck;
            }
            if (testId == Testing.LifecycleEvents.DeviceSnapshot) return DeviceSnapshot;
            if (testId == Testing.LifecycleEvents.HardwareChanged) return HardwareChanged;
            if (Testing.LifecycleEvents.IsRepair(testId)) return HardwareRepair;
            if (Testing.LifecycleEvents.IsDeliveryItem(testId)) return FinalQcStarted;
            if (string.Equals(department, Testing.Departments.Qc, System.StringComparison.OrdinalIgnoreCase)) return QcItemChecked;
            if (string.Equals(department, Testing.Departments.Software, System.StringComparison.OrdinalIgnoreCase))
                return testId != null && testId.Contains("Activation") ? ActivationCheck : testId != null && testId.Contains("Install") ? InstallCompleted : SoftwareCheck;
            if (status == "FAIL") return HardwareFail;
            if (isRetest) return HardwareRetest;
            return status == "PASS" ? HardwarePass : HardwareTest;
        }
    }
}
