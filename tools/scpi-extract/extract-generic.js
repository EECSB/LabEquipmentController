// Mine SCPI command strings out of instrument-driver source in several languages.
//
// Unlike the pymeasure extractor there is no single declarative form here, so this
// looks for string literals that *look like* SCPI: a colon-separated mnemonic path,
// an IEEE 488.2 common command, or a known bare header (MEAS, CONF, READ...).
const fs = require('fs');
const path = require('path');

const roots = process.argv.slice(3);
const out = [];

// A string is SCPI-ish if it is a ":FOO:BAR" path, a "*IDN?" common command,
// or starts with one of the standard bare root keywords.
const BARE = /^(MEAS|CONF|READ|FETC|INIT|ABOR|SENS|SOUR|OUTP|DISP|SYST|STAT|TRIG|SAMP|CALC|FORM|MMEM|HCOP|ROUT|CALIB|UNIT|INST|VOLT|CURR|RES|FREQ|FUNC|POW|APPL|WAV|ACQ|CHAN|TIM|HORI|VERT|DATA|CURVE|SWE|BAND|AVER|MARK|TRAC|LIST|PROG|ARM|DIAG|TEST|LXI|CONT|MODE)/i;

function scpiish(s) {
  if (!s || s.length < 3 || s.length > 100) return false;
  if (/[\n\t]/.test(s)) return false;
  if (/^\*[A-Z]{2,4}\??$/.test(s)) return true;              // *IDN? *RST *CLS
  if (/^:?[A-Za-z]{2,}(:[A-Za-z0-9]+)+/.test(s)) return true; // :SOUR:VOLT
  if (s.startsWith(':') && /^[:A-Za-z]/.test(s)) return true;
  if (BARE.test(s) && /[:?\s]/.test(s)) return true;
  return false;
}

// Reject prose, format strings and identifiers that merely look like paths.
function plausible(s) {
  if (/\s{2,}/.test(s)) return false;
  if (/^(https?|file|com|org|net)\b/i.test(s)) return false;
  if (/[<>{}\[\]]/.test(s) && !/[:?]/.test(s)) return false;
  if ((s.match(/ /g) || []).length > 6) return false;
  return true;
}

function walk(dir, exts) {
  let r = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (/^(\.git|node_modules|tests?|docs?|examples?)$/i.test(e.name)) continue;
      r = r.concat(walk(p, exts));
    } else if (exts.some(x => e.name.endsWith(x))) r.push(p);
  }
  return r;
}

for (const root of roots) {
  const label = path.basename(root);
  for (const f of walk(root, ['.py', '.cs'])) {
    const src = fs.readFileSync(f, 'utf8');
    const rel = path.relative(root, f).replace(/\\/g, '/');
    const model = path.basename(f).replace(/\.(py|cs)$/, '');
    for (const m of src.matchAll(/(['"])((?:\\.|(?!\1)[^\\\n])*)\1/g)) {
      const s = m[2].trim();
      if (!scpiish(s) || !plausible(s)) continue;
      out.push({ corpus: label, file: rel, model, cmd: s });
    }
  }
}

const seen = new Set();
const uniq = out.filter(r => {
  const k = r.corpus + '|' + r.model + '|' + r.cmd;
  if (seen.has(k)) return false;
  seen.add(k);
  return true;
});
fs.writeFileSync(process.argv[2], JSON.stringify(uniq, null, 1));
console.log('extracted', uniq.length, 'command strings from', new Set(uniq.map(r => r.corpus + '/' + r.model)).size, 'source files');
for (const c of new Set(uniq.map(r => r.corpus)))
  console.log('   ', c, uniq.filter(r => r.corpus === c).length);
