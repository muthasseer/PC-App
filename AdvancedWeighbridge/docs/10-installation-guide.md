# 10. Installation Guide

The release package (`AdvancedWeighbridge-1.0.0`, produced by the CI workflow) contains:

| Path | Contents |
|---|---|
| `AdvancedWeighbridgeSetup.exe` | Windows installer for the weighbridge PC. |
| `windows/AdvancedWeighbridge-1.0.0-portable-win-x64.zip` | The same application without installer (testing, support). |
| `windows/awms-indicator-simulator-1.0.0-win-x64.zip` | Indicator simulator for training and acceptance tests. |
| `android/AdvancedWeighbridge-Mobile-1.0.0*.apk` | Android app: debug build for testing, and the release build (signed only when a signing key was configured in CI, otherwise `-release-unsigned`). |
| `cloud/` | Docker deployment of the cloud server (PostgreSQL + HTTPS). |
| `docs/` | These documents and the screenshots of every screen. |
| `profiles/`, `samples/` | Example indicator profiles (unverified) and sample captures. |
| `test-reports/` | Self-test results of the built and the installed application, Android lint report. |
| `AdvancedWeighbridge-1.0.0-source.zip` | Complete source code. |
| `SHA256SUMS.txt` | Checksums of every file. Verify before installing: `Get-FileHash AdvancedWeighbridgeSetup.exe -Algorithm SHA256` (PowerShell) or `sha256sum -c SHA256SUMS.txt`. |

## 10.1 Weighbridge PC (Windows)

**Requirements**: Windows 10 version 1809 or later, or Windows 11, 64-bit; 4 GB RAM (8 GB recommended); 2 GB free
disk plus space for data and backups; screen 1366×768 or larger (1920×1080 recommended); a serial port or a
USB-serial adapter with its manufacturer's driver (install the adapter driver from the adapter manufacturer —
AWMS does not install drivers); administrator rights for the installation.

**Install**

1. Run `AdvancedWeighbridgeSetup.exe`. The .NET runtime is included; nothing is downloaded during installation.
2. Windows may show *Windows protected your PC* / *Unknown publisher* when the installer is not code-signed.
   Verify the SHA-256 checksum first. For production roll-out, sign the installer with your organisation's
   code-signing certificate (CI signs it automatically when the certificate is configured as a secret). Do not
   disable SmartScreen or antivirus protection to install AWMS.
3. Choose the options: desktop shortcut; *Start when Windows starts* (recommended for automatic weighing).
4. The installer creates `C:\ProgramData\AdvancedWeighbridge` (database, logs, backups, evidence, exports) with
   write access for local users, prepares the database (`--init`), and writes the list of serial ports to
   `C:\ProgramData\AdvancedWeighbridge\logs\install-ports.txt`.
5. Start the application, complete the first-run setup (company, administrator, site, weighbridge) and configure
   the indicator (administrator manual 8.4). Run the hardware acceptance tests (`09-hardware-acceptance-tests.md`).

**Check the installation**: `"C:\Program Files\Advanced Weighbridge\AdvancedWeighbridge.exe" --selftest --out selftest.txt`
must end with `RESULT: PASSED` (it uses the built-in simulator in a temporary folder and does not touch your data).

**Silent installation** (software deployment tools):

```
AdvancedWeighbridgeSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=install.log /TASKS=desktopicon,autostart
```

**Upgrade / repair**: run a newer (or the same) installer. Data and settings are kept; the database schema is
migrated on the first start. The application must not be running (the installer asks to close it). A newer
database cannot be opened by an older version — restore a backup made with the older version if you must roll back.

**Uninstall**: Windows *Apps* → Advanced Weighbridge → Uninstall. By default **all data is kept** in
`C:\ProgramData\AdvancedWeighbridge`. Deleting the data requires answering two confirmation prompts; a silent
uninstall deletes data only with `/DELETEDATA=1`.

**Antivirus and firewall**: AWMS needs no inbound ports. Outbound HTTPS to your cloud server (if used), HTTP to IP
cameras, ANPR and I/O controllers on the local network (Modbus TCP 502 or as configured).

**Multiple weighbridges**: one PC per weighbridge, each with its own local database and the same site code in the
cloud, or several sites each with their own code. (Sharing one PostgreSQL database between site PCs is not
offered in version 1.0; see the status document.)

## 10.2 Cloud server (optional)

Requirements: a Linux server or VM (2 vCPU, 2 GB RAM, 20 GB disk to start), Docker with the compose plugin, a DNS
name pointing at the server, ports 80 and 443 reachable from the Internet (for the automatic certificate).

```sh
cd cloud                      # from the release package (or AdvancedWeighbridge/deploy/cloud in the source)
cp .env.example .env          # then edit .env
#   AWMS_DOMAIN=awms.example.lk
#   POSTGRES_PASSWORD=<long random password>
#   AWMS_ADMIN_USER=admin
#   AWMS_ADMIN_PASSWORD=<strong password, at least 10 characters, letters, digits, a capital or symbol>
#   AWMS_TIMEZONE=Asia/Colombo
#   AWMS_REQUIRE_ADMIN_MFA=true   (recommended)
docker compose up -d --build
```

The compose file builds the server image from the source package (`--build` needs the source tree next to
`deploy/`; use the source ZIP) and starts PostgreSQL 16, the AWMS cloud server and Caddy, which obtains and renews
the TLS certificate. Open `https://<your domain>/`, sign in as the administrator, change the password, set up
two-factor sign-in, then **remove `AWMS_ADMIN_PASSWORD` from `.env`** and run `docker compose up -d` again.
Create the sites (Administration → Sites; the site key is shown once) and the cloud users.

Data lives in the Docker volumes `pgdata` (database) and `clouddata` (signing keys and data-protection keys).
Back up both (see `12-backup-and-recovery.md`). `clouddata` holds the token-signing key and the key that encrypts
the users' authenticator secrets: losing it signs everybody out and makes existing two-factor set-ups unusable
(an administrator then resets them).

Configuration (environment variables `Cloud__<Name>`, or `appsettings.json`): `DatabaseProvider` (`Sqlite` or
`PostgreSql`), `ConnectionString`, `DataDirectory`, `AccessTokenMinutes` (15), `RefreshTokenDays` (14),
`SiteOfflineAfterMinutes` (5), `LiveDataStaleSeconds` (90), `TimeZone`, `TrustForwardedHeaders` (true only behind
the bundled reverse proxy), `MaxFailedLogins` (5), `LockoutMinutes` (15), `LoginRateLimitPerMinute` (20),
`RequireMfaForAdministrators`, `PasswordPolicy`.

## 10.3 Android app

Requirements: Android 8.0 (API 26) or later.

* **Managed devices**: distribute the signed release APK through your device-management system or a private
  store listing.
* **Individual phones**: copy the APK to the phone and open it; Android asks to allow installation from that
  source for this one app. Install only APKs whose SHA-256 matches `SHA256SUMS.txt`. The `-debug` APK is for
  testing; for production use a release APK signed with your organisation's key (see `android/README.md`).

Open the app, enter the cloud server address (`https://…`), user name and password (and the authenticator code if
enabled). On Android 13 and later the app asks for permission to show notifications when alerts are turned on.
