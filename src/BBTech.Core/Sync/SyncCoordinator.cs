using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBTech.Core.Audit;
using BBTech.Core.Settings;

namespace BBTech.Core.Sync
{
    /// <summary>What one sync attempt did (for logs and the status line; the technician never needs it).</summary>
    public sealed class SyncRunOutcome
    {
        public bool Attempted { get; set; }
        public bool Success { get; set; }
        public int Uploaded { get; set; }
        public int Verified { get; set; }
        public int Purged { get; set; }
        public int KeptUnverified { get; set; }
        public int Rejected { get; set; }
        public int Recovered { get; set; }
        public string ReceiptId { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// One upload pass of the USB queue to the central BBTech API:
    ///
    ///   recover interrupted → PENDING → UPLOADING → (server receipt) → VERIFIED → DELETE_ALLOWED → removed
    ///
    /// Only records the server lists in <see cref="SyncUploadResult.VerifiedRecordIds"/> AND that were in
    /// this batch are verified and removed. Everything else returns to PENDING (or FAILED) and stays on
    /// the USB. No server, no URL, no receipt, a crash, or an exception: nothing is deleted.
    /// One pass at a time per process.
    /// </summary>
    public sealed class SyncCoordinator
    {
        private static int _running;
        private readonly IBBTechApiClient _api;
        private readonly UsbSyncQueue _queue;
        private readonly AuditStore _audit;

        public SyncCoordinator(IBBTechApiClient api, UsbSyncQueue queue, AuditStore audit)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }

        public async Task<SyncRunOutcome> RunOnceAsync(OperatorRecord actor = null)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return new SyncRunOutcome { Message = "A sync is already running." };
            try
            {
                var outcome = new SyncRunOutcome();
                // No other pass can be running here, so UPLOADING records are leftovers of a crash/restart.
                outcome.Recovered = _queue.RecoverInterrupted();

                if (!_api.IsConfigured)
                {
                    outcome.Message = "BBTech server not configured; records stay on the USB.";
                    return outcome;
                }

                var pending = _queue.GetPendingItems();
                var pendingAudit = _audit.GetPendingSyncRecords();
                if (pending.Count == 0 && pendingAudit.Count == 0)
                {
                    outcome.Success = true;
                    outcome.Message = "Nothing to sync.";
                    return outcome;
                }

                if (!await _api.CheckConnectivityAsync().ConfigureAwait(false))
                {
                    outcome.Message = "BBTech server not reachable; records stay on the USB.";
                    return outcome;
                }

                outcome.Attempted = true;
                var batchIds = pending.Select(p => p.RecordId).ToList();
                _queue.TransitionState(batchIds, SyncState.Uploading);
                outcome.Uploaded = batchIds.Count;

                SyncUploadResult result;
                try
                {
                    result = await _api.UploadRecordsAsync(new SyncUploadBatch
                    {
                        DeviceId = pending.FirstOrDefault()?.DeviceId ?? "",
                        Records = pending.ToList(),
                        AuditRecords = pendingAudit.ToList(),
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = SyncUploadResult.Fail("Upload failed: " + ex.Message);
                }

                if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.ServerReceiptId))
                {
                    string err = result?.ErrorMessage ?? "No receipt from the server.";
                    _queue.TransitionState(batchIds, SyncState.Failed, null, err);
                    _audit.Log(actor, AuditActions.SyncFailed, "CentralServer", null, null, err + " Records kept on USB.");
                    outcome.KeptUnverified = batchIds.Count;
                    outcome.Message = err;
                    return outcome;
                }

                // Only IDs that were in THIS batch and that the server explicitly verified.
                var batchSet = new HashSet<string>(batchIds);
                var verified = (result.VerifiedRecordIds ?? new List<string>()).Where(batchSet.Contains).Distinct().ToList();
                var rejected = (result.Rejected ?? new List<RejectedRecord>()).Where(r => r != null && batchSet.Contains(r.RecordId) && !verified.Contains(r.RecordId)).ToList();
                var rejectedIds = new HashSet<string>(rejected.Select(r => r.RecordId));
                var unverified = batchIds.Except(verified).Where(id => !rejectedIds.Contains(id)).ToList();
                foreach (var r in rejected)
                    _queue.TransitionState(new[] { r.RecordId }, SyncState.Failed, null, "Rejected by server: " + r.Reason);
                if (rejected.Count > 0)
                    _audit.Log(actor, AuditActions.SyncFailed, "CentralServer", null, null,
                        rejected.Count + " record(s) rejected by the server and kept on the USB: " + string.Join("; ", rejected.Select(r => r.RecordId + " (" + r.Reason + ")")));

                _queue.TransitionState(verified, SyncState.Verified, result.ServerReceiptId);
                _queue.TransitionState(verified, SyncState.DeleteAllowed);
                outcome.Purged = _queue.PurgeVerifiedItems();
                if (unverified.Count > 0)
                    _queue.TransitionState(unverified, SyncState.Pending, null, "Server receipt " + result.ServerReceiptId + " did not confirm this record.");

                var auditSet = new HashSet<string>(pendingAudit.Select(a => a.AuditId));
                _audit.MarkSynced((result.VerifiedAuditIds ?? new List<string>()).Where(auditSet.Contains));

                outcome.Success = true;
                outcome.Verified = verified.Count;
                outcome.KeptUnverified = unverified.Count + rejected.Count;
                outcome.Rejected = rejected.Count;
                outcome.ReceiptId = result.ServerReceiptId;
                outcome.Message = string.Format("Synced: {0} verified, {1} removed from USB, {2} kept.", verified.Count, outcome.Purged, unverified.Count);

                _audit.Log(actor, AuditActions.SyncCompleted, "CentralServer", null, result.ServerReceiptId, outcome.Message);
                if (outcome.Purged > 0)
                    _audit.Log(actor, AuditActions.UsbCleanup, "UsbQueue", null, null,
                        string.Format("Removed {0} server-verified records from the USB queue (receipt {1}).", outcome.Purged, result.ServerReceiptId));
                return outcome;
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }
    }
}
