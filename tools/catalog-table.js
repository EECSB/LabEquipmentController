#!/usr/bin/env node
//
// Refresh the catalog table in docs/SPEC.md from the catalogs themselves.
//
//   node tools/catalog-table.js            # rewrite the table
//   node tools/catalog-table.js --check    # exit 1 if it is out of date
//
// The table has said "the counts are generated from the catalogs rather than kept by hand"
// for a while, and that was half true: they had been read out of the catalogs once, by hand,
// and every edit since drifted. Cross-checking 1,338 more entries would have left twenty
// rows reading 0.
//
// Only the three numeric columns are touched. The Source column is prose — which guide, and
// what is odd about it — and belongs to whoever wrote the row.

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const SPEC = path.join(ROOT, 'docs', 'SPEC.md');
const DATA = path.join(ROOT, 'Core', 'CommandData');

// Row heading -> catalog file. The headings read as an engineer would say them and the
// files are named for the vendor, so the two cannot be derived from one another.
const FAMILY = {
  'R&S FSW analyzer': 'rohde-fsw-analyzer',
  'R&S FSU analyzer': 'rohde-fsu-analyzer',
  'R&S FSP analyzer': 'rohde-fsp-analyzer',
  'R&S FSQ analyzer': 'rohde-fsq-analyzer',
  'R&S FSL analyzer': 'rohde-fsl-analyzer',
  'R&S FSV analyzer': 'rohde-fsv-analyzer',
  'R&S FSIQ analyzer': 'rohde-fsiq-analyzer',
  'R&S scope': 'rohde-scope',
  'R&S spectrum analyzer': 'rohde-spectrum-analyzer',
  'R&S power supply': 'rohde-power-supply',
  'Tektronix scope': 'tektronix-scope',
  'Keysight scope': 'keysight-scope',
  'Keysight multimeter': 'keysight-multimeter',
  'Keysight power supply': 'keysight-power-supply',
  'Oscilloscope': 'oscilloscope',
  'Siglent scope': 'siglent-scope',
  'Siglent generator': 'siglent-generator',
  'Rigol spectrum analyzer': 'rigol-spectrum-analyzer',
  'Rigol multimeter': 'rigol-multimeter',
  'Rigol electronic load': 'rigol-electronic-load',
  'Waveform generator': 'scpi-generator',
  'GW Instek GDS-1000B scope': 'gwinstek-gds1000b-scope',
  'GW Instek scope': 'gwinstek-scope',
  'Keithley multimeter': 'keithley-dmm',
  'Keithley SMU': 'keithley-smu',
  'Chroma electronic load': 'chroma-electronic-load',
  'Chroma modular load': 'chroma-modular-load',
  'Chroma power supply': 'chroma-power-supply',
  'Power supply': 'power-supply',
  'Electronic load': 'electronic-load',
  'Multimeter': 'multimeter',
  'Spectrum analyzer': 'spectrum-analyzer',
  'B&K triple-output supply': 'bk-power-supply-9130b',
  'B&K electronic load': 'bk-electronic-load',
  'B&K power supply': 'bk-power-supply',
  'Fluke multimeter': 'fluke-multimeter',
};

const stats = {};
for (const [row, file] of Object.entries(FAMILY)) {
  const p = path.join(DATA, file + '.json');
  if (!fs.existsSync(p)) { console.error('no catalog for row "' + row + '" (' + file + ')'); process.exit(2); }
  const cmds = JSON.parse(fs.readFileSync(p, 'utf8')).commands || [];
  stats[row] = {
    entries: cmds.length,
    bench: cmds.filter(c => c.benchVerified).length,
    cross: cmds.filter(c => c.crossChecked).length,
  };
}

const lines = fs.readFileSync(SPEC, 'utf8').split('\n');
let changed = 0, seen = 0;
for (let i = 0; i < lines.length; i++) {
  const m = lines[i].match(/^\| ([^|]+?) \| *\d+ \| *\d+ \| *\d+ \|(.*)$/);
  if (!m) continue;
  const [, row, tail] = m;
  const s = stats[row];
  if (!s) { console.error('table row with no mapping: "' + row + '"'); process.exit(2); }
  seen++;
  const fresh = `| ${row} | ${s.entries} | ${s.bench} | ${s.cross} |${tail}`;
  if (fresh !== lines[i]) { lines[i] = fresh; changed++; }
}

const missing = Object.keys(FAMILY).length - seen;
if (missing !== 0) { console.error(`${seen} rows in the table, ${Object.keys(FAMILY).length} families mapped`); process.exit(2); }

// The totals sentence above the table counts the same things.
const total = Object.values(stats).reduce((a, s) => a + s.entries, 0);
const bench = Object.values(stats).reduce((a, s) => a + s.bench, 0);
const cross = Object.values(stats).reduce((a, s) => a + s.cross, 0);
const summary = `${Object.keys(FAMILY).length} catalogs, ${total.toLocaleString('en-US')} entries, `
  + `of which ${bench} carry a bench tick and ${cross.toLocaleString('en-US')} a cross-check.`;
for (let i = 0; i < lines.length; i++) {
  if (!/^\d+ catalogs, [\d,]+ entries, of which /.test(lines[i])) continue;
  if (lines[i] !== summary) { lines[i] = summary; changed++; }
}

if (process.argv.includes('--check')) {
  if (changed) { console.error(`docs/SPEC.md catalog table is out of date (${changed} lines).`); process.exit(1); }
  console.log('catalog table is current.');
  process.exit(0);
}

if (changed) fs.writeFileSync(SPEC, lines.join('\n'));
console.log(`${seen} rows checked, ${changed} lines updated.`);
console.log(summary);
