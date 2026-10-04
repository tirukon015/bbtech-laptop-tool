# Troubleshooting

Logs: `BBTech Data\logs\app.log` next to the EXE (or `%LOCALAPPDATA%\BBTech Laptop Tool\logs\`). The log rotates at 2 MB and contains no test results, names, serials or passwords.

| Symptom | Cause | What to do |
|---|---|---|
| Windows SmartScreen warns on start | The EXE is not code-signed | "More info" → "Run anyway"; compare the SHA-256 with `release.sha256.txt` |
| A field shows **Unknown** | The firmware reports a placeholder or nothing usable; the tool does not guess | Expected; check Details for the raw values |
| Battery Health is Unknown | BatteryInfoView and the Windows fallbacks failed, or there is no battery | Check the battery connection; try again on charger |
| Storage reliability values missing | Windows reliability counters need administrator rights | Run the EXE as administrator, or use the HD Sentinel report in the Storage test |
| "No QC inspection found for this device." | The laptop was never tested with this tool, **or** Windows was reset / the disk was replaced (local history deleted) | A local-only store cannot be recovered after a wipe ([SECURITY.md](SECURITY.md) §3) |
| "Identity conflict – QC history not shown" | Hardware identifiers disagree with the stored record (e.g. motherboard replaced) | An admin reviews the records with the PIN ([DEVICE-IDENTITY.md](DEVICE-IDENTITY.md)) |
| "Unfinished inspection found" | The app closed or crashed during an inspection | **Resume**, or start new (the old one is kept as ABANDONED) |
| Results are not being saved (banner) | No identifiers, a conflict with "test without saving", or the store could not be opened | Testing and export still work; see the banner text |
| "The BBTech settings on this USB are damaged and cannot be used." | `settings.dat` was edited or corrupted (it is authenticated) | Restore `BBTech Data\config\` from another BBTech USB |
| Settings locked for 60 seconds | 5 wrong admin PIN attempts | Wait, then enter the correct PIN |
| Forgotten admin PIN | Only a hash is stored; there is no reset | Copy `BBTech Data\config\` from a USB with a known PIN |
| Wi-Fi: "INSTALLED · MANUAL ONLY" | A profile with the same name exists without auto-connect | Click **Provision BBTech Wi-Fi** |
| Wi-Fi provisioning reports "current user only" | Windows policy denied an all-user profile | Run as administrator for an all-user profile |
| "BBTech test media is unavailable." | The EXE was built with `-NoMedia` or the media was missing at build time | Use the generated left/right tones, or rebuild with media |
| Camera preview black or unavailable | Privacy shutter, camera disabled, or the camera is used by another app | Open the shutter or close other apps; the Windows Camera fallback is offered |
| Build: file in use / access denied | `dist\release\BBTech Laptop Tool.exe` is running | Close the app and rebuild |
