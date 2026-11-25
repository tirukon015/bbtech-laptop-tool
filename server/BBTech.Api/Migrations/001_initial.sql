-- BBTech central database, schema version 1.
-- Applied by BBTech.Api at startup (Migrations/*.sql in order, each once, inside a transaction).
-- The desktop tool NEVER connects here: only BBTech.Api does.

-- ============================================================ reference data
CREATE TABLE departments (
    code        text PRIMARY KEY,                       -- Hardware, Software, QC, E-Commerce
    name        text NOT NULL,
    sort_order  int  NOT NULL DEFAULT 0
);

CREATE TABLE roles (
    code        text PRIMARY KEY,                       -- Technician, QC Inspector, Admin, Super Admin ...
    name        text NOT NULL,
    is_admin    boolean NOT NULL DEFAULT false          -- admin roles hold every permission
);

CREATE TABLE permissions (
    code        text PRIMARY KEY,                       -- hardware.test, qc.approve, ... (see AppPermissions)
    description text NOT NULL DEFAULT ''
);

CREATE TABLE role_permissions (
    role_code       text NOT NULL REFERENCES roles(code) ON DELETE CASCADE,
    permission_code text NOT NULL REFERENCES permissions(code) ON DELETE CASCADE,
    PRIMARY KEY (role_code, permission_code)
);

-- ============================================================ people
CREATE TABLE users (
    user_id     text PRIMARY KEY,                       -- same id the desktop tool uses as OperatorId
    name        text NOT NULL,
    role_code   text NOT NULL REFERENCES roles(code),
    active      boolean NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    created_by  text,
    updated_by  text,
    deleted_at  timestamptz                             -- soft delete: history keeps referring to the user
);
CREATE UNIQUE INDEX ux_users_active_name ON users (lower(name)) WHERE active AND deleted_at IS NULL;

CREATE TABLE user_departments (
    user_id         text NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    department_code text NOT NULL REFERENCES departments(code),
    PRIMARY KEY (user_id, department_code)
);

CREATE TABLE user_permissions (
    user_id         text NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    permission_code text NOT NULL REFERENCES permissions(code),
    PRIMARY KEY (user_id, permission_code)
);

-- One API key per workstation/USB (and admin consoles). Only a SHA-256 of the key is stored.
CREATE TABLE workstations (
    workstation_id uuid PRIMARY KEY,
    name           text NOT NULL,
    key_hash       text NOT NULL UNIQUE,
    is_admin       boolean NOT NULL DEFAULT false,
    active         boolean NOT NULL DEFAULT true,
    created_at     timestamptz NOT NULL DEFAULT now(),
    last_seen_at   timestamptz
);

-- ============================================================ master data
CREATE TABLE laptop_models (
    laptop_model_id     serial PRIMARY KEY,
    manufacturer        text NOT NULL,
    model               text NOT NULL,
    notes               text NOT NULL DEFAULT '',
    created_at          timestamptz NOT NULL DEFAULT now(),
    deleted_at          timestamptz,
    UNIQUE (manufacturer, model)
);

CREATE TABLE software_packages (
    package_id   text PRIMARY KEY,
    name         text NOT NULL,
    version      text NOT NULL DEFAULT '',
    required     boolean NOT NULL DEFAULT false,
    detect_rule  text NOT NULL DEFAULT 'office',
    approved     boolean NOT NULL DEFAULT false,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    created_by   text,
    deleted_at   timestamptz
);

-- How a package is installed. One active installer per package.
CREATE TABLE installers (
    installer_id        serial PRIMARY KEY,
    package_id          text NOT NULL REFERENCES software_packages(package_id) ON DELETE CASCADE,
    source_mode         smallint NOT NULL CHECK (source_mode IN (0, 1)),      -- 0 online, 1 offline
    file_name           text NOT NULL,
    download_url        text NOT NULL DEFAULT '' CHECK (download_url = '' OR download_url LIKE 'https://%'),
    offline_folder      text NOT NULL DEFAULT '',
    sha256              text NOT NULL DEFAULT '' CHECK (sha256 = '' OR sha256 ~ '^[a-f0-9]{64}$'),
    publisher           text NOT NULL DEFAULT '',
    install_arguments   text NOT NULL DEFAULT '',
    requires_elevation  boolean NOT NULL DEFAULT true,
    timeout_minutes     int NOT NULL DEFAULT 60 CHECK (timeout_minutes BETWEEN 1 AND 240),
    active              boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    CHECK (sha256 <> '' OR publisher <> '')                                   -- always verifiable
);
CREATE UNIQUE INDEX ux_installers_active ON installers (package_id) WHERE active;

CREATE TABLE laptop_model_packages (
    laptop_model_id int  NOT NULL REFERENCES laptop_models(laptop_model_id) ON DELETE CASCADE,
    package_id      text NOT NULL REFERENCES software_packages(package_id) ON DELETE CASCADE,
    PRIMARY KEY (laptop_model_id, package_id)
);

CREATE TABLE test_definitions (
    department_code text NOT NULL REFERENCES departments(code),
    test_id         text NOT NULL,
    label           text NOT NULL,
    required        boolean NOT NULL DEFAULT true,
    skip_allowed    boolean NOT NULL DEFAULT false,
    sort_order      int NOT NULL DEFAULT 0,
    active          boolean NOT NULL DEFAULT true,
    PRIMARY KEY (department_code, test_id)
);

-- Key/value settings; config_version is bumped on every master-data change.
CREATE TABLE system_configurations (
    key         text PRIMARY KEY,
    value       text NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  text
);

-- ============================================================ operational data
CREATE TABLE devices (
    device_id     text PRIMARY KEY,                     -- the desktop tool's stable device id
    serial_number text NOT NULL DEFAULT '',
    manufacturer  text NOT NULL DEFAULT '',
    model         text NOT NULL DEFAULT '',
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_devices_serial ON devices (serial_number);

-- Every test run of every department (Hardware, Software, QC checklist, QC_FINAL). Append-only:
-- a re-test is a new row; admin deletion only sets deleted_at (tombstone).
CREATE TABLE test_results (
    record_id        text PRIMARY KEY,                  -- client RecordId: makes uploads idempotent
    device_id        text NOT NULL REFERENCES devices(device_id),
    user_id          text NOT NULL REFERENCES users(user_id),
    operator_name    text NOT NULL,                     -- snapshot at the time of the test
    department_code  text NOT NULL REFERENCES departments(code),
    test_id          text NOT NULL,
    test_name        text NOT NULL DEFAULT '',
    status           text NOT NULL CHECK (status IN ('NOT TESTED','TESTING','PASS','FAIL','SKIPPED')),
    notes            text NOT NULL DEFAULT '',
    details          text NOT NULL DEFAULT '',
    tested_at        timestamptz NOT NULL,
    app_version      text NOT NULL DEFAULT '',
    content_hash     text NOT NULL,                     -- server-computed; used to verify and detect conflicting duplicates
    received_at      timestamptz NOT NULL DEFAULT now(),
    batch_id         text,
    workstation_id   uuid REFERENCES workstations(workstation_id),
    deleted_at       timestamptz,
    deleted_by       text
);
CREATE INDEX ix_test_results_latest ON test_results (device_id, department_code, test_id, tested_at DESC) WHERE deleted_at IS NULL;

CREATE VIEW software_results AS SELECT * FROM test_results WHERE department_code = 'Software';
CREATE VIEW qc_inspections  AS SELECT * FROM test_results WHERE department_code = 'QC' AND test_id <> 'QC_FINAL';

-- Final QC decisions: append-only (enforced by trigger). The latest one per device is authoritative.
CREATE TABLE qc_decisions (
    qc_decision_id  bigserial PRIMARY KEY,
    record_id       text NOT NULL UNIQUE REFERENCES test_results(record_id),
    device_id       text NOT NULL REFERENCES devices(device_id),
    user_id         text NOT NULL REFERENCES users(user_id),
    decision        text NOT NULL CHECK (decision IN ('PASS','FAIL')),
    snapshot        text NOT NULL DEFAULT '',
    decided_at      timestamptz NOT NULL,
    received_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_qc_decisions_latest ON qc_decisions (device_id, decided_at DESC);

-- Audit: client events (uploaded) and server events. Append-only (trigger).
CREATE TABLE audit_logs (
    audit_id        text PRIMARY KEY,
    occurred_at     timestamptz NOT NULL,
    actor_user_id   text NOT NULL,
    actor_name      text NOT NULL,
    action          text NOT NULL,
    target          text NOT NULL DEFAULT '',
    old_value       text NOT NULL DEFAULT '',
    new_value       text NOT NULL DEFAULT '',
    details         text NOT NULL DEFAULT '',
    client_checksum text NOT NULL DEFAULT '',
    previous_checksum text NOT NULL DEFAULT '',
    source          text NOT NULL CHECK (source IN ('client','server')),
    workstation_id  uuid REFERENCES workstations(workstation_id),
    received_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_audit_logs_time ON audit_logs (occurred_at DESC);
CREATE INDEX ix_audit_logs_actor ON audit_logs (actor_user_id);

CREATE TABLE sync_batches (
    batch_id        text PRIMARY KEY,                   -- client X-Idempotency-Key
    receipt_id      text NOT NULL UNIQUE,
    workstation_id  uuid NOT NULL REFERENCES workstations(workstation_id),
    record_count    int NOT NULL,
    verified_count  int NOT NULL,
    rejected_count  int NOT NULL,
    received_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE sync_records (
    batch_id    text NOT NULL REFERENCES sync_batches(batch_id),
    record_id   text NOT NULL,
    outcome     text NOT NULL CHECK (outcome IN ('INSERTED','DUPLICATE','REJECTED')),
    reason      text NOT NULL DEFAULT '',
    PRIMARY KEY (batch_id, record_id)
);
CREATE INDEX ix_sync_records_record ON sync_records (record_id);

-- ============================================================ append-only guards
CREATE FUNCTION bbtech_forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
END $$;

CREATE TRIGGER audit_logs_append_only BEFORE UPDATE OR DELETE ON audit_logs FOR EACH ROW EXECUTE FUNCTION bbtech_forbid_change();
CREATE TRIGGER qc_decisions_append_only BEFORE UPDATE OR DELETE ON qc_decisions FOR EACH ROW EXECUTE FUNCTION bbtech_forbid_change();

-- test_results: only the tombstone columns may change, never the result itself; never physically deleted;
-- QC_FINAL rows can never be tombstoned.
CREATE FUNCTION bbtech_test_results_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'test_results rows are never deleted (use deleted_at)'; END IF;
    IF (NEW.record_id, NEW.device_id, NEW.user_id, NEW.department_code, NEW.test_id, NEW.status, NEW.tested_at, NEW.content_hash)
       IS DISTINCT FROM
       (OLD.record_id, OLD.device_id, OLD.user_id, OLD.department_code, OLD.test_id, OLD.status, OLD.tested_at, OLD.content_hash) THEN
        RAISE EXCEPTION 'test results are immutable';
    END IF;
    IF OLD.test_id = 'QC_FINAL' AND NEW.deleted_at IS NOT NULL THEN
        RAISE EXCEPTION 'final QC decisions cannot be deleted';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER test_results_guard BEFORE UPDATE OR DELETE ON test_results FOR EACH ROW EXECUTE FUNCTION bbtech_test_results_guard();

-- ============================================================ seed reference data
INSERT INTO departments (code, name, sort_order) VALUES
    ('Hardware', 'Hardware', 1), ('Software', 'Software', 2), ('QC', 'QC', 3), ('E-Commerce', 'E-Commerce', 4);

INSERT INTO roles (code, name, is_admin) VALUES
    ('Technician', 'Technician', false), ('QC Inspector', 'QC Inspector', false),
    ('E-Commerce Specialist', 'E-Commerce Specialist', false),
    ('Admin', 'Admin', true), ('Admin / Co-Founder', 'Admin / Co-Founder', true), ('Super Admin', 'Super Admin', true);

INSERT INTO permissions (code, description) VALUES
    ('ALL', 'Every permission'),
    ('hardware.test', 'Record Hardware results'),
    ('software.test', 'Record Software results'),
    ('software.install', 'Install approved software'),
    ('software.activation.check', 'Check Windows/Office activation'),
    ('software.activation.execute', 'Run Microsoft activation'),
    ('qc.inspect', 'Record QC checklist results'),
    ('qc.approve', 'Record the final QC decision'),
    ('ecommerce', 'E-Commerce work'),
    ('users.manage', 'Manage users'),
    ('permissions.manage', 'Manage permissions'),
    ('software_packages.manage', 'Manage approved software'),
    ('audit.view', 'View audit logs'),
    ('records.delete', 'Delete (tombstone) test records'),
    ('configuration.manage', 'Manage configuration');

INSERT INTO system_configurations (key, value) VALUES ('config_version', '1');
