# Admin Guide

## Access

- **Settings** (Home or sidebar) always asks for the **admin PIN** (4–12 digits; 5 wrong tries lock it for 60 s).
  The PIN is created the first time Settings is opened on a fresh USB, so prepare every new USB yourself.
- Picking an **admin profile** (Super Admin / Admin) on the name screen also asks for the PIN.
- Technicians never need a PIN.

## With the BBTech server (recommended)

The server is the authority for users, permissions, test rules and approved software ([SERVER.md](SERVER.md)).

1. Issue one key per USB: `POST /v1/admin/workstations` (or `create-workstation` on the server).
   **Register who uses each USB** (`assign-operators <workstationId> <userId>...`). This is required for QC USBs:
   QC decisions, returns and delivery decisions are refused from a USB on which the QC approver is not registered.
2. On each USB: Settings → **BBTech Server** → address (`https://…`) + key → **Save** → **Test connection** →
   **Refresh configuration**.
3. Manage users, departments, permissions, packages, laptop models and test definitions through the admin API.
   Every change is audited and bumps the configuration version; USBs pick it up at the next start or refresh.
4. Users the server does not know are deactivated on the USB (history keeps their names). Removing or deactivating a
   user on the server ends their session on every USB at the next refresh.

## Without a server (offline USB)

Settings → **Team Profiles** manages operators locally (name, role, departments, permissions). Data stays on the USB
until a server is configured; nothing is lost.

## Approved software

Only packages in the master configuration can be installed. A package needs a SHA-256 checksum **or** a trusted
publisher (Authenticode, exact `O=`/`CN=` match), a plain `.exe`/`.msi` name, safe arguments and HTTPS downloads.
The built-in default is Microsoft's online `OfficeSetup.exe` verified by publisher `Microsoft Corporation`.
Put installers in `BBTech Data\installers\` on the USB (offline packages: the folder set in the package).

## Records

- Admins (`records.delete`) can delete a wrong Hardware/Software/QC checklist record; it becomes a tombstone and
  the deletion is audited. **Final QC decisions can never be deleted.**
- Settings → **Audit Logs** shows the local audit trail (hash-chained; edits and removals are detectable).
- Settings → **BBTech Server** shows records still waiting on the USB and any server rejection reasons.
