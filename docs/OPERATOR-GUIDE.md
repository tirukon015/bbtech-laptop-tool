# Technician Guide

## Every laptop

1. Plug in the BBTech USB and open **BBTech Laptop Tool.exe**.
2. **Start Testing** → pick your name (only the first time; you stay selected for 1 hour).
   - One department: you go straight to it. Several: pick which work you are doing now.
3. Your dashboard opens and the laptop is detected automatically.
4. Click a card → do the test → **PASS** or **FAIL**. The result is saved immediately with your name and the time.
   Running a test again creates a new record; the card shows the latest one.

| Department | Cards |
|---|---|
| Hardware | Battery, Storage, RAM, Display, Keyboard, Touchpad, Mouse, Sound, Headphone, Microphone, Camera, USB, Wi-Fi, Bluetooth, Physical, Other |
| Software | Drivers, Display, Keyboard, Mouse, Scroll, Hotkeys, Touchpad, Audio, Microphone, Camera, Office install, Windows/Office activation |
| QC | Checklist + what Hardware and Software did + **QC PASS / QC FAIL** |

**SKIPPED** is only accepted for hardware the laptop really does not have (e.g. no headphone jack, no Bluetooth).
Skipping anything else keeps the laptop from passing QC.

## QC

QC sees, for this laptop, the latest Hardware and Software result of every required test (who, when), plus what
is missing, failed or skipped. **QC PASS** stays disabled until everything required has passed. **QC FAIL** is
always possible. The decision is permanent; a later decision replaces it but both stay in history.

## Messages you may see

| Message | What to do |
|---|---|
| Select your name | Your hour is over. Pick your name again. |
| Results cannot be saved | The laptop could not be identified (no serial) or matches two laptops. Call an admin. Do not test. |
| Result not saved | Run the test again. If it repeats, call an admin. |
| QC PASS is not possible yet | The list shows exactly what is missing or failed. |
| Installer missing / checksum mismatch / not validly signed | The installer on the USB is not the approved one. Call an admin. |
| Not ready for ITMS | QC PASS is missing (or the latest decision is FAIL). |

You never need Device Manager, CMD or PowerShell.
