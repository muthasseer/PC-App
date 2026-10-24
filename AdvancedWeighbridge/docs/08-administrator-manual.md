# 8. Administrator Manual

For installation see `10-installation-guide.md`; for backups `12-backup-and-recovery.md`.

## 8.1 First start

The first start opens **Welcome to Advanced Weighbridge**: company details printed on tickets (name, address,
phone, tax number), language, the first administrator account, the site (code, name) and the first weighbridge
(code, name, capacity, minimum capture weight, indicator division). Everything can be changed later. Then sign in
as the administrator, create a personal account for every operator, and configure the indicator (8.4).

## 8.2 Users, roles and permissions (Administration → Users & roles)

| Role | Intended for | Main rights (defaults, editable) |
|---|---|---|
| Administrator | IT / system owner | Everything, including users, roles, settings, devices, cloud, backup/restore. |
| Manager | Site manager / supervisor | Weighing (all modes), approvals, edit/correct/cancel tickets, reprints, reports and export, master data, stored-tare approval, blacklist, devices view, hardware override, audit, backups. |
| Operator | Weighbridge operator | Dashboard, capture (semi-automatic; cannot switch on automatic mode), manual entry (subject to approval), edit details, print (not reprint), history, reports, master data, devices view. |
| Auditor | Internal/external audit | Read-only: dashboard, history, reports and export, master data, devices, audit trail and its verification. |
| Viewer | Read-only users | Dashboard, history and reports. |

Permissions are fine-grained (for example *capture weight*, *manual weight entry*, *override unstable weight*,
*decide approvals*, *edit transaction*, *correct weight*, *cancel transaction*, *print/reprint ticket*, *approve
vehicle tare*, *blacklist vehicle*, *configure devices*, *hardware override*, *manage users/roles/settings/cloud*,
*view audit*, *manage backups*). The role → permission matrix can be edited; changes are audited.

Per user: active/inactive, *must change password at next sign-in*, unlock after lock-out, reset password, set up
or remove the authenticator (TOTP, RFC 6238 — works with common authenticator apps). Security settings: session
timeout (30 min), failed sign-ins before lock-out (5) and lock-out time (15 min), minimum password length (8), and
*require two-factor sign-in for administrators*.

Supervisor approvals (Settings → Approvals) decide which actions need a second person who signs in on the same
dialog: manual weight entry, cancellation, capturing an unstable weight, editing completed tickets; reprints need a
reason; the minimum reason length is 5 characters. Approvals are recorded with both users.

## 8.3 Settings (Administration → Settings)

| Section | Contents |
|---|---|
| Company | Name, address, phone, e-mail, tax number, logo for tickets. |
| Localisation | Language (English, Sinhala, Tamil), theme (light/dark), display unit and decimals, thousands separator. |
| Weighing | Default mode; capture policy for unstable weights (reject / wait for stable — default, 15 s / supervisor override); freshness limit (1.5 s); negative weights; net rules; ticket number pattern (default `{SITE}-{YYYY}-{SEQ:6}`, yearly reset); duplicate warning window (15 min); required material and customer/supplier; automatic vehicle creation; stored tare validity (90 days); camera timeout. |
| Approvals | See 8.2. |
| Printing | Ticket template and paper (A4, A5, 80 mm or 58 mm thermal), print on first weighing / on completion, confirm before printing, printer, ESC/POS raw mode for receipt printers, PDF copy of every ticket. |
| Cloud | See 8.6. |
| Backup | See `12-backup-and-recovery.md`. |
| Security | See 8.2. |
| Automation | Zero band, vehicle detection weight, identification required and its timeout, stability timeout, automatic exit opening, abnormal weight change limit, communication loss timeout. |

Every settings change is written to the audit trail with old and new values.

## 8.4 Indicator (Devices → Indicator setup)

Follow `05-com-integration.md`. In short: select the port and settings (or *Detect line settings
automatically*), capture raw data, accept or adjust the suggested framing and parser, **compare with a reference
weight shown on the indicator** until it matches, set the stability rules and *Save and use this profile*. Only a
profile that matched the indicator display can be marked verified. The raw data monitor (Devices → Raw data
monitor) shows every frame and the reason for each rejection. Use a test weight or a vehicle of known weight
before trade use, and repeat the check after any change to the indicator or the profile.

## 8.5 Lights, barriers, sensors, cameras and identification (Devices → Lights & barriers)

The hardware configuration is edited as JSON on this page (users with *manage settings*). Saving checks that the
JSON is valid, asks for a reason for the audit trail, and takes effect after restarting the application. *Insert simulated lights, barriers, sensors and ANPR* fills in a complete example for training.

* **I/O controllers**: Modbus TCP, Modbus RTU (serial), HTTP relay boards, or simulated I/O for training.
* **Outputs**: entry/exit red, amber, green; entry/exit barrier open/close (level or pulse drive, pulse length);
  alarm. **Inputs**: vehicle presence, entry/exit beam blocked, safety OK (barrier area clear / emergency stop).
* **Cameras**: HTTP snapshot URL (JPEG) with basic or digest credentials (stored encrypted); images are captured at
  each weighing and stored with their SHA-256.
* **Identification**: ANPR (HTTP push or poll), RFID or QR readers on serial ports or as keyboard wedge.

*Manual control* buttons open/close the barriers and set the lights; each asks for a reason and is audited.
Interlocks are enforced in software for every barrier command, including manual control: the **exit** opens only
when the weighing was captured and saved, a vehicle is on the platform, no fault is active and the safety input is
OK; the **entry** opens only when the platform is empty, the indicator delivers valid readings, no fault is active
and the safety input is OK. A manual override needs the *hardware override* permission and is audited. Software
interlocks do not replace the electrical safety circuits of the barriers, which must comply with local
regulations. **These devices have been tested with the simulator only; test every device on site with the
procedures in `09-hardware-acceptance-tests.md`.**

## 8.6 Cloud connection (optional)

1. On the cloud web dashboard (Administration → Sites) create the site with the same code as on the PC. The site
   key is shown **once**; copy it.
2. On the PC: Settings → Cloud: enable, enter the server address (`https://…`; plain HTTP is accepted for `localhost`, or when *Allow insecure
   HTTP* is ticked — only for tests on a private network, because the site key and data then travel unencrypted), paste the site key (stored encrypted with Windows DPAPI), choose the interval (30 s) and
   what to share: live weight (on by default), driver names (off by default), audit trail (on), and whether to
   accept remote requests (cancellations by authorised cloud users).
3. The status bar shows the sync state. Weighing never waits for the cloud: changes are queued locally and sent
   when the connection is available (exponential back-off). Conflicts (the server holds different content for the
   same version) are recorded for review on both sides and never overwritten silently.

What is sent: tickets and measurements (with the MANUAL flag), notifications, the audit trail (hash-chained), the
list of user names/roles/last sign-in, heartbeats with the live weight and health (disk space, last backup, queue
sizes). **Never sent**: passwords or password hashes, authenticator secrets, the database file, camera images,
DPAPI-protected settings.

## 8.7 Cloud server administration

Web dashboard → Administration: sites (create, rotate key, activate/deactivate), cloud users (role, sites,
active, reset password, unlock, reset authenticator), conflicts, server audit (sign-ins, account changes, key
rotations, remote requests with IP address). Deployment and configuration: `10-installation-guide.md`.
API: `13-api-reference.md`.

## 8.8 Data retention and deletion

AWMS does **not** delete tickets, measurements, evidence metadata, audit events, approvals, prints or sync records
on a timer. The database prevents deleting or changing measurements, raw frames, evidence records, changes, prints,
tare history and audit events, and deleting tickets. Only these files are pruned automatically: log files (older
than 60 days) and backup files beyond the configured number of copies. Camera image files are kept.

If your organisation has an authorised retention policy that requires removing old records (for example a legal
limit for personal data such as driver names), implement it as a documented, approved procedure: export and archive
the affected period first, record the authorisation, and perform the removal on a copy of the database with
database administration tools under the four-eyes principle. Note that removing audit events breaks the audit
hash chain from that point; the archive and the authorisation record are the evidence of what was removed and why.

## 8.9 Audit trail

Administration → Audit lists every security event, setting change, master-data change, weighing, edit, print,
cancellation, approval, device and automation event, backup and restore. **Verify audit chain** recomputes the
SHA-256 chain and reports the first event that does not match (tampering or database damage). The cloud keeps a
copy of each site's chain and can verify its continuity independently.

## 8.10 Command-line options

```
AdvancedWeighbridge.exe --help
  --data-dir <folder>   other data folder (default C:\ProgramData\AdvancedWeighbridge, or AWMS_DATA_DIR)
  --init                create folders and database, then exit (used by the installer)
  --list-ports          list serial ports with USB details and exit
  --selftest            built-in self-test with the simulator in a temporary folder (exit code 0 = passed)
  --screenshots <dir>   render all main screens with demonstration data to PNG files
  --out <file>          write the output of --list-ports / --selftest to a file
```

The indicator simulator for training and acceptance tests without hardware: `awms-indicator-simulator`
(`--port COM5` with a virtual COM pair such as com0com, or `--tcp 4001`; `--template`, `--replay <file.wbcap>`,
`--auto`; keys: L loaded truck on, E empty truck on, O drive off, A automatic traffic, V overload, X garbage bytes, D pause output (simulated disconnect), Q quit).
