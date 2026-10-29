using System;
using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Storage;

namespace BBTech.Core.Integration
{
    /// <summary>
    /// Transmission state of one laptop (or inspection) towards ITMS, stored in its <see cref="SyncRecord"/>.
    ///
    ///   LOCAL ──► READY_TO_SEND ──► SENDING ──► SENT ──► ITMS_RECEIVED
    ///                 ▲   │             │          │
    ///                 │   └─► LOCAL     └─► FAILED ◄┘
    ///                 └──────────────────── FAILED (retry)
    ///
    /// SENT only means the request left the laptop. A laptop counts as registered ONLY in
    /// ITMS_RECEIVED, which requires the receipt reference returned by ITMS.
    /// </summary>
    public static class TransmissionState
    {
        public const string Local = "LOCAL";
        public const string ReadyToSend = "READY_TO_SEND";
        public const string Sending = "SENDING";
        public const string Sent = "SENT";
        public const string ItmsReceived = "ITMS_RECEIVED";
        public const string Failed = "FAILED";

        public static readonly string[] All = { Local, ReadyToSend, Sending, Sent, ItmsReceived, Failed };

        private static readonly Dictionary<string, string[]> Allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { Local, new[] { ReadyToSend } },
            { ReadyToSend, new[] { Local, Sending } },
            { Sending, new[] { Sent, Failed } },
            { Sent, new[] { ItmsReceived, Failed } },
            { Failed, new[] { ReadyToSend } },
            { ItmsReceived, new string[0] },
        };

        public static bool IsKnown(string state) => state != null && Allowed.ContainsKey(state);

        public static bool CanTransition(string from, string to) =>
            IsKnown(from) && IsKnown(to) && Allowed[from].Contains(to);

        /// <summary>True only when ITMS has confirmed receipt.</summary>
        public static bool IsConfirmed(SyncRecord r) => r != null && r.SyncStatus == ItmsReceived && !string.IsNullOrWhiteSpace(r.RemoteId);

        /// <summary>
        /// Returns a NEW record in state <paramref name="to"/>. Throws when the transition is not allowed.
        ///
        /// Idempotency: LOCAL → READY_TO_SEND assigns the request id of the send operation. A retry
        /// (FAILED → READY_TO_SEND) keeps it, so ITMS sees the same request again and can answer
        /// "already processed". Withdrawing (READY_TO_SEND → LOCAL) drops it.
        /// </summary>
        /// <param name="error">Required for FAILED (a staff-readable reason, never credentials).</param>
        /// <param name="receipt">Required for ITMS_RECEIVED: the reference ITMS returned for the received laptop.</param>
        /// <param name="newRequestId">Required for LOCAL → READY_TO_SEND: a new UUID for this send operation.</param>
        public static SyncRecord Transition(SyncRecord current, string to, DateTime now, string error = null, string receipt = null,
                                           string newRequestId = null)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (!CanTransition(current.SyncStatus, to))
                throw new InvalidOperationException("ITMS transmission cannot change from " + (current.SyncStatus ?? "(none)") + " to " + (to ?? "(none)") + ".");
            if (to == Failed && string.IsNullOrWhiteSpace(error))
                throw new ArgumentException("A failed transmission needs a reason.", nameof(error));
            if (to == ItmsReceived && string.IsNullOrWhiteSpace(receipt))
                throw new ArgumentException("ITMS_RECEIVED requires the receipt reference returned by ITMS.", nameof(receipt));
            bool startsRequest = current.SyncStatus == Local && to == ReadyToSend;
            if (startsRequest && !Guid.TryParse(newRequestId, out _))
                throw new ArgumentException("A new send operation needs a UUID request id.", nameof(newRequestId));
            if (to == ReadyToSend && !startsRequest && !Guid.TryParse(current.RequestId, out _))
                throw new InvalidOperationException("A retry needs the request id of the original send operation.");

            var next = new SyncRecord
            {
                SyncId = current.SyncId,
                EntityType = current.EntityType,
                EntityId = current.EntityId,
                SyncStatus = to,
                AttemptCount = current.AttemptCount,
                LastAttemptAt = current.LastAttemptAt,
                LastError = current.LastError,
                RemoteId = current.RemoteId,
                RequestId = current.RequestId,
                UpdatedAt = now,
            };
            switch (to)
            {
                case ReadyToSend:
                    if (startsRequest) next.RequestId = newRequestId.Trim();
                    break;
                case Local:
                    next.RequestId = null;
                    break;
                case Sending:
                    next.AttemptCount++;
                    next.LastAttemptAt = now;
                    next.LastError = null;
                    break;
                case Failed:
                    next.LastError = error.Trim();
                    break;
                case ItmsReceived:
                    next.RemoteId = receipt.Trim();
                    next.LastError = null;
                    break;
            }
            return next;
        }

        /// <summary>Maps the status names stored before schema version 2 (LOCAL_ONLY, SYNC_PENDING ...).</summary>
        public static string FromLegacy(string status)
        {
            switch (status)
            {
                case "LOCAL_ONLY": return Local;
                case "SYNC_PENDING": return ReadyToSend;
                case "SYNCING": return Sending;
                // A legacy "SYNCED" never carried an ITMS receipt, so it cannot count as received.
                case "SYNCED": return Sent;
                case "SYNC_FAILED": return Failed;
                default: return status;
            }
        }
    }
}
