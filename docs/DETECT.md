# DETECT

Primary users: E-commerce staff. Source: `src/BBTech.LaptopTool/Detect/HardwareDetector.cs`, `BatteryFallbacks.cs`, `src/BBTech.Core/Parsing/*`, `UI/Views/DetectView*.cs`.

## How DETECT starts

1. Home → **DETECT**. Detection runs only when DETECT is chosen, never at application start.
2. `HardwareDetector.DetectAsync` runs on a background thread. Progress text is shown and the UI stays responsive. On the development laptop this took about 1 second.
3. Each step (system, CPU, memory, storage, battery) is isolated. A failing step is logged, a plain-language note is added, and the field shows **Unknown**.
4. The QC history lookup then starts automatically (section 3).

## 1. ITMS fields

| Field | Source | Rule |
|---|---|---|
| **Brand** | `Win32_ComputerSystem.Manufacturer` | Normalised (e.g. "Dell Inc." → Dell, "Hewlett-Packard" → HP, "ASUSTeK COMPUTER INC." → ASUS, "Micro-Star…" → MSI, "TIMI" → Xiaomi). Unknown vendors are title-cased with corporate suffixes removed. Placeholders give **Unknown**. |
| **Model** / **Model Number** | `Model`, `SystemFamily`, `ComputerSystemProduct.Version` | See below |
| **CPU** | `Win32_Processor.Name` | Simple family only: `i3/i5/i7/i9`, `Ryzen 3/5/7/9`, `Ryzen AI 9`, `Core Ultra 5/7/9`, `Core 5/7`, `m3/m5/m7`, `Celeron`, `Pentium`, `Xeon`, `Atom`, `Snapdragon X Elite` … Otherwise **Unknown**. The full name appears only under Details. |
| **Generation** | CPU model number | Intel Core i: 3 digits = 1st; 4 digits = first digit (or first two if the number starts with "1"); 5 digits = first two. An "11th Gen" prefix wins if present. Core Ultra → "Series 1/2". AMD Ryzen → "5000 Series" etc. Otherwise **Unknown**; a generation is never guessed. |
| **RAM** | `Win32_PhysicalMemory` (sum of modules) | e.g. "32 GB". Fallback: OS-visible memory rounded up to the next common size. |
| **Storage** | `MSFT_PhysicalDisk` (fallback `Win32_DiskDrive`) | Each internal drive listed separately (e.g. "512 GB SSD + 1 TB HDD"). Marketing size within ±5 %. SSD/HDD shown only when reliable (media type, NVMe bus, spindle speed, model name). USB drives, including the BBTech USB itself, are external and not counted. |
| **Battery Health** | BatteryInfoView `/sxml`; fallbacks WMI `root\wmi` and `powercfg /batteryreport /xml` | Full-charge ÷ design capacity, **rounded down** (87.9 % shows as "87%"). Two batteries are combined. Implausible values are rejected. Unknown if not determinable. |

### Model / Model Number parsing (`ModelParser`)

- **Picks the most descriptive firmware field.** Lenovo stores the name in Product Version (its Model field holds the machine-type code); Samsung and Huawei use SystemFamily.
- **Removes noise:** brand prefixes, "Notebook PC", "Mobile Workstation PC", a screen size when a number already precedes it ("EliteBook 845 14 inch G11" → "EliteBook 845 G11"), and the ASUS duplicate pattern (`VivoBook_ASUSLaptop X515EA_X515EA`).
- **Splits the name:** the leading purely alphabetic words form the **Model**, and the rest is the **Model Number**.

| Firmware text (examples) | Brand | Model | Model Number |
|---|---|---|---|
| Dell Latitude 5420 | Dell | Latitude | 5420 |
| HP EliteBook 845 G11 | HP | EliteBook | 845 G11 |
| Lenovo ThinkPad T14 Gen 2 | Lenovo | ThinkPad | T14 Gen 2 |
| Surface Laptop 4 | Microsoft | Surface Laptop | 4 |
| Acer Aspire | Acer | Aspire | Unknown |
| Only a machine-type code (e.g. `20XXEXAMPLE`) | Lenovo | Unknown | the code |

## 2. Identification details

Serial number, system UUID and motherboard serial are read and cleaned (see [DEVICE-IDENTITY.md](DEVICE-IDENTITY.md)). The serial and UUID appear under Details and in exports. The motherboard serial is used for matching only and is not exported.

## 3. QC history lookup

```
Device identified ✓ → Searching local QC history... → result
```

- **Read-only:** the laptop's store is opened read-only. On a laptop that was never tested, nothing is created.

| Result | Shown |
|---|---|
| Match with history | **QC Inspection Found ✓**: the latest completed inspection (Inspection #, Tested By, Role, Date, Overall, Test mode, every selected test with its status) and the full list, newest first. Click a row to view or export it. |
| No store, or no matching device | **No QC inspection found for this device.** |
| Identity conflict | **Identity conflict – QC history not shown**, with the reason; **Review records (admin)** requires the admin PIN |
| No identifiers | Explanation that the laptop cannot be matched safely |
| Store locked or unreadable | The reason (e.g. different hardware identity) |

History is never chosen by model, and another laptop's records are never shown.

## 4. Exports

- **Export JSON / CSV / Copy** for the detection result: see [REPORTS.md](REPORTS.md).
- **Send to ITMS** is visible but disabled (**not implemented**).

## 5. When information is unavailable

| Situation | Behaviour |
|---|---|
| Firmware placeholders, unknown vendor | The field shows **Unknown**, with a note |
| Desktop chassis | A note: "This computer reports a desktop chassis" |
| No battery | "No battery detected (desktop PC, or battery removed/disconnected)." |
| BatteryInfoView missing or failing | Windows fallbacks; if all fail: "Battery information is currently unavailable." |
| No internal drive detected | A note; Storage shows **Unknown** |
