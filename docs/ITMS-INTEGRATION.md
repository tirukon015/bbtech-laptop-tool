# ITMS Integration

**Status: IMPLEMENTED (Direct Submission & Review Queue)**

The application connects to the ITMS (Inventory Management System) API to submit hardware detections and inspection results directly to the ITMS review queue.

---

## 1. What is Implemented

| Item | Component | Status |
|---|---|---|
| Direct DETECT Submission | `HttpItmsClient` (`src/BBTech.Core/Integration/ItmsIntegration.cs`) | **Implemented** |
| "Send to ITMS" Action | `DetectView.xaml` & `DetectView.xaml.cs` | **Implemented** (enabled once detection finishes) |
| Contract Version 1.0 Payload | `ItmsPayload.BuildInspectionPayload` | **Implemented** |
| Idempotency Key | `DetectionResult.SubmissionId` (UUID v4) | **Implemented** (stable across retries) |
| Encrypted USB Credential Storage | `ToolSettings.cs` (`SettingsStore`) | **Implemented** (AES-256 encrypted `settings.dat`) |
| Admin Settings Configuration | `SettingsWindow.cs` (Tab "ITMS") | **Implemented** (under Admin PIN) |
| Environment Variable Fallback | `ITMS_URL` & `ITMS_API_KEY` | **Implemented** (convenient for automated testing & staging) |
| Unit Tests | `tests/BBTech.Core.Tests/ItmsIntegrationTests.cs` | **Implemented** |

---

## 2. Configuration & Credentials

### Option A: USB Encrypted Settings (Recommended for Technicians)
1. In the Laptop Tool, open **Settings** (requires Admin PIN).
2. Go to the **ITMS** tab.
3. Enter:
   - **ITMS Server URL**: `https://itms.bbtech.com.my` (or `http://localhost:8000` for development)
   - **ITMS API Key**: The pre-shared secret key corresponding to `ITMS_LAPTOP_TOOL_API_KEY` on the ITMS server.
4. Click **Save ITMS Configuration**. Settings are saved encrypted to `BBTech Data\config\settings.dat`.

### Option B: Environment Variables
Alternatively, you can set standard environment variables on the test laptop:
```cmd
set ITMS_URL=https://itms.bbtech.com.my
set ITMS_API_KEY=your-secret-api-key
```

---

## 3. Workflow

```
1. Run BBTech Laptop Tool on a Windows laptop (DETECT runs automatically).
2. Operator clicks "Send to ITMS" in the DETECT toolbar.
3. Tool builds contract v1.0 payload using detection.SubmissionId.
4. Tool POSTs to /api/devices/requests with header x-itms-api-key.
5. On 201 Created: Shows notice "Queued in ITMS as Request #1002".
6. On 200 OK: Shows notice "Already received by ITMS as Request #1002" (idempotent).
7. If the network is unavailable: shows clear error, preserves offline CSV/JSON export, and keeps "Send to ITMS" enabled so the operator can retry once connected.
```

---

## 4. Contract Specification

See the complete versioned contract in [docs/ITMS-LAPTOP-TOOL-CONTRACT.md](ITMS-LAPTOP-TOOL-CONTRACT.md).
