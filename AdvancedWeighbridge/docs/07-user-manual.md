# 7. User Manual (operators and supervisors)

This manual covers daily work on the weighbridge PC. Administration (users, settings, devices, cloud) is in the
administrator manual; the Android app and web dashboard are described at the end.

## 7.1 Signing in

Start **Advanced Weighbridge** from the Start menu or desktop. Enter your user name and password (and the
6-digit authenticator code if your account uses two-factor sign-in). On the first sign-in after an account was
created or reset you must choose a new password. Language links on the sign-in window switch between English,
සිංහල and தமிழ்.

After 5 wrong passwords the account is locked for 15 minutes (configurable). An idle session ends after 30
minutes; sign in again to continue. Use **Sign out** when you leave the PC.

## 7.2 The weighing screen

* The **live weight** is shown in very large digits. The coloured frame and the status bar below it always show a
  symbol and a text as well as a colour:
  * **Green ✔ STABLE** — ready to capture.
  * **Yellow ⌛ MOTION – WAIT** or **BELOW MINIMUM WEIGHT** — wait.
  * **Red ✖** — OVERLOAD, UNDER RANGE, NEGATIVE WEIGHT, INDICATOR ERROR, INVALID DATA, READING OUT OF DATE,
    COMMUNICATION ERROR. Capturing is refused until the cause is removed.
  * **Blue 🔒 CAPTURED** — the weight has just been captured and locked.
  * **Grey 🔌** — INDICATOR DISCONNECTED, NO DATA, platform empty (ZERO), or manual mode.
* The small line under the weight shows the port, settings and indicator profile in use.
* If **SIMULATOR – training, not a real weighing** is shown at the top, the PC is using the built-in simulator.
  Never issue tickets for real vehicles in this state.

## 7.3 Semi-automatic weighing (normal operation)

**First weighing**

1. Type or select the **vehicle number** (ANPR or a tag reader may fill it in). If the vehicle has a pending
   first weight, the screen tells you and the next capture completes that ticket.
2. Choose the **direction** (outbound = dispatch / sale, inbound = receipt / purchase), the **material**, the
   **customer** or **supplier**, and optional driver, delivery note, reference and remarks.
3. Wait for **STABLE**, then press **GET WEIGHT** (or **F9**). The value captured is exactly the stable value shown
   on screen at that moment. The ticket number is assigned and the vehicle appears under *Pending (first weight)*.

**Second weighing**

1. Select the vehicle again (or double-click it in *Pending*).
2. Wait for **STABLE** and press **GET WEIGHT**. Gross, tare and net are calculated (the heavier weight is gross),
   the ticket is completed and printed (after your confirmation if configured).

**Single weighing with a stored tare** — if the vehicle has an approved, unexpired stored tare, tick
*Use approved stored tare*; one capture then completes the ticket and the ticket states that a stored tare was used.

Messages you may see:

| Message | Meaning / action |
|---|---|
| Waiting for stable weight | The weight is captured automatically as soon as it is stable (within 15 s by default); **Esc** cancels. If it does not settle, check that the vehicle is standing still and press GET WEIGHT again. |
| Supervisor authorisation and a reason are required | Your site allows capturing an unstable weight only with a supervisor's approval. The supervisor signs in on the dialog and enters the reason; this is recorded in the audit trail. |
| The same vehicle completed a weighing … minutes ago | Possible duplicate. Confirm only if it really is a new trip. |
| Vehicle … is blacklisted | Weighing is refused; contact a supervisor. |
| Select the material / customer | Required details are missing. |
| Below minimum / above capacity / negative / overload | Check the platform (vehicle fully on, nothing touching), zero the indicator if empty. |

**Clear** (Esc) resets the details. **Print** reprints the last ticket (a reason may be required).

## 7.4 Manual mode

Use manual mode only when the indicator cannot be used and your procedures allow it. Choose *Manual* in the mode
selector, press **Manual entry**, type the weight shown on the indicator or the backup scale and a reason of at
least 5 characters. A supervisor approval may be required. Manual weights are stored as **MANUAL ENTRY** and are
labelled as such on the screen, the ticket, reports, the web dashboard and the mobile app. They are never shown as
indicator measurements.

## 7.5 Automatic mode

In automatic mode the PC controls the traffic lights and barriers and weighs without operator action:
the vehicle drives on, is identified (ANPR/RFID/QR, or the operator types the number if allowed), must be fully
on the platform (position sensors), the weight must be stable, and then the weight is captured, the ticket is
printed if configured, and the exit opens. The *Automation* panel shows the current state, the lights and barrier
state, and two buttons:

* **Release vehicle** — opens the exit for a vehicle that may leave. It is refused unless the weighing was saved,
  the vehicle is on the platform, no fault is active and the safety input is OK (a supervisor with the *hardware
  override* permission can override, which is audited).
* **Acknowledge fault** — after a fault (identification timeout, unstable for too long, communication loss,
  abnormal weight change, safety interlock) clears.

The barrier never opens just because a weight message arrived. If anything is unclear, switch to semi-automatic.

## 7.6 History and tickets

**History** lists tickets with filters (dates, text, vehicle, status, mode, customer, supplier, material, manual
weights only). Double-click a ticket to open it: all weighings with the raw indicator data, camera images, every
change and print. Depending on your permissions you can **reprint** (with reason), save a **PDF**, **edit**
details, **correct the first weight**, **cancel and re-issue**, or **cancel** (with reason and, if configured,
supervisor approval). Completed weighings are never deleted; cancelled tickets stay visible as cancelled.

Each ticket has a QR code. Scanning it opens the public verification page of your cloud server (if configured),
which shows whether the ticket number, vehicle and net weight match the records.

## 7.7 Reports

Choose a report (transactions; daily, weekly, monthly, annual summaries; by vehicle, material, customer, supplier
or operator; manual weights and overrides; cancellations; communication errors; audit trail), the period and
filters, then **Export** to Excel (XLSX), CSV or PDF.

## 7.8 Master data

Vehicles (registration, RFID tag, QR code, transport company, default driver, stored tare, blacklist), customers,
suppliers, materials, drivers, transport companies and orders. Records in use are deactivated rather than deleted.
Approving a stored tare and blacklisting need the corresponding permission.

## 7.9 Mobile app and web dashboard (managers and owners)

The Android app and the web dashboard show the cloud copy of every site: status of each weighbridge, tickets with
their PDF, reports, alerts and the audit trail (web). A weight is marked **LIVE** only while the site sent it
within the last 90 seconds; otherwise it says **NOT LIVE** and how old the value is. From the ticket details an
authorised user can **request a cancellation**; the site PC carries it out the next time it is connected, records
the requester and reason in its audit trail and reports the result. Weighing, printing and barriers are always
controlled by the site PC.
