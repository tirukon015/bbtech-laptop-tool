# Testing

## 1. Automated tests

```powershell
dotnet test tests\BBTech.Core.Tests -c Release
```

**Result on 2026-10-05: 685 test cases, 685 passed, 0 failed** (xUnit, `net472`; 432 test methods, expanded by `[Theory]` data). Earlier the same day the suite had 587 cases; on 2026-10-04 it had 224.

GitHub Actions (`.github/workflows/ci.yml`) runs the same tests and builds the app on `windows-latest` for every push to `main` and every pull request. The CI build has no embedded third-party tools, because `third_party/` is not in the repository.

| File | Methods | Covers |
|---|---|---|
| `CpuParserTests.cs`, `DetectNamingTests.cs` | 9 + 11 | CPU family and generation (Intel Core i incl. 1st-gen M/Q/X names, Core Ultra, Core 3/5/7, Core m, Ryzen, other families), ordinals, laptop name splitting |
| `ModelParserTests.cs` | 26 | Brand normalisation, Model / Model Number split across vendors |
| `OtherParserTests.cs`, `ParsingEdgeTests.cs` | 25 + 19 | Capacity labels, brand catalog, BatteryInfoView and HD Sentinel parsing, battery health rules, detection export |
| `IdentityTests.cs`, `IdentityRulesTests.cs` | 14 + 17 | Identifier cleaning, hashing, fingerprint, matcher (match / not found / conflict / no identifiers) |
| `QcStoreTests.cs`, `RepositoryRulesTests.cs` | 15 + 16 | Encrypted store end to end, recovery, immutability, input validation, duplicate-record refusal, inspection numbering |
| `JournalTests.cs`, `KeyVaultTests.cs`, `CryptoTests.cs` | 11 + 5 + 18 | Journal format, corruption and tail repair, dual-locked key, AES+HMAC envelope, PBKDF2, PIN hashing, atomic writes |
| `ReportTests.cs`, `ReportFormatTests.cs`, `CsvExportTests.cs`, `HistoryReportTests.cs` | 14 + 20 + 5 + 4 | File naming, JSON writer, CSV/JSON contents, formula neutralisation, locale independence, history rows |
| `SettingsAndWifiTests.cs`, `SettingsStoreTests.cs`, `OperatorRulesTests.cs`, `WifiSettingsTests.cs` | 22 + 17 + 6 + 1 | Encrypted USB settings, tamper detection, backup copy, PIN, operators, managed networks |
| `WlanProfileXmlTests.cs`, `WifiProvisionerTests.cs` | 14 + 22 | WLAN profile XML, validation, provisioning outcomes, verification, adoption, Connect Now, native API failures |
| `TestPlanTests.cs`, `TestCatalogTests.cs`, `TestModelTests.cs`, `TestMediaCheckTests.cs` | 15 + 4 + 19 + 8 | REQUIRED / ALL TESTS modes, catalog, overall result, test items, disabled ITMS connector, speaker media |
| `ItmsIntegrationTests.cs` | 63 | ITMS device payload (keys, order, null for unknown, no ITMS ids, identity and fingerprint rules), submission with request id, batch payload, disabled connector, transmission state machine, idempotent retry, laptop resolution, the one-button send flow, per-laptop transmission flow with a scripted test transport (no network) |
| `QcSchemaTests.cs` | 10 | Schema version on new entries, in-memory upgrade of legacy entries, one-time backup, read-only mode for newer data |

**Not covered by unit tests:** the WPF UI, WMI, the audio/camera/Bluetooth/Wi-Fi hardware APIs and DPAPI on real systems. These were tested manually (section 2). On 2026-10-05 the Release build (with embedded tools, 50.8 MB) was also launched once to confirm the main window opens.

## 2. Manual end-to-end verification (performed)

On **one development laptop** (Lenovo ThinkPad T14 Gen 2i, Intel, Windows 11 build 26200), with two folder copies of the tool standing in for two USBs, during October 2026:

| Scenario | Result |
|---|---|
| QC with all 12 tests, Complete Inspection (PASS) | Passed |
| Operator switch: FAIL inspection by a second operator, then PASS by the first | Passed; correct snapshots in history |
| Crash recovery: app killed mid-inspection, then Resume | Passed |
| DETECT from the second "USB" folder showed the history | Passed |
| Identity: not found / conflict / no identifiers (simulated on a copy of the store) | Passed |
| Store contains no readable text; folder ACL as designed | Passed |
| REQUIRED mode recorded exactly the 4 required tests | Passed |
| Wi-Fi: profile `connectionMode=auto`, protected key, kept after deleting the tool folder | Passed (dummy SSIDs, removed afterwards) |
| Window sizes equivalent to 1366×768 … 2560×1440 | Layout checked |

## 3. Not yet verified

- **Hardware:** a real USB pendrive; a second physical laptop.
- **OS:** Windows 10.
- **Vendors / platforms:** Dell, HP and AMD laptops; SATA disks.
- **Display:** real DPI scaling at 125–175 % (only window sizes in device-independent pixels were tested).
- **Wi-Fi:** automatic reconnection after leaving and returning to range.

### Acceptance checklist (real hardware)

- [ ] Real pendrive: TEST on Laptop A as operator 1 → PASS → remove the pendrive.
- [ ] Second pendrive with only the EXE: DETECT on Laptop A shows "QC Inspection Found ✓", operator 1, PASS.
- [ ] Untested Laptop B: DETECT shows "No QC inspection found for this device."
- [ ] Operator switch across Laptops B and C; DETECT shows the right operator on each.
- [ ] Lenovo, Dell, HP; Windows 10 and 11; Intel and AMD; NVMe and SATA.
- [ ] Camera with the privacy shutter closed shows the real state, never PASS.
- [ ] Missing or disabled Wi-Fi / Bluetooth shows "Not available on this laptop."
- [ ] 1366×768 and 1920×1080 at 125 % and 150 % scaling.
- [ ] Real BBTech network: provision, remove the USB, leave and return to range, reconnects automatically.
