# Advanced Weighbridge Management System (AWMS) 1.0

Weighbridge software for quarries, plants and warehouses: a Windows application on the weighbridge PC that reads
any weight indicator through a configurable COM-port engine, an optional cloud server with a web dashboard, and an
Android app for managers.

| Component | Technology | Folder |
|---|---|---|
| Weighbridge PC application (`AdvancedWeighbridge.exe`) | C# / .NET 10, WPF, SQLite | `src/Weighbridge.Desktop` + libraries in `src/` |
| Installer (`AdvancedWeighbridgeSetup.exe`) | Inno Setup 6, self-contained .NET | `installer/` |
| Indicator simulator (`awms-indicator-simulator`) | .NET console | `src/Weighbridge.Simulator` |
| Cloud server and web dashboard | ASP.NET Core, PostgreSQL (or SQLite), Docker + Caddy | `src/Weighbridge.Cloud`, `deploy/cloud` |
| Android app | Kotlin, Android framework, OkHttp | `android/` |

Highlights: GET WEIGHT captures the validated reading already in memory with full evidence (raw frames, profile
version, stability); ambiguous or malformed indicator data is rejected, never guessed; a setup wizard learns new
indicator formats from their output and verifies them against the display; manual weights are always labelled as
such; automatic mode with lights, barriers and interlocks; append-only measurements and a hash-chained audit trail;
offline-first cloud sync; LIVE/NOT LIVE rule for remote views; English, Sinhala and Tamil.

## Documentation

| | |
|---|---|
| [01 Architecture](docs/01-architecture.md) | [08 Administrator manual](docs/08-administrator-manual.md) |
| [02 Database schema](docs/02-database-schema.md) | [09 Hardware acceptance tests](docs/09-hardware-acceptance-tests.md) |
| [03 Module structure](docs/03-module-structure.md) | [10 Installation guide](docs/10-installation-guide.md) |
| [04 UI wireframes](docs/04-ui-wireframes.md) | [11 Troubleshooting](docs/11-troubleshooting.md) |
| [05 COM-port integration](docs/05-com-integration.md) | [12 Backup and recovery](docs/12-backup-and-recovery.md) |
| [06 Development plan and status](docs/06-development-plan-and-status.md) | [13 Cloud API reference](docs/13-api-reference.md) |
| [07 User manual](docs/07-user-manual.md) | [14 Release notes](docs/14-release-notes.md) |

Android specifics: [android/README.md](android/README.md).

## Status

All components are implemented and pass the automated pipeline (260 .NET tests on SQLite and PostgreSQL, Windows
self-test and screenshots, installer build with silent install/uninstall, Docker smoke test, Android lint and an
emulator end-to-end run against a real cloud server). **Physical indicators, I/O devices, barriers, cameras,
printers and phones have not been tested**; every site must pass the hardware acceptance tests before trade use.
Details and outstanding items: [docs/06](docs/06-development-plan-and-status.md).

## Build and test

Requirements: .NET SDK 10 (see `global.json`); for the Android app JDK 17+ and the Android SDK (platform 36).

```sh
dotnet build AdvancedWeighbridge.sln -c Release
dotnet test AdvancedWeighbridge.sln -c Release
# PostgreSQL variants of the data and cloud tests:
WB_TEST_PG="Host=localhost;Username=postgres;Password=...;Database=postgres" dotnet test AdvancedWeighbridge.sln

# Windows: application, self-test, installer
dotnet publish src/Weighbridge.Desktop -c Release -r win-x64 --self-contained -o out/desktop
out/desktop/AdvancedWeighbridge.exe --selftest
installer/build.ps1                       # needs Inno Setup 6

# Cloud server locally (SQLite, http://localhost:5080)
Cloud__BootstrapAdminPassword='<strong password>' dotnet run --project src/Weighbridge.Cloud -- --urls http://localhost:5080

# Android
cd android && ./gradlew -p core test && ./gradlew lintDebug assembleDebug
```

The CI workflow (`.github/workflows/advanced-weighbridge.yml`) builds everything and publishes the release package
`AdvancedWeighbridge-1.0.0` (installer, portable build, simulator, APKs, cloud deployment, documentation with
screenshots, test reports, source and SHA-256 checksums) as a workflow artifact.

## Training without hardware

* Desktop: select the *Simulator* transport in the indicator setup wizard, and on Devices → Lights & barriers
  *Insert simulated lights, barriers, sensors and ANPR* and save. The weighing screen then shows
  *SIMULATOR – training, not a real weighing*; the simulator controls drive trucks on and off.
* `awms-indicator-simulator --port COM5` with a virtual COM-port pair (for example com0com) emulates a real indicator
  on a serial port; `--replay samples/<file>.wbcap` replays recorded output.
