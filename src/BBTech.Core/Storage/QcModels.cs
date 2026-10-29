using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BBTech.Core.Storage
{
    /// <summary>A physical laptop. One device has many inspections.</summary>
    [DataContract]
    public sealed class DeviceRecord
    {
        [DataMember] public string DeviceId { get; set; }
        // Display values (the whole store is encrypted at rest).
        [DataMember] public string SerialNumber { get; set; }
        [DataMember] public string SystemUuid { get; set; }
        [DataMember] public string BoardSerial { get; set; }
        // Matching values: hashes of the cleaned identifiers.
        [DataMember] public string SerialHash { get; set; }
        [DataMember] public string UuidHash { get; set; }
        [DataMember] public string BoardSerialHash { get; set; }
        [DataMember] public string HardwareFingerprint { get; set; }
        [DataMember] public string Manufacturer { get; set; }
        [DataMember] public string Model { get; set; }
        [DataMember] public string ModelNumber { get; set; }
        [DataMember] public DeviceSpecs Specs { get; set; }
        /// <summary>Set when the record was created although its identifiers conflicted with another record.</summary>
        [DataMember] public string IdentityNote { get; set; }
        [DataMember] public DateTime CreatedAt { get; set; }
        [DataMember] public DateTime UpdatedAt { get; set; }

        public string DisplayName => (Manufacturer + " " + Model + " " + ModelNumber).Replace("Unknown", "").Trim();
    }

    /// <summary>Hardware summary (the ITMS fields) at the time of detection/inspection.</summary>
    [DataContract]
    public sealed class DeviceSpecs
    {
        [DataMember] public string Cpu { get; set; }
        [DataMember] public string Generation { get; set; }
        [DataMember] public string Ram { get; set; }
        [DataMember] public string Storage { get; set; }
        [DataMember] public string BatteryHealth { get; set; }
    }

    public static class InspectionStatus
    {
        public const string InProgress = "IN_PROGRESS";
        public const string Completed = "COMPLETED";
        /// <summary>An unfinished inspection the operator chose not to resume. Kept, never deleted.</summary>
        public const string Abandoned = "ABANDONED";
    }

    /// <summary>One QC session on one device. Completed inspections are immutable.</summary>
    [DataContract]
    public sealed class InspectionRecord
    {
        [DataMember] public string InspectionId { get; set; }
        [DataMember] public string DeviceId { get; set; }
        /// <summary>1, 2, 3 ... per device.</summary>
        [DataMember] public int Number { get; set; }
        [DataMember] public string OperatorId { get; set; }
        [DataMember] public string OperatorNameSnapshot { get; set; }
        [DataMember] public string OperatorRoleSnapshot { get; set; }
        [DataMember] public string Status { get; set; }
        /// <summary>PASS / FAIL / INCOMPLETE (set when completed or abandoned).</summary>
        [DataMember] public string OverallStatus { get; set; }
        [DataMember] public string AppVersion { get; set; }
        [DataMember] public DeviceSpecs Specs { get; set; }
        /// <summary>"Required" or "AllTests" (null for inspections recorded before test modes existed).</summary>
        [DataMember] public string TestMode { get; set; }
        /// <summary>Exactly the tests selected for this inspection (null for older inspections).</summary>
        [DataMember] public List<string> SelectedTests { get; set; }
        [DataMember] public DateTime StartedAt { get; set; }
        [DataMember] public DateTime? CompletedAt { get; set; }
        [DataMember] public DateTime CreatedAt { get; set; }
        [DataMember] public DateTime UpdatedAt { get; set; }

        public bool IsOpen => Status == InspectionStatus.InProgress;
    }

    /// <summary>Latest state of one test within an inspection (every change is journaled).</summary>
    [DataContract]
    public sealed class TestResultRecord
    {
        [DataMember] public string TestResultId { get; set; }
        [DataMember] public string InspectionId { get; set; }
        /// <summary>Stable test key, e.g. "Keyboard".</summary>
        [DataMember] public string TestId { get; set; }
        [DataMember] public string TestName { get; set; }
        /// <summary>NOT TESTED / TESTING / PASS / FAIL / SKIPPED.</summary>
        [DataMember] public string Status { get; set; }
        [DataMember] public bool Required { get; set; }
        [DataMember] public string Comment { get; set; }
        [DataMember] public string Details { get; set; }
        [DataMember] public DateTime? StartedAt { get; set; }
        [DataMember] public DateTime? CompletedAt { get; set; }
    }

    public static class SyncEntity
    {
        /// <summary>A physical laptop sent to ITMS (the primary future operation: one laptop → one payload).</summary>
        public const string Device = "device";
        /// <summary>A completed QC inspection (future upload of QC history).</summary>
        public const string Inspection = "inspection";
    }

    /// <summary>
    /// FUTURE_SYNC_RECORD: ITMS transmission bookkeeping for one entity. States are defined in
    /// Integration.TransmissionState (LOCAL, READY_TO_SEND, SENDING, SENT, ITMS_RECEIVED, FAILED).
    /// No network code exists yet; records stay LOCAL.
    /// </summary>
    [DataContract]
    public sealed class SyncRecord
    {
        [DataMember] public string SyncId { get; set; }
        [DataMember] public string EntityType { get; set; }
        [DataMember] public string EntityId { get; set; }
        [DataMember] public string SyncStatus { get; set; }
        [DataMember] public int AttemptCount { get; set; }
        [DataMember] public DateTime? LastAttemptAt { get; set; }
        [DataMember] public string LastError { get; set; }
        /// <summary>The ITMS receipt reference; set only when ITMS confirmed receipt (ITMS_RECEIVED).</summary>
        [DataMember] public string RemoteId { get; set; }
        /// <summary>
        /// UUID of the current send operation (idempotency key). Created when the entity is marked
        /// READY_TO_SEND and reused for every retry of that send.
        /// </summary>
        [DataMember] public string RequestId { get; set; }
        [DataMember] public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>Which managed Wi-Fi network version was provisioned on this laptop (no password).</summary>
    [DataContract]
    public sealed class WifiProvisionRecord
    {
        [DataMember] public string Ssid { get; set; }
        /// <summary>Keyed hash of the network settings incl. password; used only to detect changes.</summary>
        [DataMember] public string SettingsFingerprint { get; set; }
        [DataMember] public bool AllUsers { get; set; }
        [DataMember] public DateTime ProvisionedAt { get; set; }
    }

    /// <summary>One appended journal entry (the encrypted unit on disk).</summary>
    [DataContract]
    public sealed class JournalEntry
    {
        public const string KindDevice = "device";
        public const string KindInspection = "inspection";
        public const string KindResult = "result";
        public const string KindSync = "sync";
        public const string KindWifi = "wifi";

        [DataMember] public string Kind { get; set; }
        /// <summary>Schema version the entry was written with (0 = written before versioning, i.e. version 1).</summary>
        [DataMember] public int SchemaVersion { get; set; }
        [DataMember] public DateTime At { get; set; }
        [DataMember] public DeviceRecord Device { get; set; }
        [DataMember] public InspectionRecord Inspection { get; set; }
        [DataMember] public TestResultRecord Result { get; set; }
        [DataMember] public SyncRecord Sync { get; set; }
        [DataMember] public WifiProvisionRecord Wifi { get; set; }
    }

    /// <summary>An inspection with its results, for history views and reports.</summary>
    public sealed class InspectionView
    {
        public InspectionRecord Inspection { get; set; }
        public List<TestResultRecord> Results { get; set; } = new List<TestResultRecord>();
    }
}
