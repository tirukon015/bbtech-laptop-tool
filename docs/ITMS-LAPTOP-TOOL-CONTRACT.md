# ITMS ↔ BBTech Laptop Tool Integration Contract

**Contract Version:** `1.0`  
**Status:** Active  
**Compatible Versions:**
- ITMS: `v0.1.0+`
- BBTech Laptop Tool: `v1.0.0+`

---

## 1. Overview & Flow

```
+---------------------------------------+
|   BBTech Laptop Tool (Windows x64)   |
|   1. Hardware Detection (DETECT)     |
|   2. Optional QC Tests (TEST)        |
|   3. Click "Send to ITMS"            |
+---------------------------------------+
                   |
                   | HTTP POST /api/devices/requests
                   | Headers: x-itms-api-key / Bearer
                   v
+---------------------------------------+
|              ITMS API                 |
|   1. Authenticate (API key or session)|
|   2. Validate schema & size (<= 1 MB) |
|   3. Idempotency check (request_id)   |
|   4. Normalize hardware identity      |
|   5. Duplicate matching               |
|   6. Staging (device_requests table)  |
+---------------------------------------+
                   |
                   v
+---------------------------------------+
|          ITMS Review Queue            |
|   Authorized staff review inspection  |
|   - Approve -> create in main_device  |
|   - Link    -> attach to existing     |
|   - Reject  -> record reason          |
+---------------------------------------+
```

---

## 2. Authentication & Authorization

### Endpoints and Access Levels

| Endpoint | Method | Role / Credential | Purpose |
|---|---|---|---|
| `/api/devices/requests` | `POST` | Laptop Tool API Key (`x-itms-api-key`) OR Approved User Session | Submit inspection to review queue |
| `/api/devices/requests` | `GET` | Approved ITMS User Session | List staged requests (paginated) |
| `/api/devices/requests/[id]` | `GET` | Approved ITMS User Session | Get inspection details & duplicate matches |
| `/api/devices/requests/[id]/review` | `POST` | Approved ITMS User Session | Approve (new/link) or Reject inspection |

### Credential Transmission
- Header: `x-itms-api-key: <API_KEY>` or `Authorization: Bearer <API_KEY_OR_JWT>`
- Secrets must **never** be passed via query string or logged in server logs.
- The Laptop Tool credential has **submit-only** permission (cannot view full inventory, approve requests, or alter database records).

---

## 3. Submission Contract (`POST /api/devices/requests`)

### Maximum Payload Size
`1 MB` (1,048,576 bytes). Requests exceeding this limit return HTTP 413.

### Request Body Schema

```json
{
  "schema_version": "1.0",
  "request_id": "c1b560d6-329b-4546-acd6-f6a1d3ba1756",
  "source": "bbtech-laptop-tool",
  "tool_version": "1.0.0",
  "inspection_date": "2026-10-28",
  "inspection_time": "16:55:00",
  "inspection_id": "optional-inspection-uuid",
  "inspection_number": 1,
  "inspection_status": "COMPLETED",
  "tested_by": "Operator Name",
  "role": "QC",
  "overall_result": "PASS",
  "device": {
    "brand": "Lenovo",
    "model": "ThinkPad T480",
    "model_number": "20L5-S02C00",
    "cpu": "Intel Core i5-8350U",
    "generation": "8th Gen",
    "ram": "16 GB",
    "storage": "256 GB SSD",
    "battery_health": "85%",
    "serial_number": "PF123456",
    "system_uuid": "01234567-89AB-CDEF-0123-456789ABCDEF",
    "board_serial": "L1HF88800AB"
  },
  "tests": [
    {
      "test": "Keyboard",
      "status": "PASS",
      "required": true,
      "comment": "All keys responsive",
      "completed_at": "2026-10-28T16:50:00Z"
    }
  ],
  "details": {
    "os": "Windows 11 Pro 64-bit",
    "bios_version": "N24ET70W (1.45)",
    "storage_devices": [],
    "warnings": []
  }
}
```

### Required Fields
- `request_id`: Valid UUID v4. Client generates this per inspection session.
- `source`: Must be `"bbtech-laptop-tool"`, `"laptop_tool"`, or `"manual"`.
- `device`: Object containing detected fields.
- At least one strong hardware identifier:
  - `device.serial_number` (non-placeholder, >= 3 alphanumeric chars)
  - OR `device.system_uuid` (valid, non-placeholder UUID)
  - OR `device.board_serial` (non-placeholder, >= 3 alphanumeric chars)

### Allowed Test Statuses
- Overall: `PASS`, `FAIL`, `INCOMPLETE`, `DETECTED`, `UNKNOWN`
- Individual tests: `PASS`, `FAIL`, `SKIPPED`, `NOT_RUN`

---

## 4. API Responses

### 201 Created (Success, New Submission)
```json
{
  "success": true,
  "status": "queued",
  "request_id": "c1b560d6-329b-4546-acd6-f6a1d3ba1756",
  "request_no": 1002,
  "item_id": "a90b4d45-...",
  "match_status": "NO_MATCH",
  "message": "Inspection received and queued for review as Request #1002."
}
```

### 200 OK (Idempotent Retry — Already Received)
When the same `request_id` is re-sent, no second record is created:
```json
{
  "success": true,
  "status": "already_received",
  "request_id": "c1b560d6-329b-4546-acd6-f6a1d3ba1756",
  "request_no": 1002,
  "item_id": "a90b4d45-...",
  "item_status": "ready",
  "message": "Inspection was already received as Request #1002."
}
```

### 400 / 422 Validation Error
```json
{
  "success": false,
  "error": "Validation failed",
  "code": "VALIDATION_ERROR",
  "request_id": "c1b560d6-329b-4546-acd6-f6a1d3ba1756",
  "problems": [
    "Hardware serial \"00000000\" is a firmware placeholder and can't identify a laptop"
  ]
}
```

### 401 Unauthorized
```json
{
  "success": false,
  "error": "Unauthorized: invalid or missing API key",
  "code": "UNAUTHORIZED"
}
```
