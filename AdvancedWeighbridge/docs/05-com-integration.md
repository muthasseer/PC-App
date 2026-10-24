# 5. COM-Port Integration Strategy

Weighbridge indicators from different manufacturers send weight data in many formats. AWMS does not hard-code any
of them. Each indicator is described by a **device profile** (data, not code) that the Setup Wizard builds from the
indicator's own output, that is versioned, and that is recorded with every captured weight.

## 5.1 Principles

1. **Never guess a weight.** A frame is either decoded to exactly one weight or rejected with a reason. Ambiguous
   frames (two numbers, glued frames, dates next to weights, conflicting units) are rejected, not "best-effort"
   parsed. Rejected frames are counted and visible in the raw data monitor.
2. **Capture from memory, not from the port.** A background service reads the port continuously and publishes an
   immutable snapshot (latest validated reading, stability, connection state). GET WEIGHT takes the snapshot that
   is already in memory, so the captured value is exactly the value on screen, without a new request.
3. **Freshness and stability are checked at capture.** The reading must be newer than the freshness limit
   (default 1.5 s), stable (software window and/or indicator flag), within capacity, above the minimum weight, not
   negative (unless allowed), and not flagged overload/error. Otherwise the capture is refused with the reason, or —
   only where the policy allows — needs supervisor approval and a reason (audited).
4. **Evidence with every capture.** Raw text and hex of the captured frame and the frames before it, reading age,
   stability evidence, port description and profile name/version are stored with the measurement (append-only).
5. **Manual values are never presented as measurements.** They have `source = Manual`, a mandatory reason, optional
   supervisor approval, and are labelled MANUAL ENTRY on screen, tickets, reports, the web dashboard and the app.

## 5.2 Connection layer

| Setting | Values |
|---|---|
| Transport | Serial (RS-232/RS-485 adapters, USB-serial), TCP client (serial-to-Ethernet servers, indicators with an Ethernet port), built-in simulator, replay of a saved capture (`.wbcap`). |
| Serial | Port, baud rate, data bits (5–8), parity (none, odd, even, mark, space), stop bits (1, 1.5, 2), handshake (none, XON/XOFF, RTS/CTS, both), DTR and RTS lines, buffer size. |
| Timing | Read timeout (no data → *Silent* state), reconnect interval (automatic reconnect with back-off after unplugging or errors). |
| Polling | For indicators that send only on request: request command (text with `<ENQ>`, `<CR>`, `\x05` style escapes) and interval. |

**Port discovery** lists COM ports with their USB vendor/product IDs and driver names, so an operator can tell
adapters apart (`AdvancedWeighbridge.exe --list-ports` writes the same list; the installer stores it in
`logs\install-ports.txt`). **Automatic line detection** listens on the port with each common setting
(9600, 4800, 2400, 1200, 19200, 38400, 57600, 115200 baud × 8N1, 7E1, 7O1) and ranks them by how clean the received
text is (printable ratio, repeating structure, UART errors). It only works for continuously transmitting
indicators; for polled indicators the request command must be set first.

## 5.3 Framing

| Mode | Use |
|---|---|
| Terminator | Lines ending in CR, LF, CRLF or ETX (most ASCII indicators). Option to accept any line ending. |
| Start/end | STX … ETX (or other markers), optionally followed by N trailer bytes (checksum). |
| Fixed length | Every frame has the same length, optionally synchronised on a start marker. |
| Idle gap | A frame ends when the line has been silent for N ms (indicators without delimiters). |

A maximum frame length protects against runaway data; overflow, noise between frames and incomplete frames are
reported as framing issues. **Checksums**: XOR-8, Sum-8, two's complement 8-bit (LRC), two's complement 7-bit
(Mettler Toledo continuous), CRC-16/MODBUS; binary or ASCII-hex; trailer or end of payload; start/end markers
included or not.

## 5.4 Parsing

| Mode | Description |
|---|---|
| Auto | Finds exactly one number plus optional unit and status words. Rejects frames with more than one number unless the profile says which one is the weight. |
| Regex | Named groups `weight` (required), `sign`, `unit`, `status`, flag groups `stable`, `motion`, `overload`, `error`, `gross`, `net` (with a timeout against pathological patterns). |
| Delimited | Delimiter plus field indexes for weight, unit and status fields. |
| Fixed position | Character spans for weight, sign, unit and status. |
| Protocol | Built-in or plug-in decoder for binary/proprietary formats (status bytes, packed digits). Built in: Mettler Toledo continuous output. |

Common options: text encoding, decimal separator (point/comma), thousands separator (none, comma, point, space,
apostrophe), implied decimals, reversed digit order, multiplier, maximum digits, negative values allowed or not,
default unit and whether the unit is required, status rules (word, contains or regex) on top of the default status
words (`ST/STA/STABLE`, `US/UNST/UNSTABLE/MO/MOTION`, `OL/OVER/OVERLOAD`, `UL/UNDER`, `ERR/ERROR`, `GS/GROSS`,
`NT/NET`, `TR/TARE`), validation and ignore patterns, length limits, rejection of control/non-ASCII bytes, and a
plausibility limit.

The formats named in the specification, as covered by the automated tests (`ParserTests`):

| Frame | Result |
|---|---|
| `12345` | 12 345 kg (profile default unit) |
| `12345 kg` | 12 345 kg |
| `ST,GS,+001234.5kg` | 1 234.5 kg, stable, gross, 1 decimal |
| `US,NT,00025.40` | 25.40 t = 25 400 kg with a profile whose default unit is tonnes; motion, net |
| `+000123.45` | 123.45 kg |
| `Gross: 12500 kg` | 12 500 kg, gross |
| `WT=12500.0` | 12 500 kg |
| `12500kg12510kg`, `12,500` (no thousands separator configured), `2026-01-05 10:22 12500kg`, `12345 kg 12 lb` | rejected: ambiguous |
| `12.34.5`, a frame with control bytes, 13 digits, `99999999 kg`, `ST,GS`, `kg 12500 lb` | rejected with the reason (separator, control bytes, too many digits, implausible, no weight, conflicting units) |

## 5.5 Learning a new indicator (Setup Wizard)

1. Choose the port (or TCP/replay) and line settings, or run automatic detection.
2. Capture raw output; the **framing analyser** proposes the framing from byte statistics (terminators, markers,
   lengths, gaps).
3. The **format learner** lists the numeric candidates in the captured frames with how they vary, proposes the
   parser (mode, field, unit, decimals, status words) and shows the decoded result for every captured frame.
   The user confirms *which number is the weight*.
4. **Reference weight check**: the user enters the value shown on the indicator display; the decoded value must
   match exactly before the profile can be marked *verified* (with user and time).
5. Choose stability rules (software window and tolerance, indicator flag, or both — recommended) and save.

Saving a changed profile creates a new version (the old one stays in `indicator_profile_versions`) and is audited.
Profiles can be exported/imported as `*.wbprofile.json`; an imported profile is always *unverified* until checked
against the indicator. The folder `profiles/` contains five **example** profiles (generic formats and Mettler Toledo
continuous output). They are tested against sample captures, not against physical indicators, and are therefore
shipped unverified.

## 5.6 Plug-ins for proprietary protocols

A protocol that cannot be described by the options above is implemented as a class implementing
`IIndicatorProtocol` (in `Weighbridge.Core`), compiled into a DLL and placed in the `plugins` folder of the
installation directory (administrators only). Plug-ins are trusted code: AWMS never downloads plug-ins or drivers,
and the installer installs none. Loaded plug-ins are listed on the Devices and System status screens.

## 5.7 Diagnosing a connection

| Symptom (status on the weighing screen) | Typical cause | What to check |
|---|---|---|
| INDICATOR DISCONNECTED | Port missing or in use | Device Manager → Ports; another program holding the port; USB adapter driver. |
| NO DATA FROM INDICATOR (*Silent*) | Indicator not transmitting | Indicator output mode (continuous vs. on demand), cable pin-out (TX/RX crossed?), polling command, baud rate. |
| INVALID DATA | Wrong line settings or profile | Raw data monitor: garbage characters → baud/parity; readable text but rejected → parser settings (see the rejection reason). |
| READING OUT OF DATE | Data stopped arriving | Cable, indicator power, interference; reconnect happens automatically. |
| COMMUNICATION ERROR | Port or adapter error | Adapter driver, cable length, RS-485 termination. |

The raw data monitor shows every frame with its decoded value or rejection reason, frame rate and error counters,
and can save a capture that reproduces the problem in the wizard or in a support case (replay transport).

## 5.8 Hardware status

The connection, framing, parsing and stability logic is covered by automated tests, including a real serial-port
round trip through a virtual null-modem on Linux CI and the indicator simulator on Windows CI. **No physical
indicator has been connected during development.** Each indicator model must pass the hardware acceptance tests in
`09-hardware-acceptance-tests.md` before trade use, and its profile must be verified against the indicator display.
