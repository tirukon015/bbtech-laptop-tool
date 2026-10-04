# Operator Management

Source: `src/BBTech.Core/Settings/ToolSettings.cs` (`SettingsStore`), `UI/Views/SettingsWindow.cs`, `UI/Views/TestView.xaml.cs`.
Tests: `OperatorTests` in `tests/BBTech.Core.Tests/SettingsAndWifiTests.cs`.

## Where operators live

Operators are staff configuration, not laptop data. They are stored on the USB in the encrypted, tamper-evident `BBTech Data\config\settings.dat`, together with the currently selected operator. To share them, copy `BBTech Data\config\` to other BBTech USBs.

## Operator record

| Field | Notes |
|---|---|
| OperatorId | Random identifier |
| Name | Required and trimmed; two active operators can never share a name (case-insensitive) |
| Role | e.g. QC (default), Senior QC, E-commerce, Technician (free text) |
| Active | Deactivated operators are hidden for new inspections |
| CreatedAt / UpdatedAt | Timestamps |

## Managing operators (admin)

Home → **Settings** → admin PIN → **QC operators**:

- **Add operator:** name and role. Takes effect immediately, without rebuilding.
- **Edit:** rename or change the role. Leaving the role empty keeps the current role. Renaming an active operator to the name of another active operator is rejected.
- **Deactivate / Reactivate.** Reactivation is rejected while another active operator uses the same name. This happens, for example, when a new staff member with the same name was added in the meantime; rename one of them first.

The admin PIN is created on first use (4–12 digits). 5 wrong attempts lock Settings for 60 seconds. See [SECURITY.md](SECURITY.md).

## Selecting the operator (QC)

- **TESTED BY** in TEST lists the active operators. The choice is saved on the USB and stays selected for the following laptops and after restarts, until someone picks another name.
- **Switching operators:** another person picks their own name, and new inspections belong to them.

## Historical snapshots

Each inspection stores **OperatorNameSnapshot** and **OperatorRoleSnapshot** at the moment it starts. Renaming, changing the role of or deactivating an operator later **does not change** past inspections, so history always shows who actually performed the test. An inspection in progress keeps its original operator, even if the selection changes.

## Example data

Names such as *Jimmy* and *Rahim* in tests and screenshots are **example data** used during development. They are not real BBTech staff records.
