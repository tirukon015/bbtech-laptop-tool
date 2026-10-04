# BBTech Laptop Tool

A portable Windows utility used by **BBTech** to inspect refurbished laptops. It runs directly from a USB drive as a single executable, with no installation.

| | |
|---|---|
| **Organization** | BBTech |
| **Lead Developer / System Architect** | Touhidul Islam Rukon ([@tirukon015](https://github.com/tirukon015)) |
| **Platform** | Windows 10 / Windows 11, x64 |
| **Version** | 1.0.0 (in development, not yet released; see [CHANGELOG](CHANGELOG.md)) |
| **Status** | Feature-complete for the local-first workflow. Real-hardware acceptance on multiple laptops, Windows 10 and a real USB pendrive is still pending (see [docs/TESTING.md](docs/TESTING.md)) |

---

## Purpose

BBTech refurbishes laptops. Two teams use this tool:

- **QC staff (TEST):** inspect a laptop's hardware and record a PASS / FAIL / INCOMPLETE inspection.
- **E-commerce staff (DETECT):** identify a laptop's specifications for listing (ITMS fields) and see its previous QC history.

> **The USB is the tool. The laptop is the device. The laptop stores its own encrypted QC history.**
> Any BBTech USB can later detect the same physical laptop and read the history stored on it. The original QC USB is not needed.

## Key features (implemented)

| Area | What it does | Details |
|---|---|---|
| **DETECT** | Brand, Model, Model Number, CPU family, CPU generation, RAM, Storage, Battery Health. Then automatic lookup of the laptop's QC history. | [docs/DETECT.md](docs/DETECT.md) |
| **TEST / QC** | "Tested By" operator, **REQUIRED** mode (Display, Sound, Keyboard, Battery) or **ALL TESTS** mode, every result saved immediately, Complete Inspection, crash recovery | [docs/TEST-QC.md](docs/TEST-QC.md) |
| **Device identity** | Matches the physical laptop by BIOS serial, system UUID and motherboard serial. Never by model alone. Conflicts are flagged. | [docs/DEVICE-IDENTITY.md](docs/DEVICE-IDENTITY.md) |
| **Inspection history** | Append-only, encrypted, stored on the laptop in `C:\ProgramData\BBTech\LaptopTool\`. Completed inspections are immutable. | [docs/DATA-STORAGE.md](docs/DATA-STORAGE.md) |
| **Operators** | Admin-managed operator list; operator selected once per USB session; name/role snapshot stored with each inspection | [docs/OPERATOR-MANAGEMENT.md](docs/OPERATOR-MANAGEMENT.md) |
| **Wi-Fi** | Installs managed BBTech networks as native Windows **auto-connect** profiles (verified from Windows) | [docs/WIFI-MANAGER.md](docs/WIFI-MANAGER.md) |
| **Reports** | CSV / JSON exports, only on explicit request, named `{Brand}_{Model}_{ModelNumber}_{Serial}_{YYYYMMDD}_{STATUS}` | [docs/REPORTS.md](docs/REPORTS.md) |
| **Security** | AES-256-CBC + HMAC-SHA256, Windows DPAPI + hardware recovery locks, admin PIN (PBKDF2) | [docs/SECURITY.md](docs/SECURITY.md) |

**Not implemented:** ITMS integration (architecture prepared only, no network code); see [docs/ITMS-INTEGRATION.md](docs/ITMS-INTEGRATION.md).

## System requirements

- **Supported:** Windows 10 x64 (1803 or newer) and Windows 11 x64. Verified on Windows 11 (build 26200). Windows 10 is **not yet verified** on real hardware.
- **Runtime:** .NET Framework 4.7.2 or newer, which is built into supported Windows versions. Nothing to install.
- **No internet, Node.js, Python or Visual Studio** is needed on the laptop being inspected.

## Portable usage

1. Copy `BBTech Laptop Tool.exe` to a USB drive. To share operators and Wi-Fi networks, also copy `BBTech Data\config\`. See [docs/INSTALLATION.md](docs/INSTALLATION.md).
2. Plug the USB into the laptop and double-click **BBTech Laptop Tool.exe**.
3. Choose one of:
   - **DETECT** (E-commerce): specifications and previous QC history appear automatically.
   - **TEST** (QC): pick your name once, run the tests, then **COMPLETE INSPECTION**.
   - **Wi-Fi** (utility): **Provision BBTech Wi-Fi** once per laptop.

## Documentation

| Topic | Document |
|---|---|
| Architecture | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Installation / USB preparation | [docs/INSTALLATION.md](docs/INSTALLATION.md) |
| Building | [docs/BUILD.md](docs/BUILD.md) |
| Testing and results | [docs/TESTING.md](docs/TESTING.md) |
| Development guide | [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) |
| DETECT · TEST/QC · Device identity | [docs/DETECT.md](docs/DETECT.md) · [docs/TEST-QC.md](docs/TEST-QC.md) · [docs/DEVICE-IDENTITY.md](docs/DEVICE-IDENTITY.md) |
| Data storage · Security | [docs/DATA-STORAGE.md](docs/DATA-STORAGE.md) · [docs/SECURITY.md](docs/SECURITY.md) |
| Operators · Wi-Fi · Reports | [docs/OPERATOR-MANAGEMENT.md](docs/OPERATOR-MANAGEMENT.md) · [docs/WIFI-MANAGER.md](docs/WIFI-MANAGER.md) · [docs/REPORTS.md](docs/REPORTS.md) |
| Troubleshooting · Performance | [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) · [docs/PERFORMANCE.md](docs/PERFORMANCE.md) |
| Release process · ITMS (planned) | [docs/RELEASE.md](docs/RELEASE.md) · [docs/ITMS-INTEGRATION.md](docs/ITMS-INTEGRATION.md) |
| Development log | [docs/development/](docs/development/) |
| Authorship and development evidence | [AUTHORS.md](AUTHORS.md) · [CONTRIBUTORS.md](CONTRIBUTORS.md) · [docs/PROJECT-OWNERSHIP.md](docs/PROJECT-OWNERSHIP.md) |
| Security policy | [SECURITY.md](SECURITY.md) |

## Development

- **Stack:** C# / WPF, .NET Framework 4.7.2 (`net472`). Built with the .NET SDK 8. Tests use xUnit.
- **Build:** `powershell -ExecutionPolicy Bypass -File build\build.ps1` produces `dist\release\BBTech Laptop Tool.exe`. See [docs/BUILD.md](docs/BUILD.md).
- **Tests:** 224 automated unit tests, all passing on 2026-10-04. See [docs/TESTING.md](docs/TESTING.md).
- **Third-party tools:** BatteryInfoView, Hard Disk Sentinel and Free PC Audit are embedded from the BBTech toolkit. `Activation.bat` from that toolkit is excluded and never used.

## Author

**Touhidul Islam Rukon**, Lead Developer / System Architect, BBTech
GitHub: [tirukon015](https://github.com/tirukon015) · Email: tirukon015@gmail.com

The implementation was produced with an AI coding assistant (Claude Code) working under the lead developer's direction. See [AUTHORS.md](AUTHORS.md) and [docs/PROJECT-OWNERSHIP.md](docs/PROJECT-OWNERSHIP.md).

## License

No license has been selected. This is a private BBTech repository; do not redistribute without BBTech's permission.
