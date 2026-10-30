# Single QR Code Generation, SSD Health Engine & UI Features

**Module:** BBTech Laptop Tool (Latest)  
**Target:** .NET Framework 4.7.2 / WPF  
**Status:** Implemented & Verified  

---

## 1. Single QR Code Generation

### Problem
Previously, payloads approaching or exceeding 2,200 bytes failed to generate with:
`Failed to generate QR code: Payload size (2269 bytes) exceeds QR code capacity.`

### Solution
1. **Payload Compression Pipeline:**
   - Canonicalizes JSON payload with sorted keys and minimal whitespace.
   - Compresses canonical payload via Gzip/Deflate.
   - Encodes compressed bytes with standard base64 and prepends header:
     `BBTECH:GZ1:<base64-content>`
2. **QR Version 40 Byte-Mode Capacity & Adaptive ECC:**
   - Under standard ISO/IEC 18004 QR Byte-Mode:
     - Version 40 @ ECC M: 2,331 bytes
     - Version 40 @ ECC L: 2,953 bytes
   - Encoder attempts ECC Level M first.
   - If payload size > 2,331 bytes, it automatically selects ECC Level L, cleanly supporting payloads up to ~2,953 bytes.
3. **Single Scan Guarantee:**
   - Generates a single high-density 2D barcode.
   - Fully compatible with handheld 2D barcode scanners and webcam/smartphone QR readers.
   - Multi-page / multipart navigation is not required.

---

## 2. SSD Health Engine & Storage Diagnostics

Located in `src/BBTech.LaptopTool/Services/SsdHealthEngine.cs`:
- Evaluates NVMe wear levels, SMART health status, power-on hours, and drive temperature.
- Integrates with:
  - Windows Storage Management (`MSFT_PhysicalDisk`)
  - Hard Disk Sentinel CLI (`HDSentinel.exe /REPORT`)
  - WMI disk indicators (`MSStorageDriver_FailurePredictStatus`, `Win32_DiskDrive`)
- Formats health as `"<Percentage>% (<Status>)"` (e.g. `98% (Healthy)`).

---

## 3. Administrator Elevation Helper

Located in `src/BBTech.LaptopTool/Services/ElevationHelper.cs`:
- Verifies current Windows process token elevation (`WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)`).
- Re-launches seamlessly with `Verb = "runas"` when administrator privileges are requested.

---

## 4. UI Features: Hover to Copy & Full Laptop Name

1. **Hover to Copy on Detect View:**
   - On `DetectView.xaml`, hovering over information cards (Brand, Model, Model Number, CPU, Generation, RAM, Storage, Battery Health) displays a copy icon.
   - Clicking copies the raw detected value to clipboard and provides feedback toast.
2. **Full Laptop Name Display:**
   - Shows formatted unified laptop title:
     `Brand + Model (Product Family) + Model Number + CPU + Generation`
     (e.g., `Lenovo ThinkPad T14 Gen 2i Intel Core i5 11th Gen`).
