# scpi-extract

Turns a vendor SCPI programming guide into a catalog in `Core/CommandData/`.

Not part of the build. Run it when you want to add an instrument family, review the
JSON it produces, and commit that. Needs Node and `pdftotext` (xpdf/poppler) on PATH.

See [docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md#the-catalog-pipeline) for where this
pipeline sits in the project, and each catalog's own `source` field for what its guide
covers and what was left out of it.

## Adding a family

```bash
curl -sSL -A "Mozilla/5.0" -o manuals/vendor-model.pdf "<programming guide URL>"
pdftotext -layout manuals/vendor-model.pdf manuals/vendor-model.txt
node parse-manual.js manuals/vendor-model.txt <style> > parsed/vendor-model.json
```

`<style>` is one of:

| Style | Layout it reads | Seen in |
|---|---|---|
| `rigol` | `Syntax :CMD <arg>` … `Description …`, or a bare `Syntax` heading with the forms below | Rigol DP800, DG1000Z, DS2000A |
| `siglent` | `Command Format` / `Description` / `Example`, including the wrapped-label and `Instruction` variants | Siglent SDL1000X, SSA3000X |
| `sds` | one entry per page, the running page header carrying the command; labels interleave, so syntax forms are matched against the heading | Siglent SDS scope guides |
| `tek` | a column-0 heading over a labelled record: `Group`, `Syntax`, `Related Commands`, `Arguments`, `Examples` | Tektronix programmer manuals |
| `keithley` | a column-0 heading, a "This command …" line, then argument forms under `Usage` | Keithley 2450, DMM6500 reference manuals |
| `keysight2` | the description paragraphs, not the labels: "The :CHANnel&lt;n&gt;:SCALe command sets …" | Keysight programmer's guides |
| `block` | set and query forms as consecutive column-0 lines, then "The command …" / "The query …" | Keysight supply and generator guides |
| `rs` | an indented heading of command + parameter placeholder, description below, then `Suffix:` / `Parameters:` / `Usage:` — or the FSU-era shape: column-0 headings carrying plain-word parameter clauses (`ON \| OFF`, `0 to fmax`), prose-stated query exceptions, spaced suffixes (`<1 to 4>`), option lead-ins | R&S RTB2000, NGL200, NGE100, HMP, FSL, FPC, FSW, FSU, FSP, FSQ |
| `gw` | a column-0 heading, description below, then a `Syntax` block whose forms also sit at column 0 | GW Instek GDS programming manuals |
| `plain` | a column-0 command followed straight by prose, optionally with a labelled block carrying the argument forms — or by a `Type:` / `Description:` / `Setting Syntax:` label block, colon-closed | Chroma 62000L, 63600, B&K Precision 8600 |
| `heading` | the command alone on a line, indented prose beneath | generic fallback |
| `toc` | a dotted-leader command index — gives the full command list without descriptions | any guide with a command summary |
| `both` | `heading` + `toc` | Rigol DSA800, Keysight E36300 |

Add `--from='<regex>'` to skip front matter. The Tektronix guides need it — their
"Command Groups" tables look exactly like the reference section but have their rows
shifted by one, so parsing them pairs each command with its neighbour's description:

```bash
node parse-manual.js manuals/tek-mdo4000.txt tek "--from=^Commands Listed in Alphabetical Order"
```

Then write `cfg/<family>.json`:

```json
{
  "instrument": "DC power supply (Rigol DP800 series)",
  "source": "Transcribed from … . Cross-checked against … .",
  "groups": [{ "file": "parsed/vendor-model.json" }],
  "commonCommands": ["*IDN?", "*RST", "…"],
  "supplements": [{ "syntax": "…", "description": "…" }],
  "mergeInto": "existing-catalog.json"
}
```

- `commonCommands` — the IEEE 488.2 mnemonics *this guide documents*. Several guides
  print them in a column layout pdftotext renders inside-out, so they are listed rather
  than extracted. Check with:
  `grep -oE '\*(CLS|ESE|IDN|OPC|RST|LANG|…)\??' manuals/vendor-model.txt | sort -u`
  Include `*LANG` for Keithley: an instrument left in TSP mode answers no SCPI at all
  until it is switched over and rebooted.
- `supplements` — entries the PDF scrambles beyond recovery, read off the guide by eye.
  Still transcribed from the vendor manual; still validated like everything else. A
  config may be *only* supplements, with `"groups": []` — that is how the Fluke catalog
  is built, because its tree tables are not safely machine-readable (see below).

**Know when to stop parsing.** The Fluke 8845A/8846A guide is indented tree tables: the
path is encoded in the indentation, and pdftotext keeps the description column straight
in some tables and shifts it by a row in others, with nothing in the layout to tell them
apart. An automatic pass over it produced commands that do not exist
(`MEASure:CURRent:VOLTage`) paired with descriptions belonging to their neighbours. If a
guide fights back like that, transcribe it by hand into `supplements` and cross-check the
result against a driver corpus — do not ship a parser you cannot verify.

The `rs` style is the one place a parser emits a command the guide does not print. R&S
states in its SCPI conventions that *"a query is defined for each setting command unless
explicitly specified otherwise"*, and marks the exceptions per entry in a `Usage` field
(`Query only`, `Setting only`, `Event`). The parser reads that field and adds the query
form where the rule allows. If you point `rs` at a manual from another vendor, check
that the same convention holds before trusting the query forms.

**Check it holds for the R&S manual in front of you, too.** The older ones have no `Usage`
field — the FSL guide has none, against the FSW's 238 — and write the exception as a
sentence: *"This command is an event and therefore has no \*RST value and no query"*, 152
times. Reading only the field there makes every command look settable and the rule invents a
query for all of them, `*RST?` included. The parser now also reads that sentence, but the
next manual will find some third way of saying it. After running `rs` on a guide you have
not used before, check what fraction of the output is query forms and read a few of them
against the guide.
- `restrictTo` — a parsed command index from the *same* guide, used as the authority on
  what that guide documents. Anything extracted whose header the index does not list is
  dropped. Parse the index with the `toc` style and point at the result:

  ```bash
  node parse-manual.js manuals/vendor-model-list.txt toc > parsed/vendor-model-list.json
  ```

  Use it whenever a guide ships a "List of Commands" appendix. The FSW needs it: its
  programming examples are written in the abbreviated spelling, sit at a heading's indent
  and are followed by a comment line, so 46 of them parsed as entries — `INIT:CONT OFF`
  described as "Switches the sweep mode to single sweep." That is a real example of a real
  command, but it is not the documented form and nothing should offer it as one. The index
  drops all 46 without a hand-written blocklist that would go stale.

  The build reports what it turned away, because a silent restriction reads as "the guide
  documents exactly this much" when it does not.
- `guide` — the document this catalog came from (`title`, `edition`, `vendor`, `url`,
  `fileName`), emitted into the catalog for the Command Library to show. Was hand-added
  after emitting until the config started carrying it.
- `mergeInto` — an existing catalog to keep intact and only *add* to. Its hand-written
  descriptions and `benchVerified` flags win over anything extracted.
- `typos` — a `{ "wrong": "right" }` map applied to the syntax before validation, for
  typographical errors in the guide itself. The Chroma manual prints
  `VOLTaget:PROTection` a few times while spelling it correctly everywhere else;
  transcribing that faithfully would put a command in the catalog that the instrument
  rejects. Use it only where the same guide contradicts itself, and say so in `source`.

```bash
node build-catalog.js cfg/<family>.json > built-<family>.json
node emit.js built-<family>.json ../../Core/CommandData/<family>.json
```

Register the family in `InstrumentFamily`, `CommandReference.ResourceName`,
`InstrumentProfile`, and `ScriptExamples`, then:

```bash
dotnet test Tests/LabEquipmentController.Tests.csproj
```

`CatalogCoverageTests` will fail if any quick command, readout query or script line
isn't documented in the catalog you just built. That failure is the point — it is what
stops an invented command reaching a bench.

## Which catalogs this pipeline can actually rebuild

24 of the 35 catalogs in `Core/CommandData/` have a config here. The other 11 do not, and
the reason is not that nobody got round to writing them: **the pipeline as committed does
not reproduce them.** Measured in August 2026, by running every style against each guide and
diffing the result against the shipped catalog. Coverage counts only parsed entries carrying
a description, because `toc` emits headers with none and otherwise wins every comparison
while producing a catalog that fails the description guard.

Re-measured after the 63600/63200A/FSU parser rounds — build a draft config without
`mergeInto`, diff against the shipped file — and the honest answer is that **none of the
twelve rebuilds faithfully today**. The nearest is the FPC: one header missing, eight extra,
and 342 of 526 descriptions differing from what the current parser produces (its shipped
descriptions came from an older parser generation — an adoption pass like the 63200A's is
the path, and the extras include genuine rule-generated queries to judge). The fresh
numbers, worst deltas first: `rohde-fsl` misses 992 (its prose-exception situation, recorded
in `cfg/rohde-fsl-analyzer.json`), `keysight-multimeter` misses 174 with 182 extra (its alternations were
expanded by hand), `multimeter` misses 175 (same, plus hand supplements), `siglent-generator`
misses 315 (the channel-prefix substitution is deliberate policy), `gwinstek-gds1000b-scope`
rebuilds to 92 of 383, `bk-power-supply-9130b` misses 68, `bk-power-supply` misses 43 by its
best style (re-transcribed by hand, documented as never), `rigol-electronic-load` rebuilds
to 61 of 144, `rigol-multimeter` misses 71 with 60 extra, `rigol-spectrum-analyzer` is close
on syntax (29 missing, 2 extra) but 373 descriptions differ, and `rohde-fsv-analyzer`
misses 113 — though the FSU round taught the parser the `BANDwidth|BWIDth` alternated
headers, which its parse now reads (+101 entries) along with the FSL's (+58), all matching
what those shipped catalogs carry by hand. The FPC has since been adopted — see its
config — which moves it off this list; the FSV was re-measured after the alternation gains
and barely moved (112 missing, 74 extra), because its shipped entries spell those
subsystems differently again — then once more after the indented wrapped-clause join,
which completed six truncated ACPower headings and brought it to 100 missing, 74 extra.
And a `rohde-power-supply` rebuild is **not** the pure
two-entry repair it first looked like: built without its merge, it diverges in both
directions — 22 gained and 27 lost. Among the gained are the completed STEP queries, and
an annex artifact: the wrapped-clause join reads the NGL200's syntax-overview annex well
enough to emit its `VOLTage[:LEVel][:IMMediate][:AMPLitude]` pair, described by the
annex's own lead-in sentence. The losses
mix entries the current parser rightly refuses (a bare `EVENt`, `IDeNtification`, an
unanchored `TRIGger` — old-parse junk the shipped catalog still carries) with genuine
commands the current parse does not reach (`READ?`, `MEASure[:SCALar]:CURRent?`, the
ARBitrary group). That catalog needs the full audit before anything is adopted, and the
junk it ships is now a known defect rather than a surprise.

`chroma-modular-load` was the first taken off this list, and what it cost is the useful part
of the measurement. Its 98.8% header coverage was real and the build still produced 48
entries of 266, because six separate things stood between the parse and the catalog: the
guide labels every line and closes the label with a colon; it writes some entries' labels at
column 0, where `Type:` and `Description:` read as headings and ended the entry above them;
a syntax line that wraps carries the rest of its arguments on the next line but one; a bare
`MODE` is a command here and a table column heading two hundred pages earlier; and two
length caps — 120 characters of syntax and 100 of parameters — sat below the 136-character
program commands the catalog already carried. None of that is visible in a coverage figure.

| Catalog | Entries | Best style | Headers covered | Rebuilt to |
|---|---:|---|---:|---|
| chroma-modular-load | 286 | `plain` | 98.8% | **reproduced** — see cfg |
| chroma-electronic-load | 339 | `plain` | 97.8% | **reproduced** — see cfg; 64 before the fixes. Its value column floats a row against the labels, its labels are singular, and two headings are misprinted |
| rigol-spectrum-analyzer | 587 | `rigol` | 96.2% | 558, 381 descriptions differing |
| rigol-electronic-load | 144 | `rigol` | 95.3% | 58 |
| rohde-fsv-analyzer | 1279 | `rs` | 93.5% | adds 85 wrong entries |
| rohde-spectrum-analyzer | 537 | `rs` | 100.0% | **reproduced** — see cfg |
| rigol-multimeter | 186 | `heading` | 82.9% | — |
| keysight-multimeter | 390 | `plain` | 78.4% | — |
| bk-power-supply-9130b | 144 | `plain` | 77.0% | — |
| gwinstek-gds1000b-scope | 383 | `tek` | 69.8% | — |
| bk-power-supply | 84 | `rs` | 62.7% | — |
| multimeter | 206 | `plain` | 52.9% | — |
| siglent-generator | 343 | `heading` | 18.1% | — |
| rohde-fsl-analyzer | 2256 | `rs` | see its cfg note | adds 92 invented queries |

The high coverage figures are misleading on their own, which is the point of the last column.
`chroma-electronic-load` matches 97.8% of the catalog's headers at the *parse* step and then
builds to 64 entries of 314, because most are dropped afterwards. A config written from the
coverage figure alone would look right and produce a catalog a fifth the size.

Three of these bottom rows have an explanation in the catalog's own `source` field: the B&K
9200B was re-transcribed by hand, the Siglent SDM's alternations were expanded by hand, and
the SDG catalog substitutes a channel prefix the guide does not use. Those will never rebuild
from a style alone.

**So do not write a config for a catalog without checking what it builds.** Build it without
`mergeInto` and diff against the shipped file. With `mergeInto` set, the existing entries win
and a config that produces nine usable entries still reports "adds 0" — which is how three
configs got written here before the check caught them.

## Tests

```bash
node tests.js
```

Run it after touching `parse-manual.js` or `build-catalog.js`. Each case is a behaviour
that regressed, or nearly did, while the FSW and 63600 catalogs were built — the wrapped
placeholders, the prose-stated query exceptions, the de-hyphenation rules, the sectioned
page numbers, the length caps. Before this file existed, the only check was hand-diffing
old parser output against new across every manual in `manuals/`, which works exactly as
often as someone remembers to do it. The C# suite guards the shipped catalogs; this guards
the tool that writes them.

## Cross-check corpora

`build-catalog.js` marks an entry `crossChecked` when the same header appears in an
independent open-source driver. Rebuild the corpora with:

```bash
git clone --depth 1 https://github.com/pymeasure/pymeasure
node extract-pymeasure.js pymeasure/pymeasure/instruments pymeasure-commands.json

git clone --depth 1 https://github.com/python-ivi/python-ivi
git clone --depth 1 https://github.com/microsoft/Qcodes
git clone --depth 1 https://github.com/tektronix/tm_devices
git clone --depth 1 https://github.com/issus/NetworkedTestEquipment
node extract-generic.js generic-commands.json python-ivi/ivi \
  Qcodes/src/qcodes/instrument_drivers tm_devices/src/tm_devices \
  NetworkedTestEquipment/OriginalCircuit.Electronics.TestEquipment
```

On Windows, clone with `git -c core.longpaths=true` — several of these repos have paths
past `MAX_PATH`.

Both files are optional: without them nothing is marked cross-checked, and the build
still succeeds.
