# Reports

Source: `src/BBTech.Core/Export/Exporters.cs`, `ReportNaming.cs`, `HistoryReport.cs`, `UI/Views/TestView.xaml.cs` (`ReportFiles`), `UI/Views/DetectView*.cs`.
Tests: `ReportTests.cs`, `OtherParserTests.cs` (`ExportTests`), `TestPlanTests.cs`.

Reports are **exports only**; the laptop's encrypted store is the source of truth. A plaintext report is created **only when staff click Export**, and it is written to the USB (`BBTech Data\reports\` or `BBTech Data\exports\`), never automatically onto the laptop.

## 1. Inspection reports (TEST / DETECT history)

**Where:**
- TEST: **Export CSV / Export JSON**, or the buttons after **Complete Inspection**. This exports the current or just-completed inspection.
- DETECT history: **Export CSV / JSON** under any inspection.

### File name

```
{Brand}_{Model}_{ModelNumber}_{SerialNumber}_{YYYYMMDD}_{STATUS}.csv | .json
Dell_Latitude_5420_SERIAL-EXAMPLE_20261004_PASS.csv
```

- **Separators:** `_` separates fields; inside a value, spaces and underscores become `-`.
- **Allowed characters:** characters invalid in Windows file names are removed; only letters, digits, `-`, `.` and `+` remain.
- **Length:** each value is capped at 20–30 characters.
- **Missing values:** `NOSERIAL`, `NOMODEL`, or `UNKNOWN` for brand/model.
- **Status** (`PASS`, `FAIL`, `INCOMPLETE`) is always last.
- **Duplicates:** a second report for the same laptop, day and status gets `-2`, `-3`… after the date, so nothing is overwritten.

### CSV contents

```
Field,Value
Inspection Date,2026-10-04
Inspection Time,14:30:00
Inspection ID,<id>
Inspection Number,2
Tested By,<operator name>
Role,QC
Test Mode,REQUIRED
Started,...
Completed,...
App Version,1.0.0
Brand,Dell
Model,Latitude
Model Number,5420
Serial Number,SERIAL-EXAMPLE
System UUID,UUID-EXAMPLE
CPU,i5
Generation,11th Gen
RAM,16 GB
Storage,512 GB SSD
Battery Health,87%
Overall Result,PASS
Selected Tests,Display; Sound; Keyboard; Battery

Test,Status,Comment
Display,PASS,
Sound,PASS,
Keyboard,PASS,Keys 79/79
Battery,PASS,Health 87%, ...
```

- **Which tests:** only the tests **selected** for that inspection are listed. A selected test without a result is shown as NOT TESTED.
- **Comments:** the evidence the test recorded, plus the operator's comment.
- **Exports of unsaved results** (nothing stored on the laptop) omit the inspection fields.

### JSON contents

The same data: `inspection_date`, `inspection_time`, `inspection_id`, `inspection_number`, `inspection_status`, `tested_by`, `role`, `test_mode`, `started_at`, `completed_at`, `overall_result`, `selected_tests[]`, `device{brand, model, model_number, cpu, generation, ram, storage, battery_health, serial_number, system_uuid}`, `tests[{test, status, required, comment, completed_at}]`, `tool`.

## 2. Detection export (DETECT)

**Export JSON / Export CSV / Copy** for the detection result:
- **File name:** `detect_<yyyyMMdd_HHmmss>_<Brand>_<Model>_<ModelNumber>.json|csv` in `BBTech Data\exports\`.
- **JSON** starts with the eight detected fields, in this order: `brand, model, model_number, cpu, generation, ram, storage, battery_health`. It then has `storage_devices` and `details` (serial number, product code, firmware values, CPU details, RAM modules, battery capacities, warnings, time and tool version).
- **CSV:** the eight fields plus `serial_number` and `detected_at`.

## 3. Format guarantees

- **Locale independence:** dates and times in file names and report contents use a fixed format (`yyyy-MM-dd`, `HH:mm:ss`, invariant culture). A laptop set to a Thai (Buddhist calendar), Arabic or Bengali region still produces `2026-10-05`. JSON numbers always use a dot.
- **Formula neutralisation:** CSV cells starting with `=`, `+`, `-`, `@`, tab or CR get a leading apostrophe, so Excel shows them as text instead of running them.
- **Quoting:** cells containing commas, quotes or line breaks are quoted (`""` for a quote).
- **Line endings:** CSV files use Windows line endings (CRLF).

## 4. Privacy

- **Content:** reports contain hardware identification (serial, UUID) and test results, which is the purpose of an inspection record. The motherboard serial and internal hashes are not exported.
- **Sharing:** handle exported files as internal BBTech records.
