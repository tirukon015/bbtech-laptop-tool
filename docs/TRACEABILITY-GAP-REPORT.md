# ITMS Traceability Report — final verification (version 1.6.0, 2026-11-25)

Data path: **NVMe/SATA drive & other hardware → `NativeStorageDiagnostics` / `InventoryCollector` → `TechnicalInventory`
(Core model) → `DEVICE_SNAPSHOT` record in the encrypted local journal → USB queue (integrity tag covers the snapshot) →
BBTech server (`device_snapshots` JSONB, history kept) → ITMS `bbtech_traceability` + `traceability_sha256` → ITMS
receipt hash checked by the tool.**

## How it was verified

| Label | Meaning |
|---|---|
| **REAL HARDWARE VERIFIED** | Captured by the tool's own code on the real laptop below, then carried through the real local store, the real BBTech server (PostgreSQL) and the real HTTP connector into an independent ITMS reference (Node.js, contract 1.1 §3.4) that stored the record and returned its own SHA-256. Result: **VERIFIED**, 141 KB request, ITMS hash = laptop hash. |
| **UNIT TEST VERIFIED** | Proven by automated tests only (829 client + 28 server tests pass), not observed on a real machine. |

Real laptop: **Lenovo ThinkPad T14 Gen 2i** (i5-1145G7, 2×16 GB DDR4-3200, Micron MTFDKBA512TFK NVMe, Windows 11 Pro
build 26200, TPM 2.0), **docked** (two external monitors, USB Ethernet), lid closed. Standard user, plus one run as
**Administrator**.

Repeatable harness: `tests/BBTech.Core.Tests/RealHardwarePipelineTests.cs` (env `BBTECH_REAL_INVENTORY`,
`BBTECH_ITMS_URL`) and `server/BBTech.Api.Tests/LifecycleApiTests.RealLaptopSnapshot_…` (env `BBTECH_REAL_INVENTORY`).

## Results per category

| Category | Collected | Stored (local + server) | Sent to ITMS | Verified | Source | Confidence |
|---|---|---|---|---|---|---|
| Serial, UUID, model | YES | YES | YES | REAL HARDWARE | SMBIOS (WMI) | DETECTED |
| Motherboard (maker, product, version, serial) | YES | YES | YES | REAL HARDWARE (LENOVO 20W0S22200, SDK0T76538 WIN) | SMBIOS Win32_BaseBoard | DETECTED |
| BIOS / UEFI / Secure Boot | YES | YES | YES | REAL HARDWARE | WMI + GetFirmwareType | DETECTED |
| CPU (family 6, model 140, stepping 1, cores, base clock, virtualization) | YES | YES | YES | REAL HARDWARE | WMI Win32_Processor | DETECTED / MEASURED (current clock) |
| CPU turbo maximum | UNKNOWN + NOT_EXPOSED_BY_HARDWARE | YES | YES | REAL HARDWARE (reason present) | — | UNKNOWN |
| RAM modules, rated vs configured speed | YES | YES | YES | REAL HARDWARE (3200 / 3200 MT/s) | SMBIOS Win32_PhysicalMemory | DETECTED |
| SSD identity (model, serial, firmware, bus, capacity, disk number, path) | YES | YES | YES | REAL HARDWARE | WMI + MSFT_PhysicalDisk | DETECTED |
| **SSD raw NVMe health log** (17 values) | YES | YES | YES | **REAL HARDWARE, standard user** — see below | NVMe SMART / Health Information log (Windows IOCTL) | MEASURED |
| SSD derived remaining life | YES (100 − percentage used) | YES | YES | REAL HARDWARE (95 %) | calculated, formula stored | CALCULATED |
| SSD health word | Rule-based only (`StorageHealthRules`) | YES | YES | REAL HARDWARE (HEALTHY + rule); manual override blocked — UNIT TEST + code | calculated | CALCULATED |
| SATA SMART attributes | Code path | — | — | UNIT TEST only (no SATA drive available) | ATA SMART | MEASURED |
| Battery (design 51,000 / full 45,160 mWh, 259 cycles) | YES | YES | YES | REAL HARDWARE | Battery report | MEASURED |
| Battery health 88.55 % | YES | YES | YES | REAL HARDWARE | calculated, formula stored | CALCULATED |
| Battery without cycle count | UNKNOWN + reason | YES | YES | UNIT TEST only | — | UNKNOWN |
| GPU internal (Iris Xe) / external | YES | YES | YES | REAL HARDWARE (integrated); discrete GPU UNIT TEST only | WMI | DETECTED |
| Display: external monitors (EDID, DisplayPort / HDMI) | YES | YES | YES | REAL HARDWARE | EDID (WmiMonitorID) | DETECTED |
| Display: built-in panel | UNKNOWN + INTERNAL_PANEL_NOT_ACTIVE (lid closed) | YES | YES | REAL HARDWARE (docked case); open-lid case UNIT TEST only | — | UNKNOWN |
| Network PHYSICAL / EXTERNAL / VIRTUAL | YES | YES | YES | REAL HARDWARE (Wi-Fi, Ethernet, BT = physical; USB GbE = external) | WMI | DETECTED |
| Network speed / IP of disconnected adapters | UNKNOWN + NOT_CONNECTED | YES | YES | REAL HARDWARE | — | UNKNOWN |
| Audio playback / recording | YES | YES | YES | REAL HARDWARE | WMI | DETECTED |
| Camera, Bluetooth | YES | YES | YES | REAL HARDWARE | WMI | DETECTED |
| USB controllers (separate from port tests) | YES | YES | YES | REAL HARDWARE | WMI Win32_USBController | DETECTED |
| **TPM details** | YES as administrator; UNKNOWN + ADMIN_REQUIRED otherwise | YES | YES | **REAL HARDWARE, Administrator:** TPM 2.0 (spec 1.59), manufacturer STM, firmware 1.771.0.0, enabled, activated. Standard user: present = true, details ADMIN_REQUIRED | WMI Win32_Tpm | DETECTED |
| Windows (edition, build, activation, channel) | YES | YES | YES | REAL HARDWARE (Activated, OEM) | WMI + SoftwareLicensingProduct | DETECTED |
| Office | YES; M365 activation UNKNOWN + reason | YES | YES | REAL HARDWARE | Registry + SoftwareLicensingProduct | DETECTED / UNKNOWN |
| Key drivers | YES | YES | YES | REAL HARDWARE (13; Windows virtual controllers excluded) | Win32_PnPSignedDriver | DETECTED |
| Hardware tests, repairs | YES | YES | YES | REAL HARDWARE payload with simulated workflow records; logic UNIT TEST | operator records | MANUAL |
| Software / Windows install | YES | YES | YES | as above | operator + Windows install date | MANUAL / DETECTED |
| Snapshots (hardware handover, software handover, QC start, QC decision, final check) | YES | YES (all 5 kept) | YES | REAL HARDWARE (5 real snapshots in one record) | tool | DETECTED |
| QC decision, final delivery (order number) | YES | YES | YES | REAL HARDWARE payload (READY_FOR_DELIVERY); rules UNIT + SERVER TEST | operator | MANUAL |
| Hardware change after QC PASS → PASS cancelled, delivery blocked | YES | YES | YES | UNIT TEST (simulated SSD swap; no part was physically replaced) | tool | DETECTED |
| Windows reinstall continuity (restore + append, nothing replaced) | YES | YES | — | UNIT TEST (store wiped, new machine key, forged re-import rejected) | — | — |
| ITMS hash receipt VERIFIED / UNCONFIRMED / MISMATCH | YES | YES | YES | REAL HTTP against a reference ITMS (Node, independent code) + UNIT TEST; **production ITMS not yet updated** | — | — |

Every UNKNOWN value in the real ITMS record carries a reason: **71 of 71** (NO_UNIQUE_ID, NOT_CONNECTED, ADMIN_REQUIRED,
NOT_EXPOSED_BY_HARDWARE, INTERNAL_PANEL_NOT_ACTIVE, SUBSCRIPTION_LICENSING_NOT_VISIBLE…, NOT_REPORTED).

### Real NVMe health log (as received and stored by ITMS)

| Value | Reading | Unit |
|---|---|---|
| Critical warning | 0 | — |
| Available spare / threshold | 100 / 5 | percent |
| Percentage used | 5 | percent |
| Data units read / written | 54,067,426 / 46,160,497 | NVMe data unit (512,000 bytes) |
| Host read / write commands | 558,582,538 / 938,689,067 | count |
| Controller busy time | 1,784 | minutes |
| Power cycles | 816 | count |
| Power-on hours | 16,605 | hours |
| Unsafe shutdowns | 60 | count |
| Media / data integrity errors | 0 | count |
| Error information log entries | 65 | count |
| Composite temperature | 317–318 (≈ 45 °C) | K |
| Warning / critical temperature time | 0 / 0 | minutes |

Derived: remaining life 95 % (100 − 5), status **HEALTHY** (no threshold of `StorageHealthRules` reached).

## Fixed in this closure

- **The NVMe health log was never read on any laptop.** The request used protocol type 1 (SCSI) instead of 3 (NVMe), so
  Windows answered "invalid function". Earlier reports blamed missing administrator rights; that was wrong. After the
  fix the log is read **as a standard user** on this laptop.
- The older counters (Windows reliability / HD Sentinel) showed 4 % and 15,560 h; the drive's own log shows 5 % and
  16,605 h. The drive's log is now the raw source; the others stay labelled separately.
- An unavailable log now says why: ADMIN_REQUIRED only for access denied; NOT_SUPPORTED_BY_DRIVER for drivers (e.g.
  Intel RST/VMD) that do not pass the log through.
- Every UNKNOWN value carries a reason; parts without a serial say NO_UNIQUE_ID; disconnected adapters say NOT_CONNECTED.
- Raw drive values carry units.
- **SSD health cannot be passed by hand:** the Storage test's PASS is refused while the health is not measured or a
  measured internal drive is CRITICAL/FAILED. The ITMS health word comes only from the snapshot, never from a PASS.
- ITMS contract 1.1 §3.4: ITMS stores `bbtech_traceability`, computes SHA-256 of `JSON.stringify(bbtech_traceability)`
  itself and returns it (`docs/ITMS-LAPTOP-TOOL-CONTRACT.md`, with a drop-in Next.js handler).

## Not verified / open

| Item | Status |
|---|---|
| **Production ITMS hash echo** | The ITMS source is not on this machine. The contract and handler are written and proven against an independent reference; until ITMS deploys §3.4, real transfers show **UNCONFIRMED**. |
| Elevated NVMe read with the fixed code | The administrator run (TPM verified) happened before the NVMe fix; a second elevated run was cancelled at the UAC prompt. The standard-user read works, and administrators have more rights, so this is low risk but not observed. |
| Dell, HP, SATA SSD, discrete GPU, battery without cycle data, non-docked laptop, Intel RST/VMD storage | Only one laptop was available. Covered by unit tests only. |
| Physical part replacement | Simulated with snapshots; no part was physically swapped. |
