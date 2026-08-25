// Draw a random sample of catalog entries and put each next to what its guide says.
//
//     node tools/sample-catalog-accuracy.js [n] [seed]     writes sample.json
//
// Reading the pairs is the work, and it is meant to be done by a person: the point is to
// measure how much of the catalogue is right, and no rule that could do the judging
// automatically would be independent of the rules the catalogs were built with.
//
// The first run — 100 entries, seed 20260815 — found 7 defective, and two of the seven
// belonged to classes of 323 and 89 that no amount of reading the catalogs had turned up.
// See docs/SPEC.md, "How much of this is right".
//
// Every other count in this repo answers "how many entries match a pattern someone thought
// to look for", which measures the search rather than the data. This draws a denominator: a
// uniform sample over all 23,978 entries with the guide's own text beside each, so the error
// rate can be judged instead of guessed at.
//
// The sample is uniform over ALL entries, not over the ones that happen to be checkable.
// Entries whose guide is not in this checkout stay in the sample and are counted as
// unverifiable — dropping them would quietly measure the best-documented half.
const fs = require('fs');
const path = require('path');
const N = Number(process.argv[2] || 100);
const SEED = Number(process.argv[3] || 20260815);

// Seeded so the exact sample can be redrawn and re-checked by anyone.
let s = SEED;
const rand = () => (s = (s * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;

const GUIDE = {
  'bk-electronic-load': ['bk-8600.txt'], 'bk-power-supply': ['bk-power-supply.txt'],
  'bk-power-supply-9130b': ['bk-power-supply-9130b.txt'],
  'chroma-power-supply': ['chroma-62000l.txt'], 'gwinstek-scope': ['gwinstek-gds2000.txt'],
  'gwinstek-gds1000b-scope': ['gwinstek-gds1000b-scope.txt'],
  'keithley-smu': ['keithley-2450.txt'], 'keithley-dmm': ['keithley-dmm6500.txt'],
  'keysight-multimeter': ['keysight-multimeter.txt'], 'keysight-power-supply': ['keysight-e36300.txt'],
  'keysight-scope': ['keysight-3000tx.txt'], 'multimeter': ['multimeter.txt'],
  'oscilloscope': ['rigol-ds2000a.txt'], 'rigol-electronic-load': ['rigol-dl3000.txt'],
  'rigol-multimeter': ['rigol-multimeter.txt'], 'rigol-spectrum-analyzer': ['rigol-dsa800.txt'],
  'rohde-fsiq-analyzer': ['rs-fsiq.txt'], 'rohde-fsl-analyzer': ['rs-fsl.txt'],
  'rohde-fsp-analyzer': ['rs-fsp.txt'], 'rohde-fsq-analyzer': ['rs-fsq.txt'],
  'rohde-fsu-analyzer': ['rs-fsu.txt'], 'rohde-fsv-analyzer': ['rs-fsv.txt'],
  'rohde-fsw-analyzer': ['rohde-fsw.txt', 'rohde-fsw-list.txt'],
  'rohde-power-supply': ['rs-ngl200.txt', 'rs-nge100.txt', 'rs-hmp.txt'],
  'rohde-scope': ['rs-rtb2000.txt'], 'rohde-spectrum-analyzer': ['rs-fpc.txt'],
  'siglent-generator': ['siglent-generator.txt'],
  'siglent-scope': ['siglent-sds.txt', 'siglent-sds3000hd.txt'],
  'tektronix-scope': ['tek-mdo4000.txt', 'tek-mso4000.txt'],
};

const FOOTER = /^(User Manual|R&S|Keysight|Tektronix|\d+$)|Remote control commands|^\s*\d+\s*$/;
const STOP = /^(Parameters?|Suffix|Setting parameters?|Query parameters?|Return values?|Example|Usage|Manual operation|Firmware|Options?|Note|Group|Syntax|Arguments|Returns)\b/;
const IS_HEADING = t => /^[A-Z*\[:][A-Za-z0-9*\[\]<>|:]*(\s*<[A-Za-z]+>|\?)?\s*$/.test(t) && /[:*<]/.test(t);
const norm = t => (t || '').replace(/\s+/g, ' ').trim();

function proseAt(man, i) {
  let out = [];
  for (let k = i + 1; k < man.length && k < i + 30; k++) {
    const t = man[k].trim();
    if (/^Description:?$/.test(t)) { out = []; continue; }
    if (!t) { if (out.length) break; continue; }
    if (FOOTER.test(t) || IS_HEADING(t)) continue;
    if (STOP.test(t)) break;
    out.push(t);
  }
  return norm(out.join(' '));
}

// The whole population, in a fixed order so the seed means something.
const pool = [];
for (const f of fs.readdirSync('Core/CommandData').filter(x => x.endsWith('.json')).sort()) {
  const j = JSON.parse(fs.readFileSync('Core/CommandData/' + f, 'utf8'));
  if (!j.commands) continue;
  const cat = f.replace('.json', '');
  j.commands.forEach((c, i) => pool.push({ cat, i, c }));
}

const picked = new Set();
while (picked.size < N) picked.add(Math.floor(rand() * pool.length));

const guides = {};
const out = [];
for (const idx of [...picked].sort((a, b) => a - b)) {
  const { cat, c } = pool[idx];
  let prose = '', why = '';
  const files = (GUIDE[cat] || []).map(g => path.join('tools/scpi-extract/manuals', g));
  if (!files.length || !files.every(fs.existsSync)) {
    why = 'no guide in this checkout';
  } else {
    if (!guides[cat]) guides[cat] = files.flatMap(f => fs.readFileSync(f, 'utf8').split('\n'));
    const man = guides[cat];
    const head = c.syntax.split(/\s/)[0].replace(/\?$/, '');
    const cands = [];
    for (let i = 0; i < man.length; i++) {
      const t = man[i].trim();
      if (t === c.syntax || t.split(/\s/)[0].replace(/\?$/, '') === head) cands.push(i);
    }
    // A contents page is long enough to pass for prose and says nothing. Every guide here
    // lists its commands two or three times before documenting them, so "the first block
    // over 25 characters" lands in the index far more often than in the entry.
    const isContents = p =>
      /\.{4}/.test(p)
      || (p.match(/\s\/\s\d{3,}/g) || []).length >= 3
      || (p.match(/\s\d{1,2}-\d{2,}/g) || []).length >= 2
      || p.split(/\s+/).filter(w => /^[:*]/.test(w)).length / p.split(/\s+/).length > 0.25
      || /Table of contents|Programming Reference$|Reference Manual$/.test(p);

    for (const i of cands) {
      const p = proseAt(man, i);
      if (p.length > 25 && !isContents(p)) { prose = p; break; }
    }
    if (!prose) why = cands.length ? 'heading found, only contents entries under it'
      : 'command not headed in the guide';
  }
  out.push({ n: out.length + 1, cat, syntax: c.syntax, desc: norm(c.description), prose, why });
}

fs.writeFileSync(process.env.OUT || 'sample.json', JSON.stringify(out, null, 1));
console.log(`drew ${out.length} of ${pool.length} entries, seed ${SEED}`);
console.log(`  with guide text: ${out.filter(o => o.prose).length}`);
const noWhy = {};
for (const o of out) if (!o.prose) noWhy[o.why] = (noWhy[o.why] || 0) + 1;
for (const [w, n] of Object.entries(noWhy)) console.log(`  ${String(n).padStart(3)}  ${w}`);
