# Wi-Fi Manager

Source: `src/BBTech.Core/Wifi/WlanProfileXml.cs`, `WifiProvisioner.cs`, `src/BBTech.LaptopTool/Test/Wireless/WlanNativeApi.cs`, `UI/Views/WifiView.cs`, `UI/Views/SettingsWindow.cs`.
Tests: `WifiTests` in `tests/BBTech.Core.Tests/SettingsAndWifiTests.cs`.

> Real Wi-Fi passwords are never written in documentation, logs, reports or source. Examples use `SSID: BBTECH_EXAMPLE`, `PASSWORD: [REDACTED]`.

## Purpose

Install BBTech's networks on laptops once, so that Windows connects automatically whenever a network is in range, **after the USB is removed and without opening the app**.

## Managing networks (admin)

Home → **Settings** → admin PIN → **BBTech Wi-Fi**:

| Action | Behaviour |
|---|---|
| **Add network** | SSID (1–32 bytes), security (WPA2-Personal / WPA3-Personal / Open), password (8–63 printable ASCII characters; WPA2 also accepts a 64-digit hex key, WPA3 does not), hidden-network option. Saving also **installs it on the current laptop** and shows the verified result. |
| **Edit network** | The password is never displayed; leaving it empty keeps the current one. When something actually changed, the network's version is incremented so laptops update on the next Provision. Saving without changes keeps the version. |
| **Remove network** | Removes it from the managed list only. Profiles already on laptops are **not** removed (by design). |

The list is stored in the encrypted USB settings file (`BBTech Data\config\settings.dat`).

## Provisioning (staff)

Home → **Wi-Fi** → **Provision BBTech Wi-Fi**, once per laptop. For each managed network:

1. **Build the profile:** native Windows WLAN profile XML is built in memory with `connectionMode=auto` ("Connect automatically"), `autoSwitch=false`, the SSID (name plus hex), and WPA2PSK/WPA3SAE + AES or open. The passphrase is included only in this in-memory XML.
2. **Skip if unchanged:** if an identical profile is already installed (same SSID, security, `auto`, and an unchanged settings fingerprint), it is left untouched.
3. **Keep a working profile:** if the laptop is currently connected to that SSID through an equivalent auto-connect profile created outside the tool, it is kept rather than overwritten. A later admin password change is still applied.
4. **Install:** `WlanSetProfile`, for **all users**. If Windows policy denies that, it falls back to the current user and says so.
5. **Verify:** the profile is **read back from Windows**. It must contain `connectionMode=auto`, the correct SSID and the correct security, otherwise the result is a failure.
6. **Record:** a keyed fingerprint of the settings (no password) is saved in the laptop's encrypted store, to detect later edits.

Out of range: the profile is still installed, and Windows connects when the network appears. **CONNECT** connects immediately; it is only needed for the first connection.

## Status display

Each network shows a status chip read **from the Windows profile**, not from app state:
- WINDOWS: CONNECT AUTOMATICALLY ✓
- INSTALLED · MANUAL ONLY – click Provision to fix
- NOT INSTALLED

It also shows CONNECTED, IN RANGE or NOT IN RANGE.

## Credential protection

- **On the USB:** inside the encrypted settings file (AES-256-CBC + HMAC-SHA256, key from PBKDF2 of the application constant and a random salt).
- **On the laptop:** Windows' own protected profile store; the exported profile shows `<protected>true</protected>`.
- **Never:** in logs, reports, exports, error messages or the provisioning state.
- **Accepted trade-off (one-click provisioning):**
  - Someone determined who has the USB and the EXE could recover the passwords.
  - Windows shows installed passwords to local administrators (`netsh wlan show profile key=clear`).
  - Profiles remain on sold laptops (BBTech decision), so their owners can see the password.

  Recommendation: use a dedicated, isolated SSID for laptops and change its password periodically.

## Verification performed (2026-10-04, development laptop)

- Dummy networks named `BBTechVerify.shop_5G` / `BBTechVerify.shop` were provisioned from a temporary folder.
- `netsh` showed *All User Profile* and *Connection mode: Connect automatically*. The exported Windows XML contained `<connectionMode>auto</connectionMode>` and `<protected>true</protected>`, with no plaintext password.
- After deleting the temporary "USB" folder, both profiles remained.
- The dummy profiles were removed afterwards.
- **Not verified:** an actual automatic reconnection after leaving and re-entering range. That behaviour belongs to Windows once a profile has `connectionMode=auto`.

## Errors handled

| Situation | Message (summary) |
|---|---|
| No adapter | "Wi-Fi is not available on this laptop…" |
| WLAN AutoConfig service stopped or not responding | "Wi-Fi is not available on this laptop right now (the Windows Wi-Fi service did not respond)." |
| Native WLAN call throws (driver problem) | Reported as a failed provision or connection with the exception type only; the profile XML (which holds the key) is never shown |
| Radio off | "Wi-Fi is switched off…" (profiles can still be installed) |
| Windows rejects the profile (`ERROR_BAD_PROFILE`) | "Windows rejected the network settings…" |
| Permission denied for all-user profiles | Installs for the current user and reports it |
| Stored without auto-connect | "Windows stored the profile without automatic connection" (failure) |
| SSID not in range when connecting | Profile stays installed; Windows connects later |
| Settings file damaged or modified | "The BBTech settings on this USB are damaged and cannot be used." |
