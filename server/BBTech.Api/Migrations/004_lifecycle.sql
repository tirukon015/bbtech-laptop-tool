-- Laptop lifecycle + traceability (tool 1.5).
-- Lifecycle events (handovers, QC returns, decisions, delivery check, repairs, snapshots) are ordinary
-- append-only rows in test_results with reserved test ids; the tables below are derived/indexed views of them.

-- Current lifecycle status of each laptop (recomputed by the API from the full history on every accepted record).
ALTER TABLE devices ADD COLUMN lifecycle_status text NOT NULL DEFAULT 'RECEIVED';
ALTER TABLE devices ADD COLUMN lifecycle_updated_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE devices ADD COLUMN qc_return_count int NOT NULL DEFAULT 0;
CREATE INDEX ix_devices_lifecycle ON devices (lifecycle_status);

-- Every status change: previous stage, next stage, who caused it and when (time-in-stage reporting, traceability).
CREATE TABLE lifecycle_events (
    lifecycle_event_id bigserial PRIMARY KEY,
    device_id    text NOT NULL REFERENCES devices(device_id),
    from_status  text NOT NULL,
    to_status    text NOT NULL,
    record_id    text NOT NULL REFERENCES test_results(record_id),
    user_id      text NOT NULL REFERENCES users(user_id),
    department_code text NOT NULL,
    reason       text NOT NULL DEFAULT '',
    occurred_at  timestamptz NOT NULL,
    received_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_lifecycle_events_device ON lifecycle_events (device_id, occurred_at);
CREATE INDEX ix_lifecycle_events_to ON lifecycle_events (to_status, occurred_at);
CREATE TRIGGER lifecycle_events_append_only BEFORE UPDATE OR DELETE ON lifecycle_events FOR EACH ROW EXECUTE FUNCTION bbtech_forbid_change();

-- Technical snapshots (identity + inventory + measured health) as queryable JSON. History is kept: a replaced
-- SSD shows as a new snapshot with a different hardware_signature; the old one stays.
CREATE TABLE device_snapshots (
    record_id          text PRIMARY KEY REFERENCES test_results(record_id),
    device_id          text NOT NULL REFERENCES devices(device_id),
    hardware_signature text NOT NULL,
    captured_at        timestamptz NOT NULL,
    inventory          jsonb NOT NULL
);
CREATE INDEX ix_device_snapshots_device ON device_snapshots (device_id, captured_at DESC);
CREATE TRIGGER device_snapshots_append_only BEFORE UPDATE OR DELETE ON device_snapshots FOR EACH ROW EXECUTE FUNCTION bbtech_forbid_change();

-- Lifecycle events and evidence can never be tombstoned (not only QC_FINAL).
CREATE OR REPLACE FUNCTION bbtech_test_results_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'test_results rows are never deleted (use deleted_at)'; END IF;
    IF (NEW.record_id, NEW.device_id, NEW.user_id, NEW.department_code, NEW.test_id, NEW.status, NEW.tested_at, NEW.content_hash)
       IS DISTINCT FROM
       (OLD.record_id, OLD.device_id, OLD.user_id, OLD.department_code, OLD.test_id, OLD.status, OLD.tested_at, OLD.content_hash) THEN
        RAISE EXCEPTION 'test results are immutable';
    END IF;
    IF NEW.deleted_at IS NOT NULL AND OLD.test_id IN ('QC_FINAL', 'QC_DELIVERY_FINAL', 'LC_HARDWARE_HANDOVER', 'LC_SOFTWARE_HANDOVER',
                                                      'LC_QC_RETURN', 'DEVICE_SNAPSHOT', 'DEVICE_HARDWARE_CHANGED') THEN
        RAISE EXCEPTION 'lifecycle events and final QC decisions cannot be deleted';
    END IF;
    RETURN NEW;
END $$;

-- The delivery checklist is its own definition group (its results are QC records).
INSERT INTO departments (code, name, sort_order) VALUES ('Delivery', 'Final delivery check', 5);

INSERT INTO test_definitions (department_code, test_id, label, required, skip_allowed, sort_order) VALUES
    ('Software', 'SoftwareWindowsInstall', 'Windows Installation', true, false, 0),
    ('QC', 'Mouse', 'Mouse', true, true, 11),
    ('QC', 'Microphone', 'Microphone', true, false, 12),
    ('QC', 'Bluetooth', 'Bluetooth', true, true, 13),
    ('QC', 'Storage', 'Storage / SSD', true, false, 14),
    ('QC', 'QcWindows', 'Windows', true, false, 15),
    ('QC', 'QcSoftware', 'Software', true, false, 16),
    ('QC', 'QcActivation', 'Activation', true, false, 17),
    ('QC', 'QcAccessories', 'Accessories', true, false, 18),
    ('QC', 'QcOverall', 'Overall', true, false, 19),
    ('Delivery', 'DeliveryPhysical', 'Physical', true, false, 1),
    ('Delivery', 'DeliveryDisplay', 'Display', true, false, 2),
    ('Delivery', 'DeliveryKeyboard', 'Keyboard', true, false, 3),
    ('Delivery', 'DeliveryCharger', 'Charger', true, false, 4),
    ('Delivery', 'DeliveryAccessories', 'Accessories', true, false, 5),
    ('Delivery', 'DeliveryWindows', 'Windows', true, false, 6),
    ('Delivery', 'DeliverySoftware', 'Software', true, false, 7),
    ('Delivery', 'DeliveryCustomerConfig', 'Customer setup', true, true, 8);

-- Normal lifecycle is Hardware → Software → QC. Set to 'true' only if departments may work out of order.
INSERT INTO system_configurations (key, value) VALUES ('allow_out_of_order_work', 'false');
UPDATE system_configurations SET value = (value::int + 1)::text, updated_at = now() WHERE key = 'config_version';
