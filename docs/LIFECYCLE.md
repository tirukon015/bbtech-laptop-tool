# Laptop Lifecycle and Traceability

BBTech Laptop Tool is a **laptop lifecycle + traceability + QC system**. Hardware, Software and QC are stages
operating on **one permanent record per physical laptop**. QC is the final control point.

```text
RECEIVED → HARDWARE_IN_PROGRESS ⇄ (fail → repair → retest) → READY_FOR_SOFTWARE
        → SOFTWARE_IN_PROGRESS → READY_FOR_QC → QC_IN_PROGRESS
        → QC PASS → READY_FOR_ECOMMERCE (ITMS) → FINAL_CHECK → READY_FOR_DELIVERY   (or DELIVERY_HOLD, with reason)
        → QC FAIL → returned to HARDWARE_REWORK or SOFTWARE_REWORK → fixed → handed over again → READY_FOR_QC
```

Code: `BBTech.Core/Testing/LaptopLifecycle.cs` (one engine, used by the desktop tool **and** the server).

## How it works

- **Everything is an event.** Test results, repairs, handovers, QC returns, QC decisions, the delivery decision,
  technical snapshots and hardware changes are append-only records of the same laptop. Nothing is overwritten.
- **The status is computed**, never stored as "finished": it is recalculated from the full history every time.
  An earlier PASS never hides a newer FAIL; a handover never hides a later failure; a QC PASS stops counting the
  moment QC returns the laptop.
- **Stage order is enforced** in the service layer and again by the server: Software work needs the Hardware
  handover, QC work needs both, a handover needs every required test of the stage passed (SKIPPED only where
  allowed), QC PASS needs everything, the delivery check needs QC PASS. (`AllowOutOfOrderWork` in the server
  configuration can relax the order if BBTech decides so.)

| Event (test id) | Who | Meaning |
|---|---|---|
| `Repair:<Test>` | the department | What was fixed between a FAIL and its retest (recorded automatically on PASS after FAIL; the retest's note says what was fixed) |
| `LC_HARDWARE_HANDOVER` | Hardware | READY_FOR_SOFTWARE ("Send to Software") |
| `SoftwareWindowsInstall` | Software | Windows installation: started (TESTING) / verified (PASS) / failed. Verified from Windows' own install date, not from "an installer ran" |
| `LC_SOFTWARE_HANDOVER` | Software | READY_FOR_QC ("Send to QC") |
| `QC_FINAL` | QC approver | QC PASS / FAIL (snapshot of what QC saw in Details) |
| `LC_QC_RETURN` | QC approver | Returned to Hardware or Software, with the problem |
| `Delivery*` items, `QC_DELIVERY_FINAL` | QC | Final check before delivery: READY FOR DELIVERY or HOLD (reason), with the ITMS order reference |
| `DEVICE_SNAPSHOT` | any stage | Technical identity + inventory + measured health ([see below](#technical-evidence)) |
| `DEVICE_HARDWARE_CHANGED` | any stage | A part was replaced between two snapshots ("Storage replaced: A (S/N) → B (S/N)") |

Lifecycle events and evidence can never be deleted (client, repository and database trigger).

## What each screen shows

- **Every department screen**: the stage strip (Hardware · Software · QC · Ready, who and when), the current
  status in large letters, "Returned by QC: ..." when applicable, **History** (full timeline) and **Details**
  (technical evidence). Hardware and Software have one action: **Send to Software / Send to QC**, enabled only when
  the stage is complete (the tooltip says what is missing).
- **Software**: a Windows card (Start install / Verify) above the checks.
- **QC**: at the top **READY / NOT READY** with `Hardware ✓  Software ✓  QC checklist n/m  Issues n`; "Fixed earlier"
  lists every item that failed and was repaired (who, when, what was fixed). The checklist is configuration.
  **QC FAIL** returns the laptop to Hardware or Software with the failed items as the reason. After QC PASS, the
  **Final check before delivery** card appears.

## Technical evidence

`InventoryCollector` (desktop) builds a `TechnicalInventory` in the background when a dashboard opens (again when a
part changed or after 12 hours): identity (manufacturer, model, product, serial, UUID, baseboard, BIOS, chassis,
asset tag, BBTech id, fingerprint), CPU, every RAM module (maker, part number, serial, type, rated + configured
speed, voltage, slot), every internal drive (model, serial, firmware, interface, capacity, **raw NVMe health log /
ATA SMART values**), battery (design/full-charge capacity, cycles, health = full ÷ design), GPU, display (EDID),
network adapters (MAC, driver, status), audio, camera, Bluetooth, TPM, boot mode, Secure Boot, Windows (edition,
version, build, install date, activation from the Microsoft licensing service) and Office.

Rules: every item has a **source** and a **confidence** (DETECTED / MEASURED / VERIFIED / MANUAL / UNKNOWN); a value
the laptop does not expose is **UNKNOWN**, never invented or defaulted to "good"; USB docks and paired Bluetooth
accessories are not counted as laptop parts; no personal data is read.

## ITMS

Every ITMS submission carries `bbtech_traceability`: BBTech device id and manufacturer identifiers, lifecycle status,
the latest technical inventory with raw values and sources, every Hardware/Software/QC result with operator and
time, repairs, reworked items, hardware changes, QC decision and delivery check (`ItmsTraceability`). ITMS can only
receive a laptop whose preparation QC passed and still counts (client gate + server READY check).

## History survives a Windows install

A clean Windows install wipes the laptop's local store. The tool therefore (1) gives each laptop a **stable device
id** derived from its hardware identity, (2) keeps an encrypted copy of each laptop's history on the USB
(`BBTech Data\carry\`), and (3) restores missing history from that copy or from the BBTech server
(`/v1/devices/{id}/history`) when it meets the laptop again. The copy is matched by the full hardware fingerprint.

## Management view (server)

`GET /v1/admin/reports/lifecycle`: laptops per stage (in Hardware, waiting for Software, in Software, in QC, ready
for E-Commerce, waiting for delivery check, ready for delivery), average hours per stage, laptops returned by QC,
QC return reasons, work per operator. Per laptop: `/v1/devices/{id}/status | timeline | history | snapshot`.
