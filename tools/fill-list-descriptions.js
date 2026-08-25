// Fill the descriptions that end on a colon — anchored on the command, and verified.
//
//     node tools/fill-list-descriptions.js . [--apply]
//
// Some descriptions end on a colon because the guide ends on a colon: the sentence introduces
// a list, and the list is a table the extractor could not follow. The sentence is true and
// finished, so CatalogDescriptionTests lets it through, but it promises values the catalog
// does not carry.
//
// The first attempt at this searched the guide for the description's last few words and took
// whatever followed. That is unanchored: those words recur, and for STAT:CLE — "Using TSP
// commands:" — it landed on a passage about changing the password and would have attached
// localnode.password = "password" to a status-clear command.
//
// The anchor is the command. Find where the guide *heads* this exact command, require the
// catalog's own sentence to appear under that heading, and only then read the rows. Two
// checks have to agree before a word is taken, and a miss is reported rather than guessed.
//
// It refuses far more than it fills, and that is the point — every rule below was written
// after watching this tool produce something wrong. What it will not do is the honest
// remainder: where a guide's table has sheared in the text layer, or nests a list inside a
// list, no amount of reading recovers the order, and those are hand work against the PDF.
//
// Needs the guides in tools/scpi-extract/manuals/, which are not in the repo — see
// tools/scpi-extract/README.md.
const fs = require('fs');
const path = require('path');
const ROOT = process.argv[2];
const APPLY = process.argv.includes('--apply');
// Long enough for the longest entry a guide actually writes. The FSP's two GSM trigger
// commands run to nine hundred characters each and are one sentence short of each other; a
// cap between them would ship one and refuse its twin for no reason a reader could see.
const CAP = 1000;

const GUIDE = {
  'chroma-power-supply': 'chroma-62000l', 'keithley-smu': 'keithley-2450',
  'keysight-multimeter': 'keysight-multimeter', 'keysight-power-supply': 'keysight-e36300',
  'keysight-scope': 'keysight-3000tx', 'multimeter': 'multimeter',
  'rohde-fsiq-analyzer': 'rs-fsiq', 'rohde-fsl-analyzer': 'rs-fsl',
  'rohde-fsp-analyzer': 'rs-fsp', 'rohde-fsq-analyzer': 'rs-fsq',
  'rohde-fsu-analyzer': 'rs-fsu', 'rohde-fsv-analyzer': 'rs-fsv',
  'rohde-fsw-analyzer': 'rohde-fsw', 'siglent-generator': 'siglent-generator',
};

const STOP = /^(Example|Parameters?|Return|Usage|Suffix|Characteristics|Mode|Manual operation|Options?|Firmware|Note|NOTE|Query|Command|Syntax|Description|Default|Menu|Type|Range|Features?|Explanation|See Also|Related|Instruction)\b|^R&S|^User Manual|^Keysight|^Chroma|^Siglent|^\d+\s*\/\s*\d+|\.{4}/;

// The next entry has begun: a line that is nothing but a command header. Without this the
// scan reads ":TRIGger:TV:STANdard?" and its answer as though they were more values.
const NEXT = /^[:*][A-Za-z][A-Za-z0-9:<>\[\]|]*\??\s*$/;

// A cross-reference is not a value: "…SGRam:FRAMe on page 482" says where to look, and a
// description assembled out of those is the defect one rule up from this one.
const XREF = /\bon page\s+\d+\.?$/i;

// pdftotext could not decode these guides' list bullet. Keysight's scope guide left U+FFFD
// where it was; the DMM guide rendered it as a lowercase 'l'. Either way the bullet is
// decoration — it marks a row, it is not part of the value — while a replacement character
// is damage, and a guard rightly refuses to let one reach a catalog.
const BULLET = /^(?:[•●▪�]|l(?=\s+[A-Z(]))\s+/;
const FFFD = /�/g;

const norm = s => (s || '').replace(/\s+/g, ' ').trim();
const words = s => norm(s).toLowerCase().replace(/[^a-z0-9 ]/g, ' ').split(/\s+/).filter(Boolean);
const listEnd = d => /[a-z]{2,}:\.?\s*$/.test(norm(d));
const cut = d => /(?:^|\s)(the|a|an|of|and|with|by)\.$/.test(norm(d))
  || /(?:^|\s)[A-Z]{2,}[A-Za-z0-9<>|.\[\]:]*:\.?$/.test(norm(d)) || norm(d).length < 12;
const esc = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// A value's name is one word. When rows carry columns, every row has to start with one —
// otherwise the columns have sheared, and "NOISy — offset — Off" is three cells from three
// different rows wearing the shape of one.
const VALUE = /^[A-Za-z][A-Za-z0-9_]*$/;

let filled = 0;
const skipped = [];

for (const [cat, guide] of Object.entries(GUIDE)) {
  const cp = path.join(ROOT, 'Core', 'CommandData', cat + '.json');
  const mp = path.join(ROOT, 'tools', 'scpi-extract', 'manuals', guide + '.txt');
  if (!fs.existsSync(cp) || !fs.existsSync(mp)) continue;
  const man = fs.readFileSync(mp, 'utf8').split('\n');
  const lines = fs.readFileSync(cp, 'utf8').split('\n');
  let touched = false;
  const pairs = [];

  for (let i = 0; i < lines.length; i++) {
    if (!/"syntax"\s*:/.test(lines[i]) || !/"description"\s*:/.test(lines[i])) continue;
    let e;
    try { e = JSON.parse(lines[i].trim().replace(/,$/, '')); } catch { continue; }
    const d = norm(e.description);
    if (!d || cut(d) || !listEnd(d)) continue;

    const head = e.syntax.split(/\s/)[0];
    const headRe = new RegExp('(^|\\s)' + esc(head) + '\\s*\\??\\s*($|[\\s{<\\[])');
    const want = words(d).slice(-6);

    const hits = [];
    for (let k = 0; k < man.length; k++) {
      if (!headRe.test(man[k])) continue;
      if (words(man.slice(k, k + 12).join(' ')).join(' ').includes(want.join(' '))) hits.push(k);
    }

    // Hits inside one block are one entry: a guide heads the set form and the query form
    // together, so two lines twenty apart are the same place, not two places.
    const blocks = hits.filter((h, n) => n === 0 || h - hits[n - 1] > 30);
    if (blocks.length !== 1) {
      skipped.push([cat, e.syntax, blocks.length === 0 ? 'no verified heading' : blocks.length + ' separate places']);
      continue;
    }

    // The rows start after the line that *completes* the description, and the description can
    // wrap. Accumulate lines until the running text carries its tail: matching a single line
    // missed the wrap and started the scan on the description itself, which was then copied
    // into its own repair.
    const at = blocks[0];
    let from = -1, acc = '';
    for (let k = at; k < at + 14 && k < man.length; k++) {
      acc = words(acc + ' ' + man[k]).join(' ');
      if (acc.includes(want.join(' '))) { from = k; break; }
    }
    if (from < 0) { skipped.push([cat, e.syntax, 'sentence never completes']); continue; }

    const rows = [];
    let blanks = 0, gap = false;
    for (let k = from + 1; k < man.length && rows.length < 16 && k < from + 26; k++) {
      const t = man[k].trim();
      if (!t) { gap = true; if (++blanks > 1 && rows.length) break; continue; }
      if (STOP.test(t) || XREF.test(t) || NEXT.test(t)) break;
      blanks = 0;
      const bullet = BULLET.test(t);
      const text = t.replace(BULLET, '').replace(/�/g, '')
        .replace(/\s{2,}/g, ' — ').replace(/\s{2,}/g, ' ').trim();
      // A table's column headings are not values: "Waveform — Characteristics — Frequency —
      // Max. — Offset2" names the columns and says nothing about what the parameter accepts.
      // A heading has no ordinary word *and* spans columns — both halves matter, since
      // testing only for the missing lower-case word threw away "Questionable Data Register
      // (STATus:QUEStionable:ENABle)", which is a value and exactly what the entry lacked.
      if (!/(^|\s)[a-z]{3,}/.test(text) && text.includes(' — ')) continue;
      if (text) { rows.push({ text, bullet, gap }); gap = false; }
    }
    if (!rows.length) { skipped.push([cat, e.syntax, 'nothing under it']); continue; }

    // Columns that have sheared cannot be repaired by reading them more carefully; the
    // ordering is gone before the text arrives. Refuse the whole capture rather than ship
    // one plausible row out of five.
    const columned = rows.filter(r => r.text.includes(' — '));
    if (columned.length && !columned.every(r => VALUE.test(r.text.split(' — ')[0]))) {
      skipped.push([cat, e.syntax, 'columns sheared in the text layer']);
      continue;
    }

    // The description can already hold the guide's first row: the parser took one line of the
    // list before it stopped, and emit.js put a full stop after it. Appending the same row
    // again writes "visualization: Operators:.; ADD" — the repair copying what it repairs.
    while (rows.length && words(d).join(' ').endsWith(words(rows[0].text).join(' '))) rows.shift();
    if (!rows.length) { skipped.push([cat, e.syntax, 'nothing under it but its own last line']); continue; }

    // emit.js ends every description with a full stop, so a promise arrives spelled ":.".
    let out = /:\.$/.test(d) ? d.slice(0, -1) : d;
    let prevBullet = false;
    for (const r of rows) {
      if (r.bullet) {
        // Items in a list are separated by the semicolon, not by the full stop a guide happens
        // to put at the end of one of them: "non-interlaced.; EDTV 480p/60" reads as a mistake.
        if (/:$/.test(out)) out += ' ' + r.text;
        else out = (prevBullet ? out.replace(/\.$/, '') : out) + '; ' + r.text;
      } else {
        // A blank line is the guide's paragraph break, and some guides leave the full stop
        // off the paragraph before it: the FSP ends "…(see table of triggers in FS-K5
        // manual)" and starts the next line "For another,". Rendering that break as a
        // sentence break keeps what is there; running the two together does not.
        const sep = /[:.!?]$/.test(out) ? ' ' : (prevBullet || r.gap) ? '. ' : ' ';
        // A guide breaks a word across lines with a hyphen: "Status Sub-" / "system".
        // Rejoining with a space spells something that is not a word.
        out = sep === ' ' && /[a-z]-$/.test(out) ? out.slice(0, -1) + r.text : out + sep + r.text;
      }
      prevBullet = r.bullet;
    }
    let merged = norm(out);
    if (!/[.!?]$/.test(merged)) merged += '.';
    if (merged.length > CAP) { skipped.push([cat, e.syntax, `${merged.length} chars — too wide to carry`]); continue; }

    // A full stop followed by a lower-case word is a sentence I invented. It happens where a
    // guide nests a list inside a list and wraps a row over three lines — flattening that
    // into one sentence spells "the :FUNCtion<m>:INTegrate:IOFFset command lets you. specify
    // a DC offset correction factor", which is worse than the truncation it replaces.
    // A question mark is not a sentence end here — it is the last letter of a command, and
    // "*IDN? contains" is the guide speaking correctly.
    if (/\.\s+[a-z]/.test(merged)) { skipped.push([cat, e.syntax, 'rows will not flatten into a sentence']); continue; }

    const from2 = '"description": ' + JSON.stringify(e.description);
    if (!lines[i].includes(from2)) { skipped.push([cat, e.syntax, 'line rewrite failed']); continue; }
    lines[i] = lines[i].replace(from2, '"description": ' + JSON.stringify(merged));
    filled++; touched = true;
    pairs.push([head.replace(/\?$/, ''), e.description, merged]);
    console.log(`[${cat}] ${e.syntax.slice(0, 46)}   ${guide}.txt:${from + 1}`);
    console.log(`   = ${merged}`);
  }

  // A command and its query form share their prose, and a guide heads them together. Where
  // one was repaired and the other was not, the pair now disagrees about the same command.
  // Carry the repair across — but only between a header and that same header with a '?', and
  // only where the old text matches exactly. Not by description alone: two FSP entries both
  // read "This command is a combination of 2 commands:." and mean different combinations.
  for (const [stem, was, now] of pairs) {
    for (let i = 0; i < lines.length; i++) {
      if (!lines[i].includes('"description": ' + JSON.stringify(was))) continue;
      let s; try { s = JSON.parse(lines[i].trim().replace(/,$/, '')); } catch { continue; }
      if (s.syntax.split(/\s/)[0].replace(/\?$/, '') !== stem) continue;
      lines[i] = lines[i].replace('"description": ' + JSON.stringify(was),
        '"description": ' + JSON.stringify(now));
      filled++; touched = true;
      console.log(`[${cat}] ${s.syntax.slice(0, 46)}   — carried from its pair`);
    }
  }

  if (APPLY && touched) { JSON.parse(lines.join('\n')); fs.writeFileSync(cp, lines.join('\n')); }
}

console.log(`\nfilled ${filled}   left alone ${skipped.length}`);
for (const [c, s, why] of skipped) console.log(`   ${c} ${s.slice(0, 42)} — ${why}`);
console.log(APPLY ? '*** written ***' : '(dry run)');
