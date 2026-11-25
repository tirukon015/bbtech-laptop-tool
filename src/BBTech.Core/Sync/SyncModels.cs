using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using BBTech.Core.Audit;
using BBTech.Core.Settings;

namespace BBTech.Core.Sync
{
    [DataContract]
    public sealed class SyncUploadBatch
    {
        [DataMember] public string BatchId { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember] public string DeviceId { get; set; }
        [DataMember] public DateTime SentAt { get; set; } = DateTime.UtcNow;
        [DataMember] public List<SyncQueueItem> Records { get; set; } = new List<SyncQueueItem>();
        [DataMember] public List<AuditRecord> AuditRecords { get; set; } = new List<AuditRecord>();
    }

    [DataContract]
    public sealed class SyncUploadResult
    {
        [DataMember] public bool Success { get; set; }
        [DataMember] public string ServerReceiptId { get; set; }
        [DataMember] public int ProcessedCount { get; set; }
        [DataMember] public int DuplicateCount { get; set; }
        /// <summary>Test records the server stored AND verified (checksum re-computed server side).</summary>
        [DataMember] public List<string> VerifiedRecordIds { get; set; } = new List<string>();
        /// <summary>Audit records the server stored.</summary>
        [DataMember] public List<string> VerifiedAuditIds { get; set; } = new List<string>();
        /// <summary>Records the server refused (unknown operator, no permission, invalid data). Kept on the USB.</summary>
        [DataMember] public List<RejectedRecord> Rejected { get; set; } = new List<RejectedRecord>();
        [DataMember] public string ErrorMessage { get; set; }

        public static SyncUploadResult Ok(string receiptId, IEnumerable<string> verifiedIds, int processed = 0, int duplicates = 0)
        {
            return new SyncUploadResult
            {
                Success = true,
                ServerReceiptId = receiptId,
                ProcessedCount = processed,
                DuplicateCount = duplicates,
                VerifiedRecordIds = verifiedIds != null ? new List<string>(verifiedIds) : new List<string>()
            };
        }

        public static SyncUploadResult Fail(string error)
        {
            return new SyncUploadResult
            {
                Success = false,
                ErrorMessage = error,
                VerifiedRecordIds = new List<string>()
            };
        }
    }

    /// <summary>
    /// Server-controlled master data. The client caches it (encrypted) for offline use; when online the
    /// server's version is authoritative. New laptop models, packages and test rules need no new release.
    /// </summary>
    [DataContract]
    public sealed class MasterConfigResponse
    {
        [DataMember] public int ConfigVersion { get; set; }
        [DataMember] public DateTime UpdatedAt { get; set; }
        [DataMember] public List<OperatorRecord> Operators { get; set; } = new List<OperatorRecord>();
        [DataMember] public List<Software.SoftwarePackage> Packages { get; set; } = new List<Software.SoftwarePackage>();
        [DataMember] public Testing.QcRequirements Requirements { get; set; }
        [DataMember] public List<LaptopModelInfo> LaptopModels { get; set; } = new List<LaptopModelInfo>();
        [DataMember] public List<string> SupportedWindowsVersions { get; set; } = new List<string>();

        /// <summary>Null when the configuration is safe to apply; otherwise why it is ignored.</summary>
        public string Validate()
        {
            if (Operators == null || Operators.Count == 0) return "The configuration has no operators.";
            if (!Operators.Any(o => o.Active && o.IsAdmin)) return "The configuration has no active administrator.";
            if (Operators.Any(o => string.IsNullOrWhiteSpace(o.OperatorId) || string.IsNullOrWhiteSpace(o.Name))) return "An operator has no id or name.";
            if (Operators.GroupBy(o => o.OperatorId).Any(g => g.Count() > 1)) return "Duplicate operator ids.";
            if (Requirements != null && !Requirements.IsValid) return "The test requirements are incomplete.";
            return null;
        }
    }

    [DataContract]
    public sealed class LaptopModelInfo
    {
        [DataMember] public string Manufacturer { get; set; }
        [DataMember] public string Model { get; set; }
        /// <summary>Packages that must be installed on this model (in addition to globally required ones).</summary>
        [DataMember] public List<string> RequiredPackageIds { get; set; } = new List<string>();
        [DataMember] public string Notes { get; set; }
    }

    /// <summary>Server view of a laptop's readiness (for QC / ITMS).</summary>
    [DataContract]
    public sealed class DeviceStatusResponse
    {
        [DataMember] public string DeviceId { get; set; }
        [DataMember] public string Readiness { get; set; }
        [DataMember] public string QcDecision { get; set; }
        [DataMember] public string QcDecidedBy { get; set; }
        [DataMember] public DateTime? QcDecidedAt { get; set; }
        [DataMember] public string LifecycleStatus { get; set; }
        [DataMember] public int QcReturnCount { get; set; }
    }

    /// <summary>One stored record of a laptop as the server returns it (history restore, timeline).</summary>
    [DataContract]
    public sealed class DeviceHistoryRecord
    {
        [DataMember] public string RecordId { get; set; }
        [DataMember] public string DeviceId { get; set; }
        [DataMember] public string OperatorId { get; set; }
        [DataMember] public string OperatorName { get; set; }
        [DataMember] public string Department { get; set; }
        [DataMember] public string TestId { get; set; }
        [DataMember] public string TestName { get; set; }
        [DataMember] public string Status { get; set; }
        [DataMember] public string Notes { get; set; }
        [DataMember] public string Details { get; set; }
        [DataMember] public DateTime TestedAt { get; set; }

        public Storage.TestResultRecord ToRecord() => new Storage.TestResultRecord
        {
            TestResultId = RecordId, DeviceId = DeviceId, OperatorId = OperatorId, OperatorName = OperatorName, Department = Department,
            TestId = TestId, TestName = TestName, Status = Status, Comment = Notes ?? "", Details = Details, Required = true,
            StartedAt = TestedAt.ToLocalTime(), CompletedAt = Status == "TESTING" || Status == "NOT TESTED" ? (DateTime?)null : TestedAt.ToLocalTime(),
        };
    }

    /// <summary>A record the server refused, and why (it stays on the USB).</summary>
    [DataContract]
    public sealed class RejectedRecord
    {
        [DataMember] public string RecordId { get; set; }
        [DataMember] public string Reason { get; set; }
    }
}
