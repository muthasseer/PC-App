# 6. Development Plan and Status (version 1.0.0)

## 6.1 Phases

| Phase | Scope | Status |
|---|---|---|
| 1 | Architecture, database schema, module structure, UI wireframes, COM-port strategy (docs 01–05) | Done |
| 2 | Core domain: weights, framing, checksums, parser engine, stability, format learner, capture validation, net calculation, ticket numbering, automation state machine, interlocks, passwords, TOTP | Done |
| 3 | Devices: serial/TCP/simulator/replay transports, indicator service, port discovery and line detection, Modbus TCP/RTU and HTTP relay I/O, cameras, ANPR/RFID/QR sources, protocol plug-ins | Done (simulator-tested) |
| 4 | Data: SQLite and PostgreSQL migrations, stores, append-only protections, hash-chained audit, backup/restore | Done |
| 5 | Reporting: tickets (A4/A5/80/58 mm, three languages, QR), PDF, print raster, ESC/POS, XLSX/CSV/PDF reports | Done |
| 6 | Application services and background workers | Done |
| 7 | Cloud: contracts, sync client with offline outbox, ASP.NET Core server, web dashboard, Docker deployment with HTTPS | Done |
| 8 | Windows desktop application (WPF) | Done |
| 9 | Android companion app | Done |
| 10 | Installer `AdvancedWeighbridgeSetup.exe`, indicator simulator, CI pipeline, release package | Done |
| 11 | Manuals and guides (docs 06–14) | Done |

## 6.2 How it was verified

Every push runs the CI workflow `.github/workflows/advanced-weighbridge.yml`; the release package is only produced
when all of the following pass:

| CI job | What it proves |
|---|---|
| Linux | All 260 .NET tests (Core 146, Devices 29, Data 24, Reporting 15, Application 21, Cloud 25) on SQLite **and PostgreSQL 16**, including a real serial-port round trip through a virtual null-modem (socat). |
| Docker | The cloud server image builds and runs against PostgreSQL in containers: sign-in, password change, site key, transaction upload, report, ticket PDF. |
| Windows | The same tests on Windows (DPAPI, Windows paths); self-contained publish; **desktop self-test** (13 steps with the simulator: DPAPI, ports, printer, simulated two-pass weighings, PDF and print raster, manual entry labelling, backup, audit chain); **screenshots of every desktop page** in English, Sinhala, Tamil and dark theme (fails on any UI exception or error message); **Inno Setup build** of `AdvancedWeighbridgeSetup.exe`; **silent install, self-test of the installed application, repair/upgrade run, silent uninstall that keeps the data**. |
| Android | Gradle wrapper checksum, 14 core unit tests, Android lint (errors fail the build), debug and release APKs. |
| Android end-to-end | The debug APK on an **Android 15 emulator against a real cloud server**: sign-in with forced password change, LIVE dashboard, tickets, details, in-app PDF, cancellation request (confirmed on the server), manual-entry label, reports, alerts, account, and NOT LIVE after the site stops sending heartbeats; fails on any crash. |
| Package | Assembles the release with SHA-256 checksums, screenshots and test reports. |

## 6.3 Not verified — must be tested on site

These parts are implemented and tested with simulators, sample data, emulators or CI machines only. **They are not
claimed to work with real equipment until they pass the hardware acceptance tests (`09-hardware-acceptance-tests.md`).**

* **Weight indicators**: no physical indicator was connected during development. The five shipped profiles are
  examples, tested against sample captures, and are marked *unverified*.
* **Lights, barriers, sensors, Modbus/HTTP I/O controllers**: simulator only. Software interlocks are unit-tested;
  electrical safety circuits are outside the software and must be provided by the installer of the barriers.
* **IP cameras, ANPR, RFID/QR readers**: protocol code is tested against simulated responses only.
* **Printers**: tickets render correctly to PDF and print rasters (tested); printing to physical A4/thermal
  printers and ESC/POS raw mode has not been tested on real printers.
* **Installer on customer PCs**: tested on CI Windows runners (silent mode). Interactive installation on Windows
  10/11 desktops, with SmartScreen and antivirus products, is not tested. The installer is **unsigned** unless a
  code-signing certificate is configured; Windows then shows an unknown-publisher warning.
* **Android on physical phones**: emulator only (Android 15). The release APK is unsigned unless a signing key is
  configured.
* **Translations**: Sinhala and Tamil texts (desktop, tickets, Android) are first translations and must be reviewed
  by native speakers before release.
* **Load and duration**: no load test of the cloud with many sites, and no multi-month run of a site database.
  Indexes exist for the common queries.

## 6.4 Outstanding / not included in 1.0

| Item | Notes |
|---|---|
| Desktop on a shared PostgreSQL database | The data layer and its tests support PostgreSQL, and the cloud uses it; the desktop application always uses its local SQLite database in 1.0 (one database per weighbridge PC, central view through the cloud). |
| Re-sending the full history to a new cloud | After a cloud data loss, only new changes are uploaded; the full history stays on the site PCs. |
| Web dashboard and installer languages | English only (the desktop, tickets and the Android app are in English, Sinhala and Tamil). |
| Visual ticket designer | Ticket layout is configured with options (paper, language, bilingual, logo, QR, images, signatures), not drawn. |
| Code signing | Requires the organisation's certificates (CI signs automatically when the secrets are set). |
| Alert timing on Android | Background checks run about every 15 minutes (Android minimum); no push service is used. |
| Recovery of a cloud whose only administrator lost the authenticator | Needs database access; there is no built-in recovery command. |

## 6.5 Constraints honoured by the design

* Manual weights are never presented as indicator measurements (data model, screens, tickets, reports, cloud, app).
* Barriers never open because a weight string arrived: interlocks require a saved weighing, a vehicle on the deck,
  no fault and the safety input.
* The mobile app and the web dashboard never show a delayed value as live (90-second rule, including the phone's own
  refresh delay).
* No passwords, password hashes, authenticator secrets, database files or camera images are sent to the cloud;
  driver names only when enabled.
* Business, financial and audit records are never deleted on a timer (only log files and surplus backup files).
* The installer installs no drivers or third-party software, downloads nothing, and does not change security
  settings; uninstalling keeps the data unless the user confirms deletion twice.
* Nothing is downloaded and executed at run time; protocol plug-ins are loaded only from the administrator-only
  installation folder.
