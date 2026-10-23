# 1. System Architecture

Advanced Weighbridge Management System (AWMS), version 1.0.

## 1.1 Goals that drive the design

| Priority | Design consequence |
|---|---|
| Accurate measurement capture | A capture is always taken from the **latest validated indicator reading** held in memory by a background service. Every capture stores the raw frame, parser profile version, reading age and stability evidence. Manual values are stored with `source = Manual` and can never be shown as indicator measurements. |
| Data integrity | Measurements and audit events are **append-only** (database triggers reject `UPDATE`/`DELETE`). Corrections create new linked records. The audit log is **hash-chained** (SHA-256), so tampering is detectable with *Verify audit chain*. |
| Offline reliability | Each weighbridge PC has a complete local database (SQLite, WAL mode). Weighing, tickets, history and reports never need the Internet. Cloud upload uses a local outbox queue. |
| Hardware compatibility | Indicators are described by **device profiles** (serial settings + framing + parser + stability rules) that are data, not code. Unknown indicators are learned in the Setup Wizard from sample output. Proprietary protocols can be added as plugins without changing the main application. |
| Operator usability | A full-screen dashboard with very large weight digits, colour **plus text plus icon** status, keyboard shortcuts (F9 = GET WEIGHT), touch-sized buttons, English / Sinhala / Tamil, light and dark themes. |

## 1.2 Deployment view

```
 ┌───────────────────────── Weighbridge site (offline-capable) ─────────────────────────┐
 │                                                                                       │
 │  Weight indicator ──RS-232/USB-serial/TCP──┐                                          │
 │  RFID / QR reader ──serial / keyboard──────┤                                          │
 │  ANPR camera ──HTTP push / poll────────────┤      ┌──────────────────────────────┐    │
 │  IP cameras ──HTTP JPEG snapshot───────────┼─────▶│  AWMS Desktop (Windows, WPF)  │    │
 │  Loop / photo sensors ─┐                   │      │  • Indicator service          │    │
 │  Traffic lights ───────┼─Modbus TCP/RTU,───┘      │  • Automation engine          │    │
 │  Barriers, alarm ──────┘ HTTP relay               │  • Weighing workflow          │    │
 │                                                   │  • Tickets / reports / export │    │
 │  A4 / thermal printer ◀── Windows spooler / RAW ──│  • SQLite DB + outbox         │    │
 │                                                   └──────────────┬───────────────┘    │
 └──────────────────────────────────────────────────────────────────┼────────────────────┘
                                                HTTPS (TLS), site API key, retry queue
                                                                    ▼
                         ┌──────────────────────────────────────────────────────┐
                         │ AWMS Cloud (ASP.NET Core, Linux/Windows, Docker)      │
                         │ • Sync API (idempotent, versioned, conflict records)  │
                         │ • REST API for web + Android (JWT, roles, branches)   │
                         │ • Web dashboard (static, same origin)                 │
                         │ • PostgreSQL                                          │
                         └──────────────────────────┬───────────────────────────┘
                                                    │ HTTPS + JWT
                                     ┌──────────────┴──────────────┐
                                     │ AWMS Android app (Kotlin)    │
                                     │ status, history, tickets,    │
                                     │ reports, notifications       │
                                     └─────────────────────────────┘
```

## 1.3 Logical layers (desktop)

```
 Weighbridge.Desktop (WPF, MVVM)      ← views, view models, localisation, themes, printing
        │
 Weighbridge.Application              ← use cases: GET WEIGHT, first/second weighing, manual entry,
        │                                approvals, automation controller, history, reports, backup,
        │                                sync worker, authentication, settings, health
        ├── Weighbridge.Core           ← pure domain, no I/O: weights & units, framing, checksums,
        │                                parser engine, format learner, stability detector, capture
        │                                policy, net calculation, automation state machine,
        │                                permissions, password hashing, TOTP
        ├── Weighbridge.Devices        ← transports (serial, TCP, simulator, replay), indicator
        │                                service, port discovery, Modbus TCP/RTU I/O, HTTP relays,
        │                                IP cameras, RFID/QR/ANPR sources, plugin loader
        ├── Weighbridge.Data           ← schema migrations (SQLite + PostgreSQL), repositories,
        │                                hash-chained audit store, backup/restore
        ├── Weighbridge.Reporting      ← ticket layout, PDF (Skia + HarfBuzz shaping, Noto fonts),
        │                                print rasters, ESC/POS, QR codes, XLSX/CSV/PDF reports
        └── Weighbridge.Sync           ← cloud client: outbox processing, heartbeats, commands
 Weighbridge.Contracts                ← DTOs shared by Sync client and Cloud server
```

Dependency rule: `Core` references nothing. Everything above it depends inwards. The WPF project is the only
Windows-only assembly (plus Windows-only code paths guarded by `OperatingSystem.IsWindows()`), so all business
logic is tested on Linux and Windows CI.

## 1.4 Threading and data flow of a reading

```
 Serial port ─bytes─▶ Transport (background thread)
                       │  ReadAsync loop, reconnect with back-off
                       ▼
                FrameAssembler  (terminator / STX-ETX / fixed length / idle gap; max length)
                       │ frames
                       ▼
                ChecksumValidator → WeightParser (profile) → WeightReading | Rejection
                       │                                         │
                       ▼                                         ▼
                StabilityDetector (sliding window)        DiagnosticsRing (raw monitor,
                       │                                   rejected frames, error counters)
                       ▼
                IndicatorSnapshot  (immutable, published with Volatile.Write — lock free)
                       │
         ┌─────────────┼──────────────────────────────┐
         ▼             ▼                              ▼
   UI dispatcher   AutomationController          GET WEIGHT button
   (throttled to   (state machine ticks,         (reads the snapshot that is
    ~10 Hz)         commands to I/O)              already in memory — no re-query)
```

* The transport and parser never touch the UI thread. The UI receives a coalesced snapshot through a bounded
  channel (`BoundedChannelFullMode.DropOldest`), so a fast indicator (e.g. 50 frames/s) cannot flood the dispatcher.
* Nothing is written to the database for transient readings. Only **captures** (with the raw frame and the last
  few frames as evidence) and **communication events** (connect / disconnect / error bursts, rate-limited) are stored.

## 1.5 Transaction life-cycle

```
           first capture                  second capture / stored tare / manual tare
  (none) ───────────────▶  PENDING  ─────────────────────────────────────────────▶  COMPLETED
                             │                                                        │
                             │ cancel (permission, reason)                            │ cancel (permission +
                             ▼                                                        ▼  approval if configured)
                         CANCELLED                                                CANCELLED
```

* One pending transaction per vehicle per site is enforced by a partial unique index.
* Gross/tare assignment: `Auto` (heavier = gross) supports both *enters loaded* and *enters empty*; or
  direction-based (`Inbound: first = gross`, `Outbound: first = tare`) with validation.
* The tare method is stored and printed: `Measured`, `StoredTare` (approved, not expired) or `ManualTare` (authorised).

## 1.6 Operating modes

| | Automatic | Semi-automatic | Manual |
|---|---|---|---|
| Live weight | yes | yes | optional (shown if connected) |
| Vehicle identification | ANPR / RFID / QR / operator fallback | operator (scanner assisted) | operator |
| Capture trigger | state machine: presence + identified + positioned + stable + limits | operator **GET WEIGHT** | operator types the value + mandatory reason |
| Print / barrier | automatic if configured; interlocks always enforced | operator confirms | operator, with permission |
| Permission | `Weighing.Automatic` to enable | `Weighing.Capture` | `Weighing.ManualEntry` (+ supervisor approval if configured) |

The **AutomationEngine** is a pure state machine (`Idle → VehicleEntering → AwaitingIdentification →
AwaitingPosition → AwaitingStable → Capturing → Completed → AwaitingExit → Idle`, plus `Fault`). It never opens a
barrier because a weight string arrived: the barrier interlock checks transaction state, vehicle presence,
authorisation and configured safety inputs.

## 1.7 Security model

* Roles: Administrator, Manager, Operator, Auditor, Viewer, mapped to ~30 fine-grained permissions
  (capture weight, change transaction data, manual weight, approve override, cancel ticket, configure indicators,
  manage users, …). Role → permission mapping is stored in the database and editable by administrators.
* Passwords: PBKDF2-HMAC-SHA256, 600 000 iterations, 16-byte salt, constant-time comparison.
* Optional TOTP (RFC 6238) multi-factor authentication. Failed-login lock-out. Idle session timeout.
* Secrets in settings (cloud API key, camera passwords) are encrypted with Windows DPAPI (machine scope).
* Cloud: TLS only (plain HTTP refused except `localhost`), JWT access tokens + rotating refresh tokens,
  site API keys stored as SHA-256 hashes, rate-limited login, per-branch authorisation.
* No passwords, password hashes or database files are ever sent to the cloud.

## 1.8 Technology choices

| Concern | Choice | Why |
|---|---|---|
| Desktop | C# 14, .NET 10 LTS, WPF, CommunityToolkit.Mvvm | Native Windows UI, high-DPI, touch, long support (Nov 2028). |
| Local DB | SQLite (WAL) via Microsoft.Data.Sqlite + Dapper | Zero-admin, transactional, file backup. Same SQL runs on PostgreSQL for central deployments. |
| Server DB | PostgreSQL 14+ via Npgsql | Robust multi-site central store. |
| Serial | System.IO.Ports (+ TCP for serial-to-Ethernet servers) | Supports all baud/parity/stop/flow settings exposed by Windows drivers. |
| PDF / tickets | SkiaSharp + HarfBuzz, bundled Noto fonts | Correct Sinhala/Tamil shaping, MIT licence, same output for print and PDF. |
| Excel | ClosedXML | MIT, no Office needed. |
| Cloud | ASP.NET Core minimal APIs | Same language/DTOs as desktop. |
| Android | Kotlin, AndroidX, Material Components | Matches the Gradle/AGP toolchain already proven on this account's CI. |
| Installer | Inno Setup 6 → `AdvancedWeighbridgeSetup.exe`, self-contained .NET | No runtime download required at install time; upgrade/repair/uninstall built in. |
