# BBTech Laptop Tool

BBTech's **laptop lifecycle, traceability and QC system**. A portable Windows tool (one EXE on a USB drive, no installation) plus a central BBTech server (API + PostgreSQL).

| | |
|---|---|
| **Organization** | BBTech |
| **Lead Developer / System Architect** | Touhidul Islam Rukon ([@tirukon015](https://github.com/tirukon015)) |
| **Platform** | Windows 10 / Windows 11, x64 |
| **Version** | 1.6.0 (see [CHANGELOG](CHANGELOG.md)) |
| **Status** | Implemented end to end and verified by 771 client + 23 server automated tests and a live client → server → PostgreSQL run. Hands-on acceptance on real laptops (all test panels, real installer, activation, 125–175% display scaling) is still to be done: see [docs/AUDIT-2026-11-25.md](docs/AUDIT-2026-11-25.md) |

---

## Purpose

```text
Operator selects name (kept 1 hour) → laptop detected → Hardware tests → Software checks, approved installs,
Windows/Office status → QC sees the latest Hardware + Software results → QC PASS / FAIL → audit →
USB queue → server verifies → USB copy removed → laptop READY → ITMS
```

The tool is not ITMS: ITMS stays the business system and only receives laptops whose final QC decision is PASS.

## Key features (implemented)

| Area | What it does | Details |
|---|---|---|
| **Lifecycle** | One permanent record per laptop: Hardware (fail / repair / retest) → Software → QC → ready for E-Commerce → final delivery check; QC returns laptops to the right department; full timeline; stage order enforced on the tool and the server | [LIFECYCLE](docs/LIFECYCLE.md) |
| **Technical evidence** | Identity, every RAM module, raw NVMe/SMART drive health, battery capacities, GPU, display, network, TPM, Windows/Office state, each with its source; UNKNOWN when not exposed; part replacements tracked; all sent to ITMS | [LIFECYCLE](docs/LIFECYCLE.md#technical-evidence) |
| **Operators** | Pick your name once; kept 1 hour; departments and permissions per profile; admin profiles and Settings need the admin PIN; fail-closed authorization | [OPERATOR-MANAGEMENT](docs/OPERATOR-MANAGEMENT.md) · [Technician guide](docs/OPERATOR-GUIDE.md) |
| **Hardware** | 16 cards (battery, SSD, RAM, display, keyboard, touchpad, mouse, sound, headphone, microphone, camera, USB, Wi-Fi, Bluetooth, physical, other); every run saved with operator, id, device, time; history kept | [TEST-QC](docs/TEST-QC.md) |
| **Software** | Drivers (no Device Manager), display, keyboard, mouse, scroll, hotkeys, touchpad, audio, microphone, camera; approved installers only (checksum / Microsoft signature); Windows / Office activation status (Microsoft licensing) | [ADMIN-GUIDE](docs/ADMIN-GUIDE.md) |
| **QC gate** | Latest Hardware + Software results (who, when, missing, failed, skipped); configurable required/optional tests; QC PASS only when everything required passed; decisions permanent | [TEST-QC](docs/TEST-QC.md) |
| **BBTech server** | ASP.NET Core API + PostgreSQL: users, permissions, test definitions, approved software, laptop models, results, QC decisions, audit; server re-checks every record | [SERVER](docs/SERVER.md) |
| **USB sync** | Offline queue; nothing leaves the USB before the server verifies it; idempotent, crash-safe | [USB-SYNC](docs/USB-SYNC.md) |
| **Audit** | Hash-chained, keyed local audit log uploaded to the server's append-only table | [SECURITY](docs/SECURITY.md) |
| **Detect / identity** | Specs for listing, stable physical-device identity (serial, UUID, board serial) | [DETECT](docs/DETECT.md) · [DEVICE-IDENTITY](docs/DEVICE-IDENTITY.md) |
| **ITMS** | Sends a laptop only when its latest QC decision is PASS (server-checked when online) | [ITMS-INTEGRATION](docs/ITMS-INTEGRATION.md) |

## System requirements

- **Supported:** Windows 10 x64 (1803 or newer) and Windows 11 x64. Verified on Windows 11 (build 26200). Windows 10 is **not yet verified** on real hardware.
- **Runtime:** .NET Framework 4.7.2 or newer, which is built into supported Windows versions. Nothing to install.
- **No internet, Node.js, Python or Visual Studio** is needed on the laptop being inspected.

## Portable usage

1. Copy `dist\release\BBTech Laptop Tool.exe` to the USB drive (keep the USB's existing `BBTech Data\`). See [docs/INSTALLATION.md](docs/INSTALLATION.md).
2. An admin sets the BBTech server address and the USB's key once (Settings → BBTech Server). Without a server the USB works offline.
3. Technicians: **Start Testing** → pick your name → test → PASS / FAIL. QC records the final decision. See [docs/OPERATOR-GUIDE.md](docs/OPERATOR-GUIDE.md).

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
| Release process · ITMS | [docs/RELEASE.md](docs/RELEASE.md) · [docs/ITMS-INTEGRATION.md](docs/ITMS-INTEGRATION.md) |
| Lifecycle · technical evidence · ITMS gap report | [docs/LIFECYCLE.md](docs/LIFECYCLE.md) · [docs/TRACEABILITY-GAP-REPORT.md](docs/TRACEABILITY-GAP-REPORT.md) |
| Server · USB sync | [docs/SERVER.md](docs/SERVER.md) · [docs/USB-SYNC.md](docs/USB-SYNC.md) |
| Technician guide · Admin guide | [docs/OPERATOR-GUIDE.md](docs/OPERATOR-GUIDE.md) · [docs/ADMIN-GUIDE.md](docs/ADMIN-GUIDE.md) |
| Audit and production readiness | [docs/AUDIT-2026-11-25.md](docs/AUDIT-2026-11-25.md) |
| Development log | [docs/development/](docs/development/) |
| Authorship and development evidence | [AUTHORS.md](AUTHORS.md) · [CONTRIBUTORS.md](CONTRIBUTORS.md) · [docs/PROJECT-OWNERSHIP.md](docs/PROJECT-OWNERSHIP.md) |
| Security policy | [SECURITY.md](SECURITY.md) |

## Development

- **Stack:** C# / WPF, .NET Framework 4.7.2 (`net472`). Built with the .NET SDK 8. Tests use xUnit.
- **Build:** `powershell -ExecutionPolicy Bypass -File build\build.ps1` produces `dist\release\BBTech Laptop Tool.exe`. See [docs/BUILD.md](docs/BUILD.md).
- **Tests:** 771 client tests (`tests/BBTech.Core.Tests`) and 23 server tests against PostgreSQL (`server/BBTech.Api.Tests`), all passing on 2026-11-25. See [docs/TESTING.md](docs/TESTING.md).
- **Server:** `server/` (ASP.NET Core 8 + PostgreSQL 17 via Docker). See [docs/SERVER.md](docs/SERVER.md).
- **Repository:** private GitHub repository `tirukon015/bbtech-laptop-tool`.
- **Third-party tools:** BatteryInfoView, Hard Disk Sentinel and Free PC Audit are embedded from the BBTech toolkit. `Activation.bat` from that toolkit is excluded and never used.

## Author

**Touhidul Islam Rukon**, Lead Developer / System Architect, BBTech
GitHub: [tirukon015](https://github.com/tirukon015) · Email: tirukon015@gmail.com

The implementation was produced with an AI coding assistant (Claude Code) working under the lead developer's direction. See [AUTHORS.md](AUTHORS.md) and [docs/PROJECT-OWNERSHIP.md](docs/PROJECT-OWNERSHIP.md).

## License

No license has been selected. This is a private BBTech repository; do not redistribute without BBTech's permission.
