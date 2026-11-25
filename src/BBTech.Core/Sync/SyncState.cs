namespace BBTech.Core.Sync
{
    /// <summary>
    /// Explicit state machine for offline USB sync records:
    /// PENDING -> UPLOADING -> SERVER_RECEIVED -> VERIFIED -> DELETE_ALLOWED -> DELETED
    /// If anything fails, data remains intact. Deletion NEVER happens before VERIFIED.
    /// </summary>
    public enum SyncState
    {
        Pending,
        Uploading,
        ServerReceived,
        Verified,
        DeleteAllowed,
        Deleted,
        Failed
    }
}
