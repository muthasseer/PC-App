# 3. Module Structure

```
AdvancedWeighbridge/
├── AdvancedWeighbridge.sln          all .NET projects
├── Directory.Build.props            .NET 10, nullable, warnings as errors, analyzers
├── Directory.Packages.props         central package versions
├── src/
│   ├── Weighbridge.Core             domain logic, no I/O
│   ├── Weighbridge.Devices          transports, indicator service, I/O, cameras, identification, plugins
│   ├── Weighbridge.Data             schema migrations, stores, audit chain, backup
│   ├── Weighbridge.Reporting        tickets, PDF, print raster, ESC/POS, QR, report exports
│   ├── Weighbridge.Application      use cases and background services (AppHost)
│   ├── Weighbridge.Contracts        DTOs shared by the sync client and the cloud server
│   ├── Weighbridge.Desktop          WPF application (AdvancedWeighbridge.exe)
│   ├── Weighbridge.Cloud            ASP.NET Core server + web dashboard
│   └── Weighbridge.Simulator        awms-indicator-simulator (virtual indicator on a COM or TCP port)
├── tests/                           xUnit test projects, one per source project
├── android/                         Android app (core/ + app/)
├── installer/                       Inno Setup script and helper texts → AdvancedWeighbridgeSetup.exe
├── deploy/cloud/                    Dockerfile, docker-compose (PostgreSQL + cloud + Caddy HTTPS)
├── profiles/                        example indicator profiles (*.wbprofile.json, marked unverified)
├── samples/                         sample indicator captures (*.wbcap) for replay and tests
├── tools/SampleGenerator            regenerates the sample captures
├── scripts/                         string-table generator for the desktop
└── docs/                            this documentation
```

## 3.1 Weighbridge.Core (no dependencies)

| Folder | Contents |
|---|---|
| `Units/` | `Weight` (integer grams), units (kg, t, g, lb), conversion and display formatting. |
| `Indicators/Framing/` | `FrameAssembler` (terminator, start/end markers, fixed length, idle gap; maximum length, overflow and noise reporting) and `Checksum` (XOR, sum, two's complement 8/7-bit, CRC-16/MODBUS; binary or ASCII-hex). |
| `Indicators/Parsing/` | `WeightParser` (auto, regex, delimited, fixed-position, protocol modes), `NumberScanner`, `ParserOptions` (decimal/thousands separators, implied decimals, reversed digits, multiplier, units, status rules, validation/ignore patterns, plausibility limit). |
| `Indicators/Protocols/` | `IIndicatorProtocol` for binary/proprietary formats; `ToledoContinuousProtocol` (Mettler Toledo continuous output with status bytes). |
| `Indicators/Stability/` | `StabilityDetector` (software window/tolerance, indicator flag, or both). |
| `Indicators/Learning/` | `FramingAnalyzer`, `LineQualityScorer`, `FormatLearner`: propose framing and parser settings from captured sample output. |
| `Indicators/` | `IndicatorProfile` (the device profile, JSON), `IndicatorDecoder` (bytes → frames → readings), `WeightReading`, `IndicatorSnapshot`. |
| `Weighing/` | `CaptureValidator` (connection, freshness, overload, error, negative, minimum, capacity, stability), `NetCalculator`, `TicketNumbering`, `DisplayStatus` (colour + text + icon per state), transaction models. |
| `Automation/` | `AutomationEngine` (state machine for automatic mode), `BarrierInterlock`, events and commands. |
| `Security/` | `Permissions` and role defaults, `PasswordHasher` + `PasswordPolicy`, `Totp`. |

## 3.2 Weighbridge.Devices

| Folder | Contents |
|---|---|
| `Transports/` | `SerialByteTransport` (System.IO.Ports, all serial settings), `TcpByteTransport` (serial-to-Ethernet servers), `SimulatorTransport`, `RawCapture` (record/replay `.wbcap`). |
| `Indicator/` | `IndicatorService`: background read loop, reconnect with back-off, polling commands, diagnostics ring for the raw monitor, lock-free snapshot publishing. |
| `Discovery/` | `PortDiscovery` (COM ports with USB vendor/product and driver names), `BaudScanner` (tries settings and scores the received text). |
| `IO/` | `HardwareController` (lights, barriers, sensors, alarm), Modbus TCP/RTU client, HTTP relay boards, simulated I/O. |
| `Cameras/` | HTTP snapshot cameras (JPEG over HTTP with optional basic/digest credentials). |
| `Identification/` | ANPR (HTTP push or poll), RFID/QR readers on serial ports or keyboard wedge. |
| `Plugins/` | Loads indicator protocol plug-ins (`IIndicatorProtocol`) from the `plugins` folder under Program Files, which only administrators can write. Plug-ins are trusted code; loaded ones are listed in the Devices and System status screens. Nothing is downloaded. |
| `Simulation/` | `SimulatorWorld`: trucks, sensors, lights and barriers for training and automated tests. |

## 3.3 Weighbridge.Data

`Database` (SQLite or PostgreSQL connection factory, `SqlDialect`), `MigrationRunner`, `SqliteBackup` (online backup API),
entity classes and stores: `TransactionStore`, `MasterDataStore`, `UserStore`, `SiteStore`, `AuditStore`
(hash chain) and `MiscStores` (settings, notifications, sync queue, approvals, device events, backups).

## 3.4 Weighbridge.Reporting

`Tickets/` (ticket model, renderer for A4/A5/thermal 80 mm/58 mm, labels in three languages, QR verification
payload), `Text/TextEngine` (HarfBuzz shaping with bundled Noto fonts for Sinhala and Tamil), `Printing/EscPos`
(raster for receipt printers), `Reports/ReportExporters` (XLSX, CSV, PDF).

## 3.5 Weighbridge.Application

`AppHost` wires everything for the desktop: settings (`SettingsService`, DPAPI `SecretProtector`), `SetupService`
(first run), `AuthService` (sign-in, lock-out, TOTP, sessions), `AuditService`, `MasterDataService`,
`WeighingService` (GET WEIGHT, first/second weighing, manual entry, approvals, cancellation, corrections),
`TicketService`, `HistoryService`, `ReportService`, `BackupService`, `HealthService`, `Hardware/` (indicator
manager, profile service, cameras and identification), `Automation/AutomationController` (drives the state
machine with real or simulated I/O) and `Sync/CloudSyncService` (outbox, heartbeat, remote commands).

## 3.6 Weighbridge.Desktop (WPF)

| Folder | Contents |
|---|---|
| `Windows/` | First-run setup, sign-in, main window (navigation, status bar, session timeout), transaction details. |
| `Views/` | Weighing dashboard, history, reports, master data, indicator setup wizard, raw data monitor, lights & barriers, users, settings, backup, audit, notifications, system status. |
| `ViewModels/` | `WeighingViewModel` (live weight, GET WEIGHT, capture flow). |
| `Infrastructure/` | Localisation (`Loc`), dialogs, printing, converters, record editor. |
| `Resources/` | Light and dark themes, control styles, string tables (en/si/ta), icons. |
| `Startup/` | Command line, `--selftest`, `--screenshots`, `--init`, `--list-ports`. |

## 3.7 Weighbridge.Cloud

`Services/` (store, authentication with JWT and refresh tokens, sync ingest, queries, admin, site monitor, site-key
authentication, secret box), `Endpoints/` (auth, sync, app, admin, public ticket verification, error middleware,
policies and rate limits), `Migrations/` (SQLite and PostgreSQL), `wwwroot/` (web dashboard: HTML, CSS, JS, no
external scripts).

## 3.8 Android

`android/core` (pure Kotlin: JSON, models, API client, display rules; unit-tested on the JVM) and `android/app`
(Android framework UI, Keystore token store, alert job, ticket provider). See `android/README.md`.

## 3.9 Tests

| Project | Covers |
|---|---|
| `Weighbridge.Core.Tests` | Framing, checksums, every parser mode and the example formats, rejection of ambiguous/malformed data, stability, format learning, capture validation, net calculation, ticket numbering, automation state machine and interlocks, passwords, TOTP. |
| `Weighbridge.Devices.Tests` | Transports (including a real serial port pair through a virtual null-modem on Linux CI), indicator service reconnects, replay, discovery, Modbus, shipped profiles against sample captures. |
| `Weighbridge.Data.Tests` | Migrations on SQLite and PostgreSQL, append-only triggers, audit chain verification and tamper detection, outbox race, backup/restore. |
| `Weighbridge.Reporting.Tests` | Ticket PDF/raster in three languages, ESC/POS, QR, XLSX/CSV/PDF exports. |
| `Weighbridge.Application.Tests` | End-to-end weighings (semi-automatic, manual with approval, stored tare, cancellation), automatic mode with the simulator, profile versioning, cloud sync with an in-memory server. |
| `Weighbridge.Cloud.Tests` | Authentication (lock-out, MFA, refresh rotation and reuse detection), sync semantics (accepted/unchanged/stale/conflict), site isolation, dashboard API, security headers, desktop-to-cloud end-to-end on SQLite and PostgreSQL. |
| `android/core` tests | JSON, live/not-live rules, report periods, API client against a mock server. |

Run everything: `dotnet test AdvancedWeighbridge.sln` (set `WB_TEST_PG` to a PostgreSQL connection string to include
the PostgreSQL variants) and `android/gradlew -p android/core test`.
