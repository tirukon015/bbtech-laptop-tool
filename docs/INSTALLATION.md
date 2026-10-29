# Installation / USB Preparation

The tool is **portable**. Nothing is installed on the laptop being inspected: no installer, no services, no startup entries, no registry changes.

## Requirements (target laptop)

- **Windows:** Windows 10 x64 (1803+) or Windows 11 x64.
  - Windows 11 is verified (build 26200).
  - Windows 10 is **not yet verified** on real hardware.
- **.NET Framework 4.7.2+:** built into supported Windows versions.
- **No internet connection** is needed.

## Preparing a BBTech USB (once per USB)

1. **Copy the EXE:** copy `BBTech Laptop Tool.exe` (from `dist\release\` on the build PC, see [BUILD.md](BUILD.md)) to the USB, in the root or any folder.
2. **Share the settings:** copy `BBTech Data\config\` from an already configured BBTech USB. It holds the encrypted operators, the admin PIN hash and the managed Wi-Fi networks.
   - **First USB:** create these in the app instead: Home → **Settings** → create the admin PIN → add operators and Wi-Fi networks. See [OPERATOR-MANAGEMENT.md](OPERATOR-MANAGEMENT.md) and [WIFI.md](WIFI.md).
3. **No history to copy:** QC history is **not** copied between USBs. It lives on each laptop.

```
USB
├── BBTech Laptop Tool.exe
└── BBTech Data\              (created by the app)
    ├── config\settings.dat   (encrypted settings – copy to other BBTech USBs)
    ├── reports\              (CSV/JSON, only when staff click Export)
    ├── exports\              (DETECT exports)
    └── logs\app.log
```

**Read-only or full USB:** if the folder next to the EXE cannot be written, data and logs fall back to `%LOCALAPPDATA%\BBTech Laptop Tool\data\` and `...\logs\` on that laptop.

## What the app writes on the laptop

| Location | When | Content |
|---|---|---|
| `C:\ProgramData\BBTech\LaptopTool\` | First TEST result or Wi-Fi provisioning | Encrypted QC history and key ([DATA-STORAGE.md](DATA-STORAGE.md)) |
| Windows WLAN profiles | Wi-Fi provisioning | BBTech auto-connect profiles |
| `%TEMP%\BBTechLaptopTool\<version>\` | When a bundled tool is needed | Extracted third-party tools; deleted on exit |

The application does **not** delete the QC history or the Wi-Fi profiles. To remove test data from a laptop manually, an administrator can delete `C:\ProgramData\BBTech\LaptopTool\`; that history is then lost permanently.

## Verifying the EXE

The build writes `dist\release.sha256.txt`. Compare it on the USB:

```powershell
Get-FileHash "E:\BBTech Laptop Tool.exe" -Algorithm SHA256
```

The EXE is **not code-signed**, so Windows SmartScreen may warn on first run ("More info" → "Run anyway").

## Recommended workflow

| Role | Steps |
|---|---|
| QC | TEST → pick your name once → run the tests → **COMPLETE INSPECTION** ([TEST.md](TEST.md)) |
| E-commerce | DETECT on any BBTech USB → specifications and QC history ([DETECT.md](DETECT.md)) |
| Any staff | Wi-Fi → **Provision BBTech Wi-Fi**, once per laptop |
