# 14. Release Notes

## Version 1.0.0

First release of the Advanced Weighbridge Management System.

### Weighbridge PC (Windows 10 1809+ / Windows 11, 64-bit)

* Weighing dashboard with very large live weight, colour + symbol + text status, GET WEIGHT (F9) capturing the
  validated reading already in memory, wait-for-stable capture (Esc cancels), automatic, semi-automatic and manual
  modes, first/second weighing, stored tare, duplicate and blacklist checks, supervisor approvals.
* Universal indicator integration: serial (all line settings), TCP, simulator and replay transports; framing by
  terminator, start/end markers, fixed length or idle gap; five checksum types; auto, regex, delimited,
  fixed-position and protocol parsers; plug-ins for proprietary protocols; strict rejection of ambiguous or
  malformed data with reasons; raw data monitor; five-step setup wizard that learns the format from sample output
  and verifies it against the indicator display; versioned device profiles with import/export.
* Evidence with every capture: raw frames, profile version, reading age, stability evidence, camera images with
  SHA-256; append-only measurements and hash-chained audit trail with verification.
* Automatic mode with traffic lights, barriers, presence/position/safety sensors (Modbus TCP/RTU, HTTP relays),
  ANPR, RFID/QR identification and software interlocks.
* Tickets on A4, A5, 80 mm and 58 mm paper in English, Sinhala and Tamil (bilingual option) with QR verification,
  PDF copies, reprint history; reports with XLSX, CSV and PDF export.
* Master data, users and roles with fine-grained permissions, two-factor sign-in, lock-out, session timeout.
* Daily backups with second copy, verification and restore; missed backups are caught up at the next start.
* Optional cloud synchronisation with an offline outbox, conflict recording and remote cancellation requests.
* Installer `AdvancedWeighbridgeSetup.exe` (self-contained, no downloads), silent install, upgrade, uninstall that
  keeps data; built-in self-test; indicator simulator tool.

### Cloud server (Docker: PostgreSQL + Caddy HTTPS)

* Sync API for sites (site keys), REST API and web dashboard: live status with NOT LIVE rule, transactions with
  ticket PDF and cancellation requests, reports and exports, alerts, audit trail with chain verification,
  administration of sites, users, conflicts and server audit; JWT with rotating refresh tokens, TOTP, rate
  limiting, strict security headers; public ticket verification page.

### Android app (Android 8.0+)

* Sign-in with two-factor code and forced password change; dashboard per site and weighbridge that shows LIVE only
  for fresh data; tickets with search and filters, details with manual entries labelled, in-app PDF and sharing;
  cancellation requests; reports; alerts with optional notifications; English, Sinhala, Tamil; light and dark
  theme; session token encrypted with the Android Keystore.

### Known limitations

See `06-development-plan-and-status.md`, sections 6.3 and 6.4. In particular: no physical indicators, I/O
devices, cameras, printers or phones were tested; installer and APK are unsigned without your certificates;
Sinhala and Tamil translations need native review; the desktop uses a local SQLite database.

### Fixes made during release testing

* Desktop: display-only checkbox columns used two-way bindings; the Users page raised an error for the read-only
  two-factor column (found by the automated screenshot run).
* Desktop: the default "wait for stable" capture policy only showed a message; it now waits and captures.
* Scheduled backups missed while the PC was off were not caught up.
* Sync outbox: a change made while an older version of the same record was being uploaded could be marked as sent.
