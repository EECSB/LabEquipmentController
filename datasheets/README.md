# Datasheets

Where the app looks for the vendor programming guides its catalogs were transcribed from.

## This folder is empty when you clone it, on purpose

Two files are committed — this README and [ARCHIVED-PAGES.md](ARCHIVED-PAGES.md). Nothing
else ever is. `.gitignore` excludes `*.pdf`, `*.zip` and `*.html` here, so a working copy
can fill up with guides while the repository stays empty.

**Why:** the guides are the vendors' copyright. Keysight, Tektronix, Rigol, Siglent, R&S,
B&K, Chroma, GW Instek, Keithley and Fluke all publish them free to download, and free to
download is not a licence to redistribute. Committing them would republish forty vendor
documents under this project's name. The same reasoning covers the archived web pages and
forum threads, more so for the threads: those are their authors' words, not a vendor's.

**What that costs you:** nothing at runtime. The command catalogs are compiled into the
executable, so every command the app offers is there on a fresh clone. The guides are only
needed to *read* — the library's third column renders one beside the commands, and **Open
Local Copy** hands the file to your PDF reader. Without them the column says so and **Open
Vendor Page** takes you to the vendor's own download.

**How to fill it:** download the guides you care about from the vendor, drop them here, and
the library finds them. Name them as the library lists each catalog's expected filename, or
keep the vendor's own name and use Open Vendor Page instead.

## Where the app looks

Whatever is set under Help ▸ Command Library ▸ **Set Datasheets Folder…**, and when nothing
is set, this folder — found by walking up from the executable, so a build under `bin/` lands
here without anyone configuring it. On an installed copy there is no such folder above the
executable and the setting stays unset until the user picks one. See
`UserSettings.EffectiveDatasheetFolder`.

The app only ever reads these files. It never writes, moves or renames them.

## A folder per manufacturer

Guides are filed the way the library's tree presents them — one folder per maker, named
exactly as the tree names it:

```
datasheets/
  B&K Precision/   Chroma/    Fluke/     GW Instek/   Keithley/
  Keysight/        Rigol/     Rohde & Schwarz/        Siglent/     Tektronix/
```

The maker's own folder is searched first, then everything under `datasheets/`. Two things
follow from that: a flat folder still works exactly as it did — nobody has to reorganise —
and where two vendors ship a guide for a similarly numbered model, the folder is what
decides which one a catalog resolves to.

The saved web pages in [ARCHIVED-PAGES.md](ARCHIVED-PAGES.md) are filed the same way, so
the page that led to a guide sits beside it. That adds one folder the tree does not have —
`Pendulum/`, holding a handbook on the SCPI standard itself rather than any one
instrument's manual. Nothing reads it; it is filed by publisher because every other saved
page is.

`.gitignore` excludes `*.pdf`, `*.zip` and `*.html` **at any depth** here, not just at the
top. The single-level pattern it used to have would have quietly started tracking every
guide the moment they moved into these folders.

## Naming

The library shows the expected filename for each catalog. It follows the catalog name:

```
oscilloscope.pdf              Rigol MSO2000A/DS2000A Programming Guide
tektronix-scope.pdf           Tektronix MDO4000C/MSO4000B/DPO4000B Programmer Manual
keysight-scope.pdf            Keysight InfiniiVision 3000T X-Series Programmer's Guide
rohde-spectrum-analyzer.pdf   R&S FPC Spectrum Analyzer User Manual
rohde-fsw-analyzer.pdf        R&S FSW Signal and Spectrum Analyzer User Manual
rohde-fsu-analyzer.pdf        R&S FSU Spectrum Analyzer Operating Manual
rohde-fsp-analyzer.pdf        R&S FSP Spectrum Analyzer Operating Manual
rohde-fsq-analyzer.pdf        R&S FSQ Signal Analyzer Operating Manual
chroma-electronic-load.pdf    Chroma 63200A Series Operation & Programming Manual
…
```

Matching is on the file name, so correctly-named PDFs are found wherever under here they
sit — the maker folders above are for reading by, not a requirement.

Clicking an instrument renders its guide in the library's third column, using the Microsoft
Edge WebView2 runtime that ships with Windows 10 and 11. Where a guide has not been
downloaded the column says so; on a machine without the runtime it says that instead, and
Open Local Copy still hands the file to whatever owns `.pdf`.

## Why not bundle them

A published build is about 48 MB. The guides behind the catalogs come to several hundred
megabytes between them, and they are revised independently of this app — a bundled copy
would be stale the week after release, and wrong in a way that is hard to notice. Linking to
the vendor means the user always lands on the current revision.

The folder holds more guides than there are catalogs. A guide can be here to be read by the
AI extraction without anyone having transcribed a catalog from it — the R&S FSL manual is,
and at 1,701 pages it is also here twice, since only its command chapter fits inside a
provider's page limit.

## Also here

[ARCHIVED-PAGES.md](ARCHIVED-PAGES.md) indexes the web pages and forum threads saved while
hunting for these guides — where each guide was eventually found, and why nothing from a
forum thread may become a catalog entry. Same rule as the PDFs: the index is committed, the
saved copies are not.
