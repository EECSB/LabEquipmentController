# Lab Equipment Controller — Specification

What the software is meant to do and the rules it must keep to. This is the durable
document: it should stay true release to release.

- [README.md](../README.md) — what it is and what it looks like; each program's own README
  (Desktop, Core, Cli, Web, Tests) has the build, run and test instructions for it.
- [ARCHITECTURE.md](ARCHITECTURE.md) — how the pieces that satisfy this document fit together.
- [UI-SPEC.md](UI-SPEC.md) — what is on screen and where, in both builds: the control
  inventory, the metrics, and the rule that the web is a port of the desktop rather than a
  redesign of it.

---

## 1. Purpose and scope

A Windows desktop application for finding and driving bench instruments — oscilloscopes,
function generators, multimeters — over **Ethernet** or **RS-232**, using **SCPI**. It
replaces per-vendor utilities and a VISA runtime with one small app that speaks the wire
protocols directly.

**In scope:** LAN discovery, connecting to several instruments at once, sending SCPI and
reading replies, instrument-aware shortcuts, small scripts, screen and trace capture,
export of what it finds.

**Out of scope, deliberately:**

- **USB/USBTMC and GPIB.** Not for the same reason each, and §17 records both. Serial was
  on this list until it was built (§13); it is here as the one that turned out to be worth
  revisiting, which is what §17 said about it while it was still an argument.
- Any dependency on NI-VISA, IVI, or a vendor SDK. The transports are implemented here.
- Instrument *emulation*, calibration workflows, or measurement automation beyond scripts.
- Inventing SCPI. Every command shipped in a catalog or a quick-command button is
  transcribed from the vendor's programming guide (§10).

## 2. Platform

| | |
|---|---|
| Language / UI | C# on **WinForms** (not WPF/XAML) |
| Target | `net10.0-windows`; the UI-free `Core` library is plain `net10.0` |
| Runtime | 64-bit Windows 10/11 |
| Distribution | Self-contained single-file `.exe` (~48 MB), or framework-dependent |
| Dependencies | No VISA, IVI or vendor SDK — the transports are implemented here. In `Core`, two packages: PdfPig (reading text out of datasheets, §11b) and `System.IO.Ports` (the serial transport, §13). In the desktop app, WebView2 (the guide viewer, §11a) |

## 3. Concepts

| Term | Meaning |
|---|---|
| **Device** | An address found by a scan (`ScpiDevice`): IP, port, transport, `*IDN?` reply |
| **Transport** | How bytes reach the instrument: raw TCP socket, VXI-11, or a serial port (`IInstrumentClient`) |
| **Identity** | The instrument's `*IDN?` string, `Manufacturer,Model,Serial,Firmware` |
| **Family** | Broad class inferred from the identity: oscilloscope, Siglent generator, SCPI generator, multimeter, generic |
| **Profile** | What a family implies: quick-command buttons, capture support (`InstrumentProfile`) |
| **Session** | One open connection and its state: client, identity, profile, history, timeout (`InstrumentSession`) |
| **Console** | The UI for one session, in a tab or its own window (`InstrumentConsole`) |

## 4. Discovery

**Interfaces.** Offer every operational, non-loopback IPv4 interface with a netmask.
Order them so the first is the best guess: interfaces **with a default gateway** first,
then by ascending subnet size. A Hyper-V "Default Switch" (/20, no instruments) must not
be selected ahead of a real 254-host LAN.

**Hosts.** Enumerate the subnet that owns the selected address, excluding the network and
broadcast addresses, capped at **4096** hosts. Report when the cap truncated the range.

**IP range.** An optional field narrows the sweep. Empty means the whole subnet, so the
default behaviour is what it always was. It accepts `192.168.1.20-60`, a bare `20-60`
against the selected interface's subnet, a single address, a `/28`-style block, and any
comma-separated mixture of those. A range that cannot be read **stops the scan** and says
why — falling back to the whole subnet because of a typo is the opposite of what was asked.
Overlapping ranges probe each host once (§13: these instruments wedge on a second
connection). The range in force is named in the status line, so a narrowed scan that finds
nothing cannot be mistaken for an empty network.

It exists for a reason smaller than speed: a bench sits at a handful of known addresses, and
sweeping all 254 knocks on every printer, camera and PLC that shares the lab network.

**Ports.** A user-editable list. `111` means VXI-11 (the RPC portmapper); every other port
is probed as a raw socket. Defaults offered: `5025` (LXI/VISA convention), `5555` (Rigol),
`111`.

**Probing rules** — these are correctness requirements, not tuning:

- Ports on one host are probed **sequentially, never concurrently**. Instruments such as
  the Rigol accept a single connection and wedge if two are opened.
- A raw-socket probe is a **TCP connect only**. It must not send `*IDN?`: on a Rigol an
  unread reply permanently poisons the output queue (§13).
- The identity comes from the **VXI-11** probe, which is framed and safe to read.
- Up to **128 hosts** are probed at a time.
- Probe timeouts are fixed and short (connect ~300 ms, identify ~3 s) and are
  **independent of the user's Timeout field**, which governs instrument communication only.
  A large communication timeout must never slow discovery.

**Results.** One row per address, not per endpoint: all probes for an address are merged,
preferring **VXI-11** when the instrument offers both, and grafting on an identity read by
whichever probe obtained one.

**Reporting.** Devices are streamed to the UI as they answer, and progress is reported at
most ~200 times per scan — a per-host report would swamp the UI thread on a /20 and delay
the Stop button behind the backlog. A scan is cancellable, and results found before the
stop are kept.

**Serial ports are swept differently**, and the difference is the physics rather than a
preference. Everything above rests on asking every address the same harmless question and
believing the answers. A serial port cannot be asked anything until its baud rate, framing
and flow control already match, and what is on the other end may be a printer, a modem or a
3D printer rather than an instrument. So the serial half of the scan card
(`SerialScanner`) splits the two halves of "discovery" that a subnet sweep gets to run
together:

- **Listing opens nothing.** `SerialScanner.List()`, over `SerialPorts.Names()` — the CLI's
  `lec ports`, the web's `GET /api/ports` and `/api/serial/list`, the desktop's port
  dropdown and its results list. This is what the card shows the moment it switches to
  Serial, and it is already enough to pick a port and connect. Nothing on anyone's bench is
  touched by looking at this list.
- **Probing opens ports, and only on a button press.** `SerialScanner.ScanAsync()` opens
  each port the user chose, at each baud rate the user named, in order, stopping at the
  first that answers `*IDN?`. It does **not** sweep parity, data bits or flow control: that
  is a combinatorial search against hardware that cannot say "wrong number", and it is the
  thing §17 refuses. What it does sweep is disclosed on the button, because opening a port
  asserts DTR and RTS and sends text to whatever is there.

Two further differences from a subnet sweep follow from a port being a thing rather than an
address. **Every port stays listed**, answered or not — an IP that does not answer is simply
not a row, but a serial port that does not answer is still a port on this machine and still
connectable at settings the scan did not try. And **a reply is checked before it is
believed**: TCP either delivers the sender's bytes or delivers nothing, while a UART at the
wrong baud rate delivers *different* bytes, so a reply is accepted as an identity only if it
is printable and comma-separated. One that arrives and fails that test is reported as noise
at those settings, which is the fact that sends someone to the baud rate rather than to the
cable (§5, §13, §17).

## 5. Addressing and connection

The Address box accepts, in this order of precedence:

1. **A VISA resource string** — `TCPIP0::192.168.1.19::inst0::INSTR` (VXI-11),
   `TCPIP0::192.168.1.19::5025::SOCKET` (raw), or `ASRL3::INSTR` / `ASRL::COM3::INSTR`
   (serial). Parsed natively so a user can paste what NI-MAX or Connection Expert reports.
   `TCPIP` and `ASRL` are the interfaces there are transports for; a `GPIB` or `USB`
   resource string is **refused by name, with the reason** — it used to fall through to
   the host branch and be accepted as a hostname, which got the user as far as a DNS
   failure for an instrument sitting on their desk.
2. **`vxi://host`** — VXI-11 without writing a resource string, and `vxi://host/gpib0,9`
   for an instrument behind a gateway. **`tcp://host`** says raw socket the same way.
3. **`serial://COM3`**, or `serial:///dev/ttyUSB0` — a serial port, with the line settings
   as a query when they are not the usual ones. In the GUI the scan card's heading switches
   to `Serial Scan` and the address box becomes a list of the machine's ports, so the scheme
   need not be typed ([UI-SPEC.md](UI-SPEC.md) §3.2); everywhere else it is written out:
   `serial://COM3?baud=115200&parity=even&databits=8&stopbits=1&flow=rtscts&term=crlf`.
   A bare number is the baud rate, so `serial://COM3?115200` means what it looks like.
   Anything unrecognised is refused rather than defaulted: connecting at 9600 to an
   instrument set to 115200 does not fail, it answers with rubbish. Defaults are
   **9600-8-N-1, no flow control, LF**. The scheme is not optional — `COM3` on its own is
   a legal hostname and cannot be told from a machine on the network.
4. **`host:port`** — the explicit port wins.
5. **A bare host** — the port and transport of a matching discovered row are reused, so a
   previously found address still connects the right way; with no matching row it takes the
   first port the Port(s) list offers.
6. **Nothing typed** — the selected row in the device list.

An explicit port of `111` means VXI-11. **A form that names its transport is taken at its
word** — a discovered row never overrides it, because saying it is the reason to type it.
For serial there is nothing to override it with: the device list is what a subnet sweep
found, and a subnet sweep does not find COM3 (§17).

**One parser, for all three front ends.** The desktop, the CLI and the web server read a
typed address through the same `InstrumentAddress` in Core. They did not: the CLI and the
server each carried a copy of it and the desktop carried neither, so its box refused
`vxi://` though [UI-SPEC.md](UI-SPEC.md) §3.3 describes both builds' boxes as taking one.

On connect: open the transport, query `*IDN?` (falling back to what the scan learned if
the instrument won't answer now), derive the profile, and open a console.

**One session per address.** Connecting to an address that already has a session must
*focus that console* rather than dial again — see §13, the Rigol wedges on a second TCP
session. This is a hard rule, not a convenience.

**A timeout is not a cancellation.** Both cut an attempt short through the same
cancellation token, so every deadline the clients impose goes through `Deadline`, which
raises `TimeoutException` when its own clock is what fired — naming the host, the port and
the wait. That covers connecting, reading a binary block over a raw socket, and any VXI-11
RPC call (which also names the procedure: a stalled `create_link` and a stalled
`device_read` want different things from the user). Only Cancel or Stop produces
"cancelled" — a word that tells the user they stopped something. When both land in the
same instant, cancellation wins.

**A reply that never ended is a failure, not a short answer.** A text line that runs out of
time throws rather than returning what arrived — this once returned the fragment, which is
harmless for an empty reply and dangerous for a partial one: `+8.39` in place of
`+8.39319298E-04` is a plausible voltage, is recorded and plotted as one, and leaves the
rest of the line in the buffer to corrupt the read after it. A binary block throws for the
same reason, a screenshot cut short being one that comes back looking complete.

**Disconnect** returns the instrument to local (front-panel) control before dropping the
link. This applies on explicit disconnect *and* on application exit, where each close runs
on a worker thread with a short bounded wait so a powered-off instrument cannot hang the
exit. A raw socket does this by closing; a serial port has no connection whose closing
means anything, so it asks in band with `SYSTem:LOCal` (§13).

## 6. Sessions, tabs and detached windows

- Each connection has its own session and its own console: log, command history, timeout,
  script editor, and command-reference window. Nothing is shared between instruments.
- Consoles live in a tab strip; the scan panel and the discovered-instruments list stay
  **shared** above them. The tab is captioned `Model (address)`.
- The main window carries a **menu bar**. Tools does things to the bench — multi-instrument
  scripts, the AI connection. Help explains things: the command library, the script language
  reference, About. Both references are reachable with nothing connected, because both are
  wanted before there is anything to connect to.
- The **About** box reports the version, the runtime and the catalog totals, all read at
  runtime — the command count is counted from the embedded catalogs rather than written
  down, so it cannot drift from what ships.
- **Export Results** sits immediately after **Scan**: it exports what the scan found.
- Any console can be **detached** into its own window and later re-attached — by its own
  button or the tab's context menu. Closing a detached window re-attaches it; it never
  silently disconnects.
- Detaching **reparents the same control**. The session, log and history must survive it
  untouched.
- Closing a console disconnects its instrument (§5) and removes only that tab.
- Every tab carries an **✕**. It is the discoverable way to reach the close that the tab's
  context menu already offered, and it acts on mouse *up*: a press that starts on the glyph
  and slides off must not disconnect an instrument.
- **Multi-Instrument Scripts** sits at the right-hand end of the tab strip. That strip is the
  list of instruments a script addresses, so the button belongs beside it — and it stays put
  when there are no tabs, which is exactly when someone might open it to see what a script
  would need.
- A console's controls are **grouped by what they act on**, top to bottom: the identity line
  and **Detach** (this console's window), then the quick commands and the tools that ask the
  instrument for something, then **Clear Log / Save Log** sitting directly on top of the log
  they act on, then the command box. **Disconnect** ends the session, so it sits at the far
  right of that log strip — away from Detach, which it has nothing to do with, and away from
  anything clicked routinely.
- The Timeout field applies to connections that are already open as well as the next one.
- When no console is present the area explains what to do instead of showing a blank panel.

## 7. Command console

- Type SCPI, press Enter (or Send). A command **containing `?`** is treated as a query and
  its reply is read; anything else is written and acknowledged with `(sent)`.
- Output is colour-coded: sent command, reply, informational note, error. A failed command
  reports the error in the log; it never throws away the console.
- **History**: Up/Down recall earlier commands for *this instrument*. The position sits one
  past the newest entry, so the first Up recalls the last command and stepping back down
  past the newest clears the input. Blank commands are not recorded.
- The console is locked out while a script drives the same link — two request/response
  streams on one connection would interleave and corrupt each other.

## 8. Instrument families and quick commands

Classification from `*IDN?`, **in this order** (the order is load-bearing):

The model is normalised first: a leading `MODEL ` is dropped and spaces and hyphens
are removed, because a Keithley answers `KEITHLEY INSTRUMENTS,MODEL 2450,…` and a
Keysight scope `MSO-X 3054T` — neither would match a prefix test as printed.

| Order | Family | Matched on |
|---|---|---|
| 1 | Tektronix scope | maker Tektronix **and** `TDS`, `DPO`, `MSO`, `MDO`, `DSA`, `LPD` |
| 2 | Keysight scope | maker Keysight/Agilent/HP **and** any oscilloscope prefix |
| 3 | R&S scope | maker Rohde&Schwarz/HAMEG **and** `RTB`, `RTM`, `RTA`, `RTO`, `RTE`, `RTP`, `RTH`, `HMO` |
| 4 | Siglent scope | maker Siglent **and** `SDS` **and** an `X` in the model — **else Generic** |
| 5 | GW Instek scope | maker GW Instek/Good Will **and** `GDS` |
| 6 | Fluke multimeter | maker Fluke **and** `884x`, `8808`, `45` |
| 7 | Keithley SMU | maker Keithley **and** `24`, `26`, `6221`, `6430`, `6514`, `6517` |
| 8 | Keithley multimeter | maker Keithley **and** `DMM`, `20`, `21`, `27` |
| 9 | R&S spectrum analyzers | maker Rohde&Schwarz **and** `FPC`, `FSL`, `FSW`, `FSU`, `FSP` (not `FSPN`), `FSQ`, `FSIQ`, `FSV`/`FSVA` — a separate catalog each, from that line's own manual |
| 10 | *(Generic)* | maker Rohde&Schwarz **and** any other analyzer prefix (`FSE`, `FSPN`) — no catalog |
| 11 | Spectrum analyzer | `DSA`, `RSA`, `SSA`, `SVA`, `FPC`, `FSL`, `FSV`, `FSW`, `N90`, `MS2` |
| 12 | Multimeter | `SDM`, `DM`, `344`, `34401`, `2000`, `2110` |
| 13 | B&K electronic load | maker B&K Precision **and** any electronic-load prefix |
| 14 | Chroma electronic load | maker Chroma **and** `632` |
| 15 | Chroma modular load | maker Chroma **and** `636` |
| 16 | *(Generic)* | maker Chroma **and** any other load prefix (the `638` AC loads) — no catalog |
| 17 | Electronic load | `DL`, `SDL`, `EL3`, `N33`, `IT8`, `63`, `86` |
| 18 | Chroma power supply | maker Chroma **and** `62` |
| 19 | Keysight power supply | maker Keysight/Agilent/HP **and** any power-supply prefix |
| 20 | R&S power supply | maker Rohde&Schwarz/HAMEG **and** any power-supply prefix |
| 21 | B&K power supply | maker B&K Precision **and** `920` |
| 22 | B&K triple-output supply | maker B&K Precision **and** `913` |
| 23 | Power supply | `DP` (not `DPO`), `SPD`, `E36`, `PWS`, `HMP`, `HMC8`, `NGE`, `NGL`, `NGM`, `NGP`, `22xx` |
| 24 | Generator | `SDG`, `DG`, `AFG`, `33` — split by maker: Siglent → its own dialect, else standard SCPI |
| 25 | Oscilloscope | `DS`, `MSO`, `SDS`, `TDS`, `MDO`, `DPO`, `DSO` |
| 26 | Generic | anything else — IEEE 488.2 common commands only |

Rows 10 and 16 route **to `Generic` on purpose**. An R&S FSE and a Chroma 63800 would
otherwise fall through to the generic analyzer and load catalogs, which are the Siglent
SSA3000X and SDL1000X sets. Those partly work, which is the problem: the buttons appear,
some succeed, and the failures read as the instrument misbehaving. A vendor-specific test
that matches and then declines is the only way to stop a later generic test claiming it.

Row 16 is now permanent as far as anyone can tell. The Chroma 63800 guide has been found
and is a scan with no text layer, so there is nothing to extract from it — see §10.

The order is load-bearing and every collision below is real:

- **Vendor-specific scopes first**, matched on the *maker* as well as the model, because
  the prefixes are shared but the dialects are not. A Tek wants `CH1:SCAle` and
  `ACQuire:STATE RUN`; a Rigol wants `:CHANnel1:SCALe` and `:RUN`; an R&S wants
  `CHANnel1:SCALe` and a bare `RUN`, with no leading colon anywhere; a Siglent wants
  `:CHANnel1:SWITch` and `:TRIGger:RUN`. `MSO` belongs to Rigol, Tektronix and Keysight
  alike, `SDS` to Siglent, and `DSA` is a Tektronix **scope** but a Rigol **spectrum
  analyzer**. Tek's `RSA` is deliberately not in that list: unlike the `DSA` it really
  is a spectrum analyzer.
- **Siglent's scope before its generator** is not the concern — `SDS` and `SDG` do not
  collide — but the SDS check must precede the generic oscilloscope test, or a Siglent
  scope inherits the Rigol catalog through the shared `SDS` prefix, as it used to.
- **Keithley before the generic multimeter test** — a `DMM6500` would otherwise be
  claimed by the `DM` prefix and given the Siglent SDM's much smaller catalog.
- **Vendor supplies before the generic one** — the four address channels four ways: a
  Rigol names one per command (`:APPLy CH1,5,1`), a Keysight uses a channel list
  (`VOLTage 5,(@1)`), an R&S selects one first (`INSTrument:NSELect 1`) and then sends
  unqualified commands to it, and a Chroma is single-output so names none at all.
- **B&K loads before the generic load test** — `86` is in that generic prefix list
  precisely because of the B&K 8600, so its own catalog has to claim it first. Chroma's
  loads are `63xxx`: the `632xx` models take the transcribed 63200A catalog, and the rest
  decline to Generic rather than inherit the Siglent SDL1000X set.
- **The two B&K supply lines are separate families** — same maker, two guides, two command
  sets. The 9200B is single-output; the 9130B is a triple-output whose commands nearly all
  act on a selected channel (`INSTrument CH2`), which the 9200B has no equivalent of.
  Handing either one the other's catalog would produce buttons that partly work.
- **Spectrum analyzers before scopes** — a Rigol `DSA815` would otherwise be taken for a
  scope by the `DS` prefix.
- **Multimeters before generators** — a Siglent `SDM` multimeter takes standard SCPI and
  must never inherit the `C1:BSWV` dialect from its own maker's `SDG`.
- **`DPO` excluded from the power-supply test** — a Tektronix `DPO4104` is a scope, and
  the Rigol `DP800`'s `DP` prefix would otherwise claim it.

Tests pin each of these.

A Keithley 2450 or DMM6500 can be set to speak either SCPI or Keithley's own TSP, and
answers none of its catalog until `*LANG SCPI` has been sent **and the instrument
rebooted**. The catalogs carry `*LANG`, and the script examples lead with `*LANG?`.

Each family supplies a button set that actually works on it (a scope's `:MEASure:VPP?` is
meaningless to a Siglent generator), plus whether screen capture and waveform capture are
offered. An unrecognised instrument still connects and is fully usable from the command
line — it just gets the safe generic button set.

## 9. Scripting

A deliberately small line-oriented language, one instruction per line:

| Form | Meaning |
|---|---|
| `*IDN?` etc. | A SCPI line. Contains `?` → query and read the reply; otherwise write |
| `# text`, `// text` | Comment |
| `DELAY <ms>`, `WAIT <ms>` | Pause |
| `PRINT <text>`, `ECHO <text>`, `LOG <text>` | Write a message to the output |
| `REPEAT <n>` … `END` | Repeat a block; may be nested. `ENDREPEAT` is accepted for `END` |

Rules:

- `REPEAT` with a count of zero or less skips the whole block.
- `END` without a matching `REPEAT` is an error and stops the script.
- The script **stops on the first command error**, reporting the offending line number.
- Cancellation (the Stop button) is honoured between every line and during a wait.
- A hard instruction ceiling (1,000,000) guards against a pathological script; the Stop
  button is the normal way out.
- Scripts are plain text, saved and loaded as `.scpi`/`.txt`. An editor belongs to one
  instrument, named in its title bar.
- **The bundled examples match the instrument the editor was opened for** — a multimeter is
  never offered `C1:BSWV`, a scope never offered `MEASure:VOLTage:DC?`. Same sourcing rule
  as §10: every command is transcribed from that family's guide, so where a guide documents
  nothing (no error query for the Siglent SDG or SDM; the Rigol spells it
  `:SYSTem:ERRor:NEXT?`) the example leaves it out. Any example that **enables a generator
  output** must warn on the line above — it can damage whatever is wired up.

### 9a. Multi-instrument scripts — one script, several instruments

A **multi-instrument script** drives more than one instrument at a time. The window is
**Multi-Instrument Scripts**; the language, the runner and the `.seq` files it saves still
say *sequence* for one of them, which is what the code is named after.

It exists because the measurements that need it are interleaved rather than sectioned: a
swept filter response sets a frequency on the generator, waits, reads the meter, and repeats
— two instruments alternating inside one loop, which a script belonging to one instrument
cannot express at all.

It is the §9 language plus five forms:

| Form | Meaning |
|---|---|
| `DEVICE <alias> : <model>` | Bind a name to a connected instrument |
| `<alias>: <command>` | Send this line to that instrument |
| `WITH <alias>` … `END` | Set the target for a block |
| `FOR <v> = <a> TO <b> STEP <n>` … `END` | Sweep a value; `POINTS <n> [LOG]` instead of `STEP` |
| `<alias>: <query>? -> <name>` | Capture the reply, used later as `$name` |
| `RECORD <a>, <b>, …` | Append a row of results |
| `COLUMNS <a>, <b>, …` | Name the result columns |

Rules:

- **A line with no target is refused when more than one instrument is declared.** Guessing
  would send a generator's command to a meter, which is the failure §10 exists to prevent.
  With exactly one declared instrument a prefix is unnecessary.
- `DEVICE` names an instrument by the **model from `*IDN?`**, matched exactly and then by
  prefix — an SDS2354X answers `SDS2354X Plus`, and the short name should find it. The
  **serial number** from `*IDN?` names one instrument in particular, which is how a script
  tells two of one model apart without an address that DHCP will move; an **address** is
  accepted too, for an instrument that will not identify itself.
- **A line binds only when exactly one connected instrument answers to it.** Two meters of
  one model, a prefix two models share, or one gateway address with two instruments behind it
  resolve to nothing rather than to whichever instrument connected first. A model also passes
  over an instrument another alias already has: a second alias asking for the one meter a
  first alias has is left with nothing, rather than both reading one meter and reporting two.
  Serial numbers, addresses and the web's picks go first and are taken at their word, and the
  model lines follow in script order — so naming one of two identical meters is enough, and
  the other line finds the one that is left. A line with nothing says why: *not connected*,
  *2 connected*, *taken by `dmm`*.
- **Both builds bind by that one rule** (Core's `SequenceBinding`). The desktop's strip shows
  its answer, and its tooltip says to name each of two of one model by serial number or
  address. The web's binding table is filled with it on the server and puts a picker against
  each alias; a pick is taken at its word, the same instrument for two aliases included. The
  CLI has no bench to bind against and binds each alias by hand: `lec seq --device gen=<address>`.
- **A run looks up the alias it was bound.** The model is what the script asks for, not a
  search key, so `DEVICE gen : SDG2042X` runs on whatever `gen` was bound to, and
  `DEVICE left : SDM3065X` and `DEVICE right : SDM3065X` are two meters.
- **Every `DEVICE` is resolved before the first command is sent.** A sweep that dies three
  lines in has already changed the instrument's state.
- A sweep accepts engineering suffixes (`1k`, `2.5M`). `POINTS n LOG` spaces points per
  decade, which is how a filter response is read — a linear sweep from 100 Hz to 100 kHz
  puts almost every point above 10 kHz and skims the corner.
- An unknown `$name` is left as written rather than blanked: `FRQ,$typo` becoming `FRQ,`
  is a command an instrument may well accept, carrying a value nobody chose.
- **`RECORD`'s columns are the commas written in the script, not the commas in the data.**
  The line is split first and each field substituted afterwards, so a captured `*IDN?` —
  four commas in every one of them — stays a single column instead of silently becoming
  five. A field left empty by a reading that never arrived is kept as a gap, because
  dropping it slides every later value one column left and turns missing data into wrong
  data; a field left empty by a trailing comma is punctuation and is dropped.
- `->` on a line without `?` is an error — there is no reply to capture.
- **Lines run in order.** Two instruments never run concurrently: the measurement is
  inherently ordered, and a connection carries one conversation at a time (§6).
- Every instrument the script uses is marked busy for its duration, so its own console is
  locked out exactly as a single-instrument script does.
- Results are rows, saved as CSV — the deliverable of a sweep is a table to plot.
- The run log and the results each have their own pair of buttons, under the pane they act
  on: Save Log / Clear Log below the log, Save CSV / Clear Results below the table. They are
  independent — clearing the table must not discard the log, which is the record of how the
  table was filled.
- The results area carries both a **table** and a **plot** of the same rows. The table is the
  record and is what gets exported; the plot is how the measurement is read, because forty
  rows of numbers are not a frequency response and the shape is the answer. It redraws per
  recorded row, so a sweep going somewhere wrong is visible on the tenth point rather than
  after the fortieth.
- Which column goes on which axis is **chosen, not guessed** — a script may record anything
  in any order. The default is the first column across and everything else up, which is right
  for a sweep and means a plot appears rather than needing assembly. Log axes are offered per
  axis and **disabled when any value is zero or negative**: a log axis through zero has no
  meaning, and quietly dropping those points would hide data.
- A reading that failed to parse drops that point from that curve only. Plotting it as zero
  would invent a measurement; dropping the row would delete another instrument's reading.
- The bundled examples carry a representative `*IDN?` per declared model, so each line can
  be checked against the catalog of the instrument it is addressed to. Same rule as §10,
  and it earns its keep: a Siglent generator's frequency is `C1:BSWV FRQ`, not `FREQ`.
- One belongs to the bench, not to a console: **Tools ▸ Multi-Instrument Scripts…**, or the
  button beside Timeout on the connect row. Modeless and single-instance, so instruments can
  be connected in the main window while it is open.

### 9b. The editor teaches the language

The language is this application's own. The commands inside it are SCPI, which is a real
standard; everything around them — `DEVICE`, `WITH`, `FOR…POINTS…LOG`, `RECORD`, `->`,
`$name` — was invented here and exists nowhere else. Nobody arrives already knowing it, so
the editor teaches it rather than assuming it. Both editors share one control.

- **Colour** marks the shape of a line: comments, keywords, instrument aliases, `$values`,
  numbers. The SCPI itself is left plain — it is the content, not the frame — and a page of
  seven colours reads worse than a page of three.
- A leading `word:` is coloured as an instrument **only when a `DEVICE` line declared it**.
  `C1:BSWV` and `gen:` are the same shape; claiming the wrong one is a lie the eye believes.
- **Snippets** — a dropdown naming every construct with a description, and the whole language
  on one page behind it. Choosing one writes it in with its blanks «selected»; Tab steps to
  the next. Typing a trigger word and pressing Tab does the same thing.
- **Completion** offers keywords, snippets, the aliases this script declared, the values it
  captured, and the documented commands of the instruments in play. Ctrl+Space forces it.
  After a `$` only captured values are offered, because `$` has exactly one meaning.
- The editor's word list and the runner's are checked against each other by test. A keyword
  the editor offers and the runner rejects teaches the wrong thing, confidently.

## 10. Command discovery and curated references

There is no universal way to enumerate an instrument's command set — the programming
manual is ground truth.

1. Try SCPI-99's `SYSTem:HELP:HEADers?`. A reply counts as genuine only if it has at least
   three lines and most of them look like SCPI headers; a stray error line or a bare `0`
   is a failure, not a command list.
2. On failure, open the **curated catalog** for the instrument's family, if one is bundled.
3. If neither is available, say so plainly and point at the manual.

Catalogs are JSON, embedded as `commands.<family>.json`, each entry carrying category,
syntax, description, optional example, whether it is a query, and how far it is trusted.
Each catalog records the guide it was transcribed from.

36 catalogs, 23,978 entries, of which 518 carry a bench tick and 2,778 a cross-check.

**No description stops mid-sentence.** A guide wraps a description at the margin, and the
extractors used to stop at the line it started on whenever the next line began with
something command-shaped — so "…the external generator selected with" lost its second half,
and the full stop `emit` adds to every description made the cut look deliberate. **Six**
readers had the fault, each in its own shape: breaking on a command-shaped continuation,
never joining at all, stopping at the blank line inside a wrapped sentence, and stopping at
the label a wrap runs into in a two-column layout.

All 129 were repaired from the guides they came from — which meant fetching thirteen guides
that were missing, the last of them by hand because its URL serves a landing page rather
than the file. `CatalogDescriptionTests` keeps the count at zero per family; a family is
listed there only when its guide cannot be had and the missing words would have to be
invented, which is now true of none of them.

**A further 307 were finished the same way**, and none of them looked broken. A sentence cut
after "…the next data after the last fetched" or "…an event on a selected trigger" ends on an
ordinary noun, so no rule catches it — they were found by re-reading every guide with the
mended readers and keeping only text that *continues* what shipped. 54 command names quoted
in prose lost a stray space at the same time ("CALC:LIM:CONT: OFFS" was never a command).

**A description ending in a colon now keeps its list.** The guides put the alternatives a
parameter accepts in a table under the sentence that introduces them, so "sets the sort mode
for the search for maxima:" answered everything except what the modes are. 60 of the 92 have
been filled in — "…: X — the maxima are sorted … according to increasing X values; Y — …" —
and the rest are tables too wide or too deep to read as a sentence. Only a description ending
in a colon is extended, and only when what precedes it reads as a sentence rather than a
heading: "Impedance:" over a list of related commands is a label, and treating it as a promise
gave three FPC entries a description made entirely of cross-references.

One of the 49 was never damage. English stands a preposition at the end of a sentence after
certain verbs, and "…the type of information that a test report consists of." finishes on
the same word a severed sentence would. The check now allows that handful of verbs and
nothing looser, because a cut that goes uncounted is the failure it exists to catch.

| Family | Entries | Bench ✓ | Cross-checked • | Source |
|---|---:|---:|---:|---|
| R&S FSW analyzer | 2358 | 0 | 111 | R&S FSW User Manual (1173.9411.02 v56), chapter 13 |
| R&S FSU analyzer | 975 | 0 | 91 | R&S FSU Operating Manual (1313.9646.12-02), chapter 6 |
| R&S FSP analyzer | 1044 | 0 | 92 | R&S FSP Operating Manual (1164.4556.12-02), chapter 6 |
| R&S FSQ analyzer | 997 | 0 | 91 | R&S FSQ Operating Manual (1313.9681.12-02), chapter 6 |
| R&S FSL analyzer | 2256 | 0 | 119 | R&S FSL Operating Manual (1300.2519.12-12) |
| Tektronix scope | 2031 | 0 | 186 | Tektronix MDO4000C/MDO4000B/MSO4000B/DPO4000B/MDO3000 Programmer Manual |
| Keysight scope | 1793 | 0 | 142 | Keysight InfiniiVision 3000T X-Series Programmer's Guide (9018-07265) |
| R&S scope | 1449 | 0 | 85 | R&S RTB2000 User Manual (1333.1611.02 v09), plus the RTM3000 and RTA4000 manuals |
| R&S FSV analyzer | 1279 | 0 | 109 | R&S FSVA/FSV Operating Manual (1307.9331.12-17) |
| Oscilloscope | 1200 | 409 | 147 | Rigol MSO2000A/DS2000A Programming Guide (Feb 2016) |
| R&S FSIQ analyzer | 1051 | 0 | 0 | R&S FSIQ Operating Manual (1119.5063.12), chapter 6 — first extraction, no adoption pass |
| Siglent scope | 859 | 0 | 120 | Siglent SDS Series Programming Guide (EN11D) + SDS3000X HD (EN11F) |
| Rigol spectrum analyzer | 586 | 0 | 88 | Rigol DSA800 Series Programming Guide (Aug. 2016) |
| R&S spectrum analyzer | 537 | 0 | 99 | R&S FPC Spectrum Analyzer User Manual (1178.4130.02 ─ 08) |
| R&S power supply | 434 | 0 | 139 | R&S NGL200/NGM200 User Manual, plus the NGE100 and HMP guides |
| Waveform generator | 430 | 0 | 89 | Rigol DG1000Z Programming Guide |
| Keysight multimeter | 390 | 0 | 100 | Keysight Truevolt Series Operating and Service Guide |
| GW Instek GDS-1000B scope | 383 | 0 | 69 | GW Instek GDS-1000B Series Programming Manual (v1.10) |
| Keithley multimeter | 376 | 0 | 78 | Keithley DMM6500 Reference Manual (DMM6500-901-01 Rev. A) |
| Siglent generator | 343 | 27 | 42 | Siglent SDG Series Programming Guide (PG02_E05B) |
| Chroma electronic load | 339 | 0 | 31 | Chroma 63200A Series Operation & Programming Manual (Oct 2024) |
| Power supply | 309 | 0 | 65 | Rigol DP800 Programming Guide (Dec 2015) |
| Keithley SMU | 293 | 0 | 74 | Keithley Model 2450 SourceMeter Reference Manual (2450-901-01 Rev. E) |
| Electronic load | 292 | 0 | 29 | Siglent SDL1000X Programming Guide (E02B) |
| Chroma modular load | 286 | 0 | 30 | Chroma 63600 Series Operation & Programming Manual (V2.2) |
| Multimeter | 206 | 82 | 20 | Siglent SDM Series Programming Guide (EN02A) |
| Spectrum analyzer | 202 | 0 | 55 | Siglent SSA3000X Programming Guide (PG0703X-E03D) |
| Rigol multimeter | 186 | 0 | 28 | Rigol Programming Guide for DM3058/DM3058E (Jan. 2015) |
| Keysight power supply | 171 | 0 | 61 | Keysight E36300 Series Programming Guide (9018-04577) |
| GW Instek scope | 169 | 0 | 36 | GW Instek GDS-2000 Series Programming Manual |
| B&K triple-output supply | 144 | 0 | 58 | B&K Precision 9130B Series Programming Manual (V051415) — 22 entries flagged misprinted |
| Rigol electronic load | 144 | 0 | 59 | Rigol DL3000 Series Programming Guide (Apr. 2019) |
| B&K electronic load | 136 | 0 | 64 | B&K Precision 8600 Series Programming Manual |
| Fluke multimeter | 125 | 0 | 77 | Fluke 8845A/8846A Programmers Manual (Sept 2006), hand-transcribed |
| Chroma power supply | 121 | 0 | 51 | Chroma 62000L Series User Manual, Remote Control Reference |
| B&K power supply | 84 | 0 | 43 | B&K Precision 9200B Series User Manual, chapter 5 — 16 entries flagged misprinted |

The counts are generated from the catalogs rather than kept by hand — several rows had
drifted by one or two before that was noticed, which is exactly how much a hand-maintained
table can be wrong without anyone seeing it. `node tools/catalog-table.js` regenerates them
and `CatalogTableTests` fails if they are stale, because for a while "generated" meant
generated once: the numbers had been read out of the catalogs by hand and every edit since
drifted away from them.

**The two marks mean different things, and only one of them needs the bench.** A `✓` says
the command was sent to the real instrument and answered. A `•` says the same command was
found in an independent open-source driver — pymeasure, python-ivi, QCoDeS, tm_devices,
OriginalCircuit — which is weaker evidence, and free: it needs no hardware, only the driver
sources, and `tools/scpi-extract/README.md` gives the clone lines that produce the corpora.
Those corpora are not committed, and while they were absent every catalog built without
them silently gained no marks at all. Re-running the check against 7,581 driver commands
took the cross-checked count from 1,460 to 2,778, twenty catalogs gaining marks and twelve
of them going from none. The bench count did not move and cannot for now: all three
instruments here are unreachable, so `✓` is stuck at 518 until they are back.

One catalog corrects its guide. The Chroma manual prints `VOLTaget:PROTection` and
`PROTecton:TRIPped?` a handful of times while spelling both correctly everywhere else;
transcribing the slip faithfully would put commands in the catalog that the instrument
rejects, so the build config lists the corrections explicitly.

**Two catalogs do the opposite, deliberately.** Both B&K supply guides misprint commands in
places where nothing else in the document settles what was meant — a dropped letter in a
query whose set form is spelled correctly, an index and a described entry that disagree, a
subsystem written two ways, a node spelled three ways across two manuals from the same
vendor. Correcting those would be guessing, so the entry carries the syntax **exactly as
printed** and a `guideMisprint` note saying what the guide prints, why it looks wrong, and
what to try instead. The library shows those entries with a **⚠**. 22 entries in the 9130B
catalog carry one and 16 in the 9200B.

The treatments are not in tension: Chroma's slips are settled *by the same guide*, which
spells the commands correctly elsewhere, and most of B&K's are not settled by anything
short of the instrument. Where the guide answers the question, follow it — the 9200B manual
states in its own §5.1 that a command is separated from its parameter by a space, so the
three entries it prints without one are written with the space, and flagged as corrected.
Where the guide does not answer, say so rather than inventing an answer. Nothing may be
corrected silently under either.

**A wrapped line is not a command.** Guides break a long name across two lines —
`SEARCH:SEARCH<x>:TRIGger:A:BUS:B<x>:FLEXray:HEADER:` then `PAYLength?` — and a line-based
extractor reads the continuation as a command of its own. The catalog then ships
`PAYLength?`, which no instrument answers, and loses the query form of the command it came
from. Eight such entries were found in the Tektronix catalog, plus a ninth where the
fragment had overwritten a real root command's description. The truncated-line check does
not catch these: a bare `PAYLength?` has balanced brackets and a letter in its header, so
it is well-formed in every way except being real. A separate invariant now looks for a
single-node entry that repeats, word for word, the description of a longer entry ending in
the same mnemonic.

**A transcription can also be wrong where the guide is right**, and that failure is
quieter: the catalog still loads and the library still lists the entry. The 9200B's first,
machine-assisted pass paired eleven entries with a neighbour's description — `VOLTage:LIMIt`
explained as a query of the OVP state — dropped both `STEP` query forms and the whole
`VOLTage:TRIGgered` pair, and carried five commands only in the abbreviated spelling their
syntax lines use, which stops the full spelling in the guide's own headings from matching
(`ScpiSyntax` derives the short form from the template, so a template that is already short
admits nothing else). Tests now pin each class.

One catalog is **hand-transcribed rather than extracted**: the Fluke 8845A/8846A guide
is a set of indented tree tables whose description column pdftotext shifts by a row in
places — Table 12 pairs `:DIODe?` with "Make a frequency measurement" — so an automatic
pass produced confidently wrong entries and invented paths like `:CURRent:VOLTage`.
Those were discarded and the catalog read off the guide by eye instead. 77 of its 125
entries are corroborated by an independent driver.

One catalog carries commands the guide does not print verbatim: the R&S manual states
in its SCPI conventions that *"a query is defined for each setting command unless
explicitly specified otherwise"*, and spells the exception out per entry in a `Usage`
field (`Query only`, `Setting only`, `Event`). Query forms are generated from that rule
where `Usage` permits — applying a documented convention, not guessing.

Two levels of confidence, both weaker than the guide is authoritative:

- **`BenchVerified`** — the command was sent to the real instrument here and answered.
- **`CrossChecked`** — the same header was also found in an independent open-source
  instrument driver (pymeasure, python-ivi, QCoDeS, tm_devices, OriginalCircuit). This
  catches a command transcribed correctly from a guide that the hardware never honoured.

### How much of this is right

Everything above describes how a catalog is built and what is checked. None of it says how
much of the result is correct, and for a long time nothing did. Every count this project
produced answered *"how many entries match a pattern someone thought to look for"* — which
measures the search, not the data. Fixing one such pattern reliably exposed another, and
there was no way to tell a long tail from an endless one.

So it was measured, on 15 August 2026. `tools/sample-catalog-accuracy.js` draws entries
uniformly from all 23,978 — including the ones whose guide is not in this checkout, which
are judged unverifiable rather than dropped, since dropping them would quietly measure the
best-documented half. The draw is seeded, so the same sample can be redrawn and rechecked.
100 entries, each read against its guide:

> **7 defective. A 7% error rate, 95% Wilson interval 3.4% to 13.7%** — between 800 and
> 3,300 entries across the catalogs, most likely about 1,700.

83 of the 100 had guide text to compare against; the other 17 were judged on internal
evidence, so if any of those are wrong the true rate is higher, not lower.

The seven were: two with a margin label spliced into the sentence, one describing a
different command outright, one opening on a stray digit, one promising three values and
giving one, one trailing off mid-clause, one missing a space.

Two of the seven turned out to belong to classes nobody had counted — 323 Rigol
descriptions opening on the tail of the guide's "Description 1", and 89 Keysight
descriptions carrying a left-margin label inline. Both were repaired the same day (421
entries with the strays they exposed) and are now held by
`No_description_carries_the_guide_page_furniture`. Neither had ever surfaced from reading
the catalogs directly; both fell out of a hundred random entries within an hour.

The rate above is therefore stale by construction — it was measured before that repair, and
those 421 entries are about a quarter of what it projected. It is left as measured rather
than adjusted by arithmetic. **Redraw the sample with a new seed before quoting a number.**

The distribution matters more than the headline. The R&S catalogs, roughly half of all
entries, gave 1 defect in 28 sampled; the Rigol and Keysight scopes gave 4 in 12. Remaining
work is concentrated, not spread.

What is known to be left, as distinct from what the sample estimates:

| | |
|---|---|
| 347 | descriptions cut at a character count that `tools/finish-capped-descriptions.js` declined to finish — 235 where the guide does not carry the words they stop on, 66 with no heading found, 44 that were already two descriptions merged before the cut, 2 where the guide says no more. The 44 are wrong rather than short. |
| 47 | descriptions shared between commands with different roots. Sampled and read as correct — a guide really does give `*LRN?` and `SET?` one sentence — but not audited one by one. |
| 13 | catalogs never audited for mispaired descriptions. Every one audited so far (Tektronix, the Keysight DMM, the R&S scope) found some. |
| 4 | B&K 9130B entries whose guide misspells `INSTrument` as `INSTument` in both its index and its body. Only a 9130B can say whether the firmware accepts the misspelling, and there is none on this bench. |

None of these blocks anything. They are recorded so the next person does not have to
rediscover them, and so that "what else is left" has an answer that is not a search.

**Never invent SCPI.** If a command cannot be transcribed from the guide, leave it out.
This is enforced, not merely stated: `CatalogCoverageTests` fails the build if any
quick-command button, live-readout query or bundled script line is not an instance of a
syntax template in that family's catalog. `ScpiSyntax` does the matching, and understands
short/long mnemonic forms, optional nodes, bracketed roots, channel suffixes and inline
alternation — so `:OUTPut2 OFF` is recognised as a use of `:OUTPut[<n>][:STATe] {ON|1|OFF|0}`.

The guides themselves:

- Rigol MSO2000A/DS2000A Programming Guide —
  <https://www.batronix.com/files/Rigol/Oszilloskope/_DS&MSO2000A/MSO2000A_DS2000A_ProgrammingGuide_EN.pdf>
- Rigol DG1000Z Programming Guide —
  <https://www.batronix.com/pdf/Rigol/ProgrammingGuide/DG1000Z_ProgrammingGuide_EN.pdf>
- Rigol DP800 Programming Guide —
  <https://www.batronix.com/pdf/Rigol/ProgrammingGuide/DP800_ProgrammingGuide_EN.pdf>
- Siglent SDL1000X Programming Guide —
  <https://www.batronix.com/files/Siglent/Elektronische-Last/SDL1000X/SDL1000X-Programming_Guide.pdf>
- Siglent SSA3000X Programming Guide —
  <https://siglentna.com/wp-content/uploads/dlm_uploads/2017/10/SSA3000X_ProgrammingGuide_PG0703X_E03D.pdf>
- Siglent SDG Series Programming Guide —
  <https://siglentna.com/USA_website_2014/Documents/Program_Material/SDG_ProgrammingGuide_PG_E03B.pdf>
- Siglent SDM Series Programming Guide — <https://siglentna.com/download/2563/>
- Tektronix MDO4000C/MDO4000B/MDO4000/MSO4000B/DPO4000B/MDO3000 Programmer Manual —
  <https://download.tek.com/manual/MDO4000-MSO4000B-and-DPO4000B-Oscilloscope-Programmer-Manual.pdf>
- Tektronix MSO4000/DPO4000 Programmer Manual (077-0248-01) —
  <https://download.tek.com/manual/077024801web.pdf>
- Keysight InfiniiVision 3000T X-Series Programmer's Guide —
  <https://www.keysight.com/us/en/assets/9018-07265/programming-guides/9018-07265.pdf>
- Keysight E36300 Series Programming Guide —
  <https://www.keysight.com/us/en/assets/9018-04577/programming-guides/9018-04577.pdf>
- Keithley Model 2450 SourceMeter Reference Manual —
  <https://res.cloudinary.com/iwh/image/upload/assets/1/7/model_2450_sourcemeter_instrument_reference_manual.pdf>
- Keithley DMM6500 Reference Manual —
  <https://www.tehencom.com/Companies/Keithley/DMM6500/Keithley_DMM6500_Reference_Manual.pdf>
- R&S RTB2000 Digital Oscilloscope User Manual —
  <https://www.rohde-schwarz.com/manual/rtb2000/>
- R&S NGL200/NGM200 Power Supply User Manual —
  <https://www.batronix.com/files/Rohde-&-Schwarz/Power-Supplies/NGL/NGL200_UserManual_en.pdf>
- R&S NGE100 Power Supply User Manual —
  <https://www.batronix.com/files/Rohde-&-Schwarz/Power-Supplies/NGE/NGE100_User_Manual_en.pdf>
- R&S HMP Series SCPI Programmers Manual —
  <https://www.batronix.com/files/Rohde-&-Schwarz/Power-Supplies/HMP/HMP_SCPI_ProgrammersManual_en.pdf>
- Siglent SDS Series Programming Guide (EN11D) —
  <https://siglentna.com/wp-content/uploads/dlm_uploads/2023/04/SDS-Series_ProgrammingGuide_EN11D.pdf>
- Siglent SDS3000X HD Programming Guide (EN11F) —
  <https://assets.testequity.com/te1/Documents/pdf/siglent/SDS3000XHD_Series_ProgrammingGuide_EN11F_0125.pdf>
- GW Instek GDS-2000 Series Programming Manual —
  <https://www.gwinstek.com/en-global/products/downloadSeriesDownNew/11848/1018>
- Fluke 8845A/8846A Programmers Manual —
  <https://assets.fluke.com/manuals/8845a___pmeng0100.pdf>
- B&K Precision 8600 Series Programming Manual —
  <https://bkpmedia.s3.amazonaws.com/downloads/programming_manuals/en-us/8600_Series_programming_manual.pdf>
- B&K Precision 9130B Series Programming Manual —
  <https://bkpmedia.s3.us-west-1.amazonaws.com/downloads/programming_manuals/en-us/9130B_Series_programming_manual.pdf>
- Chroma 62000L Series User Manual —
  <https://assets.testequity.com/te1/Documents/pdf/62000L-um.pdf>

Two more turned up later. The FSIQ's has since been catalogued — a first extraction, its
caveats recorded in the catalog's own `source` field — and the FSE's cannot be: the copy is
Volume 1, and chapter 6, the command reference, is in Volume 2:

- R&S FSEA/FSEB/FSEM/FSEK Operating Manual, Volume 1 (1065.6016.12) —
  <https://sky-brokers.com/wp-content/uploads/2020/10/Operations-Manual-Rohde-Schwarz-Spectrum-Analyzer-FSEM30.pdf>
- R&S FSIQ Operating Manual (1119.5063.12) —
  <https://hamwan.org/Labs/Radio%20Analysis/fsiq_02e.pdf>
  (also archived: <https://web.archive.org/web/20230925101545/http://hamwan.org/Labs/Radio%20Analysis/fsiq_02e.pdf>)

And one was found and **cannot be used**, which is worth recording so nobody hunts it twice:

- Chroma 63800 Series Operation & Programming Manual, Version 1.1, April 2009 —
  <https://web.archive.org/web/20250805042045id_/https://www.transcat.com/media/pdf/Chroma-63800-Series-User-Manual.pdf>

  The live Transcat copy now answers 403 and no other mirror is known; the Wayback capture
  of 5 August 2025 serves the original bytes intact. It is the right document, and it is a
  **scan**: 44 MB of page images with no text layer, from which `pdftotext` recovers 550
  bytes — the cover, and nothing else. Extraction has nothing to read. Making it usable
  means OCR, and OCR of a 2009 scan produces command spellings that would have to be
  reviewed character by character against the page images before any of it could be
  trusted, which is a different undertaking from transcription and is not planned. The
  `638` AC loads therefore stay routed to `Generic` (§8, row 16) — by document, not by
  oversight.

`pdftotext -layout` makes them far cheaper to work from than reading page images.
The extraction pipeline built on that is in
[tools/scpi-extract](../tools/scpi-extract), and each catalog's own `source` field records
which guide it came from, what that guide covers, and what was deliberately left out.

**How far a catalog can be rebuilt from its guide.** Each config records its `parse` recipe
— manual, reader style, any flags — and `node rebuild.js --all` runs the lot and reports
where each one stands. Before that the recipe was written down nowhere: a config named the
extracted files it was built from, but those live in `parsed/`, which is regenerated and not
committed, so the step that produces them survived only in whoever last ran it. Eleven
catalogs were listed as unrebuildable for having no config, which was true and beside the
point — none of the other twenty-four could be rebuilt either.

As measured on 14 August 2026, after fetching twelve guides that were missing: **1 rebuilds
byte-identically**, **21 rebuild but differ**, and **2 cannot be attempted** — the Keysight
scope because its guide no longer serves as a file, and Fluke because it is curated by hand
and extracts nothing. Before those guides were fetched only twelve of the twenty-four could
be attempted at all.

**"Differs" is the expected state, not a defect to close.** A catalog is extraction *plus*
curation, and the curation is load-bearing: query forms the guide states in prose rather than
prints, entries transcribed by hand where the layout defeats the reader, and junk removed
after reading the page it came from. Re-emitting throws all of that away and keeps only what
a parser can see.

Measured, rather than assumed. A wholesale re-emit today would **gain 153 entries and lose
129**, and the losses are real commands: `*CAL?` and `*IST?` from the R&S scope, `ALIas?`
and `CH<x>?` from the Tektronix, `:ERRor?` and `:VERsion?` from the B&K load, the whole
`:HARDcopy` tree from the GW Instek, `READ?` and both `MEASure` queries from the R&S supply.
Meanwhile the "gains" include `*PSC {OFF|ON|NR1>}` with a bracket missing,
`CH<x>:PRObe:MODel<String>` with its space eaten, and — in the supply catalog — `RUN` and
`RUN?`, the parameter values deleted by hand earlier the same day.

So the pipeline is for *building* a catalog and for checking what a guide contains, not for
regenerating one that people have since worked on. `node rebuild.js --all` reports where each
stands; a catalog that has drifted is telling you curation happened, and the way to harvest a
genuine addition from a re-read is to adopt it deliberately, entry by entry, not to overwrite
the file. A missing dump is likewise not usually a defect: `manuals/` is ignored for the same
reason `datasheets/` is, and every guide is named and linked in the list above.

## 11. Capture

**Screen.** For instruments with a known screen-dump command, query it as an IEEE 488.2
binary block and show the image in a viewer that can save it. The transfer is given
headroom over the user's timeout (at least 15 s) — a full-screen BMP is around 1 MB — and
the timeout is restored afterwards. Most vendors take the image format as a parameter of
the query (`:PRINt? BMP`); Tektronix takes it as separate state, so a profile may also
carry setup commands to send first.

Offered for Rigol (`:DISPlay:DATA?`), Keysight (`:DISPlay:DATA? PNG,COLor`), Tektronix
(`SAVe:IMAGe:FILEFormat PNG` then `HARDCopy STARt`), R&S (`HCOPy:DATA?`) and Siglent
(`:PRINt? BMP`).

**Waveform.** Read channel 1, decode, plot, and offer CSV. At least 10 s of headroom.

Five dialects, because no two vendors agree on the command tree, the way the scaling is
described, or the arithmetic that turns a stored sample into volts. Each is transcribed
from that vendor's guide and named in `WaveformDialect`; the sequences live in
`Core/Capture/WaveformReader` so they can be exercised against a fake client.

| Dialect | Reads | Scaling |
|---|---|---|
| `Rigol` | `:WAVeform:DATA?` in `BYTE` | 10-field text preamble; `v = (raw - yref - yorig) * yinc` |
| `Keysight` | `:WAVeform:DATA?` in `BYTE` | the same 10 fields; `v = ((raw - yref) * yinc) + yorig` |
| `Tektronix` | `CURVe?` | `WFMOutpre` field by field; `v = ((raw - YOFf) * YMUlt) + YZEro` |
| `RohdeAscii` | `CHANnel<m>:DATA?` in ASCII | none — the instrument returns volts |
| `Siglent` | `:WAVeform:DATA?` | binary descriptor; `v = code * (vdiv / code_per_div) - voffset` |

Rigol and Keysight are the trap. Rigol modelled its command set on Agilent's, so the
preamble is field-for-field identical and the decoder looks reusable. It is not: the two
formulae agree exactly on a trace centred at zero and differ by the offset everywhere
else, and both draw something that looks like a measurement. A test exists whose only job
is to fail if the two ever return the same number.

A preamble that cannot carry the scaling — fewer than 10 fields, a descriptor too short,
zero codes per division — is an error, reported as one.

**Where capture is refused.** A GW Instek GDS-2000 offers neither, and in both cases
because its manual documents no way rather than because the work is outstanding. `:COPY`
writes the screen to a flash disk or a printer, never back over the wire.
`:ACQuire<X>:MEMory?` does return samples, and the framing is precise — eight header
bytes, the sample interval as a little-endian float, then two bytes a point MSB first —
but the manual never states how a stored code becomes a voltage. That needs a constant
nobody wrote down. A trace drawn against a guessed vertical scale is wrong by an unknown
factor and looks entirely convincing, which is the same reason §10 refuses to invent SCPI.

**Live readout.** Instruments that answer one measurement per query — a multimeter, a
power supply or an electronic load, but not a scope, which already plots against time
itself — offer a window that polls one function on a user-set interval (100 ms to 60 s,
default 1000 ms) and plots the values against time, with the current value shown large.
The profile decides which functions are offered (`ReadoutFunctions`); an instrument with
none does not get the window. A supply offers per-channel volts, amps and watts; a load
adds resistance.

- Polling **holds the link**, so it marks the session busy exactly as a running script does
  and the console locks itself out (§7).
- The series is **bounded** — a meter polled all afternoon must not grow without limit — and
  says so when it is dropping the oldest readings.
- Changing the measured function **clears the plot**: two quantities on one axis mean nothing.
- Readings are parsed **invariantly**. On a machine whose locale uses a decimal comma,
  culture-sensitive parsing would read `1.25` as `125` and silently corrupt every value.

## 11a. Browsing the catalogs

**Tools ▸ Command Library** opens every bundled catalog for reading, whether or not
anything is connected. Manufacturers on the left, that maker's instruments beneath them,
the selected catalog's commands on the right, and one filter box matching maker, model,
instrument or command text.

Each catalog names the guide it was transcribed from, and the panel offers to open the
vendor's page for it. It will also open a **local copy**, if the user has pointed the app
at a folder of downloaded PDFs — a per-user setting, off until set. The guides themselves
are never bundled: they are the vendors' copyright, they are revised independently of this
app, and a stale bundled copy is wrong in a way that is hard to notice.

This window shows **only the curated catalogs**. Anything a model extracted (§11b) is the
user's own material and belongs to the instrument it was extracted for, not to a library of
things the app vouches for.

## 11b. AI datasheet extraction

For an instrument no bundled catalog covers, the user may connect their own AI provider and
have it read a datasheet — PDF, `.docx` or plain text. This is optional, off until
configured, and there is no built-in key: the user brings their own.

**The key.** Stored encrypted with DPAPI scoped to the current Windows user, so a
`settings.json` that is copied, synced or backed up elsewhere carries nothing usable. It is
never written in the clear and never logged. A settings file from another machine decrypts
to nothing, which is treated as "no key set" rather than as an error.

**What is sent.** Only the document the user chose, to the provider the user chose. Where a
provider accepts a PDF directly that is preferred, because flattening a two-column
programming guide to text interleaves its columns — which is how a command ends up with its
neighbour's description. Providers without file support get text extracted locally instead,
and the checkbox that controls this explains the cost either way.

**Refused before sending, not after.** Provider limits are checked against the file first —
Gemini 50 MB and 1000 pages, Anthropic 600 pages and 32 MB of *request payload*, which
base64 inflates by a third, so a 25 MB PDF fails a 32 MB cap. An upload that cannot succeed
is refused with the reason and the actual numbers, not with the provider's error an upload
later.

**Nothing is trusted on the way out.** Every candidate command is checked for the shape of
SCPI before the user sees it (`ScpiSyntax.IsValidTemplate`), which is what rejects a model
returning a sentence where a command was asked for. What survives is shown for review and
saved only if the user accepts it.

**Kept apart.** Extracted commands never join a curated catalog. They are stored per
instrument, keyed on the model rather than the address — an instrument on DHCP moves, and
its commands should follow the instrument, not the lease — and marked `◆` everywhere they
appear. A catalog in this repository means someone transcribed a vendor's guide (§10); that
guarantee is worth keeping distinct from a machine's best guess, however good.

## 11c. AI script writing

Both editors — the per-instrument one (§9) and the sequence one (§9a) — have a **Write with
AI…** button. The user describes a measurement in plain English and gets a script back. It
uses the same connection and the same stored key as §11b, and is equally optional.

**What is sent.** The request, the command catalogs of the instruments involved, the script
language, and — when the user leaves the boxes ticked — the script currently in the editor
and the tail of the last run's output. The output is what makes *"it failed with -113, fix
it"* answerable: the failure exists only in that log.

**Given the catalog, not asked to remember one.** A model writing SCPI unaided reaches for
whatever dialect it has seen most, which is how `:SOURce1:FREQuency 1000` gets sent to a
Siglent generator that has never heard of it. So the commands go in the prompt and the model
is told to use nothing else. A large catalog is trimmed to the commands the request is about
— the R&S FSL alone is 2,270, and a model given all of them chooses worse than one given the
plausible few, quite apart from the cost.

**Checked on the way back.** Every command header in the returned script is matched against
the catalog of the instrument that line addresses, which for a sequence means a different
catalog per alias. Anything unmatched is listed under the draft. The check is a *header*
check: a documented header carrying a wrong argument passes it, and the window says so rather
than letting silence read as "verified".

**Never run, never saved.** What comes back is a draft in a preview pane. It reaches the
editor when the user presses Use, and runs when the user presses Run — two deliberate acts,
because these scripts turn on outputs and apply voltages to real circuits. Nothing generated
is written to a catalog; §11b's separation holds here too.

## 12. Files and formats

| Output | Format |
|---|---|
| Scan results | CSV, RFC 4180, header `IP Address,Port,Protocol,Identity`, CRLF, fields quoted only when needed |
| Console log | Plain text, exactly as displayed |
| Waveform | CSV, header `Time (s),Voltage (V)`, `g9` invariant-culture numbers |
| Settings | JSON at `%AppData%\LabEquipmentController\settings.json` |

Settings persist the last interface address, port list, communication timeout, and the
window size and maximized state. A missing, empty or corrupt settings file must yield
defaults — persisted preferences must never block startup, and a failed write must never
block shutdown.

Open tabs are **not** persisted: the instruments are on DHCP and their addresses move, so
reconnecting on launch would be guesswork (and risks §13).

## 13. Transports and instrument-specific behaviour

`IInstrumentClient` is the transport abstraction — connect, send, query, query-binary,
return-to-local, close — with three implementations. The UI must not care which is beneath
it, and neither must the sessions, the script runners, the capture path or the catalogs.

**Raw socket.** TCP; commands terminated with a newline; replies read to the terminator.

**VXI-11.** A hand-written ONC-RPC client: portmapper `GETPORT` to find the core channel,
then `create_link`, `device_write`, `device_read`, `device_clear`, `device_local`,
`destroy_link`. XDR encoding, big-endian, 4-byte record marking.

**Serial (RS-232).** A serial port opened at the line settings the address named (§5),
`System.IO.Ports` underneath, so `COM3` on Windows and `/dev/ttyUSB0` or
`/dev/cu.usbserial-*` elsewhere all work — including the USB-to-serial adapter most benches
actually use. DTR is asserted on open, and RTS too unless hardware flow control is driving
it; a good many instruments and nearly every adapter want to see them before they will
talk. Four things differ from the LAN transports and nothing else does:

- **The line settings have to be right first.** There is nothing on the wire to negotiate
  them with, and a wrong baud rate does not fail — it answers with rubbish.
- **The terminator is not always LF.** A good many RS-232 instruments want CRLF and treat a
  bare LF as no command at all, which looks exactly like a dead port. Replies are read to
  the line feed either way, with carriage returns discarded.
- **Nothing happens when the port closes.** A raw socket returns the front panel to local
  by being closed; RS-232 has no such signal, so the request is made in band with
  `SYSTem:LOCal` — SCPI-99's own answer for an interface with no bus-level Go To Local, and
  documented in 12 of the 36 catalogs. Best-effort: on an instrument that does not have it,
  it costs one entry in an error queue that is about to be disconnected from.
- **A binary read has no total deadline**, only a gap-between-bytes one, which the driver
  enforces. That is the better question to ask of a transport this slow: 100 KB of
  screenshot at 9600 baud is a minute and three quarters of perfectly healthy transfer, and
  no fixed total that allowed for it would still catch an instrument that had stopped
  talking. A socket uses a total deadline only because an async read on a `NetworkStream`
  ignores its own read timeout.

**Discovery extends to it only as far as the physics allows** (§4, §17). Ports are *listed*
without being opened — the CLI's `lec ports`, `SerialPorts.Names()` — and that listing is
what arriving at the card gives you. Opening them is a separate act on a separate button:
`SerialScanner.ScanAsync()` tries the baud rates it was given against the ports it was
given, and nothing else. A subnet sweep can ask every address the same harmless question;
a serial port cannot be asked anything until the settings already match, and what is on the
other end may be a printer or a modem.

**IEEE 488.2 blocks.** `#<n><length><data>` definite-length blocks are parsed and the
payload returned with the header and any trailing newline stripped. A response that is not
a block is returned whole. The framing — writing a command, reading a line, reading a block
— is **one implementation** (`ScpiFraming`), shared by the socket and the serial port,
because SCPI over RS-232 is line-based in exactly the way SCPI over a raw socket is. It is
where the careful parts live and they are worth having once: a block is read by its
declared length rather than until a pause, since binary data contains newlines and arrives
in pieces.

### Hard-won facts the software must respect

**Rigol DS2202 oscilloscope**

- Its **raw socket (port 5555) is broken**: every query returns the *previous* query's
  answer, permanently, across reconnects. A known DS2000 firmware bug. Hence: never send
  `*IDN?` on a raw probe, and prefer VXI-11 whenever the instrument offers both.
- **VXI-11 framing quirk**: it sends empty, non-END `device_read` packets *before* its data
  **and part-way through a large transfer**, while it produces more. A read loop must
  continue until the END flag; it must not stop on the first empty read, nor on a count of
  consecutive empty reads. Bound the wait on **elapsed idle time** instead. (The Siglents
  never do this, which masked the bug for a long time.)
  - Measured: giving up after 10 empties truncated a screen dump at exactly 64 KB —
    `Block claims 1152054 bytes but only 65525 remain`. Waiting on the clock instead
    returns the full **1,152,054 bytes in ~10 s**. A whole screen dump is ~1.15 MB, so the
    read must tolerate a transfer running for many seconds.
- Issue **`device_clear`** on connect to flush a stale output queue.
- **A poisoned output queue survives one `device_clear`.** If a large binary transfer is
  aborted (app killed mid-`:DISPlay:DATA?`), the leftover payload is returned as the answer
  to the *next* query — `*IDN?` comes back as thousands of BMP padding bytes, or as a bare
  `#9000001400` block header. Reconnecting once more clears it; the instrument does not
  need a power cycle for this. Do not confuse it with the wedge below.
- **One TCP session only.** Rapid or parallel connections wedge the firmware — no ping, ARP
  entry goes `Incomplete`, front panel dead, though the trace keeps running. Only a
  power-cycle recovers it. **This includes automated test loops**: repeated
  connect/disconnect cycles from a test harness will wedge it just as surely as a user
  double-clicking. Space them out, and stop at the first sign of trouble.

**Siglent SDG2042X generator**

- **VXI-11 only** — no raw SCPI socket at all. Port 23 is a BusyBox root shell, not SCPI.
- Uses Siglent's own dialect: `C1:BSWV WVTP,SINE`, `C1:OUTP ON`. Not standard SCPI.

**Siglent SDM3065X multimeter**

- **Standard SCPI** (`MEASure:VOLTage:DC?`) despite sharing a maker with the SDG. This is
  why family classification checks multimeters before generators (§8).

Instruments are on **DHCP and their addresses move**. Match on the `*IDN?` model; never
hardcode an IP.

## 14. UI conventions

Rules for building a UI in WinForms at all. Which controls exist, in what order, with what
words on them — for this build and for the web one — is [UI-SPEC.md](UI-SPEC.md).

- **Every control gets a hover tooltip** describing what it does. The labels alone don't
  explain the controls. This is a standing requirement, not a one-off — anything added
  gets an entry in the owning window's `SetTooltips()`.
- **Rows share one height.** WinForms gives ComboBox, TextBox and Button different heights
  under `AutoScaleMode.Font`, so heights are normalised in code. A button added to an
  existing row must be pinned there too, or it visibly stands taller.
- **Never hardcode pixel sizes for controls created at runtime** — they miss the form's
  auto-scale pass. Copy metrics from an existing designer control, or use dock/auto-size
  layout and normalise afterwards. This includes **the height of a button bar**: fixed
  values clipped the buttons in both capture windows (one had already been nudged 38 → 54
  and clipped again), so a bar either auto-sizes or takes its height from
  `button.PreferredSize.Height` plus its own padding, in `OnLoad`.
- A **docked Label with `AutoSize` left on ignores a Height set in code**, and clips its own
  text. Turn AutoSize off for any label whose size the layout controls.
- **Every button in the app is built and sized through `ButtonStyle`** — one padding, one
  margin, one nominal 16-logical-pixel glyph size, one minimum width, and one height derived
  from the font rather than from whichever control a given window happened to measure. Windows that
  each measured their own tallest control drifted apart: the main window shipped 23, 20 and
  23 px buttons with 13, 12 and 15 px glyphs and one lone `FlatStyle.Flat`, while the console
  used three heights in one control and the script editor took its combo box's. Do not
  hand-set a button's `Padding`, `Height` or glyph size; add to `ButtonStyle` instead.
  - **Glyphs are corrected optically, not numerically** (`ButtonStyle.Optical`). The bundled
    artwork does not share a common margin: the play triangle and stop square are solid shapes
    drawn edge to edge in their canvas, while the file and magnifier glyphs are thin outlines
    inset from theirs. At one nominal size the solid ones read as roughly twice the weight —
    on the console's Run/Stop/Single row they looked like blocks. New artwork that fills its
    canvas needs an entry in that table; judge it from a glyph sheet rendered at the real
    16 px, not from the numbers.
  - **A `NumericUpDown` cannot be grown.** `UpDownBase.SetBoundsCore` clamps it to a
    font-driven height, `MinimumSize` and all. It — and any label — is centred on the row
    with `ButtonStyle.CentreInRow` instead, because a FlowLayoutPanel hangs children from the
    top of the row.
  - **A `DropDownList` ComboBox can**, but only owner-drawn, through `ItemHeight`
    (`ButtonStyle.MatchHeight`). Paint it with `ButtonStyle.DrawComboItem`: WinForms hands the
    handler the *selection* colours for the closed box whenever the combo has focus, so
    drawing what it gives you puts a blue bar across the control as soon as it is tabbed into.
  - **A single-line `TextBox` cannot either.** Anchor it `Left|Right` with no vertical anchor
    and a TableLayoutPanel will centre it in the cell rather than pin it to the top.
- Adding a glyph to a button **widens it**, so a wrapping button bar that fitted on one row
  may quietly fold onto two. Check the window at its default size after adding one, and pay
  for the row in horizontal padding before paying for it in window width.
- A button that **toggles its own label** (Start/Stop) pins its width to the wider of the two
  states, measured through `PreferredSize`. Left to auto-size it changes width on every
  press and shunts the buttons beside it sideways.
- Glyphs with no bundled artwork are **drawn at runtime** (`AppIcons.Drawn`). They render at
  roughly 16 device pixels, so keep them to one idea — an early detach icon drew a window
  *and* an arrow *and* an arrowhead, and read as a smudge.
- Long, variable text (instrument identities) is clipped with an ellipsis on one line, not
  wrapped, so panel heights don't shift with the window width. The full text stays
  available in a tooltip.
- The console log is a dark, fixed-pitch surface; colour carries meaning (§7).
- Cosmetic failures — a missing glyph, an unreadable icon — must never break or block the
  UI.
- `MainForm.Designer.cs` is **hand-edited**. It still opens in the Visual Studio designer, but
  most of the layout arithmetic lives in `MainForm.cs`, and the designer will not round-trip
  the comments. Edit it as source.
- Don't assume a fixed display scale. This bench has been seen at both 120 and 168
  `DeviceDpi` within one session (a Hyper-V guest's DPI follows its viewer window), and the
  form's `MinimumSize` scales with it — a window asked for below that minimum is silently
  clamped, which reads as a cramped layout rather than as a sizing failure.

## 15. Non-functional requirements

- **The UI thread never blocks.** Scans, connections and transfers are asynchronous;
  long synchronous ramp-up is pushed to a worker. The window stays responsive and Stop is
  handled promptly during a full-subnet scan.
- **Teardown is best effort and bounded.** An instrument that has been switched off must
  not stop a session closing or hang application exit.
- **Failures are reported, not swallowed silently, and never fatal.** A failed command, a
  corrupt catalog, an unreachable host: each degrades locally.
- **No telemetry, no network access beyond the instruments** on the selected subnet.

## 16. Quality bar

There is no CI. The standard applied to changes:

1. **Build and `dotnet test`** for anything touching `Core`. All logic that can live
   UI-free does, so that it can be tested against a fake instrument client.
2. **Screenshot the running app** for UI changes — a UI change is not verified from code
   alone.
3. **State plainly what was not verified.** Most features have never run against the real
   instruments (they are often powered off, or the bench network is unreachable). That is
   always said rather than glossed over.

Do **not** drive the physical mouse (`SetCursorPos`) while testing — the user is normally
working on the same machine.

Traps that have cost real time, all worth knowing before writing another harness:

- **Drive the app from inside `Application.Run`, never `Show()` + an `Application.DoEvents`
  pump.** Without the real message loop the WinForms `SynchronizationContext` does not
  govern `await` continuations, so the code after `await client.ConnectAsync()` in
  `ConnectSelectedAsync` resumes on a **thread-pool thread** — and the `InstrumentConsole`
  created there belongs to that thread. Reparenting it later throws *"Controls created on
  one thread cannot be parented to a control on a different thread"*, which surfaces from
  `Controls.Remove` as a bare `ERROR_INVALID_WINDOW_HANDLE`. This masqueraded as a detach
  bug across several bench runs and is entirely an artefact of the harness. Installing the
  context by hand is **not** enough; run the script from `Form.Shown` with real `await`s.
  Check it directly: a console's `InvokeRequired` must be `false` from the UI thread.
- **A UI harness that pumps `Application.DoEvents()` deadlocks on any `MessageBox`.** When
  an operation fails, the app shows a modal dialog; the dialog's own message loop starts
  *inside* the `DoEvents` call and never returns. Every later reflection-driven call then
  runs in a modal-blocked, re-entrant state, where `Controls.Remove` fails with
  `Error creating window handle` and screenshots capture stale paint. The symptoms look
  like deep WinForms bugs and are nothing of the sort. Detect it: enumerate thread windows
  for class `#32770`, and dismiss from a **separate thread** — the UI thread is blocked.
- **A DPI-unaware harness gets virtualized window coordinates.** `GetWindowRect` from a
  process that has not called `SetProcessDpiAwarenessContext` reports a 1563 px window as 909
  on this bench's 168 DPI display, so a capture sized from it comes out clipped at the right
  edge — with exactly the newest controls missing. Declare per-monitor-v2 awareness first.
- **`GetWindowText` is not a reliable read-back across processes.** It returns the window's
  own text, not the contents of another process's edit control — it happened to answer for
  the address box and came back empty for the console's command box, which *did* contain the
  text. Verify what a control holds from a screenshot, not from that call.
- **Click buttons with `PostMessage`, not `SendMessage`.** A synchronous click blocks the
  harness for as long as the handler runs, and forever if the handler opens a modal dialog.
- **Screenshot with `Graphics.CopyFromScreen`, not `Control.DrawToBitmap`.** DrawToBitmap
  paints children in `Controls` order, which is front-to-back, so a `Dock = Fill` control
  added *after* its docked edges is painted **last** and covers them. It rendered
  `CommandReferenceForm` with no filter row and no button strip — a window that is perfectly
  fine on screen. A harness that lies in this direction is worse than no harness.
- **Prefer probing at the `Core` level.** A console harness against `Vxi11Client` reports a
  failure as an exception with a real message, instead of a dialog nobody can see. That is
  how the 64 KB truncation above was finally pinned down.
- Never truncate the pipeline of a process talking to an instrument (`... | Select-Object
  -First n` in PowerShell kills it), because the process dies mid-transfer and leaves the
  instrument's output queue poisoned.
- **`Form.Close()` reports `CloseReason.None`, not `UserClosing`.** A detached window only
  hands its console back on `UserClosing`, so programmatic closes (the Re-attach button,
  app shutdown) don't fight the teardown. To exercise the window's X button from a test,
  send `WM_SYSCOMMAND` / `SC_CLOSE`.
- PowerShell does not wait for a GUI process launched with `&` unless you pipe its output
  (`& app.exe | Out-Null`), so a harness's results get read before it has written them.
- **The app holds `LabEquipmentController.Core.dll` while it runs**, so a build fails with
  MSB3021/MSB3027 naming the locking process. That is a running app, not a code error;
  `Core` and `Tests` still build, which is why a green test run can sit next to a failed
  solution build.
- **A label does not tell you when its text does not fit.** It clips nothing and scrolls
  nothing — the line is simply not drawn. A panel sized for three lines of guide text read
  "No local copy — expected:" and then named nothing, for every guide whose title wrapped.
  Nothing about that is visible from the code, and a screenshot showed it immediately.
- **`sed -i` rewrites a file with LF endings.** In a CRLF repo with `core.autocrlf=true`
  git normalises on commit, so the diff looks clean while the working tree is left mixed —
  the same trap as writing LF into a CRLF JSON catalog. Re-check out anything sed touched.

Beyond the harness, two failure modes this codebase keeps meeting are worth stating once:

- **Scaling arithmetic fails by drawing, not by throwing.** A waveform decoder given the
  wrong formula returns a plausible trace at the wrong offset. Rigol and Keysight share a
  preamble layout and not a formula, which makes reusing one decoder for both an easy and
  invisible mistake; there is a test whose only job is to fail if they ever agree.
- **A settings write that succeeds is silent.** A window that saves a whole settings object
  built from its own controls silently reverts every field it has no control for. That cost
  the AI connection and its key on every app close, and nothing reported it. Load, change
  what you own, save.

## 17. Open items

Everything specified above is implemented. Confirmed against the bench: discovery, connect,
identity, per-family profiles, three simultaneous console tabs, read-only queries, screen
capture (1.15 MB BMP), waveform capture (1400 samples), detach into a window, re-attach by
closing it, and disconnecting each console back down to none.

**585 of the 11,853 entries then catalogued answered on the bench** in that run, from a sweep of every
query that can be sent without changing an instrument's state — 388 of the Rigol scope's,
104 of the Siglent generator's, 82 of the multimeter's. What each refused is coherent: the
scope's misses are CAN triggering and bus decode, options a base DS2202 does not carry, and
the meter's are the two scanner-card commands that need an -SC model. The suite that does
this is [Tests/Bench](../Tests/Bench), off unless asked for.

Implemented but never run against the instrument it is for:

- **Four of the five waveform dialects** (§11) and the Tektronix screen dump. Only the Rigol
  path has met hardware. The decoders are checked against each vendor's own published
  worked example, which is a different claim from working.
- **Thirty-three of the thirty-six catalogs** (§10). Transcribed and cross-checked, not
  proven. Three catalogs have hardware here; there is no such instrument for the rest.

**AI datasheet extraction** (§11b) has run end to end against Gemini on the Siglent SDM
guide, returning 211 commands where the catalog transcribed from the same document by hand
holds 206, with 89% recognised. Every one of the unrecognised was checked against the guide
text and appears in it verbatim: nothing was invented. That is a measurement on a guide whose
answer was already known, which is what makes it worth anything — it says the pipeline works,
not that an extraction from an unknown guide can be trusted without the review step.

That distinction is kept deliberately: this file is what the app is specified to do, and a
specification that quietly implies bench-proof it does not have is the same failure mode as
a catalog that quietly implies a guide it does not have.

Deliberately not built:

- Dragging a console tab out of the strip to detach it — the console's Detach button and
  the tab's context menu cover the need.
- Remembering open tabs across runs — only worth doing alongside a way to re-find an
  instrument whose DHCP address has changed (§12), otherwise launch-time reconnects are
  guesswork against the one-session rule (§13).
- **USB/USBTMC and GPIB** (§1). Nothing is planned. What follows is the reasoning, written
  down because "no USB, GPIB or serial" read as one decision and was actually three — and
  the third of them has since been built, which is the best evidence that keeping them
  apart was worth the trouble.

  **GPIB — no, and not for want of effort.** It needs a controller card or adapter, and
  every one of them ships its own proprietary driver. There is no protocol to implement
  here that would get round that, so it would mean the NI-VISA or vendor-SDK dependency
  §1 exists to refuse. The instruments are also a generation older than anything on this
  bench, which is the smaller half of the reason.

  **USB/USBTMC — no, on cost.** The class protocol itself is tractable (bulk endpoints,
  bTag sequencing, a header per transfer), but reaching it means WinUSB on Windows and
  libusb elsewhere.

  Serial having landed with a native shim of its own, the distinction that matters here is
  worth stating precisely rather than by the word "native". `System.IO.Ports` does carry
  per-platform native code — a 15 KB `libSystem.IO.Ports.Native` for each non-Windows
  runtime — but it is Microsoft's own, restored by NuGet, and travels inside the package
  with nothing to install and no driver to bind. libusb is a **system library the user has
  to already have**, and on Windows a USB device speaks to WinUSB only once someone has
  bound that driver to it by hand. One is a build artefact; the other is a prerequisite on
  somebody else's machine. That is the line, and USBTMC is on the far side of it: it would
  either break Core's "plain `net10.0`, nothing Windows-only" rule or ship as a
  Windows-only transport the other two front ends could not offer — and a transport that
  exists in one build and not the others is the thing UI-SPEC §0 spends its first page
  arguing against.

**Built after being deliberated over here** (kept, because what a decision cost is worth
having next to it):

- **Serial (RS-232) — was "the one that would make sense, if any did", and now exists**
  (§13). The prediction was that `SerialInstrumentClient : IInstrumentClient` would be a
  short file because SCPI over serial is line-based exactly as `ScpiClient` already is, and
  that everything above the transport would not know the difference. Both held. What it
  actually cost was the framing being lifted out of `ScpiClient` into `ScpiFraming` so the
  two clients could share it, which the socket was better for anyway.

  The two objections held too, and became the shape of what was built rather than reasons
  not to:

  - **Discovery did not transfer, and the shape of what replaced it is the argument.** §4
    is built on sweeping a subnet and asking `*IDN?`, and a serial port cannot be swept that
    way — you can enumerate the ports, but not the baud rate, parity or handshaking each
    instrument was set to, and probing a port that turns out to be a modem or a printer is
    not the harmless connect a TCP probe is.

    That first became "ports are listed and never probed, and the choosing is the user's",
    which was right about the danger and wrong about where the line sat. It put the whole
    of `*IDN?` behind the user typing an address, when the part that was actually
    unacceptable was doing any of it *unasked*. The line that holds is **automatic**: a
    card arriving on Serial opens nothing, and no combination of settings is ever tried
    behind anyone's back. A `Scan` somebody pressed, over the ports they chose, at the baud
    rates they named, with what it does written on the button, is an act they authorised —
    and it is the only thing that opens a port. Everything the original objection was
    protecting is still protected; what changed is that the protection is a button rather
    than an absence (§4).

    Two things the subnet sweep gets for free had to be built by hand. A port that answers
    nothing **stays in the list**, because it is still a port and still connectable at
    settings the scan did not try. And a reply is **checked before it is believed**: TCP
    delivers the sender's bytes or nothing, a UART at the wrong rate delivers different
    ones, so an "identity" is only an identity if it is printable and comma-separated.
  - **It added a dependency**, taking Core from one to two (§2). Costed rather than waved
    through: 10.5 KB gzipped in the browser payload, against PdfPig's 1.83 MB.

  Addressing was the part already done, exactly as this entry predicted: `InstrumentAddress`
  was the single place `serial://COM3?baud=9600` needed reading, and all three front ends
  took it from there.
