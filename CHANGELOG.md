# Changelog

All notable changes to BBTech Laptop Tool. Dates are in YYYY-MM-DD format.

## [1.6.0] — 2026-11-25 — ITMS traceability gap audit

### Fixed
- NVMe raw health log was never read: the request used the wrong buffer layout (offset 12 instead of 8) **and** the
  wrong protocol type (1 = SCSI instead of 3 = NVMe). Verified on a real ThinkPad: all 17 health-log values are read,
  even as a standard user. An unavailable log states why (ADMIN_REQUIRED only for access denied,
  NOT_SUPPORTED_BY_DRIVER otherwise).
- The Storage test can no longer be passed by hand while SSD health is unmeasured or a measured internal drive is
  CRITICAL/FAILED.
- Every UNKNOWN value carries a reason (NOT_REPORTED, NO_UNIQUE_ID, NOT_CONNECTED, ...); raw drive values carry units.
- SATA SMART stored "0 reallocated sectors" when the drive did not report the attribute. Only reported attributes are
  stored now, each raw, plus pending/uncorrectable sectors, power cycles and LBAs read/written.
- Microsoft 365 (subscription) Office was shown as "Not Activated": now UNKNOWN with the reason; no false FAIL recorded.
- CPU "max clock" was the base clock: now `base_clock_mhz`; turbo maximum UNKNOWN (not exposed).
- Snapshot evidence is now covered by the USB integrity tag and the server content hash.
- An inactive built-in panel (docked, lid closed) is UNKNOWN with a reason and never counts as a hardware change.
- Windows' virtual storage controllers are no longer listed as laptop drivers.

### Added
- Every inventory value carries unit, source, confidence (DETECTED / MEASURED / CALCULATED / VERIFIED / MANUAL /
  UNKNOWN), the formula for calculated values and the reason for unknown ones (ADMIN_REQUIRED, NOT_EXPOSED_BY_HARDWARE,
  NOT_PRESENT, ...).
- `StorageHealthRules`: the only place the SSD health word is decided, from raw values with documented thresholds;
  derived remaining life kept separate from the drive's own percentage used.
- CPU family/model/stepping, battery health with two decimals and its formula, display connection type,
  audio playback vs recording, USB controllers (separate from physical port tests), network PHYSICAL / EXTERNAL / VIRTUAL,
  chipset drivers, TPM details UNKNOWN with ADMIN_REQUIRED.
- Snapshots at Hardware handover, Software handover, QC start, QC decision and the final check (all kept).
- Hardware changes as component / old / new / reason; a change after QC PASS cancels the PASS until QC verifies again.
- READY FOR DELIVERY requires a hardware re-check after QC PASS.
- ITMS payload `bbtech_traceability` v2 with the requested section tree, snapshot history and QC-PASS-vs-current
  hardware comparison; `traceability_sha256` sent and checked against the ITMS receipt (VERIFIED / UNCONFIRMED /
  MISMATCH, mismatch = not delivered).
- ITMS contract 1.1 §3.4 (ITMS stores the record and returns its own SHA-256), with an integration test over real HTTP.
- Real-hardware harnesses (`RealHardwarePipelineTests`, server `RealLaptopSnapshot_…`).
- `docs/TRACEABILITY-GAP-REPORT.md` with real-hardware vs unit-test verification.

## [1.5.0] — 2026-11-25 — Laptop lifecycle, QC traceability and complete device data

### Added
- **Laptop lifecycle** (one engine for the tool and the server, `LaptopLifecycle`): RECEIVED → Hardware
  (fail → repair → retest) → READY_FOR_SOFTWARE → Software → READY_FOR_QC → QC → READY_FOR_ECOMMERCE →
  final delivery check → READY_FOR_DELIVERY / HOLD. QC can return a laptop to Hardware or Software (rework loop);
  the status is always recomputed from the full, never-overwritten history.
- **Stage order enforced** in the service layer and on the server: Software needs the Hardware handover, QC needs
  both, handovers need the stage complete, QC PASS needs everything, the delivery check needs QC PASS.
- **Screens:** stage strip on every department screen (who, when, returned-by-QC reason), "Send to Software" /
  "Send to QC", Windows installation card (start / verify from Windows' own install date), QC screen answers
  READY / NOT READY first and lists items that were repaired earlier, QC FAIL returns the laptop to the right
  department, final delivery check card (with ITMS order reference), laptop **History** timeline.
- **Repairs** are recorded between a FAIL and its retest, so a laptop never looks "always perfect".
- **QC checklist** extended (mouse, microphone, Bluetooth, storage, Windows, software, activation, accessories,
  overall) and the **delivery checklist**, both configuration (server test definitions).
- **Technical evidence:** `TechnicalInventory` with source and confidence for every value: identity, BIOS, chassis,
  baseboard, CPU, each RAM module (part/serial/type/rated + configured speed), each drive with the **raw NVMe health
  log / ATA SMART values**, battery capacities (health = full ÷ design), GPU, EDID display, network adapters, audio,
  camera, Bluetooth, TPM, boot mode, Secure Boot, Windows, Office. Missing values are UNKNOWN, never invented.
  Stored as history; a replaced part creates a "Hardware changed" event. **Details** window for QC.
- **ITMS payload** carries `bbtech_traceability`: identity, lifecycle, inventory with raw values, every result with
  operator and time, repairs, hardware changes, QC and delivery decisions.
- **History survives a Windows reinstall:** stable device id from the hardware identity, encrypted per-laptop copy
  on the USB, restore from the USB or the server.
- **Server:** migration 004 (lifecycle status per device, `lifecycle_events` with from/to/who/why, `device_snapshots`
  as JSONB), server-side lifecycle rules, `/devices/{id}/history | timeline | snapshot`, readiness from the lifecycle,
  management report `/admin/reports/lifecycle`.
- Audit actions: HARDWARE_TEST/FAIL/RETEST/PASS/REPAIR/HANDOVER, SOFTWARE_STARTED, WINDOWS_INSTALLED,
  ACTIVATION_CHECK, SOFTWARE_PASS, QC_ITEM_CHECKED, QC_FAIL, QC_REWORK, QC_PASS, FINAL_QC_*, DEVICE_SNAPSHOT,
  HARDWARE_CHANGED.

### Fixed
- Windows activation was looked up with a wrong licensing application id, so "activated" could never be confirmed
  from the Microsoft licensing service.
- Driver check counted switched-off paired Bluetooth accessories as laptop driver problems.
- USB docks (DisplayLink) were reported as the laptop's GPU / network adapter.
- Virtualization showed "not supported" while a hypervisor (WSL2/Docker) was running.

## [1.4.0] — 2026-11-25 — Central server, managed software, configurable QC rules

### Added
- **BBTech server** (`server/`): ASP.NET Core 8 API + PostgreSQL schema (users, departments, roles, permissions,
  workstations, devices, test results, QC decisions, audit logs, sync batches/records, software packages,
  installers, laptop models, test definitions, configuration). Append-only guards in the database (audit, QC
  decisions, immutable results). Per-workstation API keys (hash only), admin endpoints need an admin key and an
  admin user from the database. Docker Compose, Dockerfile, migrations, bootstrap commands, 23 tests.
- **Server authority:** every uploaded record is re-authorized with server data; a QC PASS is accepted only when the
  server's own latest results satisfy every required test. Rejected records stay on the USB with the reason.
- **Master configuration** from the server (operators, test definitions, approved packages, laptop models), cached
  encrypted on the USB for offline use; server operators replace the USB list (sessions of removed users end).
- **Hardware:** Mouse and Headphone cards. **Software:** Drivers (problem devices, missing graphics driver: no Device
  Manager needed), Audio, Microphone and Camera checks (reusing the existing panels).
- **Configurable QC rules:** required/optional tests per department; SKIPPED counts only where skipping is allowed
  (e.g. no headphone jack); Windows activation required, Office activation optional by default.
- **Managed installation:** approved packages only (online bootstrapper or offline package), fixed validated
  arguments, HTTPS download, SHA-256 and/or exact Authenticode publisher, elevation only when required, exit code +
  post-install detection, recorded as a Software result and audited.
- **ITMS:** every send path requires QC PASS; when the server is reachable the tool syncs first and requires the
  server to report the laptop READY.
- Settings → **BBTech Server** tab (address, key, test connection, refresh configuration, sync now, queue status).
- Audit events: SESSION_STARTED, TEST_EXECUTED, TEST_RESULT_RECORDED, QC_STARTED, QC_RESULT_RECORDED,
  QC_DECISION_CREATED, SOFTWARE_INSTALLED, USER_CHANGED, ITMS_SUBMISSION (plus the existing ones).

### Fixed
- Authenticode publisher check matched substrings ("Microsoft Corporation Fake Ltd"); now exact O= and CN=.
- `RetryAsync` / `SendAsync` could send to ITMS without QC PASS.
- Activation: a run longer than 60 s was reported as an error; the license status afterwards decides the result.

## [1.3.0] — 2026-11-25 — Department workflow, QC gate and production hardening

### Added
- **Operator profiles:** operators pick their name once, and the choice is kept for 1 hour on this workstation. Operators with
  several departments choose from their own departments only. Administrator profiles require the admin PIN.
- **Hardware, Software and QC dashboards** with per-test history (each run is a new record; the latest counts).
- **QC is the final gate:** QC sees the latest Hardware and Software results for the laptop: who ran each test, when, and what is
  missing or failed. QC PASS is only possible when every required Hardware, Software and QC checklist item
  passed. QC FAIL can always be recorded. The decision stores a snapshot of what QC saw and cannot be deleted.
- **ITMS:** a laptop can be sent to ITMS only when its latest final QC decision is PASS.
- **Audit log:** operator selection, admin unlocks, test results, QC decisions, deletions, installs, activation
  and sync are recorded in a hash-chained log with keyed integrity tags, so edits and removals are detectable.
- **USB sync queue** for the central BBTech API. It has a separate setting from ITMS and stays disabled until configured.
- Windows and Office activation status checks (Microsoft WMI licensing classes), recorded as Software results.

### Security fixes
- Settings (users, permissions, Wi-Fi passwords, ITMS key) now always require the admin PIN.
- Permission checks fail closed: with no signed-in operator, no result is recorded, nothing is installed and nothing is activated.
- Results are never attributed to a fallback name ("Technician", or the first operator in the list).
- The workstation session file is signed. A forged or edited file is ignored. Sessions never exceed 1 hour.
- Installers run only from the approved USB folder, and only with a matching SHA-256 or a valid Microsoft
  Authenticode signature. The user's Downloads folder is no longer searched. Product keys are format-checked.

### Data safety fixes
- A damaged audit log or sync queue is set aside and the last good copy is restored, instead of being overwritten.
- USB records are removed only after the server returns a receipt that names them. Records interrupted by a crash
  or restart are queued again. An empty or unreadable server answer never counts as success.
- Dashboards show why results cannot be saved (identity conflict, no identifiers, store unavailable) instead of
  silently discarding them.

### Release
- There is one release location (`dist\release`). The build fails instead of leaving an old EXE in place, removes stale
  copies, and stamps the commit into the EXE (with `-dirty` for builds from uncommitted changes).

## [1.2.0] — 2026-11-07

### Added & Optimized
- **Mandatory UTF-8 JSON QR Format:** Single-standard uncompressed UTF-8 JSON QR code schema (`schema_version: "1.0"`) eliminating legacy compression friction with handheld and 2D hardware barcode scanners.
- **Enhanced QR Screen Readability:** Switched to `EccLevel.L` (7% redundancy) to lower QR matrix version from Version 14 to Version 10/11, enlarging individual code modules on screen by ~35%.
- **Crisp Pixel Rendering:** Enabled WPF `RenderOptions.SetEdgeMode(Aliased)` and `BitmapScalingMode.NearestNeighbor` across a 440×440px viewport to eliminate sub-pixel antialiasing and blur during screen scanning.
- **SSD Health Engine & Percentage Scoring:** Fully integrated SSD wear percentages and health status parsing across NVMe, SATA, and eMMC drives.
- **Hardware Elevation Helper:** Streamlined admin privilege elevation on startup to guarantee raw access to storage controllers and WMI diagnostics.

## [1.1.0] — 2026-10-29

### Added
- **Direct ITMS Submission Dialog:** Native `HttpItmsConnector` with retry idempotency and offline queue fallback.
- **QC Diagnostics Integration:** Comprehensive diagnostic test suites bundled into payload schema.

## [1.0.0] — 2026-10-09

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
