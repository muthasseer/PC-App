# 9. Hardware Acceptance Test Procedure

AWMS has been tested with the built-in indicator simulator, recorded sample captures, a virtual null-modem serial
pair (Linux CI), Windows CI runners, an Android emulator and a containerised cloud server. **It has not been tested
with physical indicators, I/O controllers, barriers, traffic lights, sensors, cameras, ANPR/RFID readers, printers
or physical Android devices.** Each installation must therefore pass the tests below before it is used for trade.
Record the results on the form at the end and keep it with the site documents.

Prerequisites: AWMS installed and the self-test passed (`AdvancedWeighbridge.exe --selftest`), an administrator
and a supervisor account, certified test weights or a vehicle of known weight (weighed on a certified bridge),
the indicator's manual (output format and settings), and the site's electrical safety documentation for barriers.

## A. Indicator communication

| # | Test | Procedure | Pass criteria |
|---|---|---|---|
| A1 | Port identification | Connect the indicator; open Devices → Indicator setup → Connection. | The port is listed with the adapter details; the same port is shown after unplugging and re-plugging the adapter. |
| A2 | Line settings | Enter the settings from the indicator manual, or run *Detect line settings automatically* (continuous output only). | The raw capture shows readable text (or the documented binary frames); no UART errors. |
| A3 | Framing | Capture ≥ 30 s; accept or set framing. | Every frame is complete; no "frame too long", noise or partial frames in the raw monitor. |
| A4 | Parsing | Load the platform with 3 different known weights (including near zero and near capacity, or as high as available). For each: compare the decoded value with the indicator display. | Decoded value equals the display exactly (same value, unit and decimals) for every weight; the profile can be marked verified. |
| A5 | Status flags | Produce motion (drive on/off), overload (if safely possible or by simulation per manual), zero, and gross/net if used. | The screen shows MOTION / OVERLOAD / ZERO / GROSS-NET correctly; GET WEIGHT is refused while not stable or in overload. |
| A6 | Stability | Drive a vehicle on and stop. Note the time from standstill to STABLE. | STABLE appears within the expected time (typically 2–5 s) and disappears when the vehicle moves. Adjust window/tolerance if needed and repeat. |
| A7 | Freshness | Disconnect the cable while a weight is shown. | Within 1.5 s the display shows READING OUT OF DATE / NO DATA; GET WEIGHT is refused. Reconnect: readings resume automatically without restarting the program. |
| A8 | Corrupted data | (Optional) Change the baud rate on the PC side only. | Frames are rejected as INVALID DATA; no weight is shown or captured. Restore the setting. |
| A9 | Polling (if the indicator is polled) | Configure the request command and interval. | Readings arrive at the configured rate; no "silent" periods. |
| A10 | Long run | Leave the system running for ≥ 8 hours with periodic weighings. | No disconnects except caused deliberately; error counters in the raw monitor stay at 0 (or explained). |

## B. Weighing workflow

| # | Test | Pass criteria |
|---|---|---|
| B1 | Two-pass weighing with a known vehicle (loaded in, empty out and vice versa). | Gross, tare and net correct; ticket number assigned; raw frames stored (History → ticket → Weighings). |
| B2 | Stored tare | Approve a stored tare for a vehicle; single weighing uses it; ticket states "stored tare". |
| B3 | Unstable capture | With the vehicle moving, press GET WEIGHT: waits and captures only once stable (or refuses, per policy); supervisor override is recorded with reason. |
| B4 | Manual entry | Labelled MANUAL ENTRY on screen, ticket, report, web and app; reason and approval recorded. |
| B5 | Duplicate / blacklist | Warning for the same vehicle within the window; blacklisted vehicle refused. |
| B6 | Cancellation and correction | Require reason and approval as configured; the original values remain visible; audit entries exist. |
| B7 | Power loss | Switch the PC off (hard) during a weighing; after restart the database opens, no captured weighing is lost, the audit chain verifies. |

## C. Printers and tickets

| # | Test | Pass criteria |
|---|---|---|
| C1 | A4/A5 printer | Ticket prints completely, legible, correct paper size; Sinhala and Tamil texts render correctly. |
| C2 | Thermal printer (80/58 mm) | Driver mode and, if used, ESC/POS raw mode print the full ticket without cut-off; QR code scans. |
| C3 | Reprint | Requires reason; "REPRINT" marking and print history entry. |
| C4 | QR verification | Scanning the QR code opens the verification page (cloud configured) or shows the AWMS1 code; tampered values are reported as not matching. |

## D. Lights, barriers, sensors (automatic mode)

Perform with the barrier manufacturer's safety devices active and a person ready at the emergency stop.

| # | Test | Pass criteria |
|---|---|---|
| D1 | Output mapping | Each *Manual control* button on the Lights & barriers page switches exactly the intended light/barrier. |
| D2 | Input mapping | Presence, entry/exit beams and safety input change state on the Lights & barriers page when operated. |
| D3 | Exit interlock | Try to open the exit with no saved weighing, with the platform empty, with an active fault, and with the safety input not OK. | Each attempt is refused with the reason; the barrier does not move. |
| D4 | Entry interlock | Entry does not open while the platform is occupied or the indicator is faulty. |
| D5 | Automatic cycle | A vehicle is detected, identified, positioned, captured when stable, released; lights follow the sequence; the full cycle is in the audit trail. |
| D6 | Faults | Communication loss, identification timeout, unstable timeout, abnormal weight change each stop the cycle with red lights and a fault message; acknowledging after the cause is removed resumes. |
| D7 | Override | Manual barrier override possible only for users with *hardware override*; audited. |
| D8 | PLC / controller power loss | Outputs return to the safe state (barrier closed / red) as wired; AWMS shows the controller fault. |

## E. Cameras and identification

| # | Test | Pass criteria |
|---|---|---|
| E1 | Snapshot cameras | An image per camera is stored with each weighing within the timeout; hash shown; camera failure does not block weighing but is reported. |
| E2 | ANPR | Plates are recognised and matched to the vehicle register (spaces and dashes ignored). Record the recognition rate over ≥ 50 vehicles. In automatic mode an unrecognised plate leads to the identification timeout fault, never to a weighing without a vehicle. |
| E3 | RFID / QR | Tags map to the right vehicle; unknown tags are reported. |

## F. Cloud and mobile

| # | Test | Pass criteria |
|---|---|---|
| F1 | Sync | Tickets appear in the web dashboard within the interval; disconnect the network for 1 hour, weigh, reconnect: everything uploads, nothing is duplicated. |
| F2 | Live status | Dashboard and app show LIVE with a current weight; unplug the site network: after 90 s both show NOT LIVE with the age. |
| F3 | Remote cancellation | Requested in the app/web, executed by the site PC when connected, result shown remotely; audit entries on both sides. |
| F4 | Android devices | On at least two physical phones (different Android versions ≥ 8.0): sign-in with and without two-factor code, dashboard, tickets, PDF view and share, reports, alert notifications (Android 13+ permission prompt), sign-out; rotate the screen; dark mode; Sinhala and Tamil system language. |
| F5 | Security | HTTP (non-TLS) server address refused by the app; signing out on the phone revokes the session (web: server audit). |

## G. Installer

| # | Test | Pass criteria |
|---|---|---|
| G1 | Install on a clean Windows 10/11 PC | Windows shows the publisher warning if the installer is unsigned (do not bypass security software; sign the installer for production). Installation completes; `--selftest` passes; ports list in `C:\ProgramData\AdvancedWeighbridge\logs\install-ports.txt`. |
| G2 | Upgrade | Installing a newer version keeps data and settings. |
| G3 | Uninstall | Default keeps all data; deletion only after the double confirmation. |

## Record form

```
Site: ____________________  Weighbridge: __________  Indicator make/model/firmware: ____________________
Profile name/version: ______________  Verified by: ______________  Date: __________
Tests passed (list): ______________________________________________________________________________
Tests failed / not applicable (with reason): ______________________________________________________
Test weights / reference vehicle (certificate no.): _______________________________________________
Tested by: ______________________ Signature: __________   Approved by: _____________________ Signature: __________
```
