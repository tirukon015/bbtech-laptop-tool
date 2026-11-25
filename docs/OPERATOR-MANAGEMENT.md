# Operator Management

Source: `BBTech.Core/Settings/ToolSettings.cs` (`OperatorRecord`, `OperatorSession`, `SettingsStore`),
`BBTech.Core/Security/ResultAuthorization.cs`, `UI/Views/UserProfileSelectView.xaml.cs`, server `users*` tables.

## Model

| Field | Notes |
|---|---|
| OperatorId | Stable id (same on the USB and the server) |
| Name | Unique among active operators |
| Role | Technician, QC Inspector, E-Commerce Specialist, Admin, Admin / Co-Founder, Super Admin |
| Departments[] | Hardware, Software, QC, E-Commerce (several allowed; separate from the role) |
| Permissions[] | `hardware.test`, `software.test`, `software.install`, `software.activation.check`, `software.activation.execute`, `qc.inspect`, `qc.approve`, `records.delete`, `users.manage`, ... (`ALL` for admins) |
| Active | Inactive operators cannot be selected; their history stays |

With a BBTech server, the server's list is authoritative and replaces the USB list at every configuration refresh
(`ApplyServerOperators`). Without a server, admins manage the list in Settings → Team Profiles.

## Session (1 hour)

- Selecting a name creates a session valid for **1 hour** (`OperatorSession`, capped at 1 hour, expires if the clock
  moves back). It is kept in the encrypted settings on the USB and in a **signed** workstation file; a forged or
  edited file is ignored.
- Selecting another name replaces the session immediately. Deactivating an operator ends their session.
- Normal technicians: no PIN, no repeated login, no repeated department choice. Admin profiles: admin PIN.
- Every selection is audited (`OPERATOR_SELECTED`, `SESSION_STARTED`).

## Authorization (fail-closed)

Every result write goes through `ResultAuthorization` in the service layer (and again on the server):
no operator, an inactive operator or a department the operator may not use is refused; the final QC decision needs
`qc.approve`; deleting needs `records.delete` and never applies to a final QC decision. Results store the operator's
id and a name snapshot, so renaming never rewrites history.
