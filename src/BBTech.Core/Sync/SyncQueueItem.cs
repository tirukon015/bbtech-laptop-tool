using System;
using System.Runtime.Serialization;
using BBTech.Core.Security;

namespace BBTech.Core.Sync
{
    /// <summary>
    /// An idempotent sync queue item representing a test, QC, or intake result
    /// stored temporarily on the USB/laptop awaiting verified synchronization with the central server.
    /// </summary>
    [DataContract]
    public sealed class SyncQueueItem
    {
        [DataMember] public string RecordId { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember] public string DeviceId { get; set; }
        [DataMember] public string OperatorId { get; set; }
        [DataMember] public string OperatorName { get; set; }
        [DataMember] public string Department { get; set; }
        [DataMember] public string TestId { get; set; }
        [DataMember] public string TestName { get; set; }
        [DataMember] public string Result { get; set; }
        [DataMember] public string Notes { get; set; }
        [DataMember] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        [DataMember] public string AppVersion { get; set; }
        [DataMember] public string Checksum { get; set; }
        [DataMember] public SyncState State { get; set; } = SyncState.Pending;
        [DataMember] public string ServerReceipt { get; set; }
        [DataMember] public int Attempts { get; set; }
        [DataMember] public string LastError { get; set; }
        /// <summary>Laptop description for the server's device record (display only; identity is DeviceId).</summary>
        [DataMember] public string DeviceSerial { get; set; }
        [DataMember] public string DeviceManufacturer { get; set; }
        [DataMember] public string DeviceModel { get; set; }
        /// <summary>Evidence (snapshot JSON, QC basis, hardware changes). Part of the integrity tag when present.</summary>
        [DataMember] public string Details { get; set; }

        /// <summary>Keyed integrity tag (HMAC) over the record content (not over the sync bookkeeping).</summary>
        public string ComputeChecksum()
        {
            string raw = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}",
                RecordId, DeviceId, OperatorId, OperatorName, Department, TestId, TestName, Result, Notes, Timestamp.ToUniversalTime().Ticks, AppVersion);
            // Evidence (snapshot JSON, QC basis, hardware changes) is covered too, so it cannot be edited on the USB.
            if (!string.IsNullOrEmpty(Details)) raw += "|details:" + DetailsSha256(Details);
            return RecordMac.Compute("sync-queue", raw);
        }

        public static string DetailsSha256(string details)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(details ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        public bool VerifyIntegrity()
        {
            if (string.IsNullOrEmpty(Checksum)) return false;
            return string.Equals(Checksum, ComputeChecksum(), StringComparison.OrdinalIgnoreCase);
        }

        public static SyncQueueItem FromTestResult(string deviceId, string operatorId, string operatorName,
            string department, string testId, string testName, string result, string notes, string appVersion, string recordId = null)
        {
            var item = new SyncQueueItem
            {
                RecordId = !string.IsNullOrEmpty(recordId) ? recordId : Guid.NewGuid().ToString("N"),
                DeviceId = deviceId ?? "",
                OperatorId = operatorId ?? "",
                OperatorName = operatorName ?? "",
                Department = department ?? "",
                TestId = testId ?? "",
                TestName = testName ?? "",
                Result = result ?? "NOT TESTED",
                Notes = notes ?? "",
                Timestamp = DateTime.UtcNow,
                AppVersion = appVersion ?? "1.1.0",
                State = SyncState.Pending
            };
            item.Checksum = item.ComputeChecksum();
            return item;
        }
    }
}
