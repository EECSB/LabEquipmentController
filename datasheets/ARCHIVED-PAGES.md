# Archived pages

Web pages and forum threads saved while hunting for the vendor guides. This file is the
index; the saved HTML is git-ignored, for the same reason the guides are. More so for the
forum threads — those are their authors' words, not a vendor's.

Each is filed under its publisher, in the same maker folders the guides use — so a saved
page sits beside the guide it led to. Pendulum has a folder here and no catalog: the
handbook is the SCPI standard rather than one instrument's manual, and filing it by
publisher keeps the rule single.

Re-fetch any of them from the URL in the table. Nothing in the app reads them: the library
looks for `*.pdf` by name (see [README.md](README.md)) and ignores everything else.

All eight last re-fetched 7 August 2026; every URL in the table still answered.

## What these are for, and what they are not for

**None of this may become a catalog entry.** SPEC §10 says every command in a shipped
catalog is transcribed from a vendor programming guide, and a forum post is not one — it
is somebody's transcription, with no revision, no errata and no way to tell a typo from a
firmware difference. The threads below are here for two other reasons:

1. **Finding the guide.** Several official PDFs are behind registration walls, or on URLs
   the vendor's own product page gets wrong. The Chroma 63200A guide was found on a
   third-party mirror this way, and the B&K 9200B manual had simply moved.
2. **Knowing what to look for.** A thread listing commands an instrument answers but its
   manual never prints tells you the manual is incomplete. That is worth knowing. It is
   not worth shipping — see *Undocumented commands* below.

## Archived

| File | Source | Carries | Why it is here |
|---|---|---|---|
| `Rigol/eevblog-rigol-scpi-lists.html` | [EEVblog — Lists of Rigol SCPI commands](https://www.eevblog.com/forum/testgear/lists-of-rigol-scpi-commands/) | 88 distinct commands, plus per-model zips (DS2000, DS4000, MSO5000, DM3058, DM3068, DG5000, DSA1000…) | Community-dumped command lists for the whole Rigol range, including models with no usable guide layout |
| `Keysight/eevblog-keysight-scpi-lists.html` | [EEVblog — Keysight (full) lists of SCPI commands](https://www.eevblog.com/forum/testgear/keysight-dsox1200a-g-list-of-scpi-commands/) | 104 distinct commands | Same for Keysight; the DSOX1200A has no published programmer's guide |
| `Siglent/eevblog-siglent-sdm3055-scpi.html` | [EEVblog — Siglent SDM3055, SCPI and Python](https://www.eevblog.com/forum/testgear/siglent-sdm3055-multimeter-scpi-commands-and-python/) | a handful, mostly a Python discussion | Corroborates SDM behaviour on a model this bench does not have |
| `Pendulum/pendulum-scpi-handbook.html` | [Pendulum — Programmer's Handbook](https://manuals.pendulum-instruments.com/wp-content/uploads/manuals/scpi/scpi.html) | 118 distinct, the IEEE 488.2 common set and the SCPI standard tree | The clearest plain-language statement of the standard itself — useful when a vendor guide is ambiguous about a common command |
| `Rohde & Schwarz/rohde-fsv-manual-page.html` | [R&S — FSVA/FSV User Manual](https://www.rohde-schwarz.com/us/manual/rs-fsva-and-rs-fsv-user-manual_78701-29310.html) | no commands; the download page | The FSV/FSVA guide, since transcribed |
| `Rohde & Schwarz/rohde-fsw-manual-page.html` | [R&S — FSW User Manual](https://www.rohde-schwarz.com/us/manual/fsw-user-manual-manuals_78701-29088.html) | no commands; the download page | **The page that closed the FSW.** It had been noted here only that third-party mirrors 403 or 404. That was true and beside the point: this saved copy carries a direct link to R&S's own CDN — `scdn.rohde-schwarz.com/…/FSW_UserManual_en_56.pdf` — which serves it at 200 OK. Archiving a page is worth nothing if nobody reads what was archived |
| `Chroma/chroma-63600-manuals.html` | [Chroma — 63600 Series manuals](https://www.chromausa.com/document-library/manuals-63600-series/) | no commands; the document library | The 63600 modular loads, since transcribed into a catalog of their own. Chroma's own downloads need registration; a working mirror of the Operation Manual is [idm-instrumentos.es](https://idm-instrumentos.es/wp-content/uploads/2013/10/E_63600OP_0803.pdf) |
| `B&K Precision/bk-9130b-product.html` | [B&K Precision — 9130B](https://www.bkprecision.com/products/power-supplies/9130B) | no commands; the product page | The manual link this project first recorded as a 404 now resolves: [9130B](https://bkpmedia.s3.amazonaws.com/downloads/manuals/en-us/9130B_Series_manual.pdf), [9200B](https://bkpmedia.s3.amazonaws.com/downloads/manuals/en-us/9200_Series_manual.pdf). The 7 Aug 2026 copy also links a **separately-issued programming manual** for the 9130B — see below |

## The B&K 9130B programming manual

The re-fetch turned this up, and it closed the one open gap this project had recorded for
B&K: the 9130B user manual has no command reference and points at a programming manual
issued on its own, which was not found at the time. The product page now links it:

    https://bkpmedia.s3.us-west-1.amazonaws.com/downloads/programming_manuals/en-us/9130B_Series_programming_manual.pdf

547 KB, fetched 7 August 2026 and now sitting beside the other guides as
`BKPrecision_9130B_PowerSupply_ProgrammingManual.pdf`. Opened, and unlike the two documents
§8 records as false leads, this one is the real thing: 27 pages, a complete SCPI reference
for the 9130B / 9131B / 9132B — 19 IEEE 488.2 common commands and roughly 70–90 tree
commands across `APPLy`, `MEASure`/`FETCh`, `VOLTage`/`CURRent` with protection and limits,
`OUTPut` (state, timer, track, series), `INSTrument` (select, and combine parallel / series /
track), the full `STATus` operation and questionable trees, `SYSTem`, `TRIGger` and `DISPlay`.

**It is also misprinted, in ways a transcription must not carry through.** The manual prints
`:INSTument:ENABle?` beside `:INSTrument:ENABle`, `:ISUMmay1:ENABle?` beside `:ISUMmary1`,
and `:VOLTage:PROTection:TRIPed?` where every other vendor spells it `TRIPped`. Each of
those is a dropped letter in the query form of a command the same page spells correctly.

Transcribed as `bk-power-supply-9130b.json`: 144 entries, of which **22 carry a
`guideMisprint` note** — the syntax exactly as printed, plus what the guide prints, why it
looks wrong, and what to try instead. Correcting them silently would have been inventing
SCPI, which SPEC §10 forbids; shipping both spellings would have been the same thing in a
politer form. The library shows those entries with a **⚠**. Nothing here is bench-verified:
no 9130B has been on this bench, and the bench is the only thing that settles any of it.

Beyond the dropped letters the flags cover the manual's contradictions — an index and a
described entry that disagree on a node's name (`:DELey` against `DELay`, `:TIMer:DATA`
against `:TIMer:DELay`), a subsystem written `APPLY` in every heading and `APPLy` in every
query line, and one entry whose parameters were copy-pasted from the one above it and
describe currents for a command that sets output states.

## Undocumented commands

The Rigol thread carries `:CALibration:STARt`, `:CALibration:SET`, `:SYSTem:MAC` and others
that appear in no Rigol programming guide. Those are the strongest argument for the rule
rather than against it: an undocumented calibration command is undocumented because the
vendor does not want it driven from a script, and a catalog entry is an invitation to press
the button. They stay out.

If a user needs one, the console takes any command typed into it — the app has never
restricted what can be sent, only what it offers.

## Instrument self-report

Worth knowing before reaching for any of this: some instruments will list their own command
set. The app already tries — a console's **Discover Commands** sends `SYSTem:HELP:HEADers?`
and falls back to the family's catalog when there is no answer. Where it works it beats
every source on this page, because it comes from the firmware actually running.

## R&S RTM3000 User Manual

Fetched 7 August 2026 to settle 36 entries in the R&S scope catalog that the RTB2000 manual
does not print. That catalog's chapter is said to cover the RTM3000 and RTA4000 as well, and
this is the manual for the first of them — 17 MB, version 10 (1335.9090.02), from the
Batronix mirror, since R&S's own page serves it behind a chooser:

    https://www.batronix.com/files/Rohde-&-Schwarz/Oscilloscope/RTM3000/RTM3000_UserManual_en_10.pdf

It confirmed 15 of the 36 and, in the course of the check, showed that ten more were rows of
a parameter-value table rather than commands.

## R&S RTA4000 User Manual

Fetched 7 August 2026, from the TEquipment mirror after Batronix declined — 8.7 MB,
1335.7898.02:

    https://assets.tequipment.net/assets/1/26/Rohde___Schwarz_RTA4000_-_Manual.pdf

The third instrument the R&S scope catalog's chapter covers, and the one that settled its
last ten unattributed entries: seven are documented here and nowhere in the RTB2000 or
RTM3000 manuals. It also settled the other three, by omission — all three manuals print the
list of IEEE 488.2 common commands they support and none includes *SAV, *RCL or *TST?, so
those entries were removed rather than kept on the strength of being standard.

## Siglent SDS3000X HD Programming Guide (EN11F)

Fetched 7 August 2026 from the TestEquity mirror — 9.6 MB:

    https://assets.testequity.com/te1/Documents/pdf/siglent/SDS3000XHD_Series_ProgrammingGuide_EN11F_0125.pdf

The second guide the Siglent scope catalog covers, alongside EN11D. It accounts for 25 of
the 36 entries EN11D does not print — the SDS3000X HD's DVM, counter, measurement strategy
and menu-hide commands — and shows that :TRIGger:SENT:Source is a real command rather than
a misfiled :DECode:BUS<n>:SENT:SOURce. Nine IEEE 488.2 common commands are in neither
guide; see the catalog's source line for why they stay.
