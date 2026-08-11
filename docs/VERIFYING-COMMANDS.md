# Verifying commands on your own instrument

A protocol for confirming SCPI commands against real hardware and contributing them back.
Written to be handed to an AI coding agent — point one at this file and at your instrument's
address and it has everything it needs — but every step is a person's job just as well.

**You are the only one who can do this.** The catalogs in this project are transcribed from
vendor programming guides, and 518 of 23,174 entries have ever been confirmed against a real
instrument, because this project has three instruments on its bench. Yours is a model nobody
here has. A command you confirm answers a question no amount of re-reading the guide can.

---

## 0. Before anything else

**Read [SPEC.md](SPEC.md) §10.** One rule governs everything here: *never invent SCPI*.
Every command in a catalog is transcribed from the vendor's own programming guide. Not from
a forum, not from another vendor's guide, not from a language model's memory of SCPI, and
not from what looked reasonable. If your instrument answers a command that its guide does
not print, that is an interesting finding and it still does not go in a catalog — say so in
the PR and let it be discussed.

**Safety.** These commands drive real equipment. Some of them source voltage, sink current,
or move a relay. Before you run a sweep:

- Disconnect the DUT. Run against an open circuit, a dummy load, or a known-safe fixture.
- Do not verify output-enabling commands (`OUTPut ON`, `:SOURce...`, load `INPut ON`) unless
  you have deliberately set up for it and know what is on the terminals.
- Skip anything under `CALibration`, `SYSTem:PASSword`, `*RST` on a configured instrument,
  or a firmware/update subsystem. A calibration command is undocumented or gated precisely
  because the vendor does not want it driven from a script.
- Know how to get the instrument back: front-panel Local, a power cycle, and where the
  factory-default recall is.

If you are an agent: **do not decide for the user which of these are acceptable.** Ask.

---

## 1. What counts as verified

A command is verified when, against the real instrument:

| | |
|---|---|
| **Query** | it returns a reply, and the reply is the shape the guide says (a number where a number is documented, a keyword from the documented set, a block where a block is documented) |
| **Setting** | it is accepted *and* its effect is observable — the matching query reads back what you set, or the front panel changes |
| **Not verified** | it was accepted and nothing was checked. An instrument that ignores an unknown command silently is common; acceptance alone proves nothing |

After every command, read the error queue. That is the actual test:

```
SYSTem:ERRor?          most vendors
:SYSTem:ERRor?         Rigol, Keysight, Siglent
ALLEv?                 Tektronix
STATus:QUEue[:NEXT]?   Rohde & Schwarz
```

`0,"No error"` after the command is the pass. `-113,"Undefined header"` is a fail, and it is
the failure worth reporting — it means the guide and the firmware disagree.

Drain the queue before you start, and check it once per command. Batching ten commands and
then reading one error tells you only that at least one of the ten was fine.

---

## 2. Running the sweep

The app has a **Multi-Instrument Scripts** window (Tools ▸) and a per-console script editor.
Either will do this. A script that verifies one command looks like:

```
# clear the queue, send, read back, check
SYSTem:ERRor?
:SOURce:VOLTage:LEVel:IMMediate:AMPLitude 1.5
:SOURce:VOLTage:LEVel:IMMediate:AMPLitude?
SYSTem:ERRor?
```

Read the log rather than the exit status. The runner reports what came back; you decide
whether it matches the guide.

**One command at a time, with the error queue between them.** It is slower and it is the
only way to attribute a failure to a command.

**Do not run a whole catalog unattended.** Read what a command does before you send it. The
catalogs contain commands that reset the instrument, clear its memory and change its
interface configuration — including the one you are talking to it over.

---

## 3. What to send back

Open a PR against `Core/CommandData/<family>.json`. Two shapes of contribution:

### Both shapes: add yourself to the verified-instruments table

Whichever kind of contribution you are making, **add a row to the "Verified instruments"
table in [README.md](../README.md)** for the instrument you tested, and put your name or
GitHub handle in the **Contributed by** column. If a row for that instrument is already
there, add your handle to the existing one rather than repeating the row — two people
confirming the same model on different firmware is worth more than one, and the table is
the only place that record lives.

| Instrument | Transport | Notes | Contributed by |
|------------|-----------|-------|----------------|
| Make and model, as `*IDN?` reports it | **VXI-11** or raw socket + port | Anything a future contributor needs to know — firmware quirks, a port that misbehaves, a command the guide gets wrong | your handle |

The table is what turns "518 of 23,174 entries are bench-verified" from a claim into
something traceable to a person and a piece of hardware. An entry marked `benchVerified`
whose instrument is in nobody's name is exactly the kind of unattributed assertion this
project refuses everywhere else.

### Confirming commands already in a catalog

Add `"benchVerified": true` to the entries you confirmed. Nothing else changes.

```json
{ "category": "Voltage", "syntax": "[SOURce:]VOLTage[:LEVel]?", "description": "…", "isQuery": true, "benchVerified": true }
```

In the PR description, say:

- The instrument's exact `*IDN?` reply — manufacturer, model, serial, **firmware version**.
  Firmware matters: the same model on different firmware is a different instrument for this
  purpose.
- How you connected: raw socket and port, or VXI-11.
- How you checked each one — which query read it back, or what changed on the panel.

### Adding commands a catalog does not have

Only from the vendor's programming guide for that instrument. Give, per command:

- `syntax` **exactly as the guide prints it**, including its capitalisation, which is what
  carries the short form: `VOLTage` shortens to `VOLT`, `VOLTAGE` does not shorten at all.
  Keep optional nodes in brackets — `[SOURce:]OUTPut[:STATe]`.
- `description` in the guide's own terms.
- `category`, matching the ones already in that catalog.
- `isQuery: true` on query forms.
- The guide: title, edition or document number, and the page. If the catalog's `guide` field
  names a different document, say which one yours is — several catalogs cover more than one
  manual and the field names only the first.

**If the guide is wrong**, which happens more than you would expect, do not fix it. Transcribe
it as printed and add a `guideMisprint` note saying what it prints, why it looks wrong, and
what to try instead:

```json
{
  "category": "Status",
  "syntax": "STATus:QUEStionable:INSTument:ENABle?",
  "description": "Returns the instrument query enable register value.",
  "isQuery": true,
  "guideMisprint": "'INSTument' is missing an r. Both the command index and the described entry print it that way, and both print 'INSTrument' for the set form directly above it."
}
```

Those entries show in the library with a **⚠** and their note in the tooltip. There are 45
of them today. Silently correcting a vendor's typo puts SCPI in the catalog that nobody
documented; shipping both spellings is the same thing in a politer form.

---

## 4. Checks that run on your PR

```bash
dotnet test Tests\LabEquipmentController.Tests.csproj --filter "FullyQualifiedName!~Bench"
```

The ones that will catch a malformed contribution:

- **`CatalogCoverageTests`** — every quick-command button, readout query and bundled script
  line must be an instance of a documented template. Adding a button without its command
  fails the build.
- **`No_catalog_command_is_a_truncated_line`** — brackets balance, and the header has a
  mnemonic in it.
- **`CatalogWrappedLineTests`** — a single-node entry repeating a longer entry's description
  is a wrapped line the extractor read as a command, not a command.
- **`GuideMisprintTests`** — a `guideMisprint` note has to say something useful.

A new catalog also has to be added to `CataloguedFamilies` and to one of the two lists in
`Tests/Bench/BenchInventoryTests`, which is how a catalog cannot land without a decision
about how it gets verified.

No test can check the README table — attribution is not something a machine can verify — so
it is on the reviewer to notice a PR that marks entries `benchVerified` without naming who
verified them, on what.

---

## 5. What is most wanted

In rough order:

1. **Bench ticks on any catalog with none.** Thirty-two of the thirty-five catalogs have
   never touched hardware. A model from any of them is valuable.
2. **The families that get no quick commands** — a Chroma 63800, an older R&S analyzer
   (FSU, FSP, FSQ) — because no guide reachable from here covers them. See
   the catalog's own `source` field, which records what its guide covers.
3. **The entries flagged as uncited.** The Siglent scope catalog carries nine IEEE 488.2
   common commands that neither of its guides documents; whether an SDS answers `*CLS` is
   one connection away for someone who owns one, and unknowable here.
4. **Misprints confirmed or refuted.** Every `guideMisprint` note ends in a guess. A bench
   turns it into a fact.

Corrections to what is already here are as welcome as additions. If a command in a catalog
does not work on your instrument, that is worth a PR on its own — say the model, the
firmware and the error the instrument gave.
