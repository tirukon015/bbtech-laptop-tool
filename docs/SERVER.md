# BBTech Server (API + PostgreSQL)

```text
BBTech Laptop Tool (each USB / workstation)
        |  HTTPS, per-workstation API key (X-BBTech-Api-Key)
        v
BBTech.Api   (server/BBTech.Api, ASP.NET Core 8)
        |  Npgsql (only the API holds the connection string)
        v
PostgreSQL 17 (server/docker-compose.yml)
```

The desktop tool **never** connects to PostgreSQL. The server is authoritative for users, permissions, test
definitions, approved software, laptop models, results, QC decisions and audit logs. The tool caches the master
configuration (encrypted) so it keeps working offline.

## 1. Local setup (development or a single server PC)

Requirements: Docker Desktop, .NET SDK 8.

```powershell
cd server
copy .env.example .env          # then set a long random BBTECH_DB_PASSWORD (never commit .env)
docker compose up -d            # PostgreSQL on 127.0.0.1:5433 only (container: bbtech-postgres)
powershell -ExecutionPolicy Bypass -File run-local.ps1   # migrates, then serves http://localhost:5080
```

First-time bootstrap (in another window, same `ConnectionStrings__Postgres` environment variable):

```powershell
dotnet run --project server\BBTech.Api -- create-user touhidul "Touhidul" "Super Admin"
dotnet run --project server\BBTech.Api -- create-workstation "Admin console" --admin   # prints the admin key once
dotnet run --project server\BBTech.Api -- create-workstation "USB-01"                  # prints a workstation key once
```

Only the SHA-256 of each key is stored. A lost USB is disabled by setting `workstations.active = false`.

## 2. Production deployment

- Build the container from the repository root: `docker build -f server/Dockerfile -t bbtech-api .`
- Supply `ConnectionStrings__Postgres` at runtime (environment / secret store). No secret is in the source tree.
- Put TLS in front (reverse proxy such as Caddy/nginx/IIS, or a Kestrel certificate). The API refuses plain HTTP
  from other machines unless the proxy sets `X-Forwarded-Proto: https`.
- Keep PostgreSQL bound to the private network; back it up (`pg_dump`) daily.
- Migrations run automatically at API start (`Migrations/*.sql`, each once, serialized with an advisory lock).

## 3. Database (schema version 1, `server/BBTech.Api/Migrations`)

| Table / view | Purpose |
|---|---|
| `departments`, `roles`, `permissions`, `role_permissions` | Reference data (admin roles hold every permission) |
| `users`, `user_departments`, `user_permissions` | Operators; `Departments[]` is a real many-to-many; soft delete (`deleted_at`) |
| `workstations` | One API key per USB/workstation (hash only), admin flag, last seen |
| `devices` | One row per physical laptop (the tool's stable device id) |
| `test_results` | Every Hardware / Software / QC result. **Append-only**: a re-test is a new row; results are immutable (trigger); admin delete = tombstone (`deleted_at`) |
| `software_results`, `qc_inspections` | Views over `test_results` |
| `qc_decisions` | Final QC PASS/FAIL, **append-only** (trigger), latest per device = readiness |
| `audit_logs` | Client + server events, **append-only** (trigger) |
| `sync_batches`, `sync_records` | Every upload, its receipt and the outcome of every record |
| `software_packages`, `installers` | Approved software and how it is installed (checksum **or** publisher required by a CHECK constraint; HTTPS only) |
| `laptop_models`, `laptop_model_packages` | Models and the packages they require |
| `test_definitions` | Required/optional tests per department and whether SKIPPED is allowed |
| `system_configurations` | `config_version` (bumped on every master-data change) |

## 4. API

All `/v1` endpoints need `X-BBTech-Api-Key`. Admin endpoints also need an **admin** workstation key **and**
`X-BBTech-Actor: <userId>` naming an active admin user in the database (a client claiming to be admin is not enough).
JSON uses PascalCase names, numeric enums and round-trip (`o`) dates to match the desktop tool.

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Liveness + database check (no key) |
| GET | `/v1/config/master` | Operators (with expanded permissions), test definitions, approved packages, laptop models, version |
| GET | `/v1/users` | Operators only |
| POST | `/v1/sync/upload` | USB batch upload (see [USB-SYNC.md](USB-SYNC.md)); `X-Idempotency-Key` = batch id |
| POST | `/v1/devices` | Register/find a device |
| GET | `/v1/devices/{id}/status` | `READY` / `NOT_READY` / `IN_PROGRESS` / `UNKNOWN` + latest QC decision |
| GET | `/v1/devices/{id}/results` | Latest result per test and department |
| GET | `/v1/devices/ready` | Laptops whose latest final QC decision is PASS (for ITMS) |
| POST | `/v1/admin/users` | Create/update a user (departments, permissions) |
| POST | `/v1/admin/packages` | Approve/update a software package + installer (validated) |
| PUT | `/v1/admin/test-definitions` | Replace the test definitions (every department needs a required test) |
| POST | `/v1/admin/laptop-models` | Laptop model + required packages |
| POST | `/v1/admin/workstations` | Issue a workstation key |
| PUT | `/v1/admin/workstations/{id}/operators` | Restrict a workstation key to named operators (empty = no restriction) |
| DELETE | `/v1/admin/results/{recordId}` | Tombstone a result (`records.delete`); final QC decisions are refused |
| GET | `/v1/admin/audit` | Recent audit events |

## 5. Server authority (what the server checks itself)

For every uploaded record the server re-checks, with **its own** data: the user exists and is active, the user may
record that department, `QC_FINAL` needs `qc.approve`, and a **QC PASS is accepted only if the server's own latest
results satisfy every required test** (skip only where allowed). The operator name stored is the server's, not the
client's. Rejected records are returned with a reason and stay on the USB.

### Operator identity

Technicians have no PIN by design, so the operator on a record is **asserted by the workstation** (USB key), not
proven by the person. Protection:

- **QC decisions fail closed.** QC PASS/FAIL, QC returns and delivery release/hold are accepted only when the QC
  approver is **explicitly registered on that USB key**. Register each QC USB once:
  `dotnet run --project server\BBTech.Api -- assign-operators <workstationId> jimmy` (or
  `PUT /v1/admin/workstations/{id}/operators`). Without it, QC decisions from that USB are refused with a clear
  reason and stay on the USB until the registration is done.
- A USB key with a registered list accepts records **only** for those operators.
- Every record is tied to the uploading workstation; permissions and the lifecycle are checked with server data;
  a lost USB is cut off by deactivating its key.

Remaining risk: a USB key without a list still accepts ordinary test results for any active operator. Registering
every USB closes this; strong per-person proof would need a technician PIN, which BBTech chose not to require.

## 6. Tests

`server/BBTech.Api.Tests` (27 tests) runs the real API in-process against a fresh PostgreSQL database per run
(`BBTECH_TEST_PG`, or the local container via `server/.env`). The end-to-end tests drive the desktop tool's own
`BBTech.Core` sync client against the server.

```powershell
dotnet test server\BBTech.Server.sln
```
