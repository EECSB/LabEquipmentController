// Rejoin words the extractor left broken across a line break — verified against the guide.
//
//     node tools/rejoin-split-words.js . [--apply]
//
// A guide breaks a word at the right margin with a hyphen. pdftotext keeps the hyphen and
// turns the line break into a space, so "measurements" reaches a catalog as "meas- urements".
//
// The obvious repair is wrong. "The instrument uses low- and high-frequency filters" has the
// same shape and means what it says: joining it spells "lowand". So nothing is joined on the
// shape alone. The guide decides — it is the same vocabulary the description came from, so
// the question "is this one word?" is answered by asking whether the guide ever writes it as
// one, somewhere it did not have to break.
//
// Two candidates are tried for "X- y":
//
//     Xy      the hyphen was the break         meas- urements  -> measurements
//     X-y     the hyphen was the word's own    front- panel    -> front-panel
//
// Exactly one of them has to appear in the guide. Both, or neither, and the text is left as
// it is and reported. A guide that only ever writes a word broken cannot confirm it, and an
// unrepaired description is honest in a way a guessed one is not.
const fs = require('fs');
const path = require('path');
const ROOT = process.argv[2] || '.';
const APPLY = process.argv.includes('--apply');

const GUIDE = {
  'bk-electronic-load': ['bk-8600.txt'],
  'chroma-power-supply': ['chroma-62000l.txt'],
  'gwinstek-scope': ['gwinstek-gds2000.txt'],
  'oscilloscope': ['rigol-ds2000a.txt'],
  'rohde-fsp-analyzer': ['rs-fsp.txt'],
  'rohde-fsq-analyzer': ['rs-fsq.txt'],
  'rohde-fsu-analyzer': ['rs-fsu.txt'],
  'rohde-spectrum-analyzer': ['rs-fpc.txt'],
  'siglent-scope': ['siglent-sds.txt', 'siglent-sds3000hd.txt'],
  'tektronix-scope': ['tek-mdo4000.txt', 'tek-mso4000.txt'],
  'keysight-multimeter': ['keysight-multimeter.txt'],
  'keysight-power-supply': ['keysight-e36300.txt'],
  'rohde-fsiq-analyzer': ['rs-fsiq.txt'],
  'rohde-fsl-analyzer': ['rs-fsl.txt'],
  'rohde-fsv-analyzer': ['rs-fsv.txt'],
  'rohde-fsw-analyzer': ['rohde-fsw.txt', 'rohde-fsw-list.txt'],
  'rohde-power-supply': ['rs-ngl200.txt', 'rs-nge100.txt', 'rs-hmp.txt'],
  'rohde-scope': ['rs-rtb2000.txt'],
};

// A break: letters, a hyphen, a space, then a lower-case continuation. The continuation must
// be lower-case — "Sets the range- Default" is two sentences colliding, not a broken word.
const SPLIT = /([A-Za-z]{2,})- ([a-z]{2,})/g;

// Nine the guide could not settle, decided by hand. In each case the rule did the right
// thing by refusing: the evidence is real but it is not in the vocabulary, which is the one
// place the rule looks.
//
//   high- end     The e36300 guide writes neither form. The sentence itself does: "the
//                 low-end point (MIN) must be selected and entered first, followed by the
//                 high- end point (MAX)". Its own parallel settles it.
//   to- gap       Inside "trigger-to- gap time", for a command named TRGTogap. The guide
//                 writes "trigger-to-gap" whole, but as one token — "to-gap" never stands
//                 alone, so the vocabulary never held it.
//   unaf- fected  The FSV guide breaks this word every time it uses it and never writes it
//                 out. Ordinary English, and the alternative is not a word.
//   soft- key     The NGL200 and NGE100 guides write "softkeys" — always the plural, so the
//                 singular is not in the vocabulary and neither candidate matched.
//   disap- peared The RTB2000 guide breaks this one every time as well.
//   source- measure  "This command does not trigger source- measure operations." The 8600
//                 guide writes neither half whole. The hyphen is the compound's own — a
//                 source-measure unit is what the term names — so it stays.
const BY_HAND = [
  ['bk-electronic-load', 'source- measure', 'source-measure'],
  ['keysight-power-supply', 'high- end', 'high-end'],
  ['rohde-fsiq-analyzer', 'to- gap', 'to-gap'],
  ['rohde-fsv-analyzer', 'unaf- fected', 'unaffected'],
  ['rohde-power-supply', 'soft- key', 'softkey'],
  ['rohde-scope', 'disap- peared', 'disappeared'],
];

/// Every word the guide writes, hyphens inside a word kept and at its edges dropped. A
/// newline is not a letter, so "meas-\nurements" yields "meas" and "urements" — the split
/// halves, never the whole. The vocabulary cannot manufacture the answer it is asked for.
function vocabulary(files) {
  const words = new Set();
  for (const f of files) {
    const text = fs.readFileSync(f, 'utf8');
    for (const t of text.split(/[^A-Za-z-]+/)) {
      const w = t.replace(/^-+|-+$/g, '').toLowerCase();
      if (w.length > 1) words.add(w);
    }
  }
  return words;
}

let joined = 0, left = 0, byHandDone = 0;
const unresolved = new Map();

for (const [cat, guides] of Object.entries(GUIDE)) {
  const cp = path.join(ROOT, 'Core', 'CommandData', cat + '.json');
  const files = guides.map(g => path.join(ROOT, 'tools', 'scpi-extract', 'manuals', g))
    .filter(fs.existsSync);
  if (!fs.existsSync(cp) || files.length !== guides.length) {
    console.log(`${cat}: guide missing, skipped`);
    continue;
  }

  const words = vocabulary(files);
  const byHand = BY_HAND.filter(h => h[0] === cat);
  const lines = fs.readFileSync(cp, 'utf8').split('\n');
  let here = 0, kept = 0, hand = 0;

  for (let i = 0; i < lines.length; i++) {
    if (!/"description"\s*:/.test(lines[i])) continue;
    let e;
    try { e = JSON.parse(lines[i].trim().replace(/,$/, '')); } catch { continue; }
    if (!e.description || !/[A-Za-z]{2,}- [a-z]{2,}/.test(e.description)) continue;

    let desc = e.description;
    for (const [, from, to] of byHand) {
      if (!desc.includes(from)) continue;
      desc = desc.split(from).join(to);
      hand++;
    }

    const fixed = desc.replace(SPLIT, (whole, a, b) => {
      const asWord = words.has((a + b).toLowerCase());
      const asHyphen = words.has((a + '-' + b).toLowerCase());
      if (asWord === asHyphen) {                    // both, or neither — the guide is no help
        kept++;
        unresolved.set(whole.toLowerCase(), (unresolved.get(whole.toLowerCase()) || 0) + 1);
        return whole;
      }
      here++;
      return asWord ? a + b : a + '-' + b;
    });
    if (fixed === e.description) continue;

    lines[i] = lines[i].replace('"description": ' + JSON.stringify(e.description),
      '"description": ' + JSON.stringify(fixed));
  }

  const text = lines.join('\n');
  JSON.parse(text);
  console.log(`${cat.padEnd(22)} rejoined ${String(here).padStart(4)}`
    + `   by hand ${hand}   left alone ${kept}`);
  joined += here; left += kept; byHandDone += hand;
  if (APPLY && (here || hand)) fs.writeFileSync(cp, text);
}

console.log(`\nrejoined ${joined}   by hand ${byHandDone}   left alone ${left}`);
if (unresolved.size) {
  console.log('the guide could not settle these:');
  for (const [w, n] of [...unresolved].sort((a, b) => b[1] - a[1]).slice(0, 25))
    console.log(`   ${String(n).padStart(3)}x  ${w}`);
}
console.log(APPLY ? '*** written ***' : '(dry run)');
