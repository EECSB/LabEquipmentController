#!/usr/bin/env node
//
// Run a catalog's whole recipe: parse each manual, build, emit, and say whether the result
// still matches what is shipped.
//
//   node rebuild.js cfg/rohde-fsq-analyzer.json           # rebuild and compare
//   node rebuild.js cfg/rohde-fsq-analyzer.json --write   # ... and overwrite the catalog
//   node rebuild.js --all                                 # compare every config
//
// This exists because the recipe used to live nowhere. cfg/ recorded which *parsed* files a
// catalog was built from, and parsed/ is regenerated output that is not committed — so the
// step that turns a manual into those files, the one carrying the style name and any
// --from flag, was known only for as long as someone remembered running it. Nine catalogs
// were reported as "cannot be rebuilt from their manual" for want of a config; the truth
// was worse and quieter, in that no catalog could be rebuilt from its manual, config or
// not. The `parse` block in each config now records that step, and this runs it.
//
// A recipe's `known` field says how far to trust it:
//
//   measured    every style was run over the dump in this checkout and this one's headers
//               matched the shipped catalog best. Re-runnable here, now.
//   documented  stated in README.md or in parse-manual.js. The dump may not be here.
//   inferred    the reader that this vendor and series belong to, never run against this
//               guide. Treat a rebuild from one of these as a first draft, not a check.
//
// A dump that is not in this checkout is not a fault to fix here: manuals/ is ignored, for
// the same reason datasheets/ is — vendor PDFs are free to download and not ours to
// redistribute. Each config's `guide` block names the document and links the vendor page,
// and README.md gives the pdftotext line that produces the dump.
//
// "differs" is the normal answer, and --write is rarely the right response to it. A shipped
// catalog is extraction plus curation, and only the first half is reproducible here: a
// re-emit measured across every catalog gained 153 entries and lost 129, and the losses were
// real — *CAL?, ALIas?, CH<x>?, :ERRor?, the GW Instek's whole :HARDcopy tree — while the
// gains included "*PSC {OFF|ON|NR1>}" with a bracket missing and, in the R&S supply catalog,
// the parameter values that had been deleted by hand hours earlier. Use this to see what a
// guide holds and to build a catalog the first time; adopt anything it turns up afterwards
// one entry at a time.

const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const HERE = __dirname;
const args = process.argv.slice(2);
const write = args.includes('--write');
const all = args.includes('--all');
const targets = args.filter(a => !a.startsWith('--'));

function run(file, argv) {
  return execFileSync(process.execPath, [path.join(HERE, file), ...argv],
    { cwd: HERE, maxBuffer: 1 << 28, stdio: ['ignore', 'pipe', 'pipe'] }).toString();
}

function rebuild(cfgPath) {
  const name = path.basename(cfgPath, '.json');
  const cfg = JSON.parse(fs.readFileSync(path.join(HERE, cfgPath), 'utf8'));
  const shipped = path.join(HERE, '..', '..', 'Core', 'CommandData', name + '.json');

  if (!(cfg.parse || []).length) return [name, 'skipped', 'no parse recipe'];

  // build-catalog reads the paths the config names, so a rebuild has to write there. On a
  // comparison run the previous contents are put back afterwards: parsed/ is regenerated
  // output and not committed, but the files sitting there are what the shipped catalogs
  // were actually built from, and a survey should not quietly replace them.
  const saved = [];
  for (const step of cfg.parse || []) {
    if (!fs.existsSync(path.join(HERE, step.manual))) {
      return [name, 'skipped', `${step.manual} is not in this checkout`];
    }
    const at = path.join(HERE, step.input);
    saved.push([at, fs.existsSync(at) ? fs.readFileSync(at) : null]);
    fs.writeFileSync(at, run('parse-manual.js', [step.manual, step.style, ...(step.args || [])]));
  }
  const restore = () => {
    if (write) return;
    for (const [at, prev] of saved) {
      if (prev === null) fs.unlinkSync(at); else fs.writeFileSync(at, prev);
    }
  };

  let built;
  try { built = run('build-catalog.js', [cfgPath]); }
  catch (e) { restore(); return [name, 'failed', 'build: ' + String(e.stderr || e.message).split('\n')[0]]; }

  const tmp = path.join(HERE, `built-${name}.json`);
  fs.writeFileSync(tmp, built);
  const emitted = path.join(HERE, `rebuilt-${name}.json`);
  try { run('emit.js', [tmp, emitted]); }
  catch (e) { restore(); return [name, 'failed', 'emit: ' + String(e.stderr || e.message).split('\n')[0]]; }

  const now = fs.readFileSync(emitted, 'utf8');
  const was = fs.existsSync(shipped) ? fs.readFileSync(shipped, 'utf8') : null;
  const same = was !== null && now === was;

  if (write) { fs.copyFileSync(emitted, shipped); fs.unlinkSync(emitted); return [name, 'written', '']; }
  fs.unlinkSync(emitted);
  restore();

  if (same) return [name, 'identical', ''];
  const count = s => (JSON.parse(s).commands || []).length;
  return [name, 'differs', was === null ? 'no shipped catalog'
    : `${count(was)} shipped vs ${count(now)} rebuilt`];
}

const list = all
  ? fs.readdirSync(path.join(HERE, 'cfg')).map(f => 'cfg/' + f)
  : targets;

if (!list.length) {
  console.error('usage: node rebuild.js cfg/<family>.json [--write] | --all');
  process.exit(2);
}

const rows = [];
for (const c of list) {
  try { rows.push(rebuild(c)); }
  catch (e) { rows.push([path.basename(c, '.json'), 'failed', String(e.message).split('\n')[0]]); }
}

const tally = {};
for (const [n, s, d] of rows) {
  tally[s] = (tally[s] || 0) + 1;
  console.log(n.padEnd(28) + s.padEnd(11) + d);
}
console.log('-'.repeat(60));
console.log(Object.entries(tally).map(([k, v]) => `${k}: ${v}`).join('   '));
