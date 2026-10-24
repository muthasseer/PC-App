# 4. User Interface Wireframes

The wireframes show the layout of the screens as built. Rendered screenshots of every desktop page (English,
Sinhala, Tamil, dark theme) and of the Android app are produced by every CI run and are included in the release
package under `docs/screenshots/` (desktop: `AdvancedWeighbridge.exe --screenshots <folder>`; Android: the emulator
end-to-end test). The web dashboard screenshots are in `docs/images/`.

## 4.1 Desktop: weighing dashboard (F9 = GET WEIGHT, Esc = clear)

```
┌───────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ☰ Weighing   History   Reports   Master data │ Devices: Indicator setup  Raw monitor  Lights & barriers │ … │
├───────────────────────────────────────────────────────────────────────────────────────────────────────────┤
│ Mode: [Semi-automatic ▾]                         ⚠ SIMULATOR – training, not a real weighing (if active)│
├──────────────────────────────────────────────────────────────┬────────────────────────────────────────────┤
│ ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓ │ Vehicle                                    │
│ ┃                                          31 240  kg       ┃ │ [ WP CAB-1234            ▾]  (ANPR/RFID   │
│ ┃                                                           ┃ │   fills this automatically)                │
│ ┃ ┌───────────────────────────────────────────────────────┐ ┃ │ ☐ Use approved stored tare (12 310 kg)     │
│ ┃ │ ✔  STABLE – ready to capture                          │ ┃ │ Direction  (•) Outbound  ( ) Inbound       │
│ ┃ └───────────────────────────────────────────────────────┘ ┃ │ Material   [ Metal 3/4"            ▾]      │
│ ┃ COM3 9600 8N1 · profile "Avery L215" v3 · 10 frames/s     ┃ │ Customer   [ Lanka Builders        ▾]      │
│ ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛ │ Supplier   [                       ▾]      │
│  frame colour = state colour; text and symbol always shown   │ Driver     [ … ]                           │
│                                                              │ Delivery note [ … ]  Reference [ … ]       │
│ [ ⚖ GET WEIGHT  F9 ]  [ Print ]  [ Clear ]                    │ Remarks    [ …                         ]  │
│   (Manual mode: red [ Manual entry ] instead, reason required)│                                            │
│ ┌ Result ─────────────────────────────────────────────────┐  │                                            │
│ │ ✔ Second weight captured – ticket KGL-2026-000412       │  │                                            │
│ │ Gross 32 460 kg · Tare 12 310 kg · Net 20 150 kg        │  │                                            │
│ └─────────────────────────────────────────────────────────┘  │                                            │
│ ┌ Automation (automatic mode) ──────────────────────────────┐│                                            │
│ │ State: Awaiting stable · [Release vehicle] [Ack fault]   ││                                            │
│ │ ● Entry RED   ● Exit GREEN   ● Barrier closed            ││                                            │
│ └───────────────────────────────────────────────────────────┘│                                            │
│ Pending (first weight)          │ Recent tickets             │                                            │
│ Vehicle  Ticket  First   Time   │ Ticket  Vehicle  Net  …    │                                            │
├──────────────────────────────────────────────────────────────┴────────────────────────────────────────────┤
│ Status bar: user · role · indicator connection · cloud sync (pending/failed) · session timeout            │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Weight display states (colour, symbol and text together, never colour alone):

| Colour | Conditions (text shown) |
|---|---|
| Green ✔ | Stable – ready to capture |
| Yellow ⌛ | Motion – wait; below minimum capture weight |
| Red ✖ | Overload / above capacity, under range, negative weight, indicator error, invalid data, reading out of date, communication error |
| Blue 🔒 | Weight captured and locked |
| Grey 🔌 | Disconnected, no data, platform empty (zero), manual mode – no indicator |

## 4.2 Desktop: indicator setup wizard

```
┌──────────────────────────┬──────────────────────────────────────────────────────────────────┐
│ ✔ 1. Connection          │ 2. Raw data and framing                                          │
│ ▶ 2. Raw data and framing│ [Start capture] [Stop] [Clear] [Save capture…]  ☐ hexadecimal    │
│   3. Data format         │ ┌──────────────────────────────────────────────────────────────┐ │
│   4. Live test and       │ │ 10:41:02.113  ST,GS,+0031240.0kg<CR><LF>                     │ │
│      reference weight    │ │ 10:41:02.213  ST,GS,+0031240.0kg<CR><LF>                     │ │
│   5. Stability and save  │ └──────────────────────────────────────────────────────────────┘ │
│                          │ [Suggest framing]  → "Lines end with CR LF (98 % of bytes)"      │
│                          │ Framing (advanced): mode, terminator, start/end, length, gap     │
│ [← Back]      [Next →]   │                                                                  │
└──────────────────────────┴──────────────────────────────────────────────────────────────────┘
```

1. **Connection** – COM port list with USB adapter details, baud/data/parity/stop/handshake, DTR/RTS, *Detect line
   settings automatically*, TCP host/port, polling command, or a replay file.
2. **Raw data and framing** – live raw capture (text or hex), framing suggestion, manual framing settings.
3. **Data format** – *Which number is the weight?* (candidates from the format learner), *Use the suggested parser*,
   the decoded result for every captured message, advanced parser settings (regex, delimited, fixed position,
   protocol plug-in).
4. **Live test and reference weight** – live decoded weight, state, counters, recent frames; the operator types the
   number shown on the indicator and presses *Compare*; a match is required before the profile can be marked verified.
5. **Stability and save** – stability mode, window, tolerance; profile name/manufacturer/model/notes, verified
   status, *Save and use this profile*, export and import.

## 4.3 Desktop: raw data monitor

```
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│ Raw data monitor   COM3 · 9600 8N1 · Connected · 10.0 frames/s · rejected 0 · errors 0        │
│ [Pause] [Clear] ☐ Hex  ☐ Only rejected   [Save capture…]                                      │
│ Time          Raw (printable)                 Decoded        Result                           │
│ 10:41:02.113  ST,GS,+0031240.0kg<CR><LF>      31 240 kg      Stable, gross                    │
│ 10:41:02.213  ST,GS,+003124?.0kg<CR><LF>      —              Rejected: characters not allowed │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 4.4 Desktop: other pages

| Page | Layout |
|---|---|
| History | Filter bar (from/to date by created or completed time, search in ticket/vehicle/names, vehicle, status, operating mode, customer, supplier, material, manual weights only), paged grid; double-click opens the transaction window: details, measurements with raw indicator data, camera evidence, field changes, print history, and *Reprint (reason)*, *PDF*, *Edit*, *Correct first weight*, *Cancel and re-issue*, *Cancel* — each subject to permissions and, where configured, supervisor approval. |
| Reports | Transactions; daily, weekly, monthly and annual summaries; vehicle-, material-, customer-, supplier- and operator-wise; manual weights and overrides; cancelled tickets; communication errors; audit trail — with period and filters, preview grid and *Export XLSX / CSV / PDF*. |
| Master data | Tabs for vehicles (stored tare approval, blacklist), customers, suppliers, materials, drivers, transport companies, orders; search, add, edit, deactivate. |
| Lights & barriers | *Live state* (automation state, lights, barriers, sensor inputs); *Manual control* (open/close entry and exit barrier, set lights — each needs a reason, is audited and still subject to the interlocks; submit a vehicle number for manual identification); *Simulator* controls (trucks on/off) when the simulator is active; *Hardware configuration* as JSON: I/O controllers, signal assignments, barrier drive, cameras, identification sources, with *Insert simulated lights, barriers, sensors and ANPR*. |
| Users & roles | Users (role, active, MFA, must change password, unlock, reset), role permission matrix. |
| Settings | Company, localisation and theme, weighing rules, approvals, printing and ticket template, cloud, security, automation. |
| Backup | Backup now, schedule, folders, history with SHA-256, restore (administrator, with confirmation). |
| Audit | Filterable audit trail, *Verify audit chain*. |
| Notifications, System status | Alerts; versions, data folder, database, disk, devices, plug-ins, cloud state. |

## 4.5 Android app

```
┌──────────────────────────────┐  ┌──────────────────────────────┐  ┌──────────────────────────────┐
│▓ Advanced Weighbridge  ⟳     │  │▓ Advanced Weighbridge  ⟳     │  │▓ ← Transaction               │
│▓ Ravi Kumar · awms.example.lk│  │ Site: [All sites         ▾]  │  │ QRY-2026-000101  [COMPLETED] │
│ Site: [All sites         ▾]  │  │ [Ticket, vehicle…] [Search]  │  │ QRY · WB1 · Semi-automatic   │
│ Updated 10:42:05 · every 10 s│  │ [Last 7 days▾] [All status▾] │  │ Gross        32,460 kg       │
│ ┌──────────────────────────┐ │  │ ☐ Only manual weights        │  │ Tare         12,310 kg       │
│ │ Kegalle quarry (QRY)     │ │  │ 3 of 3                        │  │ Net      20,150 kg           │
│ │ [CONNECTED] Last 4 s ago │ │  │ QRY-…0102 · NC LK-5521       │  │ Vehicle  WP CAB-1234 …       │
│ └──────────────────────────┘ │  │   [MANUAL] [COMPLETED]       │  │ Weighings                    │
│ ┌──────────────────────────┐ │  │   Net 14,200 kg   10:40      │  │  Weighing 2   12,310 kg      │
│ │ Main weighbridge (WB1)   │ │  │ QRY-…0101 · WP CAB-1234      │  │  MANUAL ENTRY — not a        │
│ │        [LIVE · STABLE]   │ │  │   [COMPLETED]                │  │  verified indicator reading  │
│ │             31,240 kg    │ │  │   Net 20,150 kg   10:39      │  │ [View ticket]                │
│ │ Mode: Semi-automatic     │ │  │ [Load more]                  │  │ [Share ticket (PDF)]         │
│ └──────────────────────────┘ │  │                              │  │ [Request cancellation]       │
├──────────────────────────────┤  ├──────────────────────────────┤  │ (ticket pages rendered here) │
│Dashboard Tickets Reports …   │  │Dashboard Tickets Reports …   │  └──────────────────────────────┘
└──────────────────────────────┘  └──────────────────────────────┘
```

When the data is older than 90 seconds (counting the phone's own refresh delay) the tile changes to
`[NOT LIVE]`, the weight is dimmed under *Last known value* and the note says *Last received … — not a current
reading.* Reports show totals and a table by day, material, customer, supplier, vehicle or site; Alerts list site
notifications (unread in bold, *Mark all read*); Account shows the user, rights, alert notifications switch,
password change, web dashboard link and sign-out.

## 4.6 Web dashboard

Same sections as the app (status, transactions with ticket PDF and cancellation request, reports with XLSX/CSV
export, alerts, audit trail with chain verification) plus administration (sites and site keys, users, conflicts,
server audit). Screenshots: `docs/images/cloud-dashboard.png`, `cloud-ticket-detail.png`, `cloud-admin.png`,
`cloud-mobile-not-live.png`.
