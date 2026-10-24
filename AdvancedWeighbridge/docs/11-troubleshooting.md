# 11. Troubleshooting Guide

Logs: `C:\ProgramData\AdvancedWeighbridge\logs\awms-YYYYMMDD.log` (kept 60 days). The System status page shows
versions, data folder, database, disk space, device and cloud state. Run `AdvancedWeighbridge.exe --selftest`
to check the installation independently of the hardware.

## 11.1 Start-up and sign-in

| Problem | Cause and solution |
|---|---|
| "Advanced Weighbridge is already running on this PC" | Only one copy may use the indicator. Close the other window (also check other Windows user sessions). |
| "The database schema is newer than this application supports" | The data was used by a newer version. Install that version or newer. |
| "The application could not start" | See the log. Disk full or no write access to `C:\ProgramData\AdvancedWeighbridge` are the usual causes; the installer grants write access to local users. |
| Account locked | Wait for the lock-out time (15 min) or ask an administrator to unlock it (Users & roles). |
| Authenticator code rejected | Check the phone's clock (automatic time). Each code can be used once. An administrator can reset two-factor sign-in. |
| Forgotten administrator password | Another administrator resets it. If there is none, restore a backup whose administrator password is known, or contact support; there is no backdoor. |

## 11.2 Indicator and weight display

Use Devices → Raw data monitor first: it shows what actually arrives and why frames are rejected.

| Status / symptom | What to check |
|---|---|
| INDICATOR DISCONNECTED | Port name changed (USB adapter moved to another socket — use the same socket or select the new COM number); adapter driver missing (Device Manager shows a yellow mark); port used by another program. |
| NO DATA FROM INDICATOR | Indicator output not enabled or set to "print" mode; wrong cable (TX/RX must be crossed between PC and indicator unless the manual says otherwise); indicator needs polling (configure the request command); RS-485 converter direction/termination. |
| Garbage characters in the raw monitor | Wrong baud rate, data bits or parity. Use *Detect line settings automatically* or the indicator manual. |
| Readable frames but INVALID DATA | The profile does not match the format. The rejection reason names the problem (ambiguous frame, unexpected text, unknown unit, checksum). Re-run steps 2–4 of the setup wizard with a fresh capture. |
| READING OUT OF DATE appears now and then | Data gaps longer than the freshness limit (1.5 s): slow polling interval, interference on long cables, USB power saving (disable *Allow the computer to turn off this device* for the adapter and USB root hubs). |
| Weight never becomes STABLE | Stability tolerance too tight for the scale division or vibration on site; the indicator never sends a stable flag (use *software* stability mode); check the stability settings in step 5 of the wizard. |
| Displayed value differs from the indicator | Wrong decimals/implied decimals, unit or multiplier in the profile. Never use the weighbridge for trade until the reference-weight comparison matches. |
| Weight negative or not zero when empty | Zero the indicator (indicator keys). AWMS does not change the indicator's zero. |

## 11.3 Weighing

| Message | Solution |
|---|---|
| GET WEIGHT button missing | The screen is in manual mode; choose semi-automatic in the mode selector. |
| "A supervisor must authorise …" | Configured approval; a user with the permission signs in on the dialog. |
| "A pending weighing for this vehicle …" / concurrency message | Another PC or window changed the same ticket; reload and repeat. |
| Ticket did not print | The ticket is saved; reprint from History. Check the printer queue, paper size and, for receipt printers in ESC/POS mode, the printer name. |
| Sinhala/Tamil text missing on a printer | Use the Windows driver mode (ESC/POS raw mode prints a rendered image, so it is independent of printer fonts — check that the image width matches the paper). |

## 11.4 Automatic mode, lights and barriers

| Symptom | Solution |
|---|---|
| Exit barrier does not open | The automation panel or the refusal message names the interlock: weighing not saved, no vehicle on the platform, active fault, or safety input not OK. Remove the cause; never bridge safety inputs. |
| Fault "communication loss" | The indicator stopped sending data during a cycle. Fix the connection, then *Acknowledge fault*. |
| Fault "identification timeout" | ANPR/RFID did not identify the vehicle; type the number if allowed, check the reader. |
| Lights or barrier react to the wrong command | Check the signal mapping and the controller addresses (Lights & barriers page, test buttons). |
| Modbus controller not reachable | IP address/port/unit id, firewall, cable; RTU: COM port, baud rate, parity, unit id. |

## 11.5 Cloud

| Symptom | Solution |
|---|---|
| Status bar shows sync failures | Settings → Cloud shows the last error. "The cloud address must use HTTPS" — use the `https://` address. "No site key" / "cannot be decrypted on this PC" — enter the site key again (after moving to a new PC). 401 — the key was rotated in the cloud. |
| Site shows *offline* in the cloud | No heartbeat for 5 minutes: PC off, application closed, no Internet. Local weighing continues; data uploads later. |
| Conflicts listed | The server has different content for the same version of a ticket (for example a database restored on the PC). Review in the cloud (Administration → Conflicts) and on the PC; nothing is overwritten automatically. |
| Remote cancellation stays *Waiting for the site PC* | The site PC is offline or *Accept remote requests* is off (Settings → Cloud). |

## 11.6 Android app

| Symptom | Solution |
|---|---|
| "The server address must start with https://" | Enter the HTTPS address of the cloud. |
| "The server cannot be reached" | Internet connection, server address, server running; the app retries on the next refresh. |
| NOT LIVE on every tile | The site has not sent a heartbeat for over 90 s (site offline), or the phone could not refresh (the line above the tiles says so). This is intentional: old values are never shown as live. |
| Signed out unexpectedly | The session was revoked (password changed, signed out on another device, account deactivated) or expired after 14 days without use. Sign in again. |
| No alert notifications | Turn on *Notify me about new alerts* (Account) and allow notifications in Android settings; alerts are checked about every 15 minutes, and battery optimisation can delay them further. |
| "This account must use two-factor sign-in" | Set it up in the web dashboard (Account), then sign in in the app. |
