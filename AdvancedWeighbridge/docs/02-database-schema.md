# 2. Database Schema

The site database is created by `src/Weighbridge.Data/Migrations/Sqlite/001_initial.sql` (SQLite, the default) or
`.../PostgreSql/001_initial.sql` (central PostgreSQL). Both scripts define the same tables, constraints and
protections; the migration runner records applied scripts in `schema_migrations` and refuses to start against a
database written by a newer version. The cloud server has its own schema (section 2.6).

Conventions:

* Weights are **integer grams** (`*_g` columns, 64-bit). No floating point is used for weights anywhere.
* Times are UTC (`*_utc`, ISO-8601 text on SQLite, `timestamptz` on PostgreSQL).
* Business records carry a `uid` (GUID) used for synchronisation; `id` is the local key.
* `version` is incremented on every change of a transaction and is what the cloud uses to detect stale or
  conflicting uploads.

## 2.1 Weighing records

| Table | Purpose | Key rules |
|---|---|---|
| `weighment_transactions` | One ticket: vehicle, parties, material, order, first/second weights, gross/tare/net, status (`Pending`, `Completed`, `Cancelled`), operating mode, tare method, manual/override flags, who/when for create, complete and cancel. | `CHECK`: a completed ticket has gross, tare and net; a cancelled one has a reason and a user. **Partial unique index** `ux_txn_pending_vehicle`: only one pending ticket per vehicle per site. Trigger `trg_txn_no_delete`: tickets are never deleted. Trigger `trg_txn_cancelled_final`: a cancelled ticket cannot change any more. |
| `weight_measurements` | Every captured weight with its evidence: source (`Indicator`, `Manual`, `StoredTare`), weight, reported value/unit/decimals, stability result and reason, span and stable time, reading age, raw text and hex of the frame, indicator flags, port, **profile uid/name/version**, operator, machine, operating mode. | **Append-only** (update/delete triggers). `CHECK`: a manual measurement must have a reason; an unstable override must have a reason; a correction must reference the corrected measurement and give a reason. |
| `raw_frames` | The last frames before a capture (evidence ring), hex and printable form, with the captured frame marked. | Append-only. |
| `camera_evidence` | Snapshot files taken at capture: camera, path, SHA-256, size, type. | Append-only; the hash detects later file changes. |
| `transaction_changes` | Field-level history of edits to tickets (old value, new value, reason, user, approver). | Append-only. |
| `print_history` | Every print, reprint and PDF export with copies, printer, template, reason and user. | Append-only. |
| `approvals` | Supervisor approvals (manual weight, unstable override, cancellation, completed-ticket edit, tare approval): request, decision, decision reason, source (`Local` or `Cloud`). | |
| `sequences` | Ticket number counters. The scope follows the numbering pattern (tokens `{SITE} {WB} {YYYY} {YY} {MM} {DD} {SEQ:n}`, default `{SITE}-{YYYY}-{SEQ:6}`) and the reset rule (never, yearly, monthly, daily). Incremented inside the capture transaction, so numbers are never reused. | |

## 2.2 Master data

| Table | Notes |
|---|---|
| `sites`, `weighbridges` | Site code/name/address; weighbridge capacity, minimum capture weight and division (grams). |
| `vehicles` | Registration as typed and normalised (`registration_norm`, upper case without spaces/dashes) for matching ANPR, RFID tag, QR code, category, transport company, default driver, **approved stored tare** (value, approver, approval and expiry time), blacklist flag with mandatory reason. |
| `vehicle_tare_history` | Every stored-tare value with its source and approval. Append-only. |
| `vehicle_categories`, `transport_companies`, `drivers` | Driver licence/NIC numbers are optional and never uploaded unless sharing is enabled. |
| `customers`, `suppliers`, `materials` | Code, name, contact and tax data; materials have category and grade. |
| `orders` | Sales/purchase orders with customer or supplier, material, ordered quantity, validity and status. |

Master records are deactivated (`is_active = 0`), not deleted, once they are referenced.

## 2.3 Indicators and devices

| Table | Notes |
|---|---|
| `indicator_profiles` | The device profile as JSON (connection, framing, checksum, parser, stability, polling), manufacturer/model, version, `verified` flag and which profile is active. |
| `indicator_profile_versions` | Every saved version of a profile with user and time. Append-only, so each measurement's `profile_version` can always be looked up. |
| `indicator_devices` | Which profile drives which weighbridge, optional port override. |
| `device_events` | Connect/disconnect/error events (rate-limited), for diagnostics. |

## 2.4 Security, audit and administration

| Table | Notes |
|---|---|
| `roles`, `role_permissions` | Five built-in roles (Administrator, Manager, Operator, Auditor, Viewer) mapped to fine-grained permissions; editable. |
| `users` | PBKDF2 password hash, role, active flag, forced password change, TOTP secret (encrypted with DPAPI), last used TOTP step (replay protection), failed attempts and lock-out time. |
| `login_history` | Every sign-in attempt with result, reason and machine. |
| `audit_events` | Hash-chained audit log: user, machine, category, action, entity, summary, old/new JSON, reason, `prev_hash`, `hash` (SHA-256 over the canonical event text and the previous hash). **Append-only.** |
| `audit_chain` | Last id and hash, updated in the same transaction as each audit insert. *Verify audit chain* recomputes the chain and reports the first broken link. |
| `settings` | JSON settings by key (company, localisation, weighing, approvals, printing, cloud, backup, security, automation, hardware); secret values are DPAPI-protected. |
| `notifications` | Local alerts (manual weights, automation faults, failed backups, remote requests). |
| `backup_history` | Each backup with file, size, SHA-256, status and user. |

## 2.5 Cloud synchronisation (site side)

| Table | Notes |
|---|---|
| `sync_queue` | Outbox: entity type/uid/version and payload, attempts, next attempt time (exponential back-off), status (`Pending`, `Sent`, `Failed`, `Conflict`), last error. Writing a record and queueing it happen in one database transaction. A newer version replaces the pending payload; marking an item sent or failed checks the version, so a change made during an upload is never lost. |
| `sync_conflicts` | Uploads the server rejected as conflicting (same version, different content). Never overwritten automatically; listed for review. |

## 2.6 Cloud server schema

`src/Weighbridge.Cloud/Migrations/CloudSqlite|CloudPostgreSql/001_cloud.sql`:

| Table | Notes |
|---|---|
| `sites` | Site code/name, **SHA-256 hash and prefix of the site key** (the key itself is shown once), active flag, last heartbeat, app version, last heartbeat payload (live status), offline-notified flag. |
| `cloud_users`, `cloud_user_sites` | Cloud accounts (separate from site accounts) with role and the sites they may see; administrators see all sites. |
| `refresh_tokens` | Hash of each refresh token, token family, expiry, revocation and replacement. Re-use of a replaced token revokes the whole family. |
| `cloud_transactions` | Latest accepted version of each ticket per site: searchable columns plus the full payload and its hash. |
| `sync_conflicts` | Conflicting uploads with the rejected payload. |
| `cloud_audit_events` | The site audit chains as uploaded (with `prev_hash`/`hash`), so continuity can be verified in the cloud. |
| `cloud_notifications`, `notification_reads` | Site alerts plus per-user read state. |
| `site_users` | Display list of each site's operator accounts (no password data). |
| `commands` | Remote requests (currently ticket cancellation) with status `Pending → Delivered → Completed/Failed` and the site's result message. |
| `server_audit` | Sign-ins, account changes, key rotations and remote requests made in the cloud, with IP address. |

## 2.7 Retention

Nothing in either schema is deleted on a timer. Tickets, measurements, evidence metadata, audit events and print
history are kept permanently; only log files (60 days) and backup files (configured count) are pruned. Any
retention beyond that must be an explicit, authorised policy — see the administrator manual.
