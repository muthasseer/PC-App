-- AWMS cloud server schema (SQLite variant for small installations and development).
-- Same conventions as the site database: ISO-8601 UTC text timestamps, integer grams, GUID text uids.

CREATE TABLE sites (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE COLLATE NOCASE,
    name TEXT NOT NULL,
    key_hash TEXT NOT NULL UNIQUE,
    key_prefix TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_utc TEXT NOT NULL,
    last_heartbeat_utc TEXT,
    app_version TEXT,
    heartbeat_json TEXT,
    offline_notified INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE cloud_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT NOT NULL UNIQUE COLLATE NOCASE,
    display_name TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    role TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    must_change_password INTEGER NOT NULL DEFAULT 0,
    mfa_secret TEXT,
    mfa_last_step INTEGER,
    failed_attempts INTEGER NOT NULL DEFAULT 0,
    locked_until_utc TEXT,
    last_login_utc TEXT,
    created_utc TEXT NOT NULL
);

CREATE TABLE cloud_user_sites (
    user_id INTEGER NOT NULL REFERENCES cloud_users(id) ON DELETE CASCADE,
    site_id INTEGER NOT NULL REFERENCES sites(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, site_id)
);

CREATE TABLE refresh_tokens (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES cloud_users(id) ON DELETE CASCADE,
    token_hash TEXT NOT NULL UNIQUE,
    family TEXT NOT NULL,
    expires_utc TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    revoked_utc TEXT,
    replaced INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_refresh_family ON refresh_tokens(family);

CREATE TABLE cloud_transactions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    site_id INTEGER NOT NULL REFERENCES sites(id),
    uid TEXT NOT NULL UNIQUE,
    ticket_no TEXT NOT NULL,
    version INTEGER NOT NULL,
    status TEXT NOT NULL,
    vehicle_reg TEXT NOT NULL,
    vehicle_norm TEXT NOT NULL,
    customer_name TEXT,
    supplier_name TEXT,
    material_name TEXT,
    gross_g INTEGER,
    tare_g INTEGER,
    net_g INTEGER,
    has_manual INTEGER NOT NULL DEFAULT 0,
    operating_mode TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    completed_utc TEXT,
    payload_json TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    received_utc TEXT NOT NULL
);
CREATE INDEX ix_ctx_site_created ON cloud_transactions(site_id, created_utc);
CREATE INDEX ix_ctx_completed ON cloud_transactions(completed_utc);
CREATE INDEX ix_ctx_vehicle ON cloud_transactions(vehicle_norm);
CREATE INDEX ix_ctx_status ON cloud_transactions(status, created_utc);

CREATE TABLE sync_conflicts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    site_id INTEGER NOT NULL REFERENCES sites(id),
    entity_type TEXT NOT NULL,
    uid TEXT NOT NULL,
    local_version INTEGER,
    server_version INTEGER,
    payload_json TEXT,
    at_utc TEXT NOT NULL
);

CREATE TABLE cloud_audit_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    site_id INTEGER NOT NULL REFERENCES sites(id),
    uid TEXT NOT NULL UNIQUE,
    local_id INTEGER NOT NULL,
    at_utc TEXT NOT NULL,
    username TEXT NOT NULL,
    machine TEXT,
    category TEXT NOT NULL,
    action TEXT NOT NULL,
    entity_type TEXT,
    entity_id TEXT,
    summary TEXT NOT NULL,
    reason TEXT,
    prev_hash TEXT NOT NULL,
    hash TEXT NOT NULL,
    received_utc TEXT NOT NULL
);
CREATE INDEX ix_caudit_site ON cloud_audit_events(site_id, local_id);
CREATE INDEX ix_caudit_at ON cloud_audit_events(at_utc);

CREATE TABLE cloud_notifications (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    site_id INTEGER NOT NULL REFERENCES sites(id),
    uid TEXT NOT NULL UNIQUE,
    at_utc TEXT NOT NULL,
    severity TEXT NOT NULL,
    category TEXT NOT NULL,
    title TEXT NOT NULL,
    message TEXT NOT NULL,
    entity_type TEXT,
    entity_id TEXT,
    received_utc TEXT NOT NULL
);
CREATE INDEX ix_cnotif_at ON cloud_notifications(at_utc);

CREATE TABLE notification_reads (
    user_id INTEGER NOT NULL REFERENCES cloud_users(id) ON DELETE CASCADE,
    notification_id INTEGER NOT NULL REFERENCES cloud_notifications(id),
    PRIMARY KEY (user_id, notification_id)
);

CREATE TABLE site_users (
    site_id INTEGER NOT NULL REFERENCES sites(id),
    username TEXT NOT NULL,
    display_name TEXT NOT NULL,
    role TEXT NOT NULL,
    is_active INTEGER NOT NULL,
    last_login_utc TEXT,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (site_id, username)
);

CREATE TABLE commands (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    site_id INTEGER NOT NULL REFERENCES sites(id),
    type TEXT NOT NULL,
    target_uid TEXT,
    reason TEXT,
    requested_by TEXT NOT NULL,
    requested_utc TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Pending',
    result_message TEXT,
    delivered_utc TEXT,
    completed_utc TEXT
);
CREATE INDEX ix_commands_site ON commands(site_id, status);

CREATE TABLE server_audit (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    at_utc TEXT NOT NULL,
    username TEXT NOT NULL,
    action TEXT NOT NULL,
    detail TEXT NOT NULL,
    ip TEXT
);
CREATE INDEX ix_server_audit_at ON server_audit(at_utc);
