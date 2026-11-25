using System;
using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Storage;

namespace BBTech.Core.Sync
{
    /// <summary>
    /// Thread-safe offline USB sync queue (temporary transport storage, not the permanent database).
    ///
    ///   PENDING -> UPLOADING -> SERVER_RECEIVED -> VERIFIED -> DELETE_ALLOWED -> DELETED (removed)
    ///
    /// Safety rules enforced here (not only in the caller):
    ///  * a record is removed from the USB only when it is DELETE_ALLOWED and carries a server receipt;
    ///  * VERIFIED can only be reached from UPLOADING / SERVER_RECEIVED (never straight from PENDING);
    ///  * records left in UPLOADING by a crash or restart go back to PENDING (<see cref="RecoverInterrupted"/>);
    ///  * a damaged queue file is set aside, never overwritten (<see cref="JsonListFile"/>).
    /// </summary>
    public sealed class UsbSyncQueue
    {
        private static readonly object FileLock = new object();
        private readonly string _queueFilePath;

        public UsbSyncQueue(string queueFilePath)
        {
            _queueFilePath = queueFilePath ?? throw new ArgumentNullException(nameof(queueFilePath));
        }

        /// <summary>Path of the last damaged queue file that was set aside, if any.</summary>
        public string LastQuarantinedFile { get; private set; }

        public void Enqueue(SyncQueueItem item)
        {
            if (item == null) return;
            if (string.IsNullOrEmpty(item.Checksum))
            {
                item.Checksum = item.ComputeChecksum();
            }

            lock (FileLock)
            {
                var items = ReadInternal();
                // Idempotent: do not duplicate RecordId
                if (!items.Any(i => i.RecordId == item.RecordId))
                {
                    items.Add(item);
                    SaveInternal(items);
                }
            }
        }

        public IReadOnlyList<SyncQueueItem> GetPendingItems()
        {
            lock (FileLock)
            {
                return ReadInternal().Where(i => (i.State == SyncState.Pending || i.State == SyncState.Failed) && i.VerifyIntegrity()).ToList();
            }
        }

        /// <summary>Records whose integrity tag does not match (edited on the USB). They are never uploaded or deleted.</summary>
        public IReadOnlyList<SyncQueueItem> GetTamperedItems()
        {
            lock (FileLock)
            {
                return ReadInternal().Where(i => !i.VerifyIntegrity()).ToList();
            }
        }

        public IReadOnlyList<SyncQueueItem> GetAllItems()
        {
            lock (FileLock)
            {
                return ReadInternal();
            }
        }

        /// <summary>
        /// Puts records interrupted mid-upload (app closed, Windows restarted) back to PENDING so they are
        /// retried. The server de-duplicates by RecordId, so a retry of something it already has is safe.
        /// Returns how many were recovered. Call only when no upload is running.
        /// </summary>
        public int RecoverInterrupted()
        {
            lock (FileLock)
            {
                var items = ReadInternal();
                int n = 0;
                foreach (var i in items.Where(x => x.State == SyncState.Uploading || x.State == SyncState.ServerReceived))
                {
                    i.State = SyncState.Pending;
                    i.LastError = "Upload was interrupted; queued again.";
                    n++;
                }
                if (n > 0) SaveInternal(items);
                return n;
            }
        }

        private static readonly Dictionary<SyncState, SyncState[]> AllowedFrom = new Dictionary<SyncState, SyncState[]>
        {
            [SyncState.Uploading] = new[] { SyncState.Pending, SyncState.Failed },
            [SyncState.ServerReceived] = new[] { SyncState.Uploading },
            [SyncState.Verified] = new[] { SyncState.Uploading, SyncState.ServerReceived },
            [SyncState.DeleteAllowed] = new[] { SyncState.Verified },
            [SyncState.Failed] = new[] { SyncState.Uploading, SyncState.ServerReceived, SyncState.Pending },
            [SyncState.Pending] = new[] { SyncState.Failed, SyncState.Uploading, SyncState.ServerReceived },
        };

        /// <summary>
        /// Moves records to <paramref name="newState"/>. Transitions that skip a step (for example
        /// PENDING -> VERIFIED) are ignored, so a bug or a bad server answer cannot make an
        /// unverified record deletable. Returns the number of records changed.
        /// </summary>
        public int TransitionState(IEnumerable<string> recordIds, SyncState newState, string serverReceipt = null, string error = null)
        {
            if (recordIds == null) return 0;
            var set = new HashSet<string>(recordIds);
            if (set.Count == 0) return 0;
            if (!AllowedFrom.TryGetValue(newState, out var from)) return 0; // DELETED is reached only by PurgeVerifiedItems

            lock (FileLock)
            {
                var items = ReadInternal();
                int changed = 0;

                foreach (var item in items)
                {
                    if (!set.Contains(item.RecordId) || !from.Contains(item.State)) continue;
                    item.State = newState;
                    if (!string.IsNullOrEmpty(serverReceipt)) item.ServerReceipt = serverReceipt;
                    if (!string.IsNullOrEmpty(error)) item.LastError = error;
                    if (newState == SyncState.Uploading) item.Attempts++;
                    changed++;
                }

                if (changed > 0) SaveInternal(items);
                return changed;
            }
        }

        /// <summary>
        /// Removes from the USB ONLY records that are DELETE_ALLOWED, carry a server receipt and still
        /// pass their integrity check. Everything else is kept.
        /// </summary>
        public int PurgeVerifiedItems()
        {
            lock (FileLock)
            {
                var items = ReadInternal();
                var remaining = items.Where(i => !(i.State == SyncState.DeleteAllowed &&
                                                    !string.IsNullOrEmpty(i.ServerReceipt) &&
                                                    i.VerifyIntegrity())).ToList();
                int purgedCount = items.Count - remaining.Count;
                if (purgedCount > 0) SaveInternal(remaining);
                return purgedCount;
            }
        }

        private List<SyncQueueItem> ReadInternal()
        {
            var items = JsonListFile.Read<SyncQueueItem>(_queueFilePath, out string aside);
            if (aside != null) LastQuarantinedFile = aside;
            return items;
        }

        private void SaveInternal(List<SyncQueueItem> items) => JsonListFile.Write(_queueFilePath, items);
    }
}
