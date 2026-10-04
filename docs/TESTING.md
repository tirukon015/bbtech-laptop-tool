# Testing

## 1. Automated tests

```powershell
dotnet test tests\BBTech.Core.Tests -c Release
```

**Result on 2026-10-04: 224 tests, 224 passed, 0 failed** (xUnit, `net472`). The test methods below expand into 224 cases through `[Theory]` data.

| File | Test methods | Covers |
|---|---|---|
| `CpuParserTests.cs` | 8 | CPU family and generation (Intel Core i, Core Ultra, Ryzen, others) |
| `ModelParserTests.cs` | 26 | Brand normalisation, Model / Model Number split across vendors |
| `OtherParserTests.cs` | 25 | Capacity formatting, BatteryInfoView and HD Sentinel parsing, detection export |
| `IdentityTests.cs` | 14 | Identifier cleaning, hashing, fingerprint, matcher (match / not found / conflict / no identifiers) |
| `QcStoreTests.cs` | 15 | Encrypted journal, corruption and partial-tail recovery, dual-locked key, DPAPI loss recovery, different-machine lockout, inspections, immutability, history order |
| `ReportTests.cs` | 14 | Report file naming, sanitising, NOSERIAL / NOMODEL, CSV / JSON contents |
| `SettingsAndWifiTests.cs` | 22 | Encrypted settings, tamper detection, PIN, operators, WLAN XML, provisioning, verify, adoption |
| `TestMediaCheckTests.cs` | 8 | Speaker media availability |
| `TestPlanTests.cs` | 15 | REQUIRED / ALL TESTS modes, selection, overall result on selected tests |

**Not covered by unit tests:** the WPF UI, WMI, the audio/camera/Bluetooth/Wi-Fi hardware APIs and DPAPI on real systems. These were tested manually (section 2).

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
