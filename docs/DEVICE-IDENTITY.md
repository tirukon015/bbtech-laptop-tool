# Device Identity and Matching

Source: `src/BBTech.Core/Identity/DeviceIdentity.cs`, `DeviceMatcher.cs`, `IdentityFactory.cs`.
Tests: `tests/BBTech.Core.Tests/IdentityTests.cs`.

## Why

QC history must be attached to **one physical laptop**. Two "Dell Latitude 5420" laptops are different devices, so a model name, CPU, RAM or storage size can never identify a specific laptop.

## Identifiers used

| Identifier | Windows source | Notes |
|---|---|---|
| BIOS serial number | `Win32_BIOS.SerialNumber` | Usually printed on the laptop's label |
| System UUID | `Win32_ComputerSystemProduct.UUID` | SMBIOS UUID |
| Motherboard serial | `Win32_BaseBoard.SerialNumber` | Changes if the board is replaced |
| Manufacturer | `Win32_ComputerSystem.Manufacturer` | Sanity check only (a different manufacturer counts as a conflict) |
| Model / Model Number | parsed (see [DETECT.md](DETECT.md)) | Display only; never used for matching |

**Never used:** IP or MAC address, Wi-Fi network, RAM or CPU usage, battery level, storage size, Windows user.

## Cleaning

Firmware often contains placeholders shared by many machines. They are discarded, so they never make two laptops look identical:

- **Serials:** empty values, fewer than 3 alphanumeric characters, a single repeated character (`00000000`, `XXXXXXX`), and known placeholders such as "To be filled by O.E.M.", "Default string", "System Serial Number", "Base Board Serial Number", "N/A", `123456789`.
- **UUIDs:** malformed values, all-zero, all-F, and known OEM defaults such as `03000200-0400-0500-0006-000700080009`.

Values are trimmed and upper-cased.

## Hashing and fingerprint

- Each cleaned identifier is hashed: `SHA-256("BBTech.LaptopTool.DeviceIdentity.v1|<kind>|<value>")`. Matching compares hashes.
- **Hardware fingerprint** = SHA-256 over the manufacturer and the three identifier hashes.
- If a laptop reports **no** usable identifier, there is **no fingerprint**. A fingerprint built from the model alone would match every laptop of that model.

## Matching rules (`DeviceMatcher.Match`)

| Situation | Result |
|---|---|
| The laptop has no usable serial, UUID or board serial | **NoIdentifiers**: history is not shown, and TEST cannot save to history |
| No stored device shares any identifier | **NotFound**: "No QC inspection found for this device." |
| Exactly one stored device agrees on ≥ 1 identifier, and nothing disagrees | **Matched** |
| An identifier present on both sides differs, or the manufacturer differs | **Conflict** |
| Several stored devices each agree on some identifier | **Conflict** |
| One side lacks an identifier (e.g. an older record without a UUID) | Tolerated; not a conflict |

**Confidence:** a match requires at least one agreeing stable identifier and zero disagreements. The result reports which identifiers matched (e.g. "serial number, system UUID").

### Example (placeholders)

| Stored device | Current laptop | Result |
|---|---|---|
| SERIAL-EXAMPLE-A, UUID-EXAMPLE-A | SERIAL-EXAMPLE-A, UUID-EXAMPLE-A | Matched |
| SERIAL-EXAMPLE-A, UUID-EXAMPLE-A | SERIAL-EXAMPLE-B, UUID-EXAMPLE-B (same model) | NotFound |
| SERIAL-EXAMPLE-A, UUID-EXAMPLE-A | SERIAL-EXAMPLE-A, UUID-EXAMPLE-X | Conflict |

## Conflict handling

A conflict is **never resolved silently**:

- **DETECT:** shows "Identity conflict – QC history not shown" with the reason. An admin can review the conflicting records with the admin PIN.
- **TEST:** asks staff to either **Create separate record** (a new device flagged with an `IdentityNote`) or **Test without saving**.
- **Typical causes:** motherboard replaced, BIOS serial re-entered incorrectly, or a disk image cloned from another laptop. The last also protects against showing laptop A's history on laptop B after cloning.

## Identity and the encryption key

The same cleaned identifiers drive the **recovery locks** of the QC store key, which reopen the history on the same laptop if Windows DPAPI changes. See [SECURITY.md](SECURITY.md).
