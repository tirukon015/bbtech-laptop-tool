using System;
using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Settings;
using BBTech.Core.Storage;

namespace BBTech.Core.Audit
{
    /// <summary>Result of checking the whole audit log.</summary>
    public sealed class AuditChainReport
    {
        public int Total { get; set; }
        /// <summary>Records whose keyed tag does not match their content (edited by hand).</summary>
        public List<string> TamperedIds { get; } = new List<string>();
        /// <summary>Records whose PreviousChecksum does not link to the record before them (inserted/removed).</summary>
        public List<string> BrokenLinkIds { get; } = new List<string>();
        public bool Intact => TamperedIds.Count == 0 && BrokenLinkIds.Count == 0;
    }

    /// <summary>
    /// Thread-safe, append-only, hash-chained audit log. Each record carries a keyed tag and the tag of
    /// the record before it, so editing, inserting or removing records is detectable (<see cref="VerifyChain"/>).
    /// A damaged file is set aside, never overwritten (see <see cref="JsonListFile"/>).
    /// There is no API to edit or delete records.
    /// </summary>
    public sealed class AuditStore
    {
        private static readonly object FileLock = new object();
        private readonly string _filePath;

        public AuditStore(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        public AuditRecord Log(OperatorRecord actor, string action, string target, string oldValue = null, string newValue = null, string details = null)
        {
            var rec = AuditRecord.Create(
                actor?.OperatorId ?? "system",
                actor?.Name ?? "System",
                action,
                target,
                oldValue,
                newValue,
                details);

            Append(rec);
            return rec;
        }

        public void Append(AuditRecord record)
        {
            if (record == null) return;

            lock (FileLock)
            {
                var list = ReadAllInternal();
                record.PreviousChecksum = list.Count > 0 ? list[list.Count - 1].Checksum : "";
                record.Checksum = record.ComputeChecksum();
                list.Add(record);
                JsonListFile.Write(_filePath, list);
            }
        }

        public IReadOnlyList<AuditRecord> GetRecords(OperatorRecord requester)
        {
            // Fail closed: viewing requires an identified operator with the audit permission.
            if (requester == null || !requester.CanViewAudit)
            {
                throw new UnauthorizedAccessException("Access Denied: You do not have permission to view audit logs.");
            }

            lock (FileLock)
            {
                return ReadAllInternal().OrderByDescending(r => r.Timestamp).ToList();
            }
        }

        /// <summary>Checks every record's tag and the chain links, in file order.</summary>
        public AuditChainReport VerifyChain()
        {
            lock (FileLock)
            {
                var list = ReadAllInternal();
                var report = new AuditChainReport { Total = list.Count };
                string prev = "";
                foreach (var r in list)
                {
                    if (!r.VerifyIntegrity()) report.TamperedIds.Add(r.AuditId);
                    if (!string.Equals(r.PreviousChecksum ?? "", prev, StringComparison.OrdinalIgnoreCase)) report.BrokenLinkIds.Add(r.AuditId);
                    prev = r.Checksum ?? "";
                }
                return report;
            }
        }

        public IReadOnlyList<AuditRecord> GetPendingSyncRecords()
        {
            lock (FileLock)
            {
                return ReadAllInternal().Where(r => !r.Synced && r.VerifyIntegrity()).ToList();
            }
        }

        /// <summary>Marks records as received by the server. "Synced" is bookkeeping and is not part of the tag.</summary>
        public void MarkSynced(IEnumerable<string> auditIds)
        {
            if (auditIds == null) return;
            var set = new HashSet<string>(auditIds);
            if (set.Count == 0) return;

            lock (FileLock)
            {
                var list = ReadAllInternal();
                bool changed = false;
                foreach (var r in list)
                {
                    if (set.Contains(r.AuditId) && !r.Synced)
                    {
                        r.Synced = true;
                        changed = true;
                    }
                }
                if (changed) JsonListFile.Write(_filePath, list);
            }
        }

        /// <summary>Path of the last damaged log that was set aside, if any (for the admin to report).</summary>
        public string LastQuarantinedFile { get; private set; }

        private List<AuditRecord> ReadAllInternal()
        {
            var list = JsonListFile.Read<AuditRecord>(_filePath, out string aside);
            if (aside != null)
            {
                LastQuarantinedFile = aside;
                // Record the event itself, chained onto whatever was recovered.
                var note = AuditRecord.Create("system", "System", AuditActions.AuditLogRecovered, "AuditLog", null, null,
                    "Damaged audit log set aside as " + System.IO.Path.GetFileName(aside));
                note.PreviousChecksum = list.Count > 0 ? list[list.Count - 1].Checksum : "";
                note.Checksum = note.ComputeChecksum();
                list.Add(note);
                JsonListFile.Write(_filePath, list);
            }
            return list;
        }
    }
}
