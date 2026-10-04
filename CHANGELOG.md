# Changelog

All notable changes to BBTech Laptop Tool. Dates are in YYYY-MM-DD format.

## [1.0.0] — Unreleased (in development)

Version 1.0.0 is planned for 2026-10-09 and is **not yet released or tagged**. Daily details: [DAY-01](docs/development/DAY-01.md) (2026-10-04), [DAY-02](docs/development/DAY-02.md) (2026-10-05).

### 2026-10-05

#### Fixed

- **Operators:** renaming or reactivating an operator can no longer create two active operators with the same name.
- **Admin PIN:** only ASCII digits are accepted (`char.IsDigit` also allowed Bengali, Arabic-Indic and full-width digits). A damaged salt or hash now fails verification instead of throwing.
- **Locale independence:** the DETECT CSV timestamp, DETECT export file names, log timestamps and on-screen dates no longer depend on the Windows region (for example the Thai Buddhist calendar produced year 2569).
- **Wi-Fi:**
  - WPA3-Personal networks reject 64-digit hex keys, which SAE does not support.
  - A malformed SSID hex no longer hides the rest of an installed profile.
  - Saving an unchanged network keeps its version, so laptops are not re-provisioned.
  - Native WLAN API exceptions and a stopped WLAN service are reported as friendly failures instead of crashing.
- **QC store:**
  - Validates test status labels, overall results, Wi-Fi records and null ids before writing.
  - Refuses to create a duplicate device record for a known laptop.
  - Reports unknown devices clearly.
  - Reads the journal even when its incomplete tail cannot be cut off.
- **Detection:** first-generation Intel names with a letter before the number ("Core i7 CPU Q 720", "X 920") now get "1st Gen".
- **Reports:**
  - History rows tolerate missing results and duplicate selections.
  - `DateTimeOffset` values are written as ISO 8601.
- **Other:**
  - A failed atomic write no longer leaves a `.tmp` file.
  - `EnvelopeCrypto.Encrypt(null)` throws `ArgumentNullException`.
  - `TestItem.CompletedAt` notifies only on change.

#### Security

- CSV exports neutralise cells that Excel would run as formulas (`=`, `+`, `-`, `@`, tab, CR).

#### Performance

- The test catalog is built once instead of by reflection on every lookup.

#### Tests and tooling

- The unit tests grew from 224 to 587 cases (358 methods). New suites: journal, key vault, crypto, settings store, operator rules, Wi-Fi XML and provisioning, report formats, CSV, history reports, identity rules, parser edge cases, CPU and model naming, test models.
- Added `.gitattributes` (LF normalisation), `.editorconfig` and a GitHub Actions workflow that runs the tests and builds the app.

### Added (2026-10-04)

- **Portable WPF application** (.NET Framework 4.7.2) delivered as one EXE, with the third-party tools embedded and extracted to `%TEMP%` on demand.
- **DETECT:** Brand, Model, Model Number (manufacturer-aware parsing), CPU family, CPU generation, RAM, Storage (multiple drives), Battery Health (BatteryInfoView with WMI and `powercfg` fallbacks), serial number, system UUID, motherboard serial.
- **QC history in DETECT:** automatic lookup of the laptop's local history, showing "QC Inspection Found ✓", "No QC inspection found for this device." or an identity-conflict warning.
- **TEST / QC:**
  - 12 hardware test panels: Display, Sound, Keyboard, Battery, Touchpad, Camera, Microphone, Headphone, USB, Wi-Fi, Bluetooth, Storage.
  - The NOT TESTED / TESTING / PASS / FAIL / SKIPPED states and the PASS / FAIL / INCOMPLETE overall result.
- **Test modes:** REQUIRED (Display, Sound, Keyboard, Battery mandatory, plus "+ Add Tests") and ALL TESTS (every test, including future ones).
- **Inspection lifecycle:**
  - Each result is saved immediately.
  - Complete Inspection makes the inspection immutable; Start over closes it as abandoned.
  - Unfinished inspections are offered for resume after an interruption.
- **Encrypted local store** in `C:\ProgramData\BBTech\LaptopTool\`: an append-only journal with crash-tail repair and skipping of damaged records.
- **Device identity:** placeholder filtering, hashed identifiers, hardware fingerprint, conflict detection.
- **Operators:** admin-managed list (add, edit, deactivate), selected once per USB session, with a name/role snapshot on every inspection.
- **Admin PIN** for Settings: PBKDF2-SHA256, lockout after 5 wrong attempts.
- **Wi-Fi Manager:**
  - Encrypted managed-network list on the USB.
  - Provisioning as native Windows all-user auto-connect profiles, read back and verified.
  - Unchanged profiles are skipped, and a working connected profile is kept.
- **Reports:** CSV and JSON with an identification block, inspection ID, operator, role, test mode and app version; file names `{Brand}_{Model}_{ModelNumber}_{Serial}_{YYYYMMDD}_{STATUS}`.
- **Sound test:** left / right / both tones, frequency sweep, and the BBTech test media playing inside the app (optional; a "BBTech test media is unavailable." message when missing).
- **Windows-native drive counters** (wear, power-on hours, temperature) when Windows provides them. HD Sentinel is optional.
- **Responsive, DPI-aware UI** (PerMonitorV2) with the official BBTech logo, splash screen and multi-resolution icon.
- **ITMS preparation:** `ISyncService`, `IItmsClient` and `SyncRecord` (LOCAL_ONLY), with no network code.

### Security

- QC history is encrypted at rest with AES-256-CBC + HMAC-SHA256. The data key is wrapped by Windows DPAPI (LocalMachine) and by per-identifier hardware recovery locks (PBKDF2-SHA256, 60,000 iterations).
- The USB settings file (operators, PIN hash, Wi-Fi networks) is encrypted and authenticated (tamper-evident).
- Logs strip paths and the Windows user name. Test results, operator names, serial numbers, PIN and Wi-Fi passwords are not logged.
- `Activation.bat` from the BBTech toolkit is excluded; there is no activation or licence-bypass code.

### Fixed (during development)

- Text encoding corruption introduced by editing UTF-8 files with Windows PowerShell 5 (repaired; the editing method was changed).
- The ordering of inspections that start in the same instant (now ordered by start time, then inspection number).
- A locked previous tool extraction now falls back to a fresh folder.
- TEST cannot start a test before the laptop is identified and any resume decision is made.

### Documentation

- README, AUTHORS, CONTRIBUTORS, SECURITY policy, CHANGELOG.
- `docs/`: architecture, development, installation, build, testing, security, data storage, device identity, DETECT, TEST/QC, operators, Wi-Fi, reports, troubleshooting, performance, release, ITMS integration, project ownership, daily development log.
