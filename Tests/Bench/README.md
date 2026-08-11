# Bench tests

Tests that talk to real instruments. **Off by default** — a `dotnet test` on a machine with
no bench runs the whole offline suite and skips these, and must stay green.

## Running them

```bash
set LEC_BENCH=1
dotnet test --filter "FullyQualifiedName~Bench"
```

That is the whole setup. **Addresses are discovered, not configured** — the suite scans the
subnet once per run and keys on what each instrument answers to `*IDN?`.

This is not convenience. All three are on DHCP and they move: the two addresses this file
used to carry were both wrong within a day, and `192.168.1.19` — recorded as the scope — had
been reassigned to the generator. A test that reaches the wrong instrument fails somewhere
deep and confusing instead of at the connection, so a stale default is worse than none.

| Variable | Default | Purpose |
|---|---|---|
| `LEC_SUBNET` | `192.168.1` | The /24 to scan |
| `LEC_SCOPE` | *(discovered)* | Skip discovery for the scope |
| `LEC_GENERATOR` | *(discovered)* | Skip discovery for the generator |
| `LEC_MULTIMETER` | *(discovered)* | Skip discovery for the multimeter |
| `LEC_BENCH_REPORTS` | `<test bin>/bench-reports` | Where sweep reports are written |

Discovery throws rather than guesses if a family is missing or ambiguous, naming everything
it did find. Two scopes on the subnet would otherwise mean the sweep silently picks whichever
answered fastest, and produces a confident report about the wrong instrument.

All three connect over VXI-11. The generator exposes no raw socket at all, and the scope's
raw port lags replies by one query.

To check what is reachable without running anything else:

```bash
dotnet test --filter "FullyQualifiedName~An_instrument_is_recognised"
```

## What they check

**`FeatureBenchTests`** — the things a sweep cannot reach.

- Each instrument's `*IDN?` classifies to the family it should. Everything else in the app
  hangs off this: a wrong answer here gives every button, readout and catalog lookup the
  wrong answer at once.
- Every read-only quick command on each profile answers.
- The scope returns a waveform that *decodes* — not just the right number of points, which a
  wrong formula also produces, but sane voltages and monotonic time.
- The scope returns a screenshot that decodes as an image of a plausible size. This transport
  once truncated at 64 KB and returned a block that was the right shape and half the picture.
- The multimeter's readout queries all parse as numbers under invariant culture.
- Each instrument survives three connect/release cycles. The DS2202's firmware wedges under
  rapid reconnection, which is what a test run does to it.

**`CatalogSweepTests`** — sends every safely-sendable query in a catalog and writes a report.

This is what turns "transcribed from the guide" into "answered on the bench". It cannot fail
the build on a rejected command: a catalog covers a model *line*, and a DS2202 legitimately
does not implement everything the MSO2000A guide documents. What it produces is
`bench-reports/sweep-<instrument>.md` listing every command and what came back, plus
`answered-<instrument>.txt` — a plain list, so that stamping `benchVerified` later is a
deliberate act and not a side effect of running tests.

**Queries only, and only those needing no argument.** A catalog is mostly setting commands,
and a sweep that sent them would turn the generator's output on, move the scope's timebase,
or arm something. Channel suffixes are filled with 1, which every instrument here has.

Brackets mean two different things and are treated differently. An optional *argument* is
dropped — `SAMPle:COUNt? [{MIN|MAX|DEF}]` stands without its tail. An optional *node* is
kept: dropping `[:VOLTage]` from `MEASure[:VOLTage]:DC?` gives `MEASure:DC?`, a short form
SCPI permits, the guide never prints, and a Siglent SDM answers by hanging — which then reads
as a command the instrument does not support. That gives:

| Instrument | Sendable | of catalog |
|---|---:|---:|
| Rigol DS2202 | 437 | 1202 |
| Siglent SDG2042X | 104 | 343 |
| Siglent SDM3065X | 83 | 207 |

Only the Rigol has an error queue in its catalog (`:SYSTem:ERRor:NEXT?`), so only for the
scope can a sweep tell "understood" from "silently ignored". Neither Siglent guide documents
one. For those two, an answer is evidence and a timeout is not proof of absence.

The queue is drained after each command rather than read once. `:SYSTem:ERRor:NEXT?` pops a
single error and the queue outlives whatever filled it, so one read attributes whatever is at
the head to whoever happens to ask — which marked `:MEASure:VPP?` as an undefined header on a
scope that answers it perfectly well, because two commands earlier had timed out and left
their errors behind. That mistake only ever runs one way: a command can be wrongly called
rejected, never wrongly credited, so a stamped tick is safe even from a run predating the fix.

**Be sparing with the scope.** A DS2202 against the MSO2000A catalog meets a few dozen
commands it has no option for, each of which kills the link, and it has wedged off the network
twice under repeated full sweeps — once needing a power cycle mid-session. One sweep, then
leave it alone.

**`BenchInventoryTests`** — offline. Records that three of the twenty-one catalogued families
have hardware here and eighteen do not, and fails if a new catalog appears without a decision
about which side it falls on. The eighteen are not outstanding work; there is no such
instrument on this bench, and saying so once stops it being re-investigated.

## AI extraction

Separate switch, because a run costs money:

```bash
set LEC_AI=1
dotnet test --filter "FullyQualifiedName~AiExtraction"
```

It uses whatever provider and key the app already has — configure it once under
**Tools ▸ AI Connection** and the test reads it from there. Nothing to put anywhere.

The datasheet defaults to the Siglent SDM guide in `datasheets/`, overridable with
`LEC_AI_PDF`. That default is deliberate: its catalog is hand-transcribed and known, so the
test can report what fraction of a model's answer matches 207 commands read by eye. The last
run returned **211 commands, 89% of them recognised**, against a hand pass that found 207.

That fraction is printed, not asserted — a model reading 158 pages will legitimately find
commands the hand pass skipped and skip some it found. What *is* asserted is that something
came back and all of it cleared the SCPI shape gate.

It is compared on the header, not with `ScpiSyntax.MatchesAny`. That answers "does this
command a user typed fit this template", and both sides here are templates: feeding it two
put the figure at 62% when the truth was near 90, because `[SENSe:]CAPacitance:RANGe` and
`SENSe:CAPacitance:RANGe` are one command written two ways and it is not built to say so.

Everything unrecognised in that run was checked by hand against the guide text and appears in
it verbatim. Nothing was invented. The remaining gap is guide typography the model reproduced
faithfully — spaces inside brace groups, and a full-width `？` that really is in Siglent's PDF.

The upload-limit checks in the same file run offline and free.

## Safety

Nothing here changes an instrument's settings, turns an output on, or arms anything. The
generator tests read its configuration back without touching it. Every test hands the front
panel back when it finishes — leaving an instrument locked out is the kind of thing that gets
a suite switched off.
