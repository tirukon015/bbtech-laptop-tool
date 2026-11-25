using System;
using System.Runtime.Serialization;
using BBTech.Core.Security;

namespace BBTech.Core.Audit
{
    [DataContract]
    public sealed class AuditRecord
    {
        [DataMember] public string AuditId { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        [DataMember] public string ActorUserId { get; set; }
        [DataMember] public string ActorName { get; set; }
        [DataMember] public string Action { get; set; }
        [DataMember] public string Target { get; set; }
        [DataMember] public string OldValue { get; set; }
        [DataMember] public string NewValue { get; set; }
        [DataMember] public string Details { get; set; }
        /// <summary>Checksum of the previous record in the log (hash chain); "" for the first record.</summary>
        [DataMember] public string PreviousChecksum { get; set; }
        /// <summary>Keyed integrity tag (HMAC) over all fields above, including PreviousChecksum.</summary>
        [DataMember] public string Checksum { get; set; }
        [DataMember] public bool Synced { get; set; }

        public static AuditRecord Create(string actorUserId, string actorName, string action, string target, string oldValue = null, string newValue = null, string details = null)
        {
            var rec = new AuditRecord
            {
                AuditId = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                ActorUserId = actorUserId ?? "system",
                ActorName = actorName ?? "System",
                Action = action ?? "UNKNOWN",
                Target = target ?? "",
                OldValue = oldValue ?? "",
                NewValue = newValue ?? "",
                Details = details ?? "",
                Synced = false
            };
            rec.Checksum = rec.ComputeChecksum();
            return rec;
        }

        public string ComputeChecksum()
        {
            string raw = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}",
                AuditId, Timestamp.ToUniversalTime().Ticks, ActorUserId, ActorName, Action, Target, OldValue, NewValue, Details, PreviousChecksum ?? "");
            return RecordMac.Compute("audit", raw);
        }

        public bool VerifyIntegrity()
        {
            if (string.IsNullOrEmpty(Checksum)) return false;
            return string.Equals(Checksum, ComputeChecksum(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
