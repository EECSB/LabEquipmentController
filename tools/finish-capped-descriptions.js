// Finish the descriptions that were cut at a character count and marked with an ellipsis.
//
//     node tools/finish-capped-descriptions.js . <catalog> [--apply]
//
// 1,180 descriptions across 14 catalogs end in "…." and none is longer than 238 characters.
// That is a cap, not a sentence: the guide had more to say and a transcription stopped
// counting. It is worse than an ordinary truncation, because the ellipsis reads as a
// deliberate abbreviation and CatalogDescriptionTests lets it through for exactly that
// reason.
//
// The repair keeps what the catalog wrote and appends only what the guide had left. It does
// not overwrite the opening: R&S prints one description for a command and its query, and the
// catalogs turn "Sets the ENABle part…" into "Query the ENABle part…" for the query form.
// Rewriting from the guide would undo that; continuing from where the cut fell does not.
//
// The anchor is the same as everywhere else in this repo — the guide's heading for that exact
// command — and the check is that the guide's prose actually contains the words the catalog
// stopped on. Where it does not, the entry is left alone and reported.
const fs = require('fs');
const path = require('path');
// Flags are not positional arguments. Reading argv[3] blindly made "--apply" the name of the
// catalog to work on, which matches nothing, so the run reported "finished 0" and wrote
// nothing at all — a silent no-op that looks exactly like success.
const args = process.argv.slice(2).filter(a => !a.startsWith('--'));
const ROOT = args[0] || '.';
const ONLY = args[1];
const APPLY = process.argv.includes('--apply');

const GUIDE = {
  'bk-electronic-load': ['bk-8600.txt'],
  'chroma-power-supply': ['chroma-62000l.txt'],
  'gwinstek-scope': ['gwinstek-gds2000.txt'],
  'oscilloscope': ['rigol-ds2000a.txt'],
  'rohde-fsiq-analyzer': ['rs-fsiq.txt'],
  'rohde-fsp-analyzer': ['rs-fsp.txt'],
  'rohde-fsq-analyzer': ['rs-fsq.txt'],
  'rohde-fsu-analyzer': ['rs-fsu.txt'],
  'rohde-fsw-analyzer': ['rohde-fsw.txt', 'rohde-fsw-list.txt'],
  'rohde-power-supply': ['rs-ngl200.txt', 'rs-nge100.txt', 'rs-hmp.txt'],
  'rohde-scope': ['rs-rtb2000.txt'],
  'rohde-spectrum-analyzer': ['rs-fpc.txt'],
  'siglent-scope': ['siglent-sds.txt', 'siglent-sds3000hd.txt'],
  'tektronix-scope': ['tek-mdo4000.txt', 'tek-mso4000.txt'],
};

const FOOTER = /^(User Manual|R&S|Keysight|Tektronix|\d+$)|Remote control commands|^\s*\d+\s*$/;
const STOP = /^(Parameters?|Suffix|Setting parameters?|Query parameters?|Return values?|Example|Usage|Manual operation|Firmware|Options?|Note|Group|Syntax|Arguments|Returns)\b/;

// R&S stacks a family's headings before printing one shared description. Those lines are
// headings, not prose, and reading them as prose is how an audit reports every entry as
// broken at once.
// The leading class carries ':' because Rigol writes its commands with one — ":TRIGger:IIC:
// CLEVel?" is a heading, and without the colon it was collected as prose, so the stacked
// query forms became the description and nothing matched.
const IS_HEADING = t => /^[A-Z*\[:][A-Za-z0-9*\[\]<>|:]*(\s*<[A-Za-z]+>|\?)?\s*$/.test(t)
  && /[:*<]/.test(t);

const norm = s => (s || '').replace(/\s+/g, ' ').trim();
// Letters and digits only — no spaces, no punctuation. An index into this is therefore a
// count of letters and digits, which is what makes it possible to walk back to the same
// place in the real text. Leaving spaces in was an off-by-however-many-words error that
// swallowed the front of every completion: "…segments using the history" arrived as
// "gments using the history".
const soft = s => norm(s).toLowerCase().replace(/[^a-z0-9]/g, '');

// Keyed twice: by the heading as printed, and by the command alone. A catalog spells the
// query form "TIMebase:POSition?" while the guide heads the pair "TIMebase:POSition <Offset>",
// so matching only the full string finds neither.
//
// Every occurrence is kept, not the first. A guide names a command in its contents, in a
// summary table, in cross-references from other entries, and once where it documents it —
// and the Rigol scope's summary is a bare stack of command names with nothing to tell it
// apart from the real thing. Taking the first occurrence pointed 171 of that catalog's 174
// entries at its table of contents. The caller tries them all and keeps the one whose prose
// carries the words the catalog stopped on, which is the check that was already there.
function headings(man) {
  const at = new Map();
  const add = (k, i) => { if (!at.has(k)) at.set(k, []); at.get(k).push(i); };
  for (let i = 0; i < man.length; i++) {
    const t = man[i].trim();
    if (!t || t.length > 90 || /on page \d+|\.{4}/.test(t)) continue;
    if (!/^[A-Z*\[:]/.test(t)) continue;
    add(t, i);
    const head = t.split(/\s/)[0].replace(/\?$/, '');
    if (/[:*]/.test(head) && head !== t) add(head, i);
  }
  return at;
}

// Rigol does not put the prose under the heading. It prints "Syntax", the command and its
// query form, then "Description" and the sentences — so the text is two labels away, and a
// reader that stops at the first blank line finds nothing at all. A lone "Description"
// restarts the collection rather than ending it.
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

let done = 0;
const left = [];

for (const [cat, guides] of Object.entries(GUIDE)) {
  if (ONLY && cat !== ONLY) continue;
  const cp = path.join(ROOT, 'Core', 'CommandData', cat + '.json');
  const files = guides.map(g => path.join(ROOT, 'tools', 'scpi-extract', 'manuals', g));
  if (!fs.existsSync(cp) || !files.every(fs.existsSync)) { console.log(`${cat}: guide missing`); continue; }

  const man = files.flatMap(f => fs.readFileSync(f, 'utf8').split('\n'));
  const at = headings(man);
  const lines = fs.readFileSync(cp, 'utf8').split('\n');
  let here = 0;

  for (let i = 0; i < lines.length; i++) {
    if (!/"description"\s*:/.test(lines[i])) continue;
    let e;
    try { e = JSON.parse(lines[i].trim().replace(/,$/, '')); } catch { continue; }
    const d = norm(e.description);
    if (!/…\.?$/.test(d)) continue;

    const stem = d.replace(/…\.?$/, '').trim();
    const bare = e.syntax.replace(/\?(\s|$)/, '$1').trim();
    const stemName = e.syntax.split(/\s/)[0].replace(/\?$/, '');
    const where = [...(at.get(e.syntax) || []), ...(at.get(bare) || []), ...(at.get(stemName) || [])];
    if (!where.length) { left.push([cat, e.syntax, 'no heading in the guide']); continue; }

    // Where the cut fell, found by the words it fell on rather than by counting characters.
    const key = soft(stem.slice(-45));
    let prose = null, pos = -1;
    for (const k of where) {
      const p = proseAt(man, k);
      const i2 = soft(p).indexOf(key);
      if (i2 >= 0) { prose = p; pos = i2; break; }
    }
    if (pos < 0) { left.push([cat, e.syntax, 'the guide does not carry the words it stops on']); continue; }

    // soft() dropped the spaces and punctuation, so an index into it is a count of letters
    // and digits. Walk that many through the real prose to land just past the last one the
    // catalog kept.
    const target = pos + key.length;
    let seen = 0, cutAt = prose.length;
    for (let c = 0; c < prose.length; c++) {
      if (!/[a-z0-9]/i.test(prose[c])) continue;
      if (++seen === target) { cutAt = c + 1; break; }
    }
    const rest = prose.slice(cutAt).trim();
    if (!rest) { left.push([cat, e.syntax, 'the guide says no more than the catalog does']); continue; }

    // The RTB2000 manual prints "using the history.." with a stray second full stop. That is
    // typography, not meaning, and guideMisprint is for errors a reader could act on — a
    // wrong command name, a contradiction — not for a doubled dot in every description that
    // quotes the sentence. Ellipses are left alone.
    // pdftotext could not decode the FSQ and FSU guides' list bullet and left U+FFFD where it
    // was. The bullet is decoration and the replacement character is damage, which
    // CatalogCoverageTests refuses to let into a catalog — the same call as in
    // rejoin-split-words.js and fill-list-descriptions.js.
    const merged = norm(stem + (/^[.,;:]/.test(rest) ? '' : ' ') + rest)
      .replace(/(?<!\.)\.\.(?!\.)/g, '.')
      .replace(/[�•]\s*/g, '')
      .replace(/\s{2,}/g, ' ').trim();
    if (merged.length <= d.length) { left.push([cat, e.syntax, 'no longer than the cut version']); continue; }

    // Where a guide stacks four headings and prints two descriptions under them, every one of
    // the four collects both. Rigol does this for :TRIGger:IIC:CLEVel and :DLEVel, and the
    // damage is already in the catalog before the cap: the entry opens "Set or query the
    // trigger level of I2C trigger when the channel source of the clock line…" and then says
    // it again for the data line. Finishing that makes it longer, not truer, and leaves the
    // two commands describing each other. The tell is the description's own opening phrase
    // turning up in it a second time.
    const opener = merged.split(/\s+/).slice(0, 4).join(' ');
    if (opener.length > 8 && merged.indexOf(opener, opener.length) > 0) {
      left.push([cat, e.syntax, 'two descriptions printed under one stack of headings']);
      continue;
    }

    lines[i] = lines[i].replace('"description": ' + JSON.stringify(e.description),
      '"description": ' + JSON.stringify(merged));
    here++; done++;
    if (process.env.VERBOSE) console.log(`${e.syntax}\n   + ${merged.slice(stem.length).trim()}\n`);
  }

  const text = lines.join('\n');
  JSON.parse(text);
  console.log(`${cat.padEnd(24)} finished ${String(here).padStart(4)}`);
  if (APPLY && here) fs.writeFileSync(cp, text);
}

console.log(`\nfinished ${done}   left alone ${left.length}`);
const why = {};
for (const [, , w] of left) why[w] = (why[w] || 0) + 1;
for (const [w, n] of Object.entries(why)) console.log(`   ${String(n).padStart(4)}  ${w}`);
console.log(APPLY ? '*** written ***' : '(dry run)');
