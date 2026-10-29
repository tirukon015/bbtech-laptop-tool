using BBTech.Core.Integration;

namespace BBTech.Core.Storage
{
    /// <summary>
    /// Versioning of the QC journal entries.
    ///
    /// Every entry is written with <see cref="Current"/>. Older entries are upgraded IN MEMORY while the
    /// journal is replayed; the file itself is never rewritten, so a migration can never destroy
    /// history. A journal that contains entries from a NEWER version is opened read-only, so an older
    /// copy of the tool cannot damage data it does not understand.
    ///
    /// History:
    ///   1  initial format (entries carried no version number).
    ///   2  ITMS transmission states (LOCAL, READY_TO_SEND, SENDING, SENT, ITMS_RECEIVED, FAILED)
    ///      replace LOCAL_ONLY / SYNC_PENDING / SYNCING / SYNCED / SYNC_FAILED; sync records gain
    ///      RequestId (idempotency key) and UpdatedAt.
    /// </summary>
    public static class QcSchema
    {
        public const int Current = 2;

        /// <summary>Entries written before versioning carry 0, which means version 1.</summary>
        public static int VersionOf(JournalEntry e) => e.SchemaVersion <= 0 ? 1 : e.SchemaVersion;

        /// <summary>Upgrades one entry to the current version (in place) and returns it.</summary>
        public static JournalEntry Upgrade(JournalEntry e)
        {
            int v = VersionOf(e);
            if (v < 2 && e.Kind == JournalEntry.KindSync && e.Sync != null)
                e.Sync.SyncStatus = TransmissionState.FromLegacy(e.Sync.SyncStatus);
            if (v < Current) e.SchemaVersion = Current;
            return e;
        }
    }
}
