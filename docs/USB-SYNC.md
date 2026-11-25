# USB Offline Queue and Server Synchronization

The USB is **temporary transport**, not the database. Every saved result is written to two places:

1. the laptop's encrypted QC journal (`C:\ProgramData\BBTech\LaptopTool\`, the local source for QC on that laptop), and
2. the USB queue `BBTech Data\sync\sync_queue.dat`, waiting for the BBTech server.

Audit events are queued the same way (`BBTech Data\audit\audit_log.dat`).

## State machine

```text
PENDING → UPLOADING → SERVER_RECEIVED → VERIFIED → DELETE_ALLOWED → DELETED (removed from the USB)
             │
             └→ FAILED (server error / rejection) → retried on the next pass
```

Enforced in `BBTech.Core.Sync.UsbSyncQueue` and `SyncCoordinator` (not only in the UI):

| Rule | How |
|---|---|
| Never delete before verification | Only `DELETE_ALLOWED` records **with a server receipt** that still pass their integrity tag are removed |
| No skipped steps | `VERIFIED` is reachable only from `UPLOADING`/`SERVER_RECEIVED`; an illegal transition is ignored |
| Only what the server confirmed | A record is verified only if its id is in the server's `VerifiedRecordIds` **and** was in this batch |
| Ambiguous answers are failures | No receipt, an empty or unreadable body, HTTP errors, exceptions: nothing is deleted |
| Rejections stay | Records the server rejects are kept as `FAILED` with the server's reason (visible in Settings → BBTech Server) |
| Crash / restart recovery | Records left in `UPLOADING` are put back to `PENDING` at the start of every pass (also at app start) |
| Idempotent uploads | The server keys results by `RecordId`; a re-sent record is confirmed, never duplicated; a different record with the same id is rejected |
| Tamper evidence | Each queued record carries a keyed integrity tag; an edited record is never uploaded or deleted |
| Damaged file | The queue file is set aside (`*.corrupt-…`) and the last good copy restored, never overwritten |

Every pass is audited (`SYNC_COMPLETED`, `SYNC_FAILED`, `USB_CLEANUP` with the receipt id).

## When does it run?

- At application start (after the master configuration refresh)
- After every saved result (background)
- Settings → BBTech Server → **Sync now** (admin)

It does nothing until a server is configured (Settings → BBTech Server, or `BBTECH_API_URL` / `BBTECH_API_KEY`).
Offline, everything keeps working; records simply wait on the USB.

## Verified on 2026-11-25

- 23 server tests, including the desktop client uploading to the real API + PostgreSQL (retry, duplicates,
  conflicting ids, unauthorised operators, QC PASS refused by the server, offline server).
- Live run: a record queued on the USB was uploaded by the actual `BBTech Laptop Tool.exe` at startup, stored and
  verified in PostgreSQL, and removed from the USB.
