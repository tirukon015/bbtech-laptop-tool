# Security

Source: `src/BBTech.Core/Security/*`, `Storage/EncryptedJournal.cs`, `Settings/ToolSettings.cs`, `Wifi/*`, `src/BBTech.LaptopTool/Services/QcStoreService.cs`.
Tests: `QcStoreTests.cs`, `SettingsAndWifiTests.cs`. The repository's vulnerability-reporting policy is in the root [SECURITY.md](../SECURITY.md).

## 0. Status summary

| Control | Status |
|---|---|
| AES-256-CBC + HMAC-SHA256 encryption of every QC journal record | IMPLEMENTED |
| Dual-locked data key (DPAPI LocalMachine + hardware recovery locks) | IMPLEMENTED |
| Restricted ACL on `C:\ProgramData\BBTech\LaptopTool` | IMPLEMENTED |
| Encrypted, tamper-evident USB settings file (operators, PIN hash, Wi-Fi) | IMPLEMENTED |
| Admin PIN (PBKDF2-SHA256, 120,000 iterations) with lockout | IMPLEMENTED |
| Wi-Fi passwords handed only to the Windows WLAN API (never logged or exported) | IMPLEMENTED |
| Log scrubbing (user paths, user name; no results, names, serials, passwords) | IMPLEMENTED |
| Code signing of the EXE | NOT IMPLEMENTED |
| Signed git commits / tags | NOT IMPLEMENTED |
| Off-laptop backup of QC history (ITMS sync) | PLANNED (see [ITMS-INTEGRATION.md](ITMS-INTEGRATION.md)) |
| Password-based protection that resists an administrator with the EXE | NOT IMPLEMENTED (by design: unattended unlock) |
| External security audit / penetration test | NOT IMPLEMENTED |

## 1. Where data lives

| Data | Location | Protection |
|---|---|---|
| QC history (devices, inspections, results) | **On the laptop:** `C:\ProgramData\BBTech\LaptopTool\qc.journal` | AES-256 + HMAC per record, data key in `qc.key` (dual-locked) |
| QC data key | `C:\ProgramData\BBTech\LaptopTool\qc.key` (+ `.bak`) | Windows DPAPI (LocalMachine) + hardware recovery locks, file obfuscated |
| Operators, current operator, admin PIN hash, managed Wi-Fi networks and passwords | **On the USB:** `BBTech Data\config\settings.dat` (+ `.bak`) | AES-256 + HMAC, key derived from the app constant + random salt; tamper-evident |
| BBTech Wi-Fi profiles | Windows WLAN profile store on the laptop | Native Windows protection (key encrypted by Windows, SYSTEM-only storage) |
| Reports (CSV/JSON) | USB `BBTech Data\reports\` | Plaintext, **only created when staff click Export** |
| Logs | USB `BBTech Data\logs\app.log` | No QC results, names, serial numbers, PIN, Wi-Fi passwords or SSID lists |

- **Folder protection:** the QC folder is created with inheritance removed. Access is limited to SYSTEM, Administrators (full control) and Authenticated Users (modify), because the app runs as a normal user and must be able to write. The folder is also marked Hidden, which is cosmetic only.
- **Nothing is deleted:** the application never deletes the QC history and never removes BBTech Wi-Fi profiles, including when a laptop is sold.

## 2. QC history encryption – the dual-locked key

1. A random 64-byte data key (32 bytes encryption + 32 bytes MAC) encrypts every journal record.
2. The data key is never stored in plaintext. `qc.key` holds it wrapped twice:
   - **Machine lock:** Windows DPAPI, LocalMachine scope, with app-specific entropy. This is the normal unlock path, and it works for any Windows account on that installation.
   - **Recovery locks:** one wrap per stable hardware identifier (BIOS serial, system UUID, motherboard serial). Each uses a key derived with PBKDF2-SHA256 (60,000 iterations) from that identifier plus the application constant.
3. **Self-healing:** if DPAPI no longer works on the same physical laptop (for example, Windows restored or reinstalled while the data folder was kept), any one unchanged identifier unlocks the key. The machine lock is then re-created automatically.
4. **Different laptop:** neither lock opens the store, so it stays unreadable. It is never overwritten.
5. **Missing key file:** if a journal exists but its key file is gone, the app reports it and **does not overwrite** the journal.

Verified on the real store: neither file contains readable names, results, serial or model text, and the journal is not a readable or SQLite file.

### Honest security level (trade-off chosen by BBTech: reliable recovery on the same laptop)

- **Who can't read it:** normal users and customers. Text editors, SQLite browsers and file browsing show only ciphertext.
- **Copied off the laptop:** the files alone do not reveal the data. Someone would also need the original laptop's identifiers and the BBTech application.
- **Who could read it:** the app must read the history without anyone typing a password. So a determined, technical person with administrator access to that laptop and the BBTech EXE could decrypt it. No local-only design without a password can prevent that.
- **No plaintext secrets:** no password is hard-coded and no key sits in plaintext. The application constant is split and obfuscated in the EXE, but it is **not** a secret against reverse engineering; DPAPI and the hardware binding provide the protection.

## 3. Windows reset / reinstall / hardware changes

| Event | Effect |
|---|---|
| App closed, USB removed, laptop rebooted | History intact |
| Different Windows user account | History readable (LocalMachine DPAPI, Authenticated Users access) |
| Windows restored or reinstalled **while `C:\ProgramData\BBTech\LaptopTool` is kept or restored** | Recovered through the hardware lock; the machine lock is re-created |
| Motherboard replaced (new UUID and board serial), BIOS serial kept | Store still unlocks (serial lock). DETECT flags an **identity conflict** instead of showing history, as designed; an admin can review the records |
| **Windows "Reset this PC", clean reinstall, disk wiped or replaced** | **The local data folder is deleted. A local-only solution cannot recover it.** |

> **Important:** if Windows is completely wiped or reinstalled and the protected data folder is deleted, the QC history on that laptop is gone. No local-only design can guarantee recovery in that case. The only robust protection is to copy the history off the laptop, which is the purpose of the planned ITMS sync (see [ITMS-INTEGRATION.md](ITMS-INTEGRATION.md) and [ARCHITECTURE.md §7](ARCHITECTURE.md)).

## 4. Admin PIN

- **Rules:** 4–12 digits.
- **Storage:** only a salted PBKDF2-SHA256 hash (120,000 iterations), kept inside the encrypted settings file.
- **Lockout:** 5 wrong attempts lock Settings for 60 seconds.
- **Tamper evidence:** hand-editing the settings file (for example, to remove the PIN) breaks its authentication. The file is then rejected and not overwritten, so the copy from another USB must be restored.
- **What it protects:** adding, editing and deactivating operators; adding, editing and removing managed Wi-Fi networks; changing the PIN; reviewing conflicting device records.

## 5. Wi-Fi credentials

- **On the USB:** passwords are stored only inside the encrypted `settings.dat`. They never appear in logs, reports, exports, error messages or the provisioning state, which keeps only a keyed hash used to detect changes.
- **Provisioning:**
  - The app builds native WLAN profile XML in memory, sets **Connect automatically**, and installs it with `WlanSetProfile`.
  - Windows encrypts the key in its own protected profile store.
  - The profile is installed for all users where Windows allows it, otherwise for the current user.
  - After installing, the profile is **read back from Windows** and must contain `connectionMode = auto`, the right SSID and the right security type. Otherwise provisioning reports a failure.
  - Verified on a real laptop: `netsh` showed *All User Profile* and *Connect automatically*, the exported Windows XML had `<connectionMode>auto</connectionMode>` and `<protected>true</protected>`, and both survived deleting the tool's folder.
  - `netsh` is never run with the password on its command line.
- **Accepted trade-off (one-click):**
  - The USB vault key is derivable by the app, so a determined person with the USB and the EXE could recover the passwords.
  - Windows itself shows installed Wi-Fi passwords to any local administrator (`netsh wlan show profile key=clear`).
  - Because BBTech keeps the profiles on sold laptops, their owners can read the password.
- **Recommendation:** use a dedicated, isolated SSID for laptops (internet-only VLAN) and change its password periodically. Edit it in Settings; laptops update the next time Provision is run.

## 6. Privacy

- **Collected:** hardware identification and test results only.
- **Not collected:** browser data, passwords, personal files or Wi-Fi history.
- **No network traffic:** no telemetry, no uploads, no ITMS calls.
- **No system persistence:** no services, no startup entries, no security settings changed.
- **No activation code:** `Activation.bat` from TEST-Laptop.zip is excluded, and no activation or licensing bypass exists.
