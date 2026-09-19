# Lab Equipment Controller — UI specification

What is on screen, where it is, and what it looks like — for both builds.

`SPEC.md` says what the application *does*. Its §14 says how WinForms must be handled to
build a UI at all — normalise row heights, never hardcode a runtime pixel size, put every
button through `ButtonStyle`. Neither says which controls exist, in what order, with what
words on them. That gap is what this file fills, and it is why the web port was reviewed by
screenshot for as long as it was: there was nothing to check it against.

Read this with `SPEC.md`, not instead of it. Where behaviour is described there — what a
scan does, what the console colours mean, how a family is classified — this file points at
the section and does not restate it.

---

## 0. The rule

**The desktop application is the specification. The web build is a port of it.**

Not an adaptation, not a modern take, not an opportunity. The same controls, in the same
order, with the same words on them, the same glyphs, and the same gestures. Someone who
knows one must not have to learn the other.

That means, concretely:

- **Labels are letter-for-letter.** `Identity (*IDN?)`, not `Identity`. `SCPI Port(s):`, not
  `Ports`. `Timeout (ms):`, not `Timeout`. If the desktop's word is clumsy, fix it in both.
- **A control that exists there exists here**, even when it can do less. Disabled and present
  beats absent: absent reads as a feature the port forgot.
- **Numbers are copied, not re-invented.** The gaps, heights and glyph sizes in §1 are the
  desktop's own constants. Where the web uses a different number, §1 says why.
- **Explanation goes in tooltips.** The desktop explains itself on hover; a paragraph of help
  text on the page is a web invention, and it is not one of the allowed ones (§9).

A departure needs a reason from §9. "It looks better this way" is not one. If a change is
genuinely an improvement, make it in the desktop first and port it.

---

## 1. Metrics

The numbers both builds are held to. Desktop values are logical pixels at 96 dpi and scale
with `DeviceDpi`; web values are CSS pixels and do not.

### Type

| | Desktop | Web | Note |
|---|---|---|---|
| UI font | Segoe UI 9 pt (12 px) | 13 px Segoe UI Variable Text | body copy, tables, tabs, menus |
| Control text | same as UI font | **14 px** (`--ctl-font`) | the value in a box is the thing you read |
| Labels | same as UI font | 13 px | one step under the control it names |
| Card caption | — (GroupBox caption) | 12 px, weight 600 | |
| Console log | Consolas 9.5 pt | 12.5 px monospace | |
| Command box | Consolas 10 pt | 14 px monospace | |
| Queue strip | Consolas 9 pt | 12 px monospace | |
| Meter readout | large, "readable across the bench" | 2.6 rem, tabular figures | |

### Controls

| | Desktop | Web |
|---|---|---|
| Height of every control on a row | one number, measured from the font by `ButtonStyle.Height` | **25 px** (`--ctl`) — the filled button is **24.5**, see below |
| Button padding | 8, 3, 8, 3 | `0 .55rem`, height fixed |
| Minimum button width | 76 | none — labels are wider than that here anyway |
| Timeout box width | 58 + spinner | `4.3rem` |
| Address box width | 200 | `size="22"` — one IPv4 address and a port |
| Serial port list width | same as the address box | same — it stands in the same place, and a row whose box changed width when you changed its kind would read as two rows |
| Scan-kind switch, per half | its own caption + 8 | its own caption + `.3rem` either side — uneven halves on purpose; matched to the wider it carried a block of empty space beside "Serial Scan" |
| Scan-kind switch height | the caption's line + 4 | `height: auto` at the caption's 12px/600, rather than the `--ctl` every other `.seg` takes — it stands in for the heading, not in a row of boxes |

One exception, and it is optical rather than metric: **the filled button is half a pixel shorter**
than the ones beside it, centred, so it loses a quarter of a pixel top and bottom. Measured against
its neighbours it was identical to the hundredth of a pixel and still read as the taller of the two —
a saturated fill against a white one looks larger than it is. `metrics.spec.js` holds both numbers.

Every control standing on a row is the **same height**. On the desktop that is
`ButtonStyle.Normalize`, which measures a probe button carrying a glyph and pins everything
to it — including fighting `ComboBox` through `ItemHeight`, because a `DropDownList` is
font-height-locked. On the web the height is stated rather than left to fall out of padding,
for the same reason: an input, a select and a button on one row must not disagree by a pixel.

Buttons that are **not** controls on a row take their height from what they sit in: a menu
row, a tab, a cell in a table, a segmented group. **So is a box on a caption line** — the
Command Reference's `Filter:` is **22 px**, because there is no row of controls beside it to
line up with and the full height made it the tallest thing on the line by a clear margin. Its
label is set at `--ctl-font`, the size of what is typed into it: a word naming a box, a size
smaller than the box's own text, reads as assembled rather than made.

### Glyphs

Nominal size is **16 against a 12 px font** on the desktop, so **18 against 14** on the web.
The ratio is what carries over, not the pixels.

The artwork does not share a common margin, so nominal size is corrected per glyph. These
fractions are `ButtonStyle.Optical`, and the web reads the same numbers out of `--f`:

| Glyph | Factor | Why |
|---|---|---|
| `stopClock` (■) | .60 | a solid square: the heaviest shape in the set |
| `startClock` (▶) | .68 | a solid triangle |
| `stepClock` (↷) | .76 | a thick arrow, corner to corner |
| `reset` (↻) | .78 | ditto, as a ring |
| `connect` | .76 | a busy diagonal shape, corner to corner |
| `program` | .84 | |
| `new` | .84 | |
| everything else | 1.0 | outlines, inset from their own canvas already |

Solid shapes drawn edge to edge read as about twice the weight of an inset outline at the
same nominal size. Drawing them smaller is what makes them look the same size. **Judge new
artwork from a sheet rendered at the real size, not from the numbers.**

### Distances along a row

Three, and only three. `MainForm` states them as three constants; the web states them
relative to one variable so they cannot drift apart.

| | Desktop | Web | Where |
|---|---|---|---|
| A label and the box it names | 2 | 2 px | `Address:` ▸ box, `Timeout (ms):` ▸ box, `Interface:` ▸ box |
| One pair and the next | 8 | `--rowgap`, .6 rem (9.6 px) | box ▸ next label |
| Before the control that answers the row | 52 (`AnswerGapLogical`) | 52 px (`.answer`) | ▸ `Scan`, ▸ `Connect` |

The web's 9.6 is the desktop's 8 read at the font this build uses. The 52 is literal: it is a
distance, not a size.

The console's command row is its own case, because Send answers the box while Clear Log and
Save Log act on the log above:

| | Desktop | Web |
|---|---|---|
| Command box ▸ Send | 0 | 6 px |
| Send ▸ Clear Log | 40, shrinking toward 6 to keep 140 px of box | 52 px, and the row wraps instead |
| Clear Log ▸ Save Log | 6 | 6 px |

The readout window is its own case too. `MultimeterReadoutForm` does not use `MainForm`'s
numbers — it sets a margin on each control instead — and what it sets is wider:

| | Desktop | Web |
|---|---|---|
| A label and the box it names | 6 | 6 px |
| One pair and the next | 14 | 14 px (`--rowgap` on `.meterrow`) |
| The number ▸ `ms` | 4 | 4 px |
| `ms` ▸ `Start` | 26 | 26 px (6 + a 14 px `.gap` + 6) |

`ms` belongs to the number in front of it rather than being the next thing along, which is why
it is closer than a pair is; `Start` stands clear because butted against the box it reads as
part of the field. `WaveformForm` sets the same margins on the same controls, so the waveform
window's row uses the same three — with a `.gap` at each group boundary, which comes to the same
twenty-six.

### Card padding (web)

A card's **bottom padding is its side padding** — `.85rem` — and so is its top, on a card with no
caption. A frame with less air under its contents than beside them reads as one that ran out of
room rather than one that was drawn, and it shows most on the console, where the command box sits
at the foot of its card. The one exception is the top of a card that opens with a caption, which is
tighter because the caption brings its own air with it.

It holds for **every** framed thing, not only `.group`: a panel and a console or waveform pane
are read the same way and had `.75rem` and `.35rem` under them against their sides' `.85` and
`.6`. And it holds **through** a nesting — a card inside a pane inside a box has three insets on
each side of its corner, and the three going down have to come to the three going across.

### Vertical rhythm (web)

The desktop has no equivalent — WinForms cards are placed, not flowed — so these are the
web's own, and they must stay consistent across every card:

| | |
|---|---|
| Card top ▸ its caption | .4 rem — tighter than the rest on purpose: a caption is a label on the box, not the first thing in it |
| Caption ▸ first row | .6 rem |
| Last row ▸ card foot | .75 rem |
| Card ▸ card, and above the first | .55 rem |
| Tabs ▸ the pane under them | .6 rem — **the same for the table and the plot** |
| Over a panel's foot controls | .75 rem — the same as under them |
| Tab strip ▸ the console below it | .3 rem, plus .1 rem on the identity line above its own text |
| Identity line ▸ quick commands | 1.2 rem — the desktop’s 18 logical units, and its reason: this row says what the console is attached to, the strip under it sends commands to an instrument |

The log box **fills** its panel: the two panels in the split are stretched to the same height
by the grid, so whichever is shorter would otherwise spend the difference on nothing. 360 px
is its floor.

---

## 2. The windows

| | Desktop | Web |
|---|---|---|
| Bench | `MainForm` | `/` (`Instruments.razor`) |
| Console | `InstrumentConsole`, a control in a tab | `ConsoleView.razor`, a pane in a tab |
| Console in its own window | `InstrumentWindow` | `/console?session=…&detached=1` |
| Single-instrument script | `ScriptForm` | tool window, `Tools/Scripts.razor` |
| Multi-instrument script | `SequenceForm` | `/sequences` |
| Discovered commands | `CommandReferenceForm` | tool window |
| Command library | `CommandLibraryForm` | `/catalog` |
| Script language reference | `ScriptReferenceForm` | `/script-language` |
| Screen capture | `ScreenCaptureForm` | tool window, `Tools/Capture.razor` |
| Waveform capture | `WaveformForm` | tool window, same component |
| Live readout | `MultimeterReadoutForm` | tool window, `Tools/Readout.razor` |
| AI datasheet extraction | `DatasheetExtractForm` | tool window, `Tools/Ai.razor` |
| AI script writing | `ScriptAiForm` | same tool window, other half |
| AI connection | `AiSettingsForm` | modal, `AiSettingsDialog.razor` |
| About | `AboutForm` | modal, `AboutDialog.razor` |

**Every page says what it is, in the title bar.** A WinForms form's `Text` is its window title,
and each of the windows above is a form with one of its own; on the web the pages in that column
are browser tabs, and several of them are open at once with all four bands identical. So the top
bar reads `Lab Equipment Controller — <this window>` — the window's own name, not a description
of it, because the status line along the foot already says what it is for. The **bench keeps the
strapline** (`— SCPI over Ethernet`): it is not one of the windows, it is the app, and
`Controller — Bench` says the same word twice. The same name goes in `<PageTitle>`, so the
browser's own tab strip can be read too.

A page that *is* one of those windows carries **no heading of its own**: the title bar names it,
the browser tab names it, and a third copy at the top of the work area is the window saying its
own name back to you. `/sequences` opens on its toolbar and its editor, as `SequenceForm` does.
Its toolbar is in a **card** there, because on a page there is nothing else for a bare row of
buttons to stand on — inside a window the body is the panel and the row reads as the window's own
furniture, the way a WinForms toolbar sits on its form.

**`Open in a tab` moves a window; it does not copy it.** The three that are also routes — the
library, the language reference, the multi-instrument editor — close on the bench as their tab
opens, and **come back when that tab is closed**. A window that moved and a window that
multiplied look the same until you close one of them, and a window that simply vanished leaves
you working out which menu it came from. It is the round trip a detached console already makes.

How it is known is a **roll-call**, not a farewell: the bench asks who is out there whenever it is
looked at, and on a timer while anything is out — two windows side by side raise no focus event in
each other. A `postMessage` from `pagehide` does not survive the document being torn down (tried;
it does not arrive), and the one occasion it would survive — a reload — is the one occasion it
would be a lie. Only a tab that has **answered once** can be noticed going quiet, so a tab still
fetching the runtime is never mistaken for one that has closed.

**Tool windows are not modal.** On the desktop each is a real window: you can leave it open,
move it, and work behind it. On the web they are `<dialog>` opened with `show()`, not
`showModal()` — no backdrop, the page behind stays live — and they can be dragged by their
title bar, because a box that opens in the middle lands on the very thing it was opened to
look at.

**About and AI connection are modal**, are `ShowDialog` on the desktop, and **do not move**.
There is nothing behind one to uncover.

---

## 3. Bench

### 3.1 Menus

The desktop has a menu strip; the web has one gear at the right of the title bar opening the
same list. Tools does things to the bench, Help explains things — that is the line the two
are drawn along, and it is why the command library sits under Help.

| Desktop | Web |
|---|---|
| — | **Theme** — light / dark / system, settled in place |
| `Tools ▸ Multi-Instrument Scripts…` | on the Instrument Consoles caption row, not in the menu |
| `Tools ▸ AI Connection…` | **AI settings…** |
| `Help ▸ Command Library…` | **Help ▸ Command library…** |
| `Help ▸ Script Language…` | **Help ▸ Script language…** |
| `Help ▸ About Lab Equipment Controller…` | **About** |

Help is a submenu on both. On the web it opens on hover *and* on focus, so a keyboard reaches
it, and it hangs to the **left** of its parent — a menu in the middle of a title bar has room
on that side and not on the other.

### 3.2 [ Network Scan | Serial Scan ]

**The caption is the control.** The heading is a two-segment switch reading
`Network Scan` · `Serial Scan`, with `Network Scan` filled by default. Each half is a whole
caption rather than a word with a shared `Scan` after it: the two read as the two things
this card can be, and the selected half is the card's title outright. It decides which bench
this window is looking at, and **everything below follows it**: the inputs on this card, the
column headings of the list in §3.3, and which box the Address row shows.

- The desktop draws it over the group box's own top border, where the heading text would be
  — a `GroupBox` reserves a gap in that border for its `Text` and for nothing else, so the
  halves cover the line themselves. The web puts a `.seg` inside the `.cap`.
- It is **the size of the caption**, not of a form control: it stands in for the heading, so
  it takes the heading's size and weight and only its border makes it more than text.
- **Each half is the width of its own caption**, so the halves are uneven. Matched to the
  wider, the switch carried a block of empty space beside `Serial Scan`.
- **The two halves touch.** One control with one border round the pair and a single hairline
  between them: the desktop overlaps them by a pixel so the two 1px borders land on each
  other, the web gives the second a `border-left` inside a `.seg` that hides its overflow.
- Radio buttons underneath on the desktop, and that is not an implementation detail: it is
  what makes the two mutually exclusive, walkable with the arrow keys, and one named group to
  a screen reader. Two ordinary buttons would be two controls that happen to agree.
- **The text is raised off centre so that it reads as centred.** Centring puts the middle of
  the *line box* in the middle of the button, and a line box reserves room under the baseline
  for descenders neither caption has, so the words land about two pixels low — enough to read
  as bottom-aligned. What wants centring is the block from the cap tops to the baseline; its
  middle sits `ascent − lineHeight/2 − capHeight/2` below the line box's, and that is how far
  up it goes. Every term comes from the font, so it holds at any display scale.

  This is why the desktop's halves are a `SegmentButton` rather than a `RadioButton` with
  `Appearance.Button`: the flat renderer ignores `Padding`, so there was nowhere else to put
  the correction.
- The fill is **grey, mixed from the border grey and the face grey** the window is already
  built out of — dark enough that the chosen half is obvious at a glance, light enough to
  stay a heading. It was the system accent for a while, on the argument that a desktop app
  can read the colour Windows was told to use where a page cannot. True, and beside the
  point: nothing else in that window is accent coloured, so an accent-filled switch was the
  one thing on screen pulling the eye, and what it pulled it towards is a heading. Then it
  was `ControlDark` flat, which was the opposite problem — a heading in a dark box reads as a
  warning. Mixed rather than picked because there is no system colour between the two. See §9
  for where the web stands on this.
- **Hover lifts only the unselected half, and lifts it the way Windows lifts every other
  button in the window**: the theme's own wash and edge, which on Windows 11 is a pale blue
  fill inside a blue line. Asked of the theme rather than guessed at — a hot button is
  rendered once into a bitmap and sampled, because the theme offers this as a drawing
  operation and not as two colours — so the switch cannot drift away from the buttons beside
  it. With visual styles off there is no blue to match and it falls back to flat greys, which
  is what the rest of that window looks like too.

  Lightening the *selected* half would make it look unselected at the exact moment somebody
  is about to click it, so it stays put.
- **Not remembered between runs**, in either build. A window that opens on Serial is one
  listing the ports of a machine whose adapter may not be plugged in this morning, and that
  costs far more than the one click it saves.
- **Switching empties the list.** Rows from the other mode are not merely stale, they are
  unreadable — an IP address under a column headed `Port` — and a row that fills the Address
  box has to fill it with something the visible box can hold.

One card, two rows, in both modes. There is **one `Scan` button**: which sweep it starts is
the switch above, never a second control.

**On `Network`** — row 1, in order: `Interface:` ▸ dropdown ▸ `IP range:` ▸ box ▸
`SCPI Port(s):` ▸ box with presets ▸ **52** ▸ `Scan`.

- The interface dropdown reads `address/prefix — name (N hosts)`.
- The IP-range box is empty by default and sweeps the whole subnet; placeholder
  `blank sweeps the whole subnet`.
- The ports box is **filled**, not hinted, with `5025, 5555, 3490, 111`. The desktop's is a
  dropdown carrying seven presets with the last already in the box; the web's is an input
  with a datalist, which is the same control.

**On `Serial`** — the same two questions asked of a serial bench: `Ports:` ▸ dropdown ▸
`Baud rate(s):` ▸ box with presets ▸ **52** ▸ `Scan`.

- `Ports:` is the counterpart of `Interface:` — how much of the bench this covers, and how
  much of it there is. Its first entry is `All serial ports  —  N found` and the rest are the
  ports themselves. Re-read each time the card switches to Serial and each time the list is
  opened, because an adapter appears the moment it is plugged in.
- `Baud rate(s):` is the counterpart of `SCPI Port(s):`, and **filled** for the same reason:
  `9600, 115200, 19200, 38400, 57600`, tried in that order and stopping at the first that
  answers. 9600 leads because it is what instruments ship at; 115200 is second because it is
  what everything built since is set to.
- Nothing else is swept. Parity, data bits and flow control are set per port in the Address
  box — a combinatorial search against hardware that cannot say "wrong number" is what
  SPEC §17 refuses.
- **Switching to Serial lists the ports straight away, unopened**, as rows in §3.3 with an
  empty Identity. That is already enough to pick one and connect; `Scan` is what opens them.
- The desktop's `Scan` tooltip on Serial **says what it does**: it opens each port, writes
  `*IDN?` to whatever is there, and asserts DTR and RTS on the way in. The network sweep has
  nothing to disclose because a TCP connect cannot be mistaken for anything else.

In both modes `Scan` becomes `Stop` while running, with the stop square. One button, two jobs.

Row 2: a determinate progress bar, then the status text. **Both are always present**, with
`Ready.` in the text when nothing is running — a row that appears when Scan is pressed shoves
the list below it down at the moment you have started watching it.

A card that can find nothing says so **and says what to do about it**: no usable network
interface, or no serial ports, each with the Docker fix named (`network_mode: host`,
`devices:`).

### 3.3 Discovered Instruments

Caption: `Discovered Instruments` with the hint `(double-click a row to connect)` beside it,
always — the thing you need to be told is how to use what appears, before it appears.

A table, four columns, letter for letter: **`IP Address` · `Port` · `Protocol` ·
`Identity (*IDN?)`** on `Network`, and **`Port` · `Settings` · `Protocol` ·
`Identity (*IDN?)`** on `Serial`. The first two are named for what they hold, and what they
hold differs: an address and a TCP port, or a port name and the line settings that have to
match before a character gets through. These are also the header row `ScanResultExport`
writes into the CSV, so the screen and the file name the same four things the same way — both
lists live in `Core/ScanResultExport` (`Columns` and `SerialColumns`) and neither build
writes its own.

On `Serial` the list behaves differently in two ways, both of them the physics rather than a
preference (SPEC §4):

- **Every port stays listed**, answered or not. An address that does not answer is not a row;
  a port that does not answer is still a port on this machine and still connectable at
  settings the scan did not try. `Scan` fills identities into rows that are already there
  rather than building a list from nothing.
- **The Identity cell has three states, not two.** Empty means not asked. `(no reply at 9600,
  115200)` means asked and silent, naming the rates so the next thing to try is obvious.
  `(answered at 9600, but not with an identity — wrong line settings?)` means something is
  there and the framing is wrong, which is the fact that sends someone to the baud rate
  instead of to the cable.

- The table is **always drawn**, with **one empty row** when nothing has answered. A list view
  keeps its row area whether or not anything is in it; four headings standing alone read as a
  table that failed to draw.
- The table is as wide as its content and no wider, and it grows with a long identity —
  **but never narrower than the address row under it**, so its right edge lands on `Connect`'s.
  Empty, it stopped in the middle of the card, which reads as a table that failed to draw. The
  slack goes to the last column: an address, a port and a word do not want stretching.
- A connect error is a sentence **under** that block, not the last item on the row — at the end
  of the row it would decide how wide the block is.
- **`Export Results…` sits under the table, flush with its right edge** — on the desktop,
  `Place(btnExport, lstDevices.Right - btnExport.Width)`. It follows the table out when a wide
  identity widens it. Dead until there is a list to write.

Then the address row: `Address:` ▸ **2** ▸ box ▸ **8** ▸ `Timeout (ms):` ▸ box ▸ **52** ▸
`Connect`.

- **No switch on this row.** Which of the two boxes shows is the scan card's heading (§3.2).
  One choice, one control: that card, its columns and this box are all about the same bench,
  and two switches that had to agree would eventually not — which is what the pair did while
  the switch lived here, leaving the card above saying `Network Scan` with COM3 in the box.
- **One box, whichever kind.** On `Network` it is the address box; on `Serial` it is the port
  list, in the same place, at the same width, and never both at once — the row is `Address:`,
  singular. Selecting a row fills whichever box is showing, and it can only ever be a row of
  the mode that is showing, because switching empties the list.
- The address box takes one endpoint and is sized for one: `255.255.255.255:65535`. It accepts
  a plain IP, an IP with port, a `vxi://` address, a `serial://COM3` port, or a VISA resource
  string. Both builds' tooltips list all of them, and both are read by one parser in Core
  (SPEC §5) — a spelling one build takes and the other refuses is the failure this whole
  document exists to prevent.
- **The port list is editable, and re-read each time it is opened.** Editable because the
  ports are offered for the sake of not remembering whether the adapter came up as COM3 or
  COM7, while a line setting still has to be typeable (`COM3?baud=115200`) and an unlisted
  port still has to be reachable. Re-read on opening because an adapter appears the moment it
  is plugged in, and that is the moment someone looks at the list. It holds a port *name*, not
  a whole address — `serial://` is added when Connect is pressed, so a dropdown of serial
  ports reads as a list of serial ports. A pasted `serial://COM3` is left alone. On the web it
  is `<input list>` with a `<datalist>`, which is a browser's editable combo box; a `<select>`
  could only be chosen from.
- **A serial address is the server's port, on the web.** `serial://COM3` means a port on the
  machine running the server, and so does everything in that list and in the results table —
  `GET /api/ports` and `/api/serial/list`, both of which list and never open, and
  `POST /api/serial/scan`, which opens because it was asked to. A browser has no serial port
  of its own. The web tooltips say so and the desktop's do not, which is the one place these
  strings differ on purpose.
- **With nothing chosen, `Connect` says what to do about it, per kind.** On `Serial` the
  desktop's "Select a device or type a valid address" would name a list that cannot contain
  one — a sweep does not find COM3 (SPEC §4) — so it names the ports instead.
- Timeout is 100–30000, step 500, default 3000, and applies to the connection **and to every
  query on it afterwards** — not to the scan, which has its own short probe timeouts. It sits
  beside Connect for that reason.
- `Connect` becomes `Cancel` while connecting, with the stop square, and cancelling drops the
  attempt at the server rather than merely ignoring it.

### 3.4 Instrument Consoles

Caption row: `Instrument Consoles`, then `Multi-Instrument Scripts…` at the right. That
button is here whether or not anything is connected — a multi-instrument script is written
before the instruments are connected as often as after.

**The box is the height of the window.** `grpConsole` is anchored on all four edges, so it
stretches to whatever `MainForm` has left under the scan panel and the log grows with the
window; the web fills the workspace the same way, and the log takes what the filling gives it
(360 px stays its floor, and a window with no room to give scrolls instead). Content-sized, it
put every pixel a taller window offered into a field of nothing under the card — the one place
on the page where the space below a card was not the space beside it, and no amount of padding
fixes that, because it was never padding.

While nothing is connected, a sentence stands where the tab strip would be, because a blank
panel with no explanation reads as a broken window. It is **centred in the box**, as
`lblNoConsole` is — docked `Fill`, `MiddleCenter` — rather than sitting at the top of a tall
empty frame.

One tab per session, showing a status dot, the profile name and the address, then **two glyphs**:
the one that opens this console in a window of its own, and a `✕` that disconnects and closes.

Both are on the tab because both are things done *to* a tab, and that is where a browser puts
them. Detaching had been a labelled button on the console's own header in both builds — a row's
width to say what a glyph says, and reachable only by first bringing that console to the front.
The desktop's tabs are owner-drawn to carry them (`WinForms` has neither), and the glyph is the
same artwork in both: a rounded frame open at its top-right corner with an arrow through the gap,
authored once on a 64×64 grid (`AppIcons.DrawMove`, `Icon.razor`).

A detached console comes back **when its window closes** — there is no re-attach control, in
either build. On the web the console is a browser tab against the same server-side session, so
closing it is the whole gesture; on the desktop the window hands the console back to its tab as
it closes.

Right-click gives the same two items the desktop's tab menu gives:

| Desktop | Web |
|---|---|
| `Detach to Its Own Window` | **`Open in new tab`** |
| `Disconnect and Close Tab` | `Disconnect and close tab` |

A tab whose console is open elsewhere stays in the strip — it is how you find that window
again — and shows plainly that it is not what you are looking at.

### 3.5 Where the connection count goes

The desktop puts `Not connected.` / `1 instrument connected.` on the address row, right of
Connect. The web puts it in a status bar along the foot of the window, with the catalog
totals and the name of the page. See §9.

### 3.6 How long the bench lives

**Closing the window closes the bench.** The desktop takes every connection with it when
`MainForm` goes, and a build that hands the next visitor the last one's sessions is offering a
console onto an instrument that may since have been switched off, moved or unplugged — and one
that now answers to a different address is worse than one that does not answer at all.

**Opening it closes it too**, and that is the rule that matters: nothing is connected until the
person at the keyboard connects it, by address or by scanning and picking a result. Not *usually*
— always. It is asked and answered **before the runtime is started** (`index.html` →
`POST /api/bench/opened`), so the page draws an empty bench instead of drawing the old consoles
and taking them away again, which would be a flicker rather than a fix.

**A reload is the one exception.** A sweep that outlives a refresh is the main thing the web has
over the desktop, and F5 must not drop an instrument mid-measurement. The bench also lingers a
short while after the last page goes rather than closing on the spot, for the case where nobody
comes back at all (`BenchService.Linger`, 20 s).

Telling a reload from an opening is the whole difficulty, and **`sessionStorage` alone cannot do
it**: browsers *restore* it when a tab is reopened or a window comes back at startup, so a mark
left in it returns from the dead and a fresh opening claims to be a refresh. Two things are asked
instead and both must say yes — the browser's own navigation type is `reload` (not `navigate`,
which is an address being opened, and not `back_forward`, which is a restore), **and** the
document before this one went seconds ago rather than hours. A timestamp survives being restored
but cannot lie about *when*.

**Nothing is guarded on who else is watching.** That was the other half of the same fault: with
any page still up — a tab left open, one the browser restored, one whose socket had not yet been
reaped — the opening did nothing and the app came up on the last bench again. Opening the bench
page clears it, full stop.

**The bench page, and no other.** It is the app; the rest of §2's Web column are windows the app
opens — a detached console onto a session that already exists, a reference or the multi-instrument
editor in a tab of its own — and opening one of those is not opening the app. Asked on all of
them, the reference you opened to read while working disconnected the instrument you were working
on.

**And every page is told when the list changes.** The queue and the lock were pushed and the list
itself was not, so a window that had been open a while drew a tab strip from whenever it last
asked — a console onto a session the server no longer has, which draws, accepts typing, and fails
on every press. That is the same phantom arrived at from the other direction. `BenchService`
sends `Bench` on every connect and disconnect; the message carries nothing, because what the list
now is has an answer already and a page told to go and read it cannot be told a stale one.

---

## 4. The console

The desktop's console window and the web's console pane hold the same six things in the same
order.

### 4.1 Identity line

One line, not two: address · transport · recognised profile · the `*IDN?` reply. The address
and the identity are monospace; the rest is not. Long identities are **clipped to one line**
with the whole of it in the tooltip, so the panel's height does not change with the window's
width.

### 4.2 Quick commands

One row of small monospace buttons, built from the family's catalog — not from a fixed list.
Run, Stop and Single carry `startClock`, `stopClock` and `stepClock`; nothing else carries a
glyph. A hairline under the row separates them from the tools.

### 4.3 Tools

`Scripts` · `Discover Commands` · `Capture Screen` · `Capture Waveform` · `Live Readout…` ·
`AI Datasheet Extraction`. Each carries its glyph. See §7 for what greys out.

**No ellipsis on `Scripts`.** The convention it belongs to is a command that stops to ask
something before it does anything — `Open…`, `Save As…`, `Browse…`. This one does not ask: it
opens a window, and the window is the point.

**Discover Commands is a question before it is a window.** SCPI-99 defines one runtime query
that lists an instrument's command headers — `SYSTem:HELP:HEADers?` — and an instrument that
answers it is describing the firmware in front of you rather than the guide that shipped with
the model. So both builds send it, name it in the log first (the answer can be several seconds
of nothing), and print the header tree that comes back. **The bundled catalog is the fallback**,
opened only when the query goes unanswered, and the log says so in as many words:
`--- instrument doesn't support live discovery; opening the built-in reference for … ---`. With
neither — no answer and no catalog — the log says that too and nothing opens. Most budget
instruments (Rigol, Siglent) do not implement the query, so the fallback is the ordinary path,
which is exactly why the web build had gone straight to it and never asked.

### 4.4 Queue strip

Above the log, always drawn, keeping its height whether or not anything is in it: an outline
that appears with the first command and vanishes with the last reads as a thing arriving
rather than as the place where things arrive.

The strip carries **no padding of its own**: a chip is what the box is there to hold, and a gap
round it reads as a border with air inside it. The word naming an empty strip is the exception —
it takes a chip's own inset (its border plus its padding) so it stands exactly where the first
chip's text will stand.

One chip per command, oldest first, `▶` between them. The queue is a **list**, not a count —
"three queued" says nothing about whether the one you just pressed is among them.
`SerializedInstrumentClient` holds it; the desktop reads it off `PendingChanged`, and the web
has the server push the same event over the hub, because the queue changes when the
instrument finishes answering rather than when a browser asks.

### 4.5 Log

A dark fixed-pitch surface. Colour carries meaning — see `SPEC.md` §7. It scrolls to the end
on every line.

It **opens on three lines**, in this order and these words: `--- connected to <host> via
<transport> ---`, `*IDN? -> <the reply>`, `--- quick commands loaded for: <profile> ---`. The
desktop prints them the moment the session is made (`MainForm`) and they stay at the top of the
log for as long as the console is open; the web writes them when a console is built, which is the
same thing said in a build where a console can be rebuilt in a browser tab of its own. They are
green, blue and grey, as they are there.

And one more line, grey, whenever `Connect` is pressed on an address that already has a
session: `--- already connected to <host>; this is its console ---`. Neither build dials an
address it already holds — a Rigol DS2202 wedges its firmware if a second TCP session is opened
against it — so the press brings that console to the front instead, and if it was already the
one on top there is otherwise nothing at all to see.

### 4.6 Command row

`[command box] Send · Clear Log · Save Log`, spaced as in §1.

Enter sends. Up and Down recall earlier commands for *this instrument*, one past the newest,
so the first Up recalls the last command and stepping back down past the newest clears the
box. Blank commands are not recorded.

### 4.7 Results

Two tabs. The desktop's are **`Results`** and **`Plot`** (`ResultsPanel`); the web's are
**`Results table`** and `Plot`, because it dropped the caption that used to stand over them
and the tab had to carry the word instead. The web's tab said `Table` before that, which
matched neither.

The pane itself is **one control in three places** — the console and both script editors — as
`ResultsPanel` is on the desktop. Two tabs, the same two controls, the same rules about which of
them shows when. The web had three copies of it that had drifted into three different things.

**Results table** — three columns in a console: `Time` · `Command` · `Value`; in a script editor,
whatever `COLUMNS` named. Only a reply that is entirely one
number is recorded; `*IDN?` and a comma-separated block are not. `Save CSV` and
`Clear Results` sit at the **foot of the panel**, and only under this tab.

The table is **there before there is anything in it**, headings and all — a `ListView` with its
columns set is what the desktop shows from the moment the window opens, and the headings say what
this run is going to record. `Clear Results` empties the rows and **keeps the headings**
(`ResultsPanel.Clear`): they are what the table is, not what is in it.

Each column is **as wide as the widest thing in it, and the last takes the rest** out to the
panel’s edge — the rule `MainForm.LayoutDeviceColumns` settles for the device list, where the
narrow columns are fixed and Identity is given “whatever room is left over”. Three short columns
each taking a third of a wide panel puts a hand’s width of nothing between a time and its
reading.

The picker strip is as short as ResultPlotPanel makes it: no gap between the three switches, and
the column list 150×62 — four rows in sixty-two pixels, so a row is a checkbox tall rather than a
line of prose tall. Its own comment says why: that column is the tallest thing in the strip, and
the strip's height comes off the curve.

**Plot** — the curve first, the pickers under it: `X:` and `X Unit:` in a column, the `Y:`
checklist beside them, then `Log X` / `Log Y` / `Points`, then the camera at the right, which
saves the plot as a PNG. The pickers go under because they are what you reach for *after*
looking at the plot — a wrong axis is something the picture tells you.

The picture is drawn **in the units of the box it is in**, one to the pixel, and redrawn when that
box changes — `ResultPlotPanel` fills its tab page and repaints. A canvas with an aspect ratio of
its own made the plot tab a different height from the table tab beside it, so pressing `Plot`
resized the card and stretched whatever shared its grid row.

All the arithmetic — axis choice, tick placement, log suitability, unit guessing — is Core's
`ResultPlot`. The web reaches it over the wire through `PlotService` and does geometry only.
Neither build reimplements it. The six series colours are the desktop's.

---

## 5. Tool windows

Each has a title bar naming **the tool and the instrument** — with several consoles open,
which one this was opened from is the whole question.

They are **windows, not panels**, and everything that follows from that holds in both builds:

- **Several stand open at once.** The desktop opens each with `Show()`; nothing there makes them
  take turns, and a command reference is something kept beside the script it is being read for.
- **The one touched is in front.** A press anywhere inside brings it forward.
- **Asking again raises rather than reopens.** Pressing a tool whose window is up does not build
  a second and does not rebuild the first, which would throw away what was written in it.
- **They can be moved and sized.** Each opens in the middle, which is where the work is, so a
  window routinely lands on the thing it was opened to look at, and a reference read beside a
  script wants to be narrow where a capture wants the screen. The web drags by the title bar and
  sizes by a **drawn grip** in the bottom-right corner; the desktop is a window and the window
  manager does both. Not `resize: both`: the browser draws that as four hairlines in the border
  colour, under the end of the body's scrollbar, where they read as part of the scrollbar.
- **What is inside follows the size.** A window whose content is docked on the desktop fills here
  too — the script editors, where the corner is what gives the room to the editor and the output
  (`.tool-body.fill`), and where the editor takes **two thirds** of what is left once the toolbar
  and the status line have their own heights — the split below it takes the other third, with a
  floor of 10 rem so a window dragged short cannot squeeze it below the caption, log and button
  row it holds. Shares of the remainder rather than percentages of the window: written as
  percentages the two of them ask for the whole body between them and the toolbar becomes an
  overflow, which on a short window pushed the status line out of the bottom. Those open large, as
  `ScriptForm` and `SequenceForm` do (1960×1640 and 1990×1400, both clamped to the screen). The
  rest size to their content and scroll.

  **The reference fills the same way, and both of its halves do.** `CommandReferenceForm` docks
  its list `Fill`, and `CommandLibraryForm` docks the viewer `Fill` in the panel beside it — so
  the list and the guide are each the height of the window and each grow with it. On the page
  they stand at three fifths of the viewport; in a window that ceiling and that floor left the
  shorter of the two carrying a strip of empty card under it, and gave the rest of the window's
  height to nothing at all. In a window the two halves are also **stretched to the same height**,
  because they are one thing read across: a guide that stops short of the list beside it is a
  column that ran out.
- **A filling window opens at the size its own desktop form asks for.** Filling is not one size:
  `MultimeterReadoutForm` asks for 980×520 and `DatasheetExtractForm` for 980×620, and either
  one given the editors' 1480×1040 is a window with nothing in the bottom half of it. The
  reference goes the other way — `CommandReferenceForm` opens at **1720×1120** and clamps it to
  the working area, which on most screens is the whole of it: several hundred commands beside
  the page they were transcribed from is not a window worth opening small.
  `ToolWindow.OpenWidth` / `OpenHeight` carry the desktop's client size, clamped to the viewport
  the way the stylesheet clamps its own, and the corner overrides both. The sizes are in
  `ConsoleTools.Opens`, except the AI script window's 1320×920 which is set where that window
  is opened. A filling window with no size of its own quietly took the editors' — which is what
  the datasheet extractor was doing, opening at the whole viewport for a drop box and a grid.
- **A window that fills in late is pulled back on screen.** A catalog of 24,000 commands and a
  language reference both arrive a moment after their window does, and one pinned where it was
  centred while still empty grows downwards off the bottom of the screen.
- **They belong to the instrument, not to the tab.** Bringing another instrument forward leaves
  them open, still on the instrument named in their title bar. On the web this is why they are
  built by the page rather than by the console (`ConsoleTools`): a console that is not on top is
  hidden, and a window inside a hidden subtree is one nobody can see and nobody can dismiss.
- **Nothing behind them is greyed out or out of reach.** They are `Show()` on the desktop and
  non-modal `<dialog>` here — see §6 for the two boxes that are not.

| Window | Controls, in order |
|---|---|
| **Script Editor** | toolbar, in **three** groups as `ScriptForm` counts them — `New` · `Open…` · `Save` · `Save As…` ▸ `Examples…` · `Snippets ▾` · `Script with AI…` ▸ `Run` / `Stop`. Script with AI is *inside* the middle group: "all three answer the same question — where a script comes from when you do not have one yet". The toolbar sits the same distance from the title bar as from the card under it. Then the editor, alone in its card — no caption over it and no status under it. Then the lower split: `Output` with `Clear Log` · `Save Log` on the left, the results pane on the right. Status along the foot of the window |
| **Multi-Instrument Scripts** | the same toolbar less `New` and `Save As…`, which `SequenceForm` does not have. Then **the binding strip**, docked between the toolbar and the editor and in the card with it: what each `DEVICE` line resolves to *right now* — a row per alias carrying its name, the model the script asks for, and a picker holding the open connection playing the part; red on the two naming columns when nothing is, with the picker saying why in the place the connection would be — `not connected`, `2 connected: pick one` or `taken by dmm`, the three reasons `SequenceForm` prints in brackets — and a grey sentence in place of the table when the script declares no instruments at all. It is one control, not a report and a form: on the web it was a line of text here **and** a `Bindings` card under the editor, the same three facts twice with the answer in the copy you could not read from where the complaint was. No lede above it either — the language belongs on the status line and in the reference, not in a band of prose between the title bar and the work. Kept filled as the script is typed and as instruments come and go — `SequenceForm` re-reads it on every keystroke and on a one-second timer — so there is **no `Check devices` button**, which is a press to be told what the window already knows. `Run` follows the strip: withheld while something named is missing, available when the script declares no instruments at all, because `PRINT`, `DELAY` and `RECORD` need none. Then the editor, the lower split and the status line, as the Script Editor has them |
| **Command Reference** | the command list, with `Filter:` **on its caption line, at the right-hand end** — the filter belongs to the list, and on the row that picks the catalog it read as a second thing to set before anything would appear; `CommandReferenceForm` gives it a strip of its own directly over the list. **Double-clicking a command puts it in the console's box** (`_list.DoubleClick += (_, _) => InsertSelected()`) — into the box, not onto the wire, so what to send and when to send it stay two decisions. `Copy` · `Insert` do the same from the foot. **Three columns** as `CommandReferenceForm` has them — the provenance mark, the command sized to the longest one listed, and the description taking the rest — with **the category as a heading over the commands in it** rather than a fourth column printing the same word thirty times; the desktop builds a `ListViewGroup` per category, and a category returned to later in the guide is still headed once. **One line per command**, cut with an ellipsis and carrying the whole of it as a tooltip, because a `ListView` row is one line and a list read by running the eye down the left edge stops working when every third entry is two lines tall. **No gridlines**: this is the one list in the desktop app built without them, and with the categories headed there is nothing left to rule off. Set a size smaller than the page, as the window sets Segoe UI 9pt. The marks are `MarkFor`'s: `◆` read out of a datasheet, `✓` confirmed on hardware, `•` corroborated by another driver |
| **Script Language Reference** | the lede; a **switch** between `Single instrument` and `Multi-instrument` with a line saying what the chosen one is for; then a card per idea: heading, prose, worked example. The examples are **coloured by the editor's own colouriser** — the same grammar and theme, as `ScriptReferenceForm` colours its own with the tokenizer its editor is built on — and each carries a `Copy`, which the desktop does not: there the reference is a rich text box and this is select-and-Ctrl+C |
| **Command Library** | filter box (`Filter by maker, model or command…`); the catalog tree; `Open Vendor Page`; the programming guide itself, beside the commands; `Set Datasheets Folder…` (desktop only — see §9) |
| **Screen Capture** | one card, and **the capture is already running when it opens** — the desktop's console takes the picture and only then builds `ScreenCaptureForm` around the bitmap, so the window never exists without one and there is nothing to press. **On the web the window opens first, so it says what it is doing while it does it**: a turning ring and `Capturing the instrument's screen…` until the picture lands, and `Capturing…` in the foot for every capture after that. The desktop says the same word in the console the moment it starts — `--- capturing screen (:DISPlay:DATA?) ---`. What must never stand there is the sentence for having no instrument at all: a transfer is seconds of an answer that is on its way, and a window that spends them claiming the connection is missing is claiming it about the connection it is using. The image, then along the foot: the size and type on the left, and `Capture Screen` · *gap* · `Copy` · `Save as PNG…` at the right. `Capture Screen` is kept, and set apart from the other two by the gap the console puts before `Send`, for the one thing the desktop can only do by closing the window and starting again: take the same screen after the instrument has moved on. It acts on the instrument; the two beside it act on what came back |
| **Waveform** | the same shape, and **three groups on its control row**: `Channels` ▸ a chip per channel, then `Capture Waveform`, then `Run` · `every N ms`. Then the trace, then along the foot the point count, a Vpp per captured channel and the span on the left, with `Save CSV…` and a **camera** at the right — the same glyph and the same gesture the plot carries, aimed at the trace as drawn here rather than at the instrument's own screen, which is the other window. The dark ground is drawn inside the SVG, not applied to it, so it survives being serialised into the image |
| **Readout** | **One card filling the window**, as `MultimeterReadoutForm` is docked: `Measure:` picker · `shown in` unit picker · `every` N `ms` ▸ `Start` / `Stop` along the top, on that form's own distances (§1); the large value and the run figures under it; the plot taking everything left; and `Clear` · `Save CSV…` at the end of the plot's picker strip, beside the button that saves the picture — the desktop has one row of actions along the foot of the window, and a second row of two buttons under a strip that already ends in empty space is a row spent saying nothing. **No caption**: the title bar says `Readout` and says which instrument as well. Opens 980×520 |
| **AI Datasheet Extraction** | the file on the left, what will read it on the right: a **drop box** of 18 rem by 6, and beside it a column carrying `Using:` (a picker of the connections), `Effort:` (web only so far — see §9), `Extract text locally before sending`, and `Extract` / `Stop`. The two stand in a frame of their own and the box is as tall as the frame — the column sets the height and the target fills it, because they are one control between them: a file, and what will be done with it. A wider gap between the two than the ordinary one between two controls, and air under the switch, so `Extract` reads as the end of the column rather than the third thing in a list. Then the grid — tick · `Command` · `Category` · `Description` — **always there, filling the window**; status and `Save Ticked` along the foot. The box is the web's answer to the desktop's path box: a browser will not be talked out of drawing its own control on a file input, so the input is hidden and the box stands in its place — dashed while empty, solid once it holds a name, and lit while a file is over the window. It is a target rather than a strip because that is what it is for, and there is **no `Browse…`** beside it: the box is the button, and a second control opening the same picker only asks which one to press |
| **Write a Script with AI** | what the model is being given, as a sentence and a bullet per instrument (`ScriptAiForm.ContextSummary`); then what the conversation costs — `N turn(s) — about X kB of transcript goes with every request` — and under that line **the conversation** itself, with `Clear` **in its own top-right corner**, over the first line of it, which is the nearest thing the pane has to a header. The window is a chat: every exchange stays on screen, oldest first, each carrying what was asked, what was written, a `Use This Script` of its own, and what the catalog check made of it — and **all of it goes back with the next request**, which is what makes "now do the same at 5 V" a request at all rather than a sentence about nothing. `Clear` forgets the lot, and the figure above it is characters rather than tokens — nothing here can count tokens and no two providers count them alike — but deciding whether to clear is the whole reason a number is there at all. **`Use This Script` belongs to the answer, not to the window**: under the script it takes, right-aligned, one per turn. A single one at the foot could only ever mean the newest, which in a conversation is the wrong one as often as it is the right one — you ask for a change, the change is worse, and the draft you wanted is three inches up the transcript with nothing to press. Then the composer, **under** the transcript the way a chat is laid out, and a clear step below it rather than against it: the request box is **one line, and as many as the text needs** — a pasted script or a Shift+Enter grows it to a ceiling, after which it scrolls inside itself rather than eating the transcript. **Enter sends; Shift+Enter starts a new line**, which is what Enter does in a chat. Sending empties the box, because what was in it is now in the transcript above. It **opens on a worked request, written in rather than greyed behind it** — a placeholder cannot be edited, selected or sent, and it goes the moment you type over it, so the one hint about how much detail helps is read by nobody; written, it is a request that already works. The same words stand behind the box once it is empty, as an example, which is all a placeholder can be. The options row is: `Using:` (a picker of the connections) **over** `Effort:`, `Revise the current script` **over** `Include the last run's output`, and `Write Script` at the end. Two stacks rather than one long line — five things do not fit the width of the request box, and a row that wraps puts its own button on a line of its own beside a field of nothing. **Both pickers, as the datasheet window has both** — which model and how hard to work it are one decision taken twice, and a window offering the first without the second sends you to the settings box for the other half. This is where the second half earns its keep: a sweep with a `WITH` block and a `FOR` in it is not transcription. The transcript is **there from the moment the window opens**, empty. **Everything runs the full width of the window** — transcript, box and both rows, edge to edge and lined up with each other. It was held to a 52rem measure, on the argument that a line of prose that long is hard to read back; asked for directly the other way, because this window is dragged to the width its reader wants and half of it standing empty is the worse answer. **The transcript takes the window and the composer keeps its own height**: the history scrolls and the box you type in does not move. Then, at the foot, the status line and nothing else. The conversation **outlives the window**: this one closes the moment a draft is used, so a history kept inside it would be a chat of exactly one turn — the desktop keeps a static keyed on the language, the web a `ScriptChats` service, and reopening opens on what it was closed with. The window opens at **1320×920**, which is what `ScriptAiForm` asks for; it is two text boxes and a row of switches and does not want the screen. **No instrument picker**: a console's script runs on that console's instrument, and the desktop asks nobody — `ScriptForm` hands over the one, `SequenceForm` the bench. The web keeps ticks in the multi-instrument editor only, where choosing among several catalogs is the point |

The script toolbar is **three groups** — the file pair; the three ways a script comes into being,
an example or a snippet or a model writing one; and running it — with a wider gap between groups
than within them. `Script with AI…` is in the middle group, not one of its own: `ScriptForm`'s
own comment says why — "all three answer the same question — where a script comes from when you
do not have one yet".

**Both editors colour what is typed**, in the desktop's own colours. `ScriptEditor.ColorFor`
gives comments green, keywords blue, an alias purple, a captured name orange and an operator
grey — and a SCPI command **no colour at all**, because it is the point of the line rather than
an annotation on it. The web uses Monaco, vendored under `wwwroot/lib/monaco` (see the README
beside it), with a Monarch grammar following `ScriptLanguage.Tokenize`; the colours are stated
once, in `wwwroot/js/monaco.js`.

One thing the web cannot copy exactly: the desktop colours `gen:` at the head of a line as an
alias, but only after checking the name against the aliases the script declared. A Monarch
grammar sees one line at a time and has nothing to check against, so a rule for it coloured
every SCPI mnemonic as an instrument name. Aliases are coloured where they are *declared* —
after `DEVICE` and `WITH` — and left alone where they are used.

**And both complete.** `ScriptLanguage.Complete` is what knows the language: the keywords, the
snippets, the aliases a sequence has declared and the names a capture has bound. The web asks
the server rather than keeping a second word list — two lists of what the language contains
would be one list and one guess. Ctrl+Space offers; a `$` offers captured names and nothing
else, because a `$` has exactly one meaning.

A capture window is **one card**: image, then the facts about it, then the buttons. Not three.

**A waveform window holds as many of those cards as are wanted** — one capture, drawn as
several pictures — and both builds do. A card is a set of channels: alone on a card a channel
has the vertical scale to itself, together on one card they share it, and which of those a
measurement wants is not something the app can know, so it is asked. Each card carries its own
channel pickers and its own ✕; a dashed frame under the last one adds another, landing on the
lowest channel no card is showing, so pressing it walks 1, 2, 3, 4.

**`Capture Waveform` · `Run` · `every N ms` sit above the cards**, in both builds. What
you press to fill them comes before what it fills, and a strip along the bottom of a stack of
cards reads as belonging to the last card rather than to the window.

- **One capture serves them all.** The union of what the cards ask for goes to the instrument
  once, so two cards showing two channels cost the same two reads as one card showing both.
  Unticking a channel redraws from what is already in hand, and a card added on a channel the
  capture already holds is drawn at once rather than waiting to be asked.
- **Each card zooms on its own.** Two cards on one capture are two views of it, and a zoom that
  moved both would make the second a copy of the first.
- **A card whose channels the capture does not hold says what to do about it** rather than
  reporting nothing: `Nothing on this card yet — press Capture Waveform.`
- **The last card can go too.** What is left is the frame that puts it back, which says what to
  do more plainly than a ✕ that refused to work would, and Capture and Run grey out while there
  is nothing to capture.
- **Four cards.** One per channel is as far apart as they go, and the frame goes away there.

This is the one thing in this document the web build led on: it was asked for there, ported to
`WaveformForm` after, and `Capture Waveform` came with it — the desktop window opens with a
capture already in hand and had only the Run loop, which is a poor way to ask for one picture.

### Capturing more than one channel

A scope with two probes on it is the ordinary case, and reading one channel against another is
what two probes are for. So the waveform window captures **several channels at once**, and both
builds do — neither did before, which is the one place in this document where the desktop was
following rather than leading.

- **A chip per channel**, four offered, each drawn in the colour the instrument itself draws that
  channel in: yellow, cyan, magenta, blue, as printed beside the BNC connectors. A trace in a
  colour the instrument does not use for it is a trace you have to look up. **The last channel on
  cannot be turned off** — a capture of nothing is a request with no answer.
- **One time axis.** Every channel of one capture is sampled on the same time base and the same
  trigger; that is what makes two traces comparable and is the whole reason to take them together.
  The times come from the first channel that answered, and a second copy per channel would be a
  megabyte of agreement.
- **One vertical scale**, across every channel drawn. Two traces drawn to two scales look like a
  comparison and are not one: a 100 mV ripple and a 5 V square wave would come out the same height.
- **A channel that cannot be read fails on its own.** The others are still drawn, and the foot says
  what happened to it. Asked for a channel it does not have, a Rigol does not refuse — it answers
  with two samples against fourteen hundred on the time base, so a length that disagrees with the
  axis is read as "no such channel" rather than drawn as a flat line across a channel that is not
  there.
- **The CSV is one time column then a column of volts per channel**, and it holds the whole record
  rather than the visible slice: the zoom is a way of looking at the capture, not a way of choosing
  what was measured.

The reading itself is Core's `ChannelCaptures.ReadAsync` — called directly by the desktop and
through its server by the web — so the rules above are stated once rather than twice: one channel
at a time because the connection carries one conversation at a time, the order asked for is the
order returned, and a channel that refuses or that answers against the wrong time base is reported
against that channel. `Tests/Capture/ChannelCapturesTests.cs` holds them.

### The programming guide

**Both builds show the guide beside the commands it documents.** `CommandLibraryForm` splits its
right half — the list on the left, the PDF on the right, a little over half the width to the list —
and the web does the same, dropping to one column under 1100px because a PDF beside four columns
of a command is not worth having.

- **The app ships none of them.** They are the manufacturers' copyright. The desktop points at a
  folder on the machine it runs on; the server keeps its own under `LEC_DATA/datasheets`, and the
  browser puts files into it by uploading — a server has nobody at a keyboard to point anywhere.
- **Filed under a folder per manufacturer**, which is how the desktop's collection is filed and
  what stops two vendors' identical model numbers picking each other's guide. An upload is filed
  under the manufacturer of the catalog it was uploaded for.
- **The matching is Core's** — `DatasheetLocator` — so the guide the desktop finds for a catalog is
  the guide the server finds. An uploaded file is looked up again by that matching rather than
  assumed to be the answer: a PDF whose name says nothing about the guide is saved and **says so**,
  rather than leaving an empty pane after an upload that appeared to work.
- **A path from a browser is not a path the server chose.** It is resolved and then required to
  still be under the collection, and to end in `.pdf`. Both refusals are 404, and both are tested.
- **No copy held says so, and says what to do**: `Open Vendor Page` to fetch one, or `Upload a PDF…`
  to put the one you already have where this build can read it. Those are the desktop's two answers.

---

### Zooming a trace

Both builds, the same gestures, the same numbers — Core's `WaveformView` does the arithmetic for
both, referenced by the web client rather than copied into it, because two implementations of one
rule are one rule and one guess.

| Gesture | What it does |
|---|---|
| Wheel | Slides the view sideways, 15% of the visible span per notch. Up goes earlier |
| Ctrl + wheel | Zooms about the pointer, ×1.25 per notch, so whatever is under it stays under it |
| Drag | Moves the trace with the hand; drag right and the view goes earlier |
| Double-click | The whole record again |

**And the same three as buttons**, on the card, at the right of its head and set apart from the ✕:
`+` · `−` · `|↔|`. A wheel and a drag are quicker once you know they are there, and nothing
on a plot says that they are. The buttons zoom **about the middle**, by the wheel's own ×1.25,
because a button has no pointer to zoom about — that is what the wheel is still for. Zoom out and
show-it-all grey at the whole record, where neither has anything to do, and both grey on a card
with no capture on it. Drawn as one control with three ends, because they are three answers to one
question.

**The line under the trace names the gestures**, whether or not it is zoomed:
`whole record — scroll to move, Ctrl+scroll to zoom`, becoming
`64.0% of the record — scroll to move, Ctrl+scroll to zoom, double-click to reset` once it is.
It used to appear only while zoomed, which is to say it told you about the wheel only after you had
found the wheel. The reset is still named only when there is something to reset.

The view is held as **fractions of the record, not sample indices**: a running capture replaces the
samples several times a second and a retriggered scope returns a different number of them, so
indices would move the view every time that happened. The **vertical scale follows the visible
slice** with 8% of air above and below — zooming into a ripple riding on a 5 V level is the reason
to zoom at all, and a fixed scale would leave it a flat line along the top. The times at the ends
of what is drawn are on the plot.

---

## 6. Modal boxes

**About** — the application icon, the name, the version with a `web build` pill on that
build, the blurb, then a definition list: `Catalogs:` · `Server:` · `Serving:` ·
`Source Code:`. Every figure is read at runtime — the version off the assembly, the runtime
and OS off the framework, the totals by counting the catalogs — so nothing on it can go stale
while looking authoritative. No OK button: it states facts and takes no decision. Esc closes.

**Both builds say where the source is**, and both read the address and the licence off the
assembly rather than carrying a copy — `AppInfo`, from the two lines Core's project file
already declares for the NuGet listing, so the desktop box, the web box and the package
listing cannot disagree. The desktop makes it a real link and opens it with the shell; the web
makes it an anchor. A build whose assembly carries no repository shows **no row at all**
rather than a dead one, which is the same rule the rest of the box is held to.

**AI Connection** — `Connection:` (a picker, with `New` and `Delete`) · `Name:` ·
`Provider:` · `Endpoint:` · `Model:` · `Effort:` · `API key:` · `Timeout (s):` ·
`PDF extraction:` (the `Extract text locally before sending` checkbox) · `Apply`. The key is
never shown back once stored; the desktop says so in the placeholder (`A key is stored. Type
to replace it.`). `New` and `Delete` are marks rather than words, and sit on the row
with the picker they act on: two labelled buttons under a dropdown read as a second row of
settings. There is no `Forget the key` — it existed while there was one connection and
losing its key meant losing the only one; with a list, the way to be rid of a key is to be rid
of the connection it belongs to.

**Effort** is one scale — `Provider default` · `Minimal` · `Low` · `Medium` · `High` — and
Core translates it per provider, because every provider offers this and every one spells it
differently: Gemini as `generation_config.thinking_level`, an OpenAI-compatible endpoint as
`reasoning_effort`, Anthropic as a token budget for an extended-thinking block (with
`max_tokens` raised to clear it, or the model thinks and has nowhere to write). **Provider
default sends no such field at all**, which is what a connection starts on and the only safe
setting for an endpoint that has never heard of it — some refuse a request carrying a
parameter they do not know. It is reachable from the windows that spend it as well as from
this box — the script writer in both builds, the datasheet window on the web only so far
(§9) — for the same reason the PDF checkbox is: it is a decision taken while looking at the
work it applies to. Setting it anywhere sets it on the connection itself, so no two places
can disagree about what is in force. `Tests/Ai/AiRequestTests.cs` pins the three shapes.

### Several connections

There is a **list** of AI connections, not one, because the model is a choice: reading a command
out of a two-column programming guide is transcription, and working out what a sequence of SCPI
ought to be is not, and the cheap fast model that suits the first is not the one you want for the
second. Each carries its own provider, endpoint, model, effort, timeout, PDF setting and key.

- **One selection, for the whole app.** Picking a connection anywhere picks it everywhere — the
  same rule the PDF switch and the effort setting are held to. Two places that can disagree about
  which key is being spent is one place too many.
- **Every window that spends one offers the list.** The desktop's datasheet extractor had a line
  reading `Using: Google Gemini · gemini-3.6-flash`; it is a picker now, and the script writer
  has the same one beside its switches. Both builds, all four windows.
- **A window whose connection has no key still shows the picker**, when there is more than one to
  choose from. Otherwise the one state where you most need to switch is the one that hides the
  way to switch.
- **Names are optional.** A connection is called after the model it reaches; two on the same model
  take the provider as well, and two the same all the way down take a number. `AiConnections.Labels`
  composes them, so every picker in both builds reads the same. The number counts within the group
  that shares the name, so deleting an unrelated connection does not renumber the rest.
- **Adding one selects it**, because adding a connection is how you say you want to use it.
  **The last one can go**: a bench with no AI connection on it is the state this starts in.
- **A key belongs to a connection, by id.** A connection can be renamed, pointed at another
  provider or moved up the list, and none of those loses the key stored against it. The desktop
  encrypts each with DPAPI for the signed-in account; the server writes them to its own file.
- **What was there before is folded in.** A settings file holding one connection and one key
  becomes the first entry of the list, keeping the key — nobody sets their connection up twice.

Both are modal, both grey out what is behind them, and **neither moves**.

---

## 7. Enabled, disabled, and what must never be disabled

A control that cannot do its job is **present and greyed**, with the reason in its tooltip.
Never absent: absent reads as a port that forgot it.

| Control | Live when |
|---|---|
| `Capture Screen` | the profile has a screen-capture command |
| `Capture Waveform` | the profile supports waveform capture |
| `Live Readout…` | the profile documents at least one readout function |
| `Export Results…` | the scan found something |
| `Save CSV` / `Clear Results` | there are rows |
| `Clear Log` / `Save Log` | there is a log |
| `Send` | the command box is not empty |

**What must not be disabled.** The quick commands and Send stay live while a command is in
flight. The connection carries one conversation at a time, but that is the connection's
business, not the console's: `SerializedInstrumentClient` holds the rest in order and the
strip above the log shows them waiting. Greying them until a reply came back made the queue
unreachable — there was no way to put a second command into a queue whose whole purpose is to
hold one.

The console **is** locked out while a script or a live readout is driving the same link
(`Session.IsBusy`, set in exactly those two places, and `SPEC.md` §7). That is a different
thing: another window has taken the connection for a run, not merely for one exchange. Live
Readout itself stays reachable while it polls — that window is how you stop it.

**What the lock reaches**, in both builds: the quick commands, the command box, Send, Discover
Commands (it sends commands of its own to find out what the instrument answers to), and the two
captures. **What it leaves alone**: Clear Log and Save Log, which act on the log rather than on
the wire; the tool windows themselves; and **Live Readout**, which is the exception — while the
readout is the thing driving the link, that window is how you stop it, and a lock that took away
its own key would be no use.

On the web the hold is kept **on the server**, not in the page that started the run
(`BenchService.Drive`, counted rather than a flag so a script and a readout can hold the same
instrument without one releasing the other's grip). It reaches every browser watching that
instrument, because the bench is one shared workspace and a console open in a second tab has the
same reason not to be typed into. `SessionDto.Driven` carries it, so a console opened mid-run
starts locked, and the hub's `Driven` message keeps it fresh.

---

## 8. Gestures

| | |
|---|---|
| Click a discovered row | puts it in the box that is showing, **as it would be typed** — `vxi://host` for a VXI-11 row, `host:port` for a raw socket, the port the scan actually found; on Serial, the port name with the rate that answered, `COM3?baud=115200` |
| Double-click a discovered row | connects |
| Right-click a console tab | the tab menu |
| `✕` on a tab | disconnects and closes |
| Enter in the command box | sends |
| Up / Down in the command box | command history for this instrument |
| Esc in any dialog | closes it |
| Click outside a **modal** | closes it — both ends of the press must land outside |
| Click outside a **tool window** | nothing. It is a window, not a popup |
| Drag a tool window's title bar | moves it, clamped so a grabbable strip stays on screen |
| F5 in a script editor | runs — exactly when `Run` would, never around it: while a `DEVICE` line has nothing to run on, the status line says which and why, in the strip's own words (`Not run — left → SDM3065X (2 connected).`), and nothing is sent. Pressed again mid-run, it is not a second run |

`Open in new tab` is a **link**, not a button calling `window.open`: nothing can pop-up-block
it, and the browser's own conventions come free — Shift for a separate window, Ctrl for a
background tab, middle-click for a tab. Its target is named after the session, so pressing it
twice returns to the console already open instead of starting a second.

**F5 on the web.** A browser's F5 is its reload key as well. A reload shuts a tool window, and
the script being written in it is lost. So the web takes the key only inside a script editor's
window. The desktop takes it anywhere in `ScriptForm` or `SequenceForm`, and here the window is
the tool window, or the tab when the editor is open in one. Everywhere else the key is left to
the browser: on the bench, which a reload does not disconnect (`index.html` says why), and in
every window that is not an editor, including the reference and the AI window opened from one.
`Ctrl+F5` and `Ctrl+R` are never taken, so a reload is always one keystroke away. Held down, F5
is one press, as holding a button down is one click. A key goes to the window that has the
focus. When nothing has it (`Run` greys out under the pointer as the run starts, and a
window closing under the focus leaves it on nothing), the key goes to the window last pressed
in, or to the window that one was open over if it has closed. The reason on the status line is
the picker's, so on the web it carries the remedy too: `(2 connected: pick one)`. Behind the key,
`Run` binds afresh before it starts, as `SequenceForm.RunAsync` does, because the table is only
as new as the last pause in typing. And `Run` is one run however it is pressed. Before this, a
double-click started two.

---

## 9. Where the web is allowed to differ

Only these. Each is something a browser forces, not a preference.

1. **Chrome.** A menu strip becomes one gear at the end of a title bar; the desktop's window
   frame becomes a title bar the page draws.
2. **Detaching.** The desktop reparents the control into a new window. A page cannot, so the
   console is rebuilt in a second browser tab against the same server-side session: the log
   starts empty, the instrument and its connection are untouched. Hence the rename to
   `Open in new tab` — the desktop's word described a window.
3. **File dialogs.** Open and Save become the browser's download and file-picker.
4. **Theme.** The desktop follows Windows; the web has to be told, so the gear menu carries a
   light / dark / system toggle.
5. **The status bar.** The desktop's `1 instrument connected.` label sits on the address row.
   The web has a status bar carrying that plus the catalog totals and the page name, because
   a browser window has a foot and nothing else was using it.
6. **Server-side paths.** Anything naming a folder names a folder *on the server*.
   `Set Datasheets Folder…` is **absent on the web**, and stays absent: a folder picker on a page
   would be picking a folder on the server's disk, which is not a thing the person at the browser
   can see or reach. The viewer it configured is no longer missing — see below.
7. **Scrollbars and wrapping.** Where the desktop clamps a gap to keep a box's width, the web
   may keep the width and let the row wrap. Same promise, kept the way a browser keeps it.

Everything else on the web that has no desktop counterpart is a debt, not a feature. It is
inventoried separately and awaiting a keep-or-drop decision.

### Known differences that are not on that list

Found while writing the tests, and recorded rather than quietly kept:

- **The scan-kind switch's fill.** The desktop's selected half is grey; the web's is still
  `--accent`. Not a decision, a decision half-made: the desktop was changed because nothing
  else in that window is accent coloured, and the web has yet to be looked at with the same
  question. Whichever way it goes, the two should end up saying the same thing, and §3.2
  carries the argument.

- **The idle status line.** `ScriptForm`, `SequenceForm` and `ScriptAiForm` all open with their
  status label reading `Ready.`. The web opens with it **empty** and keeps the line's height, so
  the first real message does not move anything. Asked for directly: a window that has just opened
  has nothing to report.

- **The binding strip is a table.** `SequenceForm` resolves each `DEVICE` line to a connected
  instrument and prints the answer as a line of text; there is nothing to choose. It does not guess
  between two instruments of one model (SPEC §9a): the line says `(2 connected)`, its tooltip says to
  name each by serial number or address, and the script says which — the remedy is not on the line
  itself, which at the window's narrowest would run the strip past its height and lose the rest.
  The web binds by the same rule — worked out on the server by the same Core code, so the two
  builds cannot bind one script two ways — and puts a picker against each alias, for the case of
  two instruments of one model on the bench: that is where the web says which plays which part. A
  pick is taken at its word, the same instrument for two aliases included, and making one keeps
  the rows already filled as they are rather than letting the rule hand one row's instrument to
  another. The picker is the web-only part.

  It used to be *both* — the desktop's line docked over the editor and a card of bindings below
  it, which is the same three facts about the same aliases said twice, and only one of the two
  answerable: the line reported that something was not connected and left you to go and find
  where to say otherwise. The table took the strip's place. Same position, same three states
  (an alias with a connection against it, one with nothing playing the part in red, and the
  grey sentence while the script names none), and the answer is where the complaint is.


- **`Effort:` in the datasheet window.** The web's datasheet half carries the picker beside
  `Using:`; `DatasheetExtractForm` does not yet, and its effort is set in the AI Connection
  box instead. The script writer carries it in both builds.

- **The Script Editor's starting script.** The desktop opens on a commented worked example —
  what a comment looks like, then `PRINT`, then a `REPEAT` with a `DELAY` inside it — which
  teaches the language in the place where it is needed (`ScriptForm.SampleScript`). The web
  opens on a single `*IDN?`. Both are a starting point rather than a blank page, which is what
  the tests assert; that they are not the *same* starting point is a gap. The **multi-instrument**
  editor is no longer one of these: both builds open it on `SequenceExamples.All[0]`, which is
  also what leaves `Revise the current script` with something to revise — the desktop ticks that
  switch when there is a script, and on the desktop there always is one.

The other one found that way — the console not locking during a run — is closed: §7 describes
what both builds now do, and `Web/tests/e2e/lock.spec.js` holds it there.

---

## 10. Checking a change

Not by screenshot. Open the desktop window and the web page side by side and go **control by
control, left to right, top to bottom**:

1. Is every control there? Same order?
2. Is the label the same string, character for character, including the ellipsis and the
   parenthesis?
3. Is the glyph the same glyph, at the size §1 gives it?
4. Do the numbers match — the range, the step, the default, the width?
5. Does it grey out under the same condition, and say why on hover?
6. Do the gestures work — click, double-click, right-click, Enter, Up, Down?
7. Are the distances the three of §1, and no fourth one?

A finding is a diff against this file. If this file is wrong, fix this file in the same change
— a spec that lost an argument with the code is worse than no spec.

Measure rather than eyeball. Both builds can be asked what they actually did: WinForms will
tell you a control's `Bounds`, and the browser will tell you a `getBoundingClientRect()`. Every
number in §1 was settled that way.

And for the web build, much of this pass is now automated. `Web/tests/e2e` drives a real
browser against a real server and asserts the inventories and the metrics above — the four
column headings letter for letter, the one control height, the optical factors, the three row
distances, the enable rules, and the one in §7 that matters most: that pressing a quick
command while another is in flight is *accepted*. It needs no bench; a fake SCPI listener
stands in for one. Run it before believing a UI change:

```bash
cd Web/tests && npm run test:e2e
```

What it cannot check is the desktop, which has no such harness — so the left-hand column of
every table above is still read by hand, and is still the thing being copied from.
