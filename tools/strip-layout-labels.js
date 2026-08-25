// Remove a guide's layout furniture from the descriptions it leaked into.
//
//     node tools/strip-layout-labels.js . [--apply]
//
// Both classes here are the same defect wearing two faces: a label that belongs to the page
// rather than to the sentence, joined into the sentence by an extractor reading lines.
//
// Rigol numbers a command's two forms:
//
//     :MEASure:FDELay
//     Syntax 1
//     :MEASure:FDELay <chanA>,<chanB>
//     Description 1
//     Enable the delay (falling edge-falling edge) measurement function between two channels.
//
// The word "Description" was stripped and its number was not, so 323 entries in the Rigol
// scope catalog open with a bare "1 " or "2 ". Only those two digits occur, and no
// description in that catalog begins with a number that is part of the sentence, so the
// digit can go.
//
// Keysight puts its labels in a left margin column, which pdftotext returns inline:
//
//                      The :ACQuire:COUNT? query returns the currently selected count value for
//            See Also  averaging mode.
//
// joins as "…count value for See Also averaging mode." The label sits in the middle of a
// sentence that reads correctly the moment it is taken out.
//
// Only labels proved to be furniture are removed, and only in the catalog where that was
// shown. "Errors" and "Mode" match the same shape in two other catalogs and are words there:
// the FSL's "Modulation Errors measurement" and Tektronix's "Horizontal Delay Mode" are the
// names of things, and stripping them would break 46 correct descriptions to fix none.
const fs = require('fs');
const path = require('path');
const args = process.argv.slice(2).filter(a => !a.startsWith('--'));
const ROOT = args[0] || '.';
const APPLY = process.argv.includes('--apply');

const RULES = {
  // The tail of "Description 1" / "Description 2".
  'oscilloscope': [[/^([12])\s+(?=[A-Z])/, '']],

  // Margin labels, each verified in the guide as a column rather than as prose.
  'keysight-scope': [
    [/([a-z,]) See Also (?=[a-z])/g, '$1 '],
    [/([a-z,]) Errors (?=[a-z])/g, '$1 '],
    [/([a-z,]) Return Format (?=[a-z])/g, '$1 '],
  ],
};

// Two GW Instek query forms carry their return-value table where their description should be:
//
//     :HARDcopy:LAYout?   "1 Color Portrait."
//     :HARDcopy:MODe?     "0 Save image 2 USB Printer."
//
// A different accident from the two above — nothing leaked in, the wrong block was taken —
// but it has the same shape from outside, a description opening on a bare digit, and it is
// what the leading-digit check finds once the Rigol ones are gone. Each set form already
// carries the sentence the guide prints, so the pair supplies the answer.
const FROM_PAIR = {
  'gwinstek-scope': [[':HARDcopy:LAYout?', ':HARDcopy:LAYout'], [':HARDcopy:MODe?', ':HARDcopy:MODe']],
};

let done = 0;
const problems = [];

for (const [cat, pairs] of Object.entries(FROM_PAIR)) {
  const p = path.join(ROOT, 'Core', 'CommandData', cat + '.json');
  if (!fs.existsSync(p)) continue;
  const j = JSON.parse(fs.readFileSync(p, 'utf8'));
  const lines = fs.readFileSync(p, 'utf8').split('\n');
  let here = 0;

  for (const [query, set] of pairs) {
    const src = j.commands.find(c => c.syntax === set);
    const at = lines.findIndex(l => l.includes('"syntax": ' + JSON.stringify(query) + ','));
    if (!src || at < 0) { problems.push(`${cat} ${query}: no pair to take it from`); continue; }
    const was = JSON.parse(lines[at].trim().replace(/,$/, '')).description;
    if (was === src.description) continue;
    lines[at] = lines[at].replace('"description": ' + JSON.stringify(was),
      '"description": ' + JSON.stringify(src.description));
    here++; done++;
    console.log(`${query}\n   was: ${was}\n   now: ${src.description.slice(0, 100)}\n`);
  }
  const text = lines.join('\n');
  JSON.parse(text);
  if (APPLY && here) fs.writeFileSync(p, text);
}

for (const [cat, rules] of Object.entries(RULES)) {
  const p = path.join(ROOT, 'Core', 'CommandData', cat + '.json');
  if (!fs.existsSync(p)) { console.log(`${cat}: not found`); continue; }
  const lines = fs.readFileSync(p, 'utf8').split('\n');
  let here = 0;

  for (let i = 0; i < lines.length; i++) {
    if (!/"description"\s*:/.test(lines[i])) continue;
    let e;
    try { e = JSON.parse(lines[i].trim().replace(/,$/, '')); } catch { continue; }

    let d = e.description;
    for (const [re, to] of rules) d = d.replace(re, to);
    d = d.replace(/\s{2,}/g, ' ').trim();
    if (d === e.description) continue;

    // What is left has to read as a sentence: it starts with a capital and ends on a stop.
    if (!/^[A-Z:*\[<]/.test(d) || !/[.!?]$/.test(d)) { problems.push(`${cat} ${e.syntax}: ${d.slice(0, 70)}`); continue; }

    lines[i] = lines[i].replace('"description": ' + JSON.stringify(e.description),
      '"description": ' + JSON.stringify(d));
    here++; done++;
    if (process.env.VERBOSE) console.log(`${e.syntax}\n   was: ${e.description.slice(0, 110)}\n   now: ${d.slice(0, 110)}\n`);
  }

  const text = lines.join('\n');
  JSON.parse(text);
  console.log(`${cat.padEnd(18)} cleaned ${here}`);
  if (APPLY && here) fs.writeFileSync(p, text);
}

console.log(`\ncleaned ${done}`);
for (const q of problems) console.log('   left alone, would not read as a sentence: ' + q);
console.log(APPLY ? '*** written ***' : '(dry run)');
