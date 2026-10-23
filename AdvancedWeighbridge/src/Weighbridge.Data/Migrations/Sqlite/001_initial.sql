-- Advanced Weighbridge Management System – initial schema (SQLite).
-- Conventions: timestamps are ISO-8601 UTC text (yyyy-MM-ddTHH:mm:ss.fffZ), weights are INTEGER grams,
-- booleans are 0/1, uid columns are GUID text used for cloud synchronisation.

CREATE TABLE roles (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    description TEXT,
    is_system INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE role_permissions (
    role_id INTEGER NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    permission TEXT NOT NULL,
    PRIMARY KEY (role_id, permission)
);

CREATE TABLE users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    username TEXT NOT NULL UNIQUE COLLATE NOCASE,
    display_name TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    role_id INTEGER NOT NULL REFERENCES roles(id),
    is_active INTEGER NOT NULL DEFAULT 1,
    must_change_password INTEGER NOT NULL DEFAULT 0,
    mfa_secret_protected TEXT,
    mfa_last_step INTEGER,
    failed_attempts INTEGER NOT NULL DEFAULT 0,
    locked_until_utc TEXT,
    last_login_utc TEXT,
    password_changed_utc TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE login_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER REFERENCES users(id),
    username TEXT NOT NULL,
    success INTEGER NOT NULL,
    reason TEXT,
    machine TEXT,
    at_utc TEXT NOT NULL
);
CREATE INDEX ix_login_history_at ON login_history(at_utc);

CREATE TABLE sites (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    code TEXT NOT NULL UNIQUE COLLATE NOCASE,
    name TEXT NOT NULL,
    address TEXT,
    phone TEXT,
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE indicator_profiles (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    manufacturer TEXT,
    model TEXT,
    version INTEGER NOT NULL,
    profile_json TEXT NOT NULL,
    verified INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

-- Every saved version of a profile is kept so that the parser behind any capture can be reconstructed.
CREATE TABLE indicator_profile_versions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    profile_id INTEGER NOT NULL REFERENCES indicator_profiles(id),
    version INTEGER NOT NULL,
    profile_json TEXT NOT NULL,
    changed_by INTEGER REFERENCES users(id),
    changed_utc TEXT NOT NULL,
    UNIQUE (profile_id, version)
);

CREATE TABLE weighbridges (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    site_id INTEGER NOT NULL REFERENCES sites(id),
    code TEXT NOT NULL COLLATE NOCASE,
    name TEXT NOT NULL,
    capacity_g INTEGER NOT NULL,
    min_capture_g INTEGER NOT NULL,
    division_g INTEGER NOT NULL DEFAULT 10000,
    is_active INTEGER NOT NULL DEFAULT 1,
    UNIQUE (site_id, code)
);

CREATE TABLE indicator_devices (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    weighbridge_id INTEGER NOT NULL REFERENCES weighbridges(id),
    profile_id INTEGER NOT NULL REFERENCES indicator_profiles(id),
    port_override TEXT,
    is_enabled INTEGER NOT NULL DEFAULT 1,
    updated_utc TEXT NOT NULL
);

CREATE TABLE vehicle_categories (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    description TEXT
);

CREATE TABLE transport_companies (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    code TEXT UNIQUE COLLATE NOCASE,
    name TEXT NOT NULL,
    contact_person TEXT,
    phone TEXT,
    email TEXT,
    address TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE drivers (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    license_no TEXT,
    nic_no TEXT,
    phone TEXT,
    transport_company_id INTEGER REFERENCES transport_companies(id),
    is_active INTEGER NOT NULL DEFAULT 1,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE customers (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    code TEXT UNIQUE COLLATE NOCASE,
    name TEXT NOT NULL,
    address TEXT,
    phone TEXT,
    email TEXT,
    tax_no TEXT,
    notes TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE suppliers (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    code TEXT UNIQUE COLLATE NOCASE,
    name TEXT NOT NULL,
    address TEXT,
    phone TEXT,
    email TEXT,
    tax_no TEXT,
    notes TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE materials (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    code TEXT UNIQUE COLLATE NOCASE,
    name TEXT NOT NULL,
    category TEXT,
    grade TEXT,
    unit TEXT NOT NULL DEFAULT 'kg',
    notes TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE vehicles (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    registration TEXT NOT NULL,
    registration_norm TEXT NOT NULL UNIQUE,
    category_id INTEGER REFERENCES vehicle_categories(id),
    vehicle_type TEXT,
    make_model TEXT,
    axles INTEGER,
    transport_company_id INTEGER REFERENCES transport_companies(id),
    default_driver_id INTEGER REFERENCES drivers(id),
    rfid_tag TEXT UNIQUE,
    qr_code TEXT UNIQUE,
    stored_tare_g INTEGER,
    stored_tare_utc TEXT,
    stored_tare_approved_by INTEGER REFERENCES users(id),
    stored_tare_approved_utc TEXT,
    stored_tare_expires_utc TEXT,
    is_blacklisted INTEGER NOT NULL DEFAULT 0,
    blacklist_reason TEXT,
    photo_path TEXT,
    notes TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    CHECK (is_blacklisted = 0 OR blacklist_reason IS NOT NULL)
);

CREATE TABLE vehicle_tare_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    vehicle_id INTEGER NOT NULL REFERENCES vehicles(id),
    tare_g INTEGER NOT NULL,
    source TEXT NOT NULL,
    transaction_id INTEGER,
    approved_by INTEGER REFERENCES users(id),
    approved_utc TEXT,
    expires_utc TEXT,
    created_by INTEGER NOT NULL REFERENCES users(id),
    created_utc TEXT NOT NULL
);

CREATE TABLE orders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    order_type TEXT NOT NULL CHECK (order_type IN ('Purchase', 'Sales')),
    order_no TEXT NOT NULL UNIQUE COLLATE NOCASE,
    customer_id INTEGER REFERENCES customers(id),
    supplier_id INTEGER REFERENCES suppliers(id),
    material_id INTEGER REFERENCES materials(id),
    ordered_qty_g INTEGER,
    status TEXT NOT NULL DEFAULT 'Open' CHECK (status IN ('Open', 'Closed', 'Cancelled')),
    valid_from_utc TEXT,
    valid_to_utc TEXT,
    notes TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE sequences (
    scope TEXT PRIMARY KEY,
    value INTEGER NOT NULL
);

CREATE TABLE weighment_transactions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    ticket_no TEXT NOT NULL UNIQUE,
    site_id INTEGER NOT NULL REFERENCES sites(id),
    weighbridge_id INTEGER NOT NULL REFERENCES weighbridges(id),
    status TEXT NOT NULL CHECK (status IN ('Pending', 'Completed', 'Cancelled')),
    direction TEXT NOT NULL CHECK (direction IN ('Inbound', 'Outbound', 'Internal')),
    operating_mode TEXT NOT NULL,
    vehicle_id INTEGER REFERENCES vehicles(id),
    vehicle_reg TEXT NOT NULL,
    vehicle_reg_norm TEXT NOT NULL,
    driver_id INTEGER REFERENCES drivers(id),
    driver_name TEXT,
    transport_company_id INTEGER REFERENCES transport_companies(id),
    customer_id INTEGER REFERENCES customers(id),
    customer_name TEXT,
    supplier_id INTEGER REFERENCES suppliers(id),
    supplier_name TEXT,
    material_id INTEGER REFERENCES materials(id),
    material_name TEXT,
    order_id INTEGER REFERENCES orders(id),
    delivery_note_no TEXT,
    reference_no TEXT,
    remarks TEXT,
    replaces_transaction_id INTEGER REFERENCES weighment_transactions(id),
    first_measurement_id INTEGER,
    second_measurement_id INTEGER,
    first_g INTEGER,
    second_g INTEGER,
    gross_g INTEGER,
    tare_g INTEGER,
    net_g INTEGER,
    first_is_gross INTEGER,
    tare_method TEXT,
    has_manual_weight INTEGER NOT NULL DEFAULT 0,
    has_override INTEGER NOT NULL DEFAULT 0,
    created_by INTEGER NOT NULL REFERENCES users(id),
    created_utc TEXT NOT NULL,
    completed_by INTEGER REFERENCES users(id),
    completed_utc TEXT,
    cancelled_by INTEGER REFERENCES users(id),
    cancelled_utc TEXT,
    cancel_reason TEXT,
    updated_utc TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1,
    CHECK (status <> 'Completed' OR (gross_g IS NOT NULL AND tare_g IS NOT NULL AND net_g IS NOT NULL AND completed_utc IS NOT NULL)),
    CHECK (status <> 'Cancelled' OR (cancel_reason IS NOT NULL AND cancelled_utc IS NOT NULL))
);
-- A vehicle can have only one open (pending) weighing per site.
CREATE UNIQUE INDEX ux_txn_pending_vehicle ON weighment_transactions(site_id, vehicle_reg_norm) WHERE status = 'Pending';
CREATE INDEX ix_txn_created ON weighment_transactions(created_utc);
CREATE INDEX ix_txn_completed ON weighment_transactions(completed_utc);
CREATE INDEX ix_txn_status_created ON weighment_transactions(status, created_utc);
CREATE INDEX ix_txn_vehicle ON weighment_transactions(vehicle_reg_norm, created_utc);
CREATE INDEX ix_txn_customer ON weighment_transactions(customer_id, created_utc);
CREATE INDEX ix_txn_supplier ON weighment_transactions(supplier_id, created_utc);
CREATE INDEX ix_txn_material ON weighment_transactions(material_id, created_utc);
CREATE INDEX ix_txn_operator ON weighment_transactions(created_by, created_utc);

CREATE TABLE weight_measurements (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    transaction_id INTEGER NOT NULL REFERENCES weighment_transactions(id),
    stage INTEGER NOT NULL CHECK (stage IN (1, 2)),
    source TEXT NOT NULL CHECK (source IN ('Indicator', 'Manual', 'StoredTare')),
    weight_g INTEGER NOT NULL,
    reported_value TEXT,
    reported_unit TEXT NOT NULL,
    reported_decimals INTEGER NOT NULL DEFAULT 0,
    is_stable INTEGER NOT NULL,
    stability_reason TEXT,
    stability_span_g INTEGER,
    stable_for_ms INTEGER,
    unstable_override INTEGER NOT NULL DEFAULT 0,
    override_reason TEXT,
    override_approved_by INTEGER REFERENCES users(id),
    manual_reason TEXT,
    manual_approved_by INTEGER REFERENCES users(id),
    captured_utc TEXT NOT NULL,
    reading_received_utc TEXT,
    reading_age_ms INTEGER,
    raw_text TEXT,
    raw_hex TEXT,
    indicator_flags INTEGER NOT NULL DEFAULT 0,
    port_description TEXT,
    profile_uid TEXT,
    profile_name TEXT,
    profile_version INTEGER,
    operating_mode TEXT NOT NULL,
    operator_id INTEGER NOT NULL REFERENCES users(id),
    machine TEXT,
    corrects_measurement_id INTEGER REFERENCES weight_measurements(id),
    correction_reason TEXT,
    CHECK (source <> 'Manual' OR manual_reason IS NOT NULL),
    CHECK (unstable_override = 0 OR override_reason IS NOT NULL),
    CHECK (corrects_measurement_id IS NULL OR correction_reason IS NOT NULL)
);
CREATE INDEX ix_measurement_txn ON weight_measurements(transaction_id);
CREATE INDEX ix_measurement_profile ON weight_measurements(profile_name);

CREATE TABLE raw_frames (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    measurement_id INTEGER NOT NULL REFERENCES weight_measurements(id),
    seq INTEGER NOT NULL,
    received_utc TEXT NOT NULL,
    hex TEXT NOT NULL,
    display TEXT NOT NULL,
    is_captured INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_raw_frames_measurement ON raw_frames(measurement_id);

CREATE TABLE camera_evidence (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    transaction_id INTEGER NOT NULL REFERENCES weighment_transactions(id),
    measurement_id INTEGER REFERENCES weight_measurements(id),
    camera_id TEXT NOT NULL,
    camera_name TEXT NOT NULL,
    file_path TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    content_type TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    captured_utc TEXT NOT NULL
);
CREATE INDEX ix_evidence_txn ON camera_evidence(transaction_id);

CREATE TABLE transaction_changes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    transaction_id INTEGER NOT NULL REFERENCES weighment_transactions(id),
    field TEXT NOT NULL,
    old_value TEXT,
    new_value TEXT,
    reason TEXT,
    user_id INTEGER NOT NULL REFERENCES users(id),
    approved_by INTEGER REFERENCES users(id),
    at_utc TEXT NOT NULL
);
CREATE INDEX ix_txn_changes_txn ON transaction_changes(transaction_id);

CREATE TABLE ticket_templates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    paper TEXT NOT NULL,
    template_json TEXT NOT NULL,
    is_default INTEGER NOT NULL DEFAULT 0,
    updated_utc TEXT NOT NULL
);

CREATE TABLE print_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    transaction_id INTEGER NOT NULL REFERENCES weighment_transactions(id),
    action TEXT NOT NULL,
    copies INTEGER NOT NULL DEFAULT 1,
    printer TEXT,
    template TEXT,
    reason TEXT,
    user_id INTEGER NOT NULL REFERENCES users(id),
    at_utc TEXT NOT NULL
);
CREATE INDEX ix_print_history_txn ON print_history(transaction_id);

CREATE TABLE approvals (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    request_type TEXT NOT NULL,
    entity_type TEXT,
    entity_id TEXT,
    requested_by INTEGER NOT NULL REFERENCES users(id),
    requested_utc TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('Pending', 'Approved', 'Rejected')),
    decided_by INTEGER REFERENCES users(id),
    decided_utc TEXT,
    decision_reason TEXT,
    payload_json TEXT,
    source TEXT NOT NULL DEFAULT 'Local'
);
CREATE INDEX ix_approvals_status ON approvals(status, requested_utc);

CREATE TABLE audit_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    at_utc TEXT NOT NULL,
    user_id INTEGER,
    username TEXT NOT NULL,
    machine TEXT,
    category TEXT NOT NULL,
    action TEXT NOT NULL,
    entity_type TEXT,
    entity_id TEXT,
    summary TEXT NOT NULL,
    old_json TEXT,
    new_json TEXT,
    reason TEXT,
    prev_hash TEXT NOT NULL,
    hash TEXT NOT NULL
);
CREATE INDEX ix_audit_at ON audit_events(at_utc);
CREATE INDEX ix_audit_entity ON audit_events(entity_type, entity_id);
CREATE INDEX ix_audit_category ON audit_events(category, at_utc);

-- Head of the audit hash chain; updating it serialises concurrent writers.
CREATE TABLE audit_chain (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    last_id INTEGER NOT NULL,
    last_hash TEXT NOT NULL
);
INSERT INTO audit_chain (id, last_id, last_hash) VALUES (1, 0, 'GENESIS');

CREATE TABLE settings (
    key TEXT PRIMARY KEY,
    value_json TEXT NOT NULL,
    is_secret INTEGER NOT NULL DEFAULT 0,
    updated_utc TEXT NOT NULL,
    updated_by INTEGER REFERENCES users(id)
);

CREATE TABLE sync_queue (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type TEXT NOT NULL,
    entity_uid TEXT NOT NULL,
    entity_version INTEGER NOT NULL DEFAULT 0,
    payload_json TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    attempts INTEGER NOT NULL DEFAULT 0,
    next_attempt_utc TEXT NOT NULL,
    last_error TEXT,
    status TEXT NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Sent', 'Failed', 'Conflict')),
    sent_utc TEXT
);
CREATE INDEX ix_sync_queue_due ON sync_queue(status, next_attempt_utc);

CREATE TABLE sync_conflicts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type TEXT NOT NULL,
    entity_uid TEXT NOT NULL,
    local_version INTEGER,
    remote_version INTEGER,
    detail TEXT,
    at_utc TEXT NOT NULL,
    resolved INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE notifications (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    at_utc TEXT NOT NULL,
    severity TEXT NOT NULL,
    category TEXT NOT NULL,
    title TEXT NOT NULL,
    message TEXT NOT NULL,
    entity_type TEXT,
    entity_id TEXT,
    is_read INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_notifications_at ON notifications(at_utc);

CREATE TABLE device_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    at_utc TEXT NOT NULL,
    weighbridge_id INTEGER REFERENCES weighbridges(id),
    device TEXT NOT NULL,
    event_type TEXT NOT NULL,
    message TEXT NOT NULL
);
CREATE INDEX ix_device_events_at ON device_events(at_utc);

CREATE TABLE backup_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    started_utc TEXT NOT NULL,
    completed_utc TEXT,
    kind TEXT NOT NULL,
    file_path TEXT,
    size_bytes INTEGER,
    sha256 TEXT,
    status TEXT NOT NULL,
    error TEXT,
    initiated_by INTEGER REFERENCES users(id)
);

-- ---------------------------------------------------------------- immutability
CREATE TRIGGER trg_measurements_no_update BEFORE UPDATE ON weight_measurements BEGIN SELECT RAISE(ABORT, 'weight_measurements are immutable'); END;
CREATE TRIGGER trg_measurements_no_delete BEFORE DELETE ON weight_measurements BEGIN SELECT RAISE(ABORT, 'weight_measurements are immutable'); END;
CREATE TRIGGER trg_raw_frames_no_update BEFORE UPDATE ON raw_frames BEGIN SELECT RAISE(ABORT, 'raw_frames are immutable'); END;
CREATE TRIGGER trg_raw_frames_no_delete BEFORE DELETE ON raw_frames BEGIN SELECT RAISE(ABORT, 'raw_frames are immutable'); END;
CREATE TRIGGER trg_evidence_no_update BEFORE UPDATE ON camera_evidence BEGIN SELECT RAISE(ABORT, 'camera_evidence is immutable'); END;
CREATE TRIGGER trg_evidence_no_delete BEFORE DELETE ON camera_evidence BEGIN SELECT RAISE(ABORT, 'camera_evidence is immutable'); END;
CREATE TRIGGER trg_changes_no_update BEFORE UPDATE ON transaction_changes BEGIN SELECT RAISE(ABORT, 'transaction_changes are immutable'); END;
CREATE TRIGGER trg_changes_no_delete BEFORE DELETE ON transaction_changes BEGIN SELECT RAISE(ABORT, 'transaction_changes are immutable'); END;
CREATE TRIGGER trg_print_no_update BEFORE UPDATE ON print_history BEGIN SELECT RAISE(ABORT, 'print_history is immutable'); END;
CREATE TRIGGER trg_print_no_delete BEFORE DELETE ON print_history BEGIN SELECT RAISE(ABORT, 'print_history is immutable'); END;
CREATE TRIGGER trg_audit_no_update BEFORE UPDATE ON audit_events BEGIN SELECT RAISE(ABORT, 'audit_events are immutable'); END;
CREATE TRIGGER trg_audit_no_delete BEFORE DELETE ON audit_events BEGIN SELECT RAISE(ABORT, 'audit_events are immutable'); END;
CREATE TRIGGER trg_tare_history_no_update BEFORE UPDATE ON vehicle_tare_history BEGIN SELECT RAISE(ABORT, 'vehicle_tare_history is immutable'); END;
CREATE TRIGGER trg_tare_history_no_delete BEFORE DELETE ON vehicle_tare_history BEGIN SELECT RAISE(ABORT, 'vehicle_tare_history is immutable'); END;
CREATE TRIGGER trg_profile_versions_no_update BEFORE UPDATE ON indicator_profile_versions BEGIN SELECT RAISE(ABORT, 'indicator_profile_versions are immutable'); END;
CREATE TRIGGER trg_profile_versions_no_delete BEFORE DELETE ON indicator_profile_versions BEGIN SELECT RAISE(ABORT, 'indicator_profile_versions are immutable'); END;

-- Transactions are never deleted; the weights of a completed transaction never change (corrections create a
-- new measurement and are recorded in transaction_changes, which is handled before completion).
CREATE TRIGGER trg_txn_no_delete BEFORE DELETE ON weighment_transactions BEGIN SELECT RAISE(ABORT, 'weighment_transactions cannot be deleted; cancel them instead'); END;
CREATE TRIGGER trg_txn_completed_weights BEFORE UPDATE OF gross_g, tare_g, net_g, first_g, second_g, first_measurement_id, second_measurement_id ON weighment_transactions
WHEN OLD.status = 'Completed'
BEGIN SELECT RAISE(ABORT, 'weights of a completed transaction cannot be changed'); END;
CREATE TRIGGER trg_txn_cancelled_final BEFORE UPDATE ON weighment_transactions
WHEN OLD.status = 'Cancelled'
BEGIN SELECT RAISE(ABORT, 'a cancelled transaction cannot be modified'); END;
