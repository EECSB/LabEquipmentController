// Parse a vendor SCPI programming guide (pdftotext -layout output) into
// {syntax, description, example} triples — the shape of the app's CommandRef.
//
// Two entry layouts are recognised, covering every guide downloaded so far:
//
//   Rigol      "Syntax <cmd>"  [continuation lines]  "Description <text>"
//   Siglent    "Command Format <cmd>"  "Description <text>"  "Example <cmd>"
//   Keysight   a command-summary table: the command header alone on a line.
const fs = require('fs');

const file = process.argv[2];
const style = process.argv[3] || 'auto';

// Optional "--from=<regex>": ignore everything before the first line matching it.
//
// Needed for the Tektronix guides, whose front matter carries "Command Groups"
// tables laid out as command-and-description columns. They look exactly like the
// reference section to a parser, but pdftotext shifts their rows, so a command
// ends up paired with its neighbour's description — "RECAll:MASK" documented as
// "Specifies the nominal value, in volts, to be used to vertically offset...".
// Only the alphabetical reference section is authoritative, so start there.
const fromArg = process.argv.find(a => a.startsWith('--from='));
// Form feeds are page breaks. They matter here because they sit at the *start* of
// the first line of each page, which both defeats a "^heading" match and makes a
// column-0 command heading look indented.
let lines = fs.readFileSync(file, 'utf8').replace(/\f/g, '').split(/\r?\n/);
if (fromArg) {
  const re = new RegExp(fromArg.slice('--from='.length));
  const at = lines.findIndex(l => re.test(l));
  if (at < 0) { console.error(`--from pattern never matched in ${file}`); process.exit(1); }
  lines = lines.slice(at);
}

// SCPI headers are written with a lot of optional-node punctuation that varies
// by vendor — "[:SOURce]:INPut", "[:SOURce[<n>]]:VOLTage[:LEVel]", "CH<x>:SCAle".
// Rather than try to match all of it, normalise the punctuation away and check
// that what remains is a colon-separated run of mnemonics.
// Single-mnemonic commands, which would otherwise be indistinguishable from an
// ordinary word. The Tektronix set is long — its guides document ~20 of them
// (AUTOSet, CURVe, HARDCopy, DESkew, TEKSecure...) and each is a real command.
const BARE_ROOTS = new RegExp('^(' + [
  // SCPI-99 and common usage
  'READ', 'ABOR', 'INIT', 'FETC', 'MEAS', 'CONF', 'SYST', 'STAT', 'TRIG', 'OUTP',
  'DISP', 'SENS', 'SOUR', 'WAI', 'RUN', 'STOP', 'SINGLE', 'AUTOSCALE', 'CLS', 'RST',
  // Tektronix
  'ACQuire', 'ALLEv', 'AUTOSet', 'AUXin', 'BUS', 'BUSY', 'CLEARMenu', 'CURSor',
  'CURVe', 'DATa', 'DATE', 'DESE', 'DESkew', 'EVENT', 'EVMsg', 'EVQty', 'FACtory',
  'FILESystem', 'HARDCopy', 'HEADer', 'HIStogram', 'HORizontal', 'ID', 'LANGuage',
  'LOCk', 'MARK', 'MATHVAR', 'MEASUrement', 'MESSage', 'NEWpass', 'PAUSe', 'REM',
  'SEARCH', 'SELect', 'SET', 'TEKSecure', 'TIMe', 'TOTaluptime', 'TRIGger', 'UNLock',
  'USBTMC', 'VERBose', 'WAVFrm', 'WFMInpre', 'WFMOutpre', 'ZOOm',
  // Siglent SDS — ":PRINt" is its screen dump, so losing it costs a whole feature.
  'AUToset', 'COUNter', 'DECode', 'DIGital', 'DVM', 'HISTORy', 'MTESt', 'PRINt', 'SEARch',
  // Prefix, not exact: a single mnemonic may be sent in its short or long form,
  // so "TRIG" and "TRIGger" both have to match the one entry.
].join('|') + ')', 'i');

// The list above is one pool shared by every vendor, and a root that is real for one is a
// parameter value for another. R&S prints its trigger types and persistence modes in
// two-column tables — the value on one line, what it means on the next — which is exactly
// how it prints a command and its description, so BUS, ID, RUNT, TIME and IDDT were read
// as commands and their glosses as descriptions. Ten of those shipped in the R&S scope
// catalog. R&S has almost no bare single-node commands, so for that style the pool is
// this short list and nothing else.
//
// ABORt joined it from the FSW manual, where it is the command that stops the current
// measurement and resets the trigger system — documented like any other entry, and absent
// from the scope and supply guides this list was first written against.
const RS_BARE_ROOTS = /^(RUN|STOP|SINGLE|ABORt)$/i;
const bareRoots = () => (style === 'rs' ? RS_BARE_ROOTS : BARE_ROOTS);

function isCmd(s) {
  if (!s) return false;
  const tok = s.trim().split(/\s/)[0];
  if (!tok) return false;
  if (/^\*[A-Za-z]{2,4}\??$/.test(tok)) return true;
  const h = tok
    .replace(/<[^>]*>/g, 'N')      // <n>, <x>, <1-3> are all "a suffix"
    .replace(/[\[\]]/g, '')        // optional nodes
    .replace(/^:/, '')
    .replace(/\?$/, '');
  if (!h) return false;
  // A node may be an alternation of two spellings — the FSL-era manuals head whole
  // subsystems "[SENSe<1|2>:]BANDwidth|BWIDth[:RESolution]", and the catalogs carry
  // that shape verbatim. Every alternate must be a mnemonic on its own.
  const parts = h.split(':');
  if (!parts.every(p => p.split('|').every(a => /^[A-Za-z][A-Za-z0-9_]*$/.test(a))))
    return false;
  if (parts.length > 1) return true;

  // A trailing "?" used to be enough on its own, and that is how a wrapped command name
  // became a command. Guides break a long name across two lines, the first ending in a
  // colon:
  //
  //     SEARCH:SEARCH<x>:TRIGger:A:BUS:B<x>:FLEXray:HEADER:
  //     PAYLength?
  //
  // and the continuation arrives here as a single mnemonic with a question mark. Eight
  // of those shipped in the Tektronix catalog — PAYLength?, DBCA?, QUALifier?, VALue?,
  // HIVALue?, CYCLEcount?, INSTR?, SUBSF? — none of which any instrument answers, while
  // the query forms they were the tail of went missing. A single mnemonic now has to
  // earn its place whether or not it ends in a question mark.

  // A single mnemonic is only a command if it is a known bare root *and* is cased
  // like one. SCPI writes the abbreviation in capitals — "AUTOSet", "CURVe", "SET" —
  // so a mnemonic always opens with two or more. An ordinary capitalised word opens
  // with exactly one, which is what keeps a description beginning "Sets the vertical
  // scale..." from being read as the SET command and truncating the entry.
  // Test the normalised header, not the raw token: Rigol writes its bare commands
  // with a leading colon (":RUN", ":STOP", ":AUToscale"), and that colon would fail
  // the capitals test and lose them.
  return /^[A-Z]{2,}/.test(h) && bareRoots().test(h);
}

// Strip page furniture that pdftotext interleaves into the body.
const junk = s =>
  !s ||
  /^(RIGOL|SIGLENT|Keysight|Programming Guide|Chapter|Page|\d+)$/i.test(s.trim()) ||
  /^\s*\d+\s*$/.test(s);

function clean(s) {
  return s
    .replace(/\s{2,}/g, ' ')
    .replace(/[‘’]/g, "'")
    .replace(/[“”]/g, '"')
    .replace(/�/g, '')
    .trim();
}

const out = [];

// A contents page reads as a description: a run of command names each followed by the
// page it is on. Two FSV entries shipped with "…on page 487 CALCulate<n>:MARKer<m>:
// MAXimum[:PEAK] on page 520…" where their text should have been, and the catalog loaded
// and the library listed them, which is what makes this worth catching here.
const CONTENTS_PAGE = /on page \d+.*on page \d+/i;

// Words the manual itself uses, for rejoining what its typesetting broke — plain words in
// one set, genuinely hyphenated compounds in another. Built once, lazily, from the file.
let vocabulary = null, compounds = null;
function vocab() {
  if (!vocabulary) {
    const all = lines.join('\n');
    vocabulary = new Set();
    for (const w of all.match(/[A-Za-z]{3,}/g) || []) vocabulary.add(w.toLowerCase());
    compounds = new Set();
    for (const w of all.match(/[A-Za-z]{2,}-[a-z]{2,}/g) || []) compounds.add(w.toLowerCase());
  }
  return vocabulary;
}

// These guides are justified and hyphenate at the margin, and pdftotext keeps the hyphen:
// "This com-" / "mand is only available" joins to "This com- mand is only available". 218
// of the FSW's descriptions read like that, and 270 of the 1279 already in the FSV catalog.
//
// A hyphen at a line break cannot be told from a real one by shape alone — "single-" then
// "ended" must stay hyphenated while "com-" then "mand" must not. So ask the manual: join
// only when the joined word is one this same document uses elsewhere without a hyphen.
// "command" and "measurement" appear hundreds of times; "singleended" appears nowhere.
// A compound that happens to wrap at its own hyphen — "self-" then "alignment" — is the
// third case: joining it is wrong and leaving the space is wrong too, so close the space
// and keep the hyphen. "x- and y-axes" matches neither set and is left exactly as printed,
// which is what it should be: that hyphen is suspended, not broken.
function dehyphenate(s) {
  return s.replace(/([A-Za-z]{2,})- ([a-z]{2,})/g, (whole, a, b) => {
    const joined = (a + b).toLowerCase();
    if (vocab().has(joined)) return a + b;
    if (compounds.has(`${a.toLowerCase()}-${b.toLowerCase().replace(/[^a-z].*$/, '')}`)) return `${a}-${b}`;
    return whole;
  });
}

function pushEntry(syntaxes, desc, example, category) {
  desc = dehyphenate(clean(desc));
  if (CONTENTS_PAGE.test(desc)) desc = '';
  for (const s of syntaxes) {
    const syntax = clean(s);
    // 160, not 120: Chroma's sixteen-argument program commands run to 136 characters, and
    // the shorter limit dropped them here before build-catalog ever saw them.
    if (!syntax || !isCmd(syntax) || syntax.length > 160) continue;
    out.push({
      syntax,
      description: desc,
      example: example ? clean(example) : null,
      isQuery: syntax.includes('?'),
      category: category ? clean(category) : undefined,
    });
  }
}

// ---- Rigol style: "Syntax" ... "Description" ----------------------------
//
// pdftotext renders these guides as a narrow label column beside a content
// column, and the two interleave rather than staying on the same line:
//
//         Syntax  [:SOURce[<n>]]:FREQuency[:FIXed] {<frequency>|MINimum|MAXimum}
//   Description
//                 [:SOURce[<n>]]:FREQuency[:FIXed]? [MINimum|MAXimum]
//    Parameter
//                 Set the frequency of the waveform ...
//
// So a bare label on its own line is furniture to skip, not a section boundary:
// stopping at the first "Description" would drop every query form in the guide.
const LABELS = 'Description|Parameter|Return Format|Explanation|Example|Related|Commands|Syntax|Remarks?';
const BARE_LABEL = new RegExp(`^(${LABELS})\\s*$`, 'i');
const LABELLED = new RegExp(`^(?:${LABELS})\\s+(\\S.*)$`, 'i');

function parseRigol() {
  for (let i = 0; i < lines.length; i++) {
    // Two layouts: "Syntax :CHANnel<n>:SCALe <scale>" all on one line (DP800,
    // DG1000Z), or a bare "Syntax" heading with the forms on the lines below
    // (DS2000A). The scan loop below reads the forms either way.
    const inline = lines[i].match(/^\s*Syntax\s+(.*\S)\s*$/);
    if (!inline && !/^\s*Syntax\s*$/.test(lines[i])) continue;

    const syntaxes = inline ? [inline[1]] : [];
    let desc = '';
    let j = i + 1;
    for (; j < lines.length && j < i + 16; j++) {
      const t = lines[j].trim();
      if (!t) continue;
      if (BARE_LABEL.test(t)) continue;             // column furniture
      if (isCmd(t)) { syntaxes.push(t); continue; } // another syntax form
      if (/^[\{\[<]/.test(t)) continue;             // wrapped parameter clause
      // First line of prose: the description, possibly still carrying its label.
      desc = (t.match(LABELLED) || [, t])[1];
      break;
    }
    // Continuation of the description, up to the next label or command.
    for (let k = j + 1; k < lines.length && k < j + 5; k++) {
      const t = lines[k].trim();
      if (!t || BARE_LABEL.test(t) || LABELLED.test(t) || isCmd(t)) break;
      desc += ' ' + t;
    }

    let example = null;
    for (let k = j; k < lines.length && k < j + 25; k++) {
      const em = lines[k].match(/^\s*Example\s+(\S.*)$/);
      if (em) { example = em[1]; break; }
      if (/^\s*Syntax\s/.test(lines[k]) && k > j) break;
    }
    pushEntry(syntaxes, desc, example);
    i = j;
  }
}

// ---- Siglent style: "Command Format" ... "Description" ... "Example" ----
function parseSiglent() {
  for (let i = 0; i < lines.length; i++) {
    // Either "Command Format <cmd>" on one line, or a two-column layout where
    // the label itself wraps: "Command   <cmd>" / "Format    <cmd?>".
    let m = lines[i].match(/^\s*Command\s*Format\s+(.*\S)\s*$/i);
    let j = i + 1;
    if (!m) {
      const c = lines[i].match(/^\s*Command\s{2,}(.*\S)\s*$/i);
      const f = (lines[i + 1] || '').match(/^\s*Format\s{2,}(.*\S)\s*$/i);
      if (!c || !isCmd(c[1].trim())) continue;
      m = c;
      if (f) { j = i + 2; }
    }
    const syntaxes = [m[1]];
    // The wrapped-label form puts a second syntax form on the "Format" line.
    const f2 = (lines[i + 1] || '').match(/^\s*Format\s{2,}(.*\S)\s*$/i);
    if (j === i + 2 && f2 && isCmd(f2[1].trim())) syntaxes.push(f2[1]);
    for (; j < lines.length && j < i + 7; j++) {
      const t = lines[j].trim();
      if (/^(Description|Instruction|Example|Response|Parameter)/i.test(t)) break;
      if (!t) continue;
      if (isCmd(t)) syntaxes.push(t); else break;
    }
    // The spectrum-analyzer guides label the prose "Instruction", not "Description".
    let desc = '';
    if (/^\s*(Description|Instruction)/i.test(lines[j] || '')) {
      desc = (lines[j].match(/^\s*(?:Description|Instruction)\s*(.*)$/i) || [, ''])[1];
      for (let k = j + 1; k < lines.length && k < j + 6; k++) {
        const t = lines[k].trim();
        if (!t || /^(Example|Response|Parameter|Return|Default|Menu|Command|Note)/i.test(t)) break;
        desc += ' ' + t;
      }
    }
    let example = null;
    for (let k = j; k < lines.length && k < j + 20; k++) {
      const em = lines[k].match(/^\s*Example\s+(\S.*)$/i);
      if (em) { example = em[1]; break; }
      if (/^\s*Command\s*Format/i.test(lines[k]) && k > i) break;
    }
    pushEntry(syntaxes, desc, example);
    i = j;
  }
}

// ---- Keysight style: indented command headers in a subsystem listing ----
function parseKeysight() {
  for (let i = 0; i < lines.length; i++) {
    const t = lines[i].trim();
    if (junk(t)) continue;
    // A command header line: a SCPI path, possibly with a parameter clause,
    // and nothing that reads like prose after it.
    if (!/^(\[?[A-Z]+[a-z]*\]?)(:\[?[A-Z]+[a-z]*\]?)+/.test(t) && !/^\*[A-Z]{2,4}/.test(t)) continue;
    if (/\b(the|and|for|with|this|that|when|from|are|will)\b/i.test(t)) continue;
    if (t.length > 110) continue;
    // Description: the next non-empty line that reads like prose.
    let desc = '';
    for (let k = i + 1; k < lines.length && k < i + 4; k++) {
      const d = lines[k].trim();
      if (!d) continue;
      if (/^[A-Z][a-z]+\b.*\b(the|a|an|to|of)\b/.test(d)) { desc = d; }
      break;
    }
    pushEntry([t], desc, null);
  }
}

// ---- Heading style: the command sits alone on a line, prose follows -----
// Tektronix programmer manuals and the body of the Rigol/Keysight guides.
function parseHeading() {
  for (let i = 0; i < lines.length; i++) {
    const t = lines[i].trim();
    if (!t || t.length > 110) continue;
    if (!isCmd(t)) continue;
    // A heading is a bare command: no trailing prose, no dotted leader.
    if (/\.{4,}/.test(t)) continue;
    if (/\b(the|and|for|with|this|that|when|from|are|will|is|to|of|in)\b/i.test(t)) continue;
    // Collect the indented prose that follows.
    let desc = '';
    for (let k = i + 1; k < lines.length && k < i + 12; k++) {
      const d = lines[k];
      const dt = d.trim();
      if (!dt) { if (desc) break; else continue; }
      if (isCmd(dt) && !desc) break;                 // next heading, no prose
      if (/^(Group|Syntax|Related|Arguments|Returns|Examples?)$/i.test(dt)) { if (desc) break; else continue; }
      if (!/^\s{4,}/.test(d) && !desc) break;        // prose is indented under the heading
      desc += (desc ? ' ' : '') + dt;
      if (/[.!?]$/.test(dt)) break;
    }
    if (desc.length < 12) continue;
    pushEntry([t], desc, null);
  }
}

// ---- Tektronix style ---------------------------------------------------
//
// The cleanest layout of the lot. The command is a heading at column 0, and the
// block beneath it is a labelled record:
//
//   CH<x>:SCAle
//                    Sets or returns the vertical scale for the channel ...
//           Group    Vertical
//           Syntax   CH<x>:SCAle <NR3>
//                    CH<x>:SCAle?
//         Examples   CH4:SCALE 100E-03 sets the channel 4 scale to 100 mV per division.
//
// Worth its own parser rather than reusing "heading": the Syntax block carries the
// argument forms the heading lacks, and "Group" gives a real category — Vertical,
// Trigger, Acquisition — instead of one guessed from the root mnemonic.
const TEK_LABEL = /^(Group|Syntax|Related Commands|Arguments|Returns|Examples?)\b\s*(.*)$/;

function parseTek() {
  // Headings first, so each block is bounded by the next one.
  const heads = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (/^\s/.test(line)) continue;              // headings start at column 0
    // 409 headings in the MDO4000 guide carry a "(Query Only)" or "(No Query Form)"
    // annotation. Dropping it matters: without this the entry is missed entirely and
    // its Syntax block is read as part of the *previous* command's record, which is
    // how ":MARK:CREATE" ended up documented as "the time base horizontal scale".
    let t = line.trim().replace(/\s*\((?:Query Only|No Query Form)\)$/, '');
    if (!t || t.length > 90 || /\s/.test(t)) continue;   // a bare command, no args

    // A heading too long for the column wraps onto the next line, both at column 0,
    // the first ending in ":". 45 of the deepest SEARCH:… commands are printed this
    // way; taken alone the second line is a fragment like "SOUrce:VALue".
    const prev = (lines[i - 1] || '');
    let at = i;
    if (prev && !/^\s/.test(prev) && /:$/.test(prev.trim()) && !/\s/.test(prev.trim())) {
      t = prev.trim() + t;
      at = i - 1;
    }

    if (!isCmd(t)) continue;
    heads.push([at, t]);
  }

  for (let h = 0; h < heads.length; h++) {
    const [start, head] = heads[h];
    const end = h + 1 < heads.length ? heads[h + 1][0] : Math.min(lines.length, start + 200);

    let desc = '', group = '';
    const syntaxes = [], examples = [];
    let field = null;                            // which label we are inside

    for (let k = start + 1; k < end; k++) {
      const t = lines[k].trim();
      if (!t) continue;
      if (/^\d+-\d+\s|Programmer Manual$|^Commands Listed/.test(t)) continue;   // page furniture

      // "Returns", "Arguments" and "Examples" are field labels — but they are also
      // ordinary first words, and the description comes *before* any label:
      //   *IDN? (Query Only)
      //           Returns the oscilloscope identification code.
      //           Group Miscellaneous
      // Reading that first line as a "Returns" field costs the entry its description
      // and drops it entirely. Until the record's structured part opens — which only
      // "Group" or "Syntax" does — every line is prose.
      const m = t.match(TEK_LABEL);
      const structural = m && (field !== null || /^(Group|Syntax)$/i.test(m[1]));
      if (structural) {
        field = m[1].toLowerCase();
        const rest = m[2].trim();
        if (!rest) continue;
        if (field === 'group') group = rest;
        else if (field === 'syntax' && isCmd(rest)) syntaxes.push(rest);
        else if (field.startsWith('example')) examples.push(rest);
        continue;
      }

      if (field === 'syntax') { if (isCmd(t)) syntaxes.push(t); continue; }
      if (field && field.startsWith('example')) { if (isCmd(t)) examples.push(t); continue; }
      if (field === null) {
        // Still in the leading prose: the description, one sentence of it.
        if (isCmd(t) && !desc) break;
        desc += (desc ? ' ' : '') + t;
        if (/[.!?]$/.test(t) && desc.length > 30) field = 'described';
      }
    }

    if (!syntaxes.length) syntaxes.push(head);   // no Syntax block: use the heading
    if (desc.length < 12) continue;

    // The guide gives a set example and a query example. Give each form the one
    // that matches it — a "?" entry illustrated by a set command reads as a bug.
    const cleaned = examples.map(cleanTekExample).filter(Boolean);
    for (const syntax of syntaxes) {
      const wantQuery = syntax.includes('?');
      const example = cleaned.find(e => e.includes('?') === wantQuery) ?? null;
      pushEntry([syntax], desc, example, group);
    }
  }
}

// A Tektronix example runs the command straight into prose: "CH4:SCALE 100E-03 sets
// the channel 4 scale to 100 mV per division." Keep the command, drop the sentence —
// tokens stop being part of it at the first all-lowercase word.
function cleanTekExample(example) {
  if (!example) return null;
  const kept = [];
  for (const tok of example.split(/\s+/)) {
    if (/^[a-z]+$/.test(tok)) break;
    kept.push(tok);
  }
  const s = kept.join(' ').trim();
  return s.length >= 3 ? s : null;
}

// ---- Keithley style -----------------------------------------------------
//
//   :SOURce[1]:CONFiguration:LIST:DELete          <- heading at column 0
//
//   This command deletes a source configuration list.     <- description
//
//   Type              Affected by      Where saved     Default value
//   Command only      Not applicable   Not applicable  Not applicable
//
//   Usage
//                 :SOURce[1]:CONFiguration:LIST:DELete "<name>"
//                 :SOURce[1]:CONFiguration:LIST:DELete "<name>", <index>
//
//   Details
//                 Deletes a configuration list. If the index is not specified, ...
//
// The cleanest of all the layouts met so far: heading, one-line description, then
// the argument forms under "Usage".
function parseKeithley() {
  const heads = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (/^\s/.test(line) || line !== line.trimEnd()) continue;
    const t = line.trim();
    if (!t || t.length > 90 || /\s/.test(t) || !t.includes(':')) continue;
    if (/\.{3,}/.test(t)) continue;                      // a table-of-contents line
    if (!isCmd(t)) continue;
    heads.push([i, t]);
  }

  for (let h = 0; h < heads.length; h++) {
    const [start, head] = heads[h];
    const end = h + 1 < heads.length ? heads[h + 1][0] : Math.min(lines.length, start + 160);

    let desc = '';
    const syntaxes = [];
    for (let k = start + 1; k < end; k++) {
      const t = lines[k].trim();
      if (!t) continue;
      if (!desc) {
        if (/^(Type|Usage|Details|Example|Also see)\b/.test(t)) continue;
        if (isCmd(t)) continue;
        desc = t;
        // The guide writes every description as "This command/query/attribute ...".
        // Drop that opening so the catalog reads as instructions, like the others.
        desc = desc.replace(/^This (command|query|attribute|function)\s+/i, '');
        continue;
      }
      if (/^Usage\b/.test(t)) {
        for (let m = k + 1; m < end; m++) {
          const u = lines[m].trim();
          if (!u) continue;
          if (/^(Details|Example|Also see|Type)\b/.test(u)) break;
          if (isCmd(u)) syntaxes.push(u); else break;
        }
        break;
      }
    }

    // A "Usage" form belongs to the heading it sits under. When pdftotext wraps a
    // long form it can lose a node — ":CALCulate2:<function>:LIMit<Y>:CLEar" coming
    // back as ":CALCulate2:<function>:CLEar" — and that is a command nobody has.
    const norm = s => s.split(/[\s(]/)[0]
      .replace(/<[^>]*>/g, 'N').replace(/[\[\]]/g, '').replace(/^:/, '').replace(/\?$/, '')
      .toUpperCase();
    const want = norm(head);
    const kept = syntaxes.filter(s => norm(s).startsWith(want) || want.startsWith(norm(s)));

    if (!kept.length) kept.push(head);
    if (desc.length < 12) continue;
    desc = desc[0].toUpperCase() + desc.slice(1);
    pushEntry(kept, desc, null);
  }
}

// ---- Keysight style -----------------------------------------------------
//
// Keysight's programmer guides stack their labels in a narrow left column that
// pdftotext detaches from the content, so "Command Syntax" can end up beside the
// *query* form and "Return Format" beside an argument list. Anchoring on those
// labels produces confidently mismatched entries.
//
// The prose saves it: every description names the command it belongs to —
//
//   The :CHANnel<n>:SCALe command sets the vertical scale, or units per division.
//   The :CHANnel<n>:SCALe? query returns the current scale setting for the channel.
//
// so parsing the paragraphs alone gives correctly paired entries and ignores the
// column layout entirely. 1,669 of them in the InfiniiVision 3000T guide.
function parseKeysight2() {
  const seen = new Set();
  for (let i = 0; i < lines.length; i++) {
    // The paragraph starts the line, or follows a detached left-column label
    // ("Example Code     The :WAVeform:FORMat command sets ..."). Requiring two
    // spaces before "The" is what separates that from a mid-sentence mention in
    // a bullet, which describes a *different* command.
    const m = lines[i].match(/^(?:\s*|.*?\s{2,})The\s+([:*]?[A-Za-z][^\s,]*)\s+(command|query)\s+(\S.*)$/);
    if (!m) continue;
    let [, syntax, kind, rest] = m;

    // The prose is inconsistent about the leading colon — "The HARDcopy:INKSaver
    // command controls..." alongside "The :CHANnel<n>:SCALe command sets...". Accept
    // both, but only when the name is really a command path, not an English word.
    if (!syntax.startsWith('*')) {
      if (!syntax.includes(':')) continue;
      if (!syntax.startsWith(':')) syntax = ':' + syntax;
    }

    // Keysight is inconsistent about writing the "?" in prose — "The :WAVeform:DATA
    // query returns the binary block ...". Trust the noun, and fix the form to match.
    const isQuery = kind === 'query';
    if (isQuery && !syntax.endsWith('?')) syntax += '?';
    if (!isQuery && syntax.endsWith('?')) syntax = syntax.slice(0, -1);
    if (!isCmd(syntax)) continue;

    // Continue the sentence across wrapped lines.
    let desc = rest;
    for (let k = i + 1; k < lines.length && k < i + 5; k++) {
      const t = lines[k].trim();
      if (!t || isCmd(t) || /^(The|NOTE|Example)\b/.test(t)) break;
      desc += ' ' + t;
      if (/[.!?]$/.test(t)) break;
    }
    desc = desc.split(/(?<=\.)\s+(?=[A-Z])/)[0];        // first sentence only
    if (desc.length < 12) continue;
    desc = desc[0].toUpperCase() + desc.slice(1);

    // Prefer a form carrying its arguments, if one is printed nearby.
    let full = syntax;
    for (let k = Math.max(0, i - 30); k < i; k++) {
      const t = lines[k].trim();
      if (!t.startsWith(syntax + ' ')) continue;
      if (t.length > syntax.length + 60) continue;
      if (/\b(the|and|for|with|see|page)\b/i.test(t.slice(syntax.length))) continue;
      full = t;
      break;
    }

    const key = full.toUpperCase();
    if (seen.has(key)) continue;
    seen.add(key);
    pushEntry([full], desc, null);
  }
}

// ---- Block style --------------------------------------------------------
//
// Keysight's supply and generator guides print the set and query forms as
// consecutive column-0 lines, then describe them in a following paragraph:
//
//   [SOURce:]VOLTage:SENSe[:SOURce] INTernal | EXTernal, (@<chanlist>)
//   [SOURce:]VOLTage:SENSe[:SOURce]? (@<chanlist>)
//
//                          Only supported by E36312A and E36313A models.
//
//   The command specifies whether the power supply uses remote or local sensing.
//   The query returns the selected state of the remote sense relay.
//
// The "The command ..." / "The query ..." split maps straight onto the two forms.
function parseBlock() {
  for (let i = 0; i < lines.length; i++) {
    if (/^\s/.test(lines[i])) continue;
    const t = lines[i].trim();
    if (!t || !isCmd(t) || /\.{3,}/.test(t)) continue;

    // Gather the run of column-0 command lines. A long form wraps its parameter
    // clause onto the next line — "... | DEFault," then "(@<chanlist>)" — and that
    // continuation belongs to the form above it, not to the end of the run:
    // breaking there loses the set form and keeps only the query.
    const syntaxes = [];
    let k = i;
    for (; k < lines.length && k < i + 8; k++) {
      const u = lines[k];
      if (/^\s/.test(u)) break;
      const s = u.trim();
      if (!s) break;
      if (isCmd(s)) { syntaxes.push(s); continue; }
      if (syntaxes.length && /^[(\[{<]/.test(s) && s.length < 40 &&
          /[,|]\s*$/.test(syntaxes[syntaxes.length - 1])) {
        syntaxes[syntaxes.length - 1] += ' ' + s;
        continue;
      }
      break;
    }
    if (!syntaxes.length) continue;

    // Then the prose, within a short reach — skipping model notes and tables.
    let setDesc = '', queryDesc = '';
    for (let m = k; m < lines.length && m < k + 14; m++) {
      const s = lines[m].trim();
      if (!s) continue;
      if (isCmd(s)) break;
      const cm = s.match(/^The command\s+(\S.*)$/i);
      const qm = s.match(/^The query\s+(\S.*)$/i);
      if (cm && !setDesc) { setDesc = cm[1]; continue; }
      if (qm && !queryDesc) { queryDesc = qm[1]; continue; }
      if (!setDesc && !queryDesc && /^(Sets?|Returns?|Queries|Specifies|Enables?|Selects?|Clears?)\b/.test(s))
        setDesc = s;
    }
    if (!setDesc && !queryDesc) { i = k - 1; continue; }

    for (const s of syntaxes) {
      const isQuery = s.includes('?');
      const d = (isQuery ? queryDesc || setDesc : setDesc || queryDesc);
      if (!d || d.length < 12) continue;
      pushEntry([s], d[0].toUpperCase() + d.slice(1), null);
    }
    i = k - 1;
  }
}

// ---- Rohde & Schwarz style ----------------------------------------------
//
//   CHANnel<m>:SCALe <Scale>              <- heading, indented, command + params only
//
//   Sets the vertical scale for the indicated channel.      <- description
//
//   Suffix:            .
//   <m>                1..4
//   Parameters:        Scale value, given in Volts per division.
//   <Scale>
//   Manual operation:  See "[Scale]" on page 40
//
// The whole manual is written this way, so the trick is telling a heading from the
// many cross-references to the same command ("CHANnel<m>:SCALe on page 292") and
// from the contents list. Both are excluded below.
const RS_LABEL = /^(Suffix|Parameters?|Setting parameters?|Query parameters?|Return values?|Usage|Example|Manual operation|Options?|Firmware|Mode|Characteristics|Asynchronous command)\s*:/i;

// Is a heading's tail one of the two plain-word parameter shapes the FSU-era manuals
// print — a pipe alternation of single value tokens ("ON | OFF", "ABSolute | RELative")
// or a numeric range ("0 to 65535")? Anything looser lets the long-form example lines
// in: "…FREQuency:CENTer 1.230GHZ" is an example, "…:PTRansition 0 to 65535" a heading,
// and an arguments-only test passes both.
function bareParamsRs(rest) {
  // A range needs its "to" — that word is what separates "0 to fmax" and "10Hz to
  // 10MHz", which are headings, from "COUNt 7" and "CENTer 1.230GHZ", which are example
  // lines. A bound may carry a unit, spaced or not, or be the symbolic fmax / max. the
  // FSU writes, and a range may trail a parenthetical note. An alternation's branches
  // are each a single value token or a whole range — "2.5 ms to 16000 s (frequency
  // domain) | 1 µs to 16000 s (time domain)" is one heading's clause — but a lone token
  // without a pipe stays rejected, or "COUNt 7" walks straight in.
  const bound = '(?:-?[\\d.]+\\s*[A-Za-zµμ%]{0,4}|f?(?:max|min)\\.?|MAX\\w*|MIN\\w*)';
  const range = new RegExp(`^${bound}\\s*(?:to|\\.\\.\\.|…)\\s*${bound}(?:\\s*\\([^)]*\\))?$`, 'i');
  const token = /^[A-Za-z0-9<>{}\[\]._+-]+$/;
  if (!rest.includes('|')) return range.test(rest.trim());
  return rest.split('|').every(b => token.test(b.trim()) || range.test(b.trim()));
}

function parseRs() {
  // Locate the headings first so each block is bounded by the next.
  const isBlank = i => !(lines[i] || '').trim();

  // The FSP prints its suffix ranges with spaces — "DELTamarker<1 to 4>" — and the space
  // splits the header token everywhere a header is split: isCmd rejects the heading, and
  // whatever survives, validate rejects at build. Thirty-seven entries died that way,
  // the whole marker tree among them. Carried as "<1...4>", the spelling every other R&S
  // catalog uses; the config's source field says so.
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].includes(' to ')) lines[i] = lines[i].replace(/<(\d+) to (\d+)>/g, '<$1...$2>');
  }

  // A long heading may wrap its own path: the FSU breaks
  //
  //   CALCulate<1|2>:DELTamarker<1...4>:FUNCtion:FIXed:
  //   RPOint:X
  //
  // at the margin, and the tail line then reads as a bare fragment. Join a column-0
  // head-shaped line ending in a colon to the head-shaped line below it, when the
  // result is a command — the same defence isCmd's comment describes for the Tektronix
  // guides, finally implemented where a manual actually needs it.
  for (let i = 0; i + 1 < lines.length; i++) {
    const cur = lines[i];
    if (/^\s/.test(cur) || !/:$/.test(cur.trimEnd())) continue;
    const head = cur.trim();
    if (!/^[\[A-Z]/.test(head) || /\s/.test(head)) continue;
    // A wrapped heading's first half is a path — at least two colons — and never a bare
    // label: "Example:" ends in a colon too, and joining it to the command on the next
    // line manufactured "Example:VOLT 10V" entries in the NGL200.
    if ((head.match(/:/g) || []).length < 2) continue;
    if (/^(Example|Note|Mode|Suffix|Usage|Parameters?|Characteristics)/i.test(head)) continue;
    let n = i + 1;
    while (n < lines.length && !lines[n].trim()) n++;
    // Both halves at column 0 — that is where this manual wraps its headings, and the
    // requirement keeps the join from rearranging manuals that never do this.
    if (/^\s/.test(lines[n] || ' ')) continue;
    const next = (lines[n] || '').trim();
    const m = next.match(/^([A-Za-z][A-Za-z0-9:<>|.\[\]]*\??)(\s.*)?$/);
    if (!m) continue;
    if (!isCmd(head + m[1])) continue;
    lines[i] = head + m[1] + (m[2] || '');
    lines[n] = '';
  }

  // A heading whose parameter placeholder does not fit the column wraps it onto its own
  // indented line, sometimes across a blank one:
  //
  //   [SENSe:]CORRection:FRESponse<si>:BASeband:USER:FLISt<fli>:INSert
  //           <FilePath>
  //
  // Left as two lines the placeholder reads as the entry's description, is then too short
  // to keep, and the stray line also breaks the run of consecutive headings that share one
  // description — so the whole group goes with it. That cost the FSW 47 commands. Join the
  // placeholder back on before anything else looks at the text.
  for (let i = 1; i < lines.length; i++) {
    const t = (lines[i] || '').trim();
    if (!/^(<[A-Za-z][\w .]*>|\{[^{}]*\})$/.test(t)) continue;
    let p = i - 1;
    while (p >= 0 && !(lines[p] || '').trim()) p--;
    if (p < 0) continue;
    const prev = lines[p];
    const head = prev.trim();
    // Only a parameterless command heading takes a continuation, and the wrap is always
    // indented past it — which is what separates it from a placeholder standing alone in
    // a parameter table.
    if (/\s/.test(head) || !isCmd(head)) continue;
    if (lines[i].search(/\S/) <= prev.search(/\S/)) continue;
    lines[p] = prev.replace(/\s*$/, '') + ' ' + t;
    lines[i] = '';
  }

  // A column-0 heading may wrap its parameter clause onto an indented line — three of
  // the FSU's do, and each cut is different: "<measurement" is an open placeholder,
  // "…,<trigger" ends mid-list, and the BAUD rates close their line balanced with the
  // rest of the alternation below ("| 57600 | 115200 | 128000"). Only the FSU puts its
  // headings at column 0, so keying the join on that keeps it out of every other manual.
  for (let i = 0; i < lines.length; i++) {
    if (/^\s/.test(lines[i]) || !/^\[?[A-Z][A-Za-z0-9]*(<[^>]*>)?[:\[]/.test(lines[i])) continue;
    if (!lines[i].includes(' ')) continue;
    for (;;) {
      let n = i + 1;
      while (n < lines.length && !lines[n].trim()) n++;
      // The continuation is usually indented, but the FSP wraps one heading's clause at
      // column 0 — "time>, <period>, < # of pulses to measure>" — so indentation is not
      // required: the trigger below only fires while the heading is genuinely open, and
      // a column-0 line that is a label or the next command still ends the join.
      if (n >= lines.length) break;
      const nt = lines[n].trim();
      if (RS_LABEL.test(nt) || isCmd(nt)) break;
      const unbal = (lines[i].match(/</g) || []).length !== (lines[i].match(/>/g) || []).length;
      const trig = unbal || /[,|]\s*$/.test(lines[i].trimEnd())
                || /^[|,]/.test(nt) || /^[a-z #][^<]{0,40}>/.test(nt);
      if (!trig) break;
      lines[i] = lines[i].trimEnd() + ' ' + nt;
      lines[n] = '';
    }
  }

  const heads = [];
  for (let i = 1; i < lines.length; i++) {
    const t = lines[i].trim();
    // 160, matching validate's cap: a joined FSU heading — MSUMmary? with its four
    // placeholders, the BAUD alternation — runs to ~135, and at the old 100 the join
    // above completed them only for the head scan to drop them silently, which no
    // guard can see. Everything after the length still has to pass isCmd and the
    // rest-shape tests, so prose does not slip in with the room.
    if (!t || t.length > 160) continue;
    // Contents list or cross-reference. The dotted leader is matched as a long run of
    // dots, or a short one followed by the page number, rather than as any "..." at all:
    // R&S writes a variable-length parameter list with a trailing ellipsis, and
    // "CALCulate<n>:LIMit<li>:CONTrol[:DATA] <LimitLinePoints>..." is a heading, not a
    // leader. Reading it as one silently cost the FSW its four limit-line data commands.
    if (/\.{4,}|\.{3,}\s*\d+\s*$|\bon page\b/.test(t)) continue;
    if (!isCmd(t)) continue;

    // A heading carries the command and its parameter placeholder, nothing else:
    // "CHANnel<m>:SCALe <Scale>". Anything with prose after the header is a mention.
    // A heading carries a placeholder — "<Scale>", "{ON|OFF}" — or a bare parameter
    // clause: the single literal keyword of an event command ("…REFerence:AUTO ONCE"),
    // or, in the FSU-era manuals, the whole alternation or range spelled in plain words
    // on the heading line — "…:MODE:HCONtinuous ON | OFF", "…:PTRansition 0 to 65535".
    // Ninety of the FSU's 167 headings carried their parameters that way and were
    // rejected wholesale. The clause must read as arguments, not prose, and the head
    // must be in the long form — a lower-case letter in it — which is the same guard
    // that keeps the abbreviated example lines ("INIT:CONT OFF") out.
    // A "(" tail is never accepted: "(option TV Trigger, B6)" is an annotation sixteen
    // FSL headings carry, and even "(@1)" turned out to mark an example row rather than
    // a heading when it was tried.
    const rest = t.slice(t.split(/\s/)[0].length).trim();
    const head0 = t.split(/\s/)[0];
    if (rest && !/^[<{\[]/.test(rest) &&
        !((/^[A-Z]{3,10}$/.test(rest) || bareParamsRs(rest)) && /[a-z]/.test(head0))) continue;
    if (/\b(the|and|for|with|this|that|see|use|command)\b/i.test(rest)) continue;

    // Headings are set off by a blank line, which is what separates a real "STOP"
    // entry from the word STOP inside a paragraph about I2C stop conditions —
    // single-mnemonic commands are otherwise indistinguishable from prose.
    //
    // Or by exactly one kind of lead-in: the FSP introduces its option commands with
    // "Command for option FS-K82 cdma2000 BTS:" directly above the heading, and
    // demanding the blank cost every such entry. The word "option" is load-bearing —
    // anything looser is not survivable. A first draft accepted any colon-ended prose
    // line and admitted wrapped-heading fragments across five manuals; a second anchored
    // on "Command(s) for" and pulled a boilerplate annex — "commands for this section:"
    // over a list of standardized SCPI — into the RTB2000 and FPC, complete with
    // SENSe:FREQuency:STOP for an oscilloscope.
    const prev = (lines[i - 1] || '').trim();
    const afterBlank = isBlank(i - 1);
    const afterHead = heads.length && heads[heads.length - 1][0] === i - 1;
    const afterLeadIn = /^Commands? for (the )?option/i.test(prev) && /:$/.test(prev);
    if (!afterBlank && !afterHead && !afterLeadIn) continue;

    heads.push([i, t]);
  }

  // Consecutive headings share one description — the manual lists every subsystem the
  // command applies to (BUS<b>:…:ALL?, DIGital<m>:…:ALL?, CHANnel<m>:…:ALL?) and then
  // describes them once. Group them so all the variants are kept, not just the last.
  // Blank lines may separate them: the manual sets the variants apart when their headings
  // are long, and joining a wrapped placeholder above leaves an emptied line behind. Only
  // blanks may intervene — any prose between two headings is the first one's description,
  // and that is what stops unrelated entries being merged.
  const onlyBlankBetween = (a, b) => {
    for (let k = a + 1; k < b; k++) if (!isBlank(k)) return false;
    return true;
  };
  for (let h = 0; h < heads.length; h++) {
    const group = [heads[h]];
    while (h + 1 < heads.length && onlyBlankBetween(heads[h][0], heads[h + 1][0])) { group.push(heads[++h]); }

    const start = group[group.length - 1][0];
    const end = h + 1 < heads.length
      ? Math.min(heads[h + 1][0], start + 60)
      : Math.min(lines.length, start + 60);

    let desc = '';
    for (let k = start + 1; k < end; k++) {
      const t = lines[k].trim();
      if (!t) continue;
      if (RS_LABEL.test(t) || isCmd(t)) break;
      // Page furniture: the running heads, and the FPC's per-application chapter titles —
      // "Remote Commands of the VNA Application." reached three entries as their prose.
      if (/^R&S|^User Manual|^Remote Control Commands$|^Remote Commands of the /.test(t)) continue;
      // A contents line is not a description — "FREQuency:SETTings:COUPling:ENABle……122"
      // reached one FPC entry as its prose, and no real sentence runs four dots deep.
      if (/\.{4}/.test(t)) continue;
      desc = t;
      for (let m = k + 1; m < end && m < k + 3; m++) {                       // wrapped sentence
        const u = lines[m].trim();
        if (!u || RS_LABEL.test(u) || isCmd(u) || /^R&S|^User Manual/.test(u)) break;
        desc += ' ' + u;
        if (/[.!?]$/.test(u)) break;
      }
      break;
    }
    if (desc.length < 12) continue;

    // The query form is a documented rule, not a guess. The manual's SCPI conventions
    // state: "A query is defined for each setting command unless explicitly specified
    // otherwise", and the exception is spelled out per entry in the Usage field —
    // "Query only", "Setting only" or "Event". So read Usage, and add the query form
    // to a settable command exactly as the guide says it exists.
    let usage = '';
    for (let k = start + 1; k < end; k++) {
      const u = lines[k].match(/^\s*Usage:\s*(\S.*)$/);
      if (u) { usage = u[1].trim(); break; }
    }

    // Older R&S manuals state the same exception in prose instead, and have no Usage field
    // at all — the FSL guide has zero of them against 152 sentences reading "This command is
    // an event and therefore has no *RST value and no query". Reading only Usage there makes
    // every command look settable, and the rule then invents a query for all of them,
    // including *RST? and "position the marker to the next peak?". Take the sentence as the
    // Usage field it stands in for.
    let prose = '';
    for (let k = start + 1; k < end; k++) {
      const t = lines[k];
      if (/^\s*(Suffix|Parameters?|Example|Manual operation)\s*:/i.test(t)) break;
      prose += ' ' + t;
    }
    const noQuery = /\bis an "?event"?\b|\bhas no query\b|\bno \*RST value and no query\b/i.test(prose);

    const settable = !noQuery && !/Query only|Setting only|Event/i.test(usage);

    const syntaxes = [];
    for (const [, head] of group) {
      syntaxes.push(head);
      const bare = head.split(/\s/)[0];
      if (settable && !bare.endsWith('?')) syntaxes.push(bare + '?');
    }
    pushEntry(syntaxes, desc, null);
  }
}

// ---- Siglent SDS style ---------------------------------------------------
//
// Each entry opens a page, and the running page header carries the command:
//
//   :CHANnel<n>:SCALe                             SDS Series Programming Guide
//   Command/Query
//   DESCRIPTION        The command sets the vertical sensitivity in Volts/div. If the
//   COMMAND SYNTAX     probe attenuation is changed, the scale value is multiplied by
//                      the probe's attenuation factor.
//   QUERY SYNTAX
//   RESPONSE FORMAT    The query returns the current vertical sensitivity ...
//   EXAMPLE
//   RELATED COMMANDS   :CHANnel<n>:SCALe <scale>
//                      ...
//                      :CHANnel<n>:SCALe?
//
// The labels stack in a left column that pdftotext detaches from the content, exactly
// as in the Keysight guides — "QUERY SYNTAX" can sit beside the description's third
// line. So the labels are used only to find the description, and the syntax forms are
// gathered by matching every command in the block against the heading's own header.
const SDS_LABEL = /^(DESCRIPTION|COMMAND SYNTAX|QUERY SYNTAX|RESPONSE FORMAT|EXAMPLE|RELATED COMMANDS|NOTE)\s*(.*)$/;

function parseSds() {
  const norm = s => s.split(/[\s(]/)[0]
    .replace(/<[^>]*>/g, 'N').replace(/[\[\]]/g, '').replace(/^:/, '').replace(/\?$/, '')
    .toUpperCase();

  // The typesetting drops a space into a command path here and there — the query form
  // of the channel coupling is printed ":CHANnel<n>: COUPling?". Closing that up is
  // what makes the query half of those entries visible at all.
  const tighten = s => s.replace(/:\s+(?=[A-Za-z<])/g, ':');

  // Heading lines repeat on every page of a multi-page entry, so collapse runs.
  const heads = [];
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(/^(:\S+)\s{2,}SDS.*Programming Guide\s*$/);
    if (!m || !isCmd(m[1])) continue;
    if (heads.length && norm(heads[heads.length - 1][1]) === norm(m[1])) continue;
    heads.push([i, m[1]]);
  }

  for (let h = 0; h < heads.length; h++) {
    const [start, head] = heads[h];
    const end = h + 1 < heads.length ? heads[h + 1][0] : Math.min(lines.length, start + 200);
    const want = norm(head);

    // "Command/Query", "Command" or "Query" — the guide states which forms exist.
    let kind = '';
    for (let k = start + 1; k < end && k < start + 4; k++) {
      const t = lines[k].trim();
      if (!t) continue;
      if (/^(Command\/Query|Command|Query)$/.test(t)) kind = t;
      break;
    }

    let desc = '';
    const syntaxes = [];
    for (let k = start + 1; k < end; k++) {
      const raw = lines[k];
      const t = raw.trim();
      if (!t) continue;
      if (/^\d+\s*\/\s*\d+|www\.siglent\.com/.test(t)) continue;      // page furniture

      const lm = t.match(SDS_LABEL);
      const content = lm ? lm[2].trim() : t;

      // A syntax form: a command whose header is the entry's own.
      const tight = tighten(content);
      if (tight && isCmd(tight) && norm(tight) === want) {
        syntaxes.push(tight);
        continue;
      }
      if (lm && lm[1] === 'DESCRIPTION' && content && !desc) {
        desc = content;
        for (let m2 = k + 1; m2 < end && m2 < k + 5; m2++) {
          const u = lines[m2].trim();
          if (!u) break;
          const um = u.match(SDS_LABEL);
          const uc = um ? um[2].trim() : u;
          if (!uc || isCmd(uc)) break;
          desc += ' ' + uc;
          if (/[.!?]$/.test(uc)) break;
        }
      }
    }

    if (!syntaxes.length) {
      // No form printed in the block: fall back to the heading, honouring the type.
      if (kind === 'Query') syntaxes.push(head.endsWith('?') ? head : head + '?');
      else syntaxes.push(head.replace(/\?$/, ''));
    }
    if (desc.length < 12) continue;

    // The page header repeats the command bare, with neither parameters nor "?".
    // Drop that when a real form is present — it is the entry's title, not a
    // command anyone would send.
    let forms = [...new Set(syntaxes)];
    if (forms.length > 1) {
      const real = forms.filter(s => s.includes(' ') || s.endsWith('?'));
      if (real.length) forms = real;
    }
    pushEntry(forms, desc, null);
  }

  // Second pass. On some pages the running title does not render and the DESCRIPTION
  // text lands on the header line instead:
  //
  //   :PRINt                        The query captures the screen and returns the data
  //   Query                         image format.
  //   DESCRIPTION
  //   QUERY SYNTAX                  :PRINt? <type>
  //
  // Those entries are invisible to the pass above — which costs ":PRINt", the SDS
  // screen dump — so catch them here. Anything already found wins, being the fuller
  // record; the de-duplication at the end keeps the longest description.
  const TYPE_LABEL = /^(Command\/Query|Command|Query)\s{2,}/;

  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(/^(:\S+)\s{2,}([A-Z][a-z]\S*\s+\S.*)$/);
    if (!m) continue;
    const [, header, first] = m;
    if (!isCmd(header) || /Programming Guide/.test(first)) continue;

    let desc = first.trim();
    const syntaxes = [];
    for (let k = i + 1; k < lines.length && k < i + 25; k++) {
      const t = lines[k].trim();
      if (!t) continue;
      if (/^\d+\s*\/\s*\d+|www\.siglent\.com/.test(t)) continue;

      // Strip whichever column label the line opens with, and read the content.
      const raw = lines[k].replace(TYPE_LABEL, '').replace(SDS_LABEL, '$2');
      const content = raw.trim();
      if (!content) continue;

      const tight = tighten(content);
      if (isCmd(tight) && norm(tight) === norm(header)) { syntaxes.push(tight); continue; }
      if (isCmd(tight)) break;                                // a different command: next entry
      if (!/[.!?]$/.test(desc)) desc += ' ' + content;
    }

    if (desc.length < 12) continue;
    let forms = [...new Set(syntaxes.length ? syntaxes : [header])];
    if (forms.length > 1) {
      const real = forms.filter(s => s.includes(' ') || s.endsWith('?'));
      if (real.length) forms = real;
    }
    pushEntry(forms, desc, null);
  }
}

// ---- GW Instek style -----------------------------------------------------
//
//   :ACQuire:AVERage                       <- heading at column 0
//   Select the average number of waveform acquisition. The range for ...
//   Syntax
//   :ACQuire:AVERage <NR1>
//   :ACQuire:AVERage ?
//   Arguments
//   ...
//
// Two quirks: the heading can carry a "(query only)" or "(no query form)" note, and
// the guide writes a query with a space before the mark — ":TIMebase:SCALe ?" — which
// has to be closed up or every query form is read as its own set command.
// Anchored to the end of the line on purpose. These labels sit alone, and matching
// them as a prefix would swallow the description of every query in the guide —
// they almost all open "Return the value of ...", which "Returns?\b" happily eats.
const GW_LABEL = /^(Syntax|Arguments?|Examples?|Returns?)\s*:?\s*$/i;
const GW_NOTE = /^Note\s*:/i;

function parseGwInstek() {
  const tighten = s => s.replace(/\s+\?/g, '?');
  const norm = s => tighten(s).split(/[\s(]/)[0]
    .replace(/<[^>]*>/g, 'N').replace(/[\[\]]/g, '').replace(/^:/, '').replace(/\?$/, '')
    .toUpperCase();

  // The Syntax block prints its forms at column 0 too, so ":CHANnel<X>:SCALe?" looks
  // exactly like a heading. Track which block we are in and skip candidates inside a
  // Syntax listing — otherwise every query form starts a bogus entry of its own and
  // is lost from the one it belongs to.
  const heads = [];
  let scanningSyntax = false;
  let current = '';
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();
    if (GW_LABEL.test(trimmed)) { scanningSyntax = /^Syntax/i.test(trimmed); continue; }

    if (/^\s/.test(line)) continue;
    const t = trimmed.replace(/\s*\((?:query only|no query form)\)$/i, '');
    if (!t || t.length > 90 || /\.{3,}/.test(t)) continue;
    if (!isCmd(tighten(t))) { scanningSyntax = false; continue; }

    // Inside a Syntax listing, a command is one of the current entry's forms — but
    // only if it *is* the current command. The next entry's heading often follows
    // with no blank line between (":RUN"'s Syntax block runs straight into ":STOP"),
    // and a different command there means the listing has ended.
    if (scanningSyntax && norm(tighten(t)) === current) continue;
    scanningSyntax = false;

    // The heading is the command alone; a line with arguments is a Syntax entry.
    if (/\s/.test(tighten(t))) continue;
    current = norm(tighten(t));
    heads.push([i, tighten(t)]);
  }

  for (let h = 0; h < heads.length; h++) {
    const [start, head] = heads[h];
    const end = h + 1 < heads.length ? heads[h + 1][0] : Math.min(lines.length, start + 60);
    const want = norm(head);

    let desc = '';
    const syntaxes = [];
    let inSyntax = false;
    for (let k = start + 1; k < end; k++) {
      const t = lines[k].trim();
      if (!t) continue;
      if (/^\d+$|Programming Manual$/.test(t)) continue;          // page furniture

      if (GW_NOTE.test(t)) continue;
      if (GW_LABEL.test(t)) { inSyntax = /^Syntax/i.test(t); continue; }

      const tight = tighten(t);
      // Only the Syntax block holds real forms. Elsewhere in the entry the same
      // command appears with literal arguments — "*RCL 1", ":TIMebase:SCALe 5e-3
      // sets the horizontal scale" — which are examples, not syntax.
      if (inSyntax) {
        if (isCmd(tight) && norm(tight) === want) syntaxes.push(tight);
        continue;
      }
      if (isCmd(tight)) break;                   // a different command: next entry
      if (!desc) {
        desc = t;
        for (let m = k + 1; m < end && m < k + 4; m++) {
          const u = lines[m].trim();
          if (!u || GW_LABEL.test(u) || GW_NOTE.test(u) || isCmd(tighten(u))) break;
          desc += ' ' + u;
          if (/[.!?]$/.test(u)) break;
        }
      }
    }

    if (!syntaxes.length) syntaxes.push(head);
    if (desc.length < 12) continue;
    pushEntry([...new Set(syntaxes)], desc, null);
  }
}

// ---- Fluke style ---------------------------------------------------------
//
// The whole reference is command-summary tables whose rows carry a tree:
//
//   MEASure[:SCALar]                            Path to measure control
//     :CAPacitance?                               Preset and make capacitance measurement
//     :CURRent
//        :DC?                                     Make a dc current measurement
//
// Indentation encodes the path, so a row has to be joined to its parents. Worse,
// pdftotext keeps the two columns aligned in some tables and shifts the description
// column by a row in others — Table 12 pairs ":DIODe?" with "Make a frequency
// measurement" and ":FREQuency?" with "Make a 4-wire resistance measurement".
//
// There is no way to tell a shifted table from a straight one by looking at the
// layout, so the check is on the content: a description is only accepted when it
// shares a distinctive word with the command it sits beside. "Make a dc current
// measurement" corroborates ":CURRent:DC?"; "Make a frequency measurement" does not
// corroborate ":DIODe?", and that row is dropped.
function parseFluke() {
  const STOP = /^(Command|Description|Table \d+\.|Note|\d+$)/;

  // Words in a command, split on the SCPI casing, for the corroboration check.
  const wordsOf = s => (s.toLowerCase().match(/[a-z]{3,}/g) || [])
    .filter(w => !['scalar', 'path', 'the', 'command', 'sense', 'configure', 'measure'].includes(w));

  let stack = [];               // [indent, mnemonic] from the shallowest down
  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i];
    const m = raw.match(/^(\s*)(\S[^\s]*(?:\s+"[^"]*"|\s+\{[^}]*\})?)\s{2,}(\S.*)$/);
    if (!m) {
      // A path row: a command fragment with no description opens a new level.
      const p = raw.match(/^(\s*)([:A-Za-z][^\s]*)\s*$/);
      if (p && p[2].length < 40 && !STOP.test(p[2])) {
        const indent = p[1].length;
        stack = stack.filter(([ind]) => ind < indent);
        stack.push([indent, p[2]]);
      }
      continue;
    }

    const [, pad, cmdPart, desc] = m;
    const indent = pad.length;
    if (STOP.test(cmdPart) || STOP.test(desc)) continue;

    stack = stack.filter(([ind]) => ind < indent);
    const head = cmdPart.split(/\s/)[0];

    // A row that is only a path ("Path to ...") extends the stack instead.
    if (/^Path to /i.test(desc)) { stack.push([indent, head]); continue; }

    const full = stack.map(([, s]) => s).join('') + head;
    const syntax = full.replace(/\]\[/g, '][').replace(/::+/g, ':');
    if (!isCmd(syntax)) continue;

    // The corroboration check that makes the shifted tables safe.
    const cw = new Set(wordsOf(syntax));
    const dw = wordsOf(desc);
    if (!dw.some(w => cw.has(w) || [...cw].some(c => c.startsWith(w) || w.startsWith(c)))) continue;

    pushEntry([syntax], desc.trim(), null);
  }
}

// ---- Plain style: a column-0 command, then prose --------------------------
//
// Chroma:
//   MEASure:CURRent?
//   This command is used to query the current output value measured by the supply.
//
// B&K Precision, which adds a labelled block whose right-hand column carries the
// argument forms — interleaved as usual, so those are matched against the heading
// rather than read from beside their labels:
//   SENSe:AVERage:COUNt
//   The command is used to specify the filter count. ...
//                      Command Syntax   SENSe:AVERage:COUNt <NRf+>
//                        Query Syntax   SENSe:AVERage:COUNt?
//
// A bare subsystem name ("SYSTem", "MEASure") heads a section rather than being a
// command, so a heading has to carry a colon, a "?" or an argument to count.
function parsePlain() {
  // A syntax line too long for the column wraps, and Chroma puts a blank line between the
  // halves like it does everywhere else:
  //
  //   Setting Syntax: PROGram:DATA:LIST<space><Arg1>,<Arg2>,<Arg3>,<Arg4>,
  //
  //                      <Arg5>,<Arg6>,<Arg7>,<Arg8>,<Arg9>,<Arg10>,<Arg11>,
  //
  // Read a half at a time it becomes a command taking four arguments where the guide
  // documents sixteen. The trailing comma is the tell, and it is unambiguous: no complete
  // syntax line ends in one.
  for (let i = 0; i < lines.length; i++) {
    if (!/^\s*(Setting|Query|Command)\s+Syntax\s*:/.test(lines[i])) continue;
    while (/,\s*$/.test(lines[i])) {
      let n = i + 1;
      while (n < lines.length && !lines[n].trim()) n++;
      if (n >= lines.length) break;
      const next = lines[n].trim();
      // Only a continuation, never the next labelled line or a new heading.
      if (/^\S+\s*:/.test(next) && !/^[A-Za-z\[][A-Za-z0-9:<>\[\]|]*[,<]/.test(next)) break;
      if (!/^[<\[(A-Za-z]/.test(next)) break;
      lines[i] = lines[i].replace(/\s*$/, '') + next;
      lines[n] = '';
    }
  }

  // A heading misprinted with a space after a colon — "[ADVance:]OCP: LATCh" — reads as
  // a one-word tail and is rejected, taking its whole entry down. The 9130B guide does
  // the same ("OUTPut: PARallel"), and that catalog's precedent is to close the space
  // rather than carry the misprint into syntax nothing accepts. Mend only the narrow
  // shape: column 0, exactly head-colon and one mnemonic (plus at most the floated Type
  // word), the joined result a command, and the head not a label — "Type: Channel-Specific"
  // must stay two words.
  // Two teeth keep the mend off section titles. The head must be more than one node —
  // an inner colon or a bracketed root — because "Configuration: Capacitance" is a
  // Keysight chapter heading and one mnemonic deep, where a misprinted command head like
  // "[ADVance:]OCP:" carries its path with it. And it must be cased like SCPI, upper
  // shortform first: "Configuration" opens with one capital, "OCP" with three.
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(
      /^((?:\[[A-Za-z]+:\])?[A-Za-z][A-Za-z0-9:\[\]]*:) ([A-Za-z][A-Za-z0-9\[\]]*\??)(\s{2,}(?:Channel-Specific|Global|All Channels))?\s*$/);
    if (!m) continue;
    if (/^(Type|Description|Note|Remarks?|Examples?|Parameters?|Syntax)/i.test(m[1])) continue;
    if (!/[:\]].*:$/.test(m[1])) continue;             // one node deep is a title, not a path
    if (!/^\[?[A-Z]{2,}/.test(m[1])) continue;         // and SCPI leads with its short form
    const joined = m[1] + m[2];
    if (!isCmd(joined)) continue;
    lines[i] = joined + (m[3] || '');
  }

  const norm = s => s.split(/[\s(]/)[0]
    .replace(/<[^>]*>/g, 'N').replace(/[\[\]]/g, '').replace(/^:/, '').replace(/\?$/, '')
    .toUpperCase();

  // Is what follows the header a parameter clause rather than prose? Strip the
  // placeholder groups and see whether ordinary words are left: "{OFF|ON}" leaves
  // nothing, "<NRf> completed." leaves "completed" and is a wrapped sentence.
  const argsOnly = rest => {
    const bare = rest.replace(/<[^>]*>/g, '').replace(/\{[^}]*\}/g, '')
                     .replace(/\[[^\]]*\]/g, '').replace(/\([^)]*\)/g, '');
    return !/[a-z]{3,}/.test(bare);
  };

  const heads = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (/^\s/.test(line)) continue;
    // B&K titles its common commands "*CLS -- Clear Status"; drop the gloss. It also
    // lets a neighbouring column wrap onto the line — "*RCL <NRf>      completed." —
    // so cut at a run of three spaces, which is a column break rather than a space.
    const t = line.trim().replace(/\s+--\s+.*$/, '').split(/\s{3,}/)[0].trim();
    if (!t || t.length > 100 || /\.{3,}/.test(t)) continue;
    // A label is not a command, and these guides do not always indent theirs. Chroma's
    // 63600 sets most entries' labels in from the margin but writes some at column 0, and
    // there "Type:" and "Description:" have a colon in them like any command path — so
    // they were read as headings, which ended the real entry's block on the line after it
    // and left the command with no description at all. PROGram:DATA:LIST went that way.
    if (/^(Command|Query|Setting|Return|Returned)?\s*(Syntax|Parameters?|Examples?|Description|Type|Note|Remarks?)\s*:/i.test(t)) continue;

    if (!isCmd(t)) continue;

    const head = t.split(/\s/)[0];
    const rest = t.slice(head.length).trim();
    // Whatever follows the header must be a parameter clause, not prose — otherwise
    // a sentence opening with a command name ("*OPC does not prevent processing of
    // subsequent commands...") is read as an entry of its own.
    if (rest && (!/^[{<\[(]/.test(rest) || !argsOnly(rest))) continue;
    if (!head.includes(':') && !head.endsWith('?') && !rest) continue;   // section header

    // The Chroma 63200A prints the Type value on the heading's own line — pdftotext
    // floats the value column against the labels, so the whole block runs one row off
    // and the "Type:" line below carries the *description*. The trailing word is the
    // tell, and it has to be remembered: it licenses reading Type's content as prose.
    const typeAbsorbed = /\s{2,}(Channel-Specific|Global|All Channels)\s*$/
      .test(line.trim().replace(/\s+--\s+.*$/, ''));
    heads.push([i, t, typeAbsorbed]);
  }

  for (let h = 0; h < heads.length; h++) {
    // Consecutive command lines share the description below them — the B&K guide
    // lists ":MEASure:VOLTage[:DC]?" and ":MEASure:CURRent[:DC]?" together and then
    // describes both. Without grouping, the first of each pair is lost.
    const group = [heads[h]];
    while (h + 1 < heads.length && heads[h + 1][0] === heads[h][0] + 1) group.push(heads[++h]);

    const [start, first] = group[group.length - 1];
    const end = h + 1 < heads.length ? heads[h + 1][0] : Math.min(lines.length, start + 50);
    const want = norm(first);
    const typeAbsorbed = group.some(g => g[2]);
    let absorbClosed = false;

    let desc = '';
    const syntaxes = group.map(g => g[1]);

    // Does this entry label every line? Chroma's do:
    //
    //   ADVance:SINE:FREQuency
    //         Type:            Channel-Specific
    //         Description:     Set frequency for sine wave dynamic mode.
    //         Setting Syntax: ADVance:SINE:FREQuency<space><NRf+>[suffix]
    //         Query Syntax: ADVance:SINE:FREQuency?[<space><MAX | MIN>]
    //
    // with a blank line between each. Breaking at the first blank once a description is
    // in hand is right for a guide whose entry ends with its prose, and wrong here — it
    // stops before the two Syntax lines, which are where the forms actually are.
    let labelledBlock = false;
    for (let k = start + 1; k < end; k++) {
      if (/^\s*(Description|Type|Setting Syntax|Query Syntax|Command Syntax)\s*:/.test(lines[k])) {
        labelledBlock = true; break;
      }
    }

    for (let k = start + 1; k < end; k++) {
      const t = lines[k].trim();
      if (!t) { if (desc && !labelledBlock) break; else continue; }
      if (/^\d+[-–]\d+$|^Page |^\d+$/.test(t)) continue;                  // page furniture

      // Strip a right-aligned label so the content column can be read, and note
      // whether it was a syntax label — that is what licenses a form to extend the
      // heading below, rather than merely equal it.
      //
      // Two spellings: the label set off by a column of spaces, and the label closed by
      // a colon. Chroma writes the colon and then a single space, so matching only the
      // first spelling left "Type: Channel-Specific" standing as the description of 134
      // of the 63600's entries and lost every Query Syntax form.
      // Singular and plural both: the 63600 writes "Setting Parameters:", the 63200A
      // "Setting Parameter:" — and the unmatched singular left the label glued to its
      // content, which then read as neither syntax nor prose. The whole VOLTage and
      // POWer subsystems shipped without their parameter clauses that way.
      const LABELS = '(?:Command|Query|Setting) Syntax(?: [12])?'
                   + '|(?:Setting|Query|Return(?:ed)?) Parameters?'
                   + '|(?:Setting|Query|Return) Examples?'
                   + '|Parameters?|Examples?|\\*RST Value|Description|Type';
      const lm = t.match(new RegExp(`^(${LABELS})\\s*:\\s*(.*)$`))
              || t.match(new RegExp(`^(${LABELS})\\s{2,}(.*)$`));
      const label = lm ? lm[1] : '';
      // "Type" is which channel the command acts on, not what it does — except in a
      // block whose heading absorbed the Type value: there the value column runs a row
      // off against the labels, and what sits beside "Type:" is the description.
      if (/^Type$/i.test(label) && !(typeAbsorbed && lm[2].trim())) continue;
      const labelled = /Syntax( [12])?$/.test(label);
      // Chroma spells a literal space in a syntax line "<space>", and puts it inside the
      // optional group rather than before it: "...FREQuency?[<space><MAX | MIN>]" is the
      // form written "...FREQuency? [<MAX | MIN>]" everywhere else in these catalogs.
      // Only the first column: a body row can carry a second column the way a heading
      // can — "CONFigure:AUTO:ON<space><CRD | NR1>       state to ON." floats the tail
      // of a description beside the syntax, and "CONFigure:ENTer:KEY?   [Unit = None]"
      // floats the return parameter. Read whole, the first swallowed prose into its
      // parameter clause and the second shipped bracket junk as arguments.
      const content = (lm ? lm[2] : t).split(/\s{3,}/)[0].trim()
        .replace(/<space>/gi, ' ').replace(/\[ +/g, ' [').trim();
      // A form may extend the heading rather than equal it: the entry headed
      // "[SOURce:]INPut" documents "[SOURce:]INPut[:STATe] <bool>", which is the
      // spelling worth keeping.
      const cRest = content.slice(content.split(/\s/)[0].length).trim();
      const n = norm(content);
      const belongs = n === want || (labelled && n.startsWith(want + ':'));
      if (isCmd(content) && belongs && (!cRest || argsOnly(cRest))) {
        syntaxes.push(content);
        // Each form opens its own wrap scope: the fence a Return or Example label set
        // for the previous form must not survive into this one, or "Query Syntax 2"
        // below an example row can never collect its own continuation.
        absorbClosed = false;
        continue;
      }
      // A syntax value too wide for its column wraps across the rows below, and in a
      // skewed block the halves sit beside unrelated labels. The last form knows it is
      // unfinished — it ends mid-list or mid-group — so rows of bare arguments are
      // pulled onto it until it closes: "PROGram:DATA:LIST <Arg1>,…<Arg4>," plus two
      // more rows of arguments is one sixteen-argument command, not a command and prose.
      //
      // Two fences keep it honest. A Return or Example label closes the form for good:
      // a syntax wrap never runs past its own label group, and without the fence the
      // return parameter "<aard>" and a row of example values pass the bare-arguments
      // test and are glued on. And the wrap may split *inside* a token — the guide
      // breaks "<space>" as "<s" / "pace>" — which per-line normalisation cannot mend,
      // so a lowercase tail closing a placeholder is glued raw and the whole form
      // normalised again.
      const open = s => /[,|]$/.test(s)
        || (s.match(/</g) || []).length !== (s.match(/>/g) || []).length
        || (s.match(/\[/g) || []).length !== (s.match(/\]/g) || []).length;
      if (/Example|Return/i.test(label)) absorbClosed = true;
      const last = syntaxes.length - 1;
      if (!absorbClosed && last > 0 && open(syntaxes[last]) && content && !isCmd(content)) {
        const tail = /<[a-z]*$/i.test(syntaxes[last]) && /^[a-z]+>/i.test(content);
        if (tail || argsOnly(content)) {
          syntaxes[last] = (syntaxes[last] + content)
            .replace(/<space>/gi, ' ').replace(/\[ +/g, ' [').trim();
          continue;
        }
      }
      if (!desc && !isCmd(content) && !/^\d+\s*\/\s*\d+\b/.test(content)
          && !/Series Programming (guide|manual)$/i.test(content) && !/\.{4}/.test(content)) {
        // The dotted-leader guard at the end: a contents line — "FREQuency:SETTings:
        // COUPling:ENABle......... 122" — reached one FPC entry as its description, and
        // no real sentence carries a run of four dots.
        // The page-furniture guard: "66 / 158 SDM Series Programming guide" is the
        // running footer, in both its spellings — the page count and the title survive
        // pdftotext separately, and a heading that reaches this point with only the
        // footer under it must stay descriptionless rather than be described by the
        // page it is on.
        desc = content;
        for (let m = k + 1; m < end && m < k + 4; m++) {
          // The wrap may sit beside the next label — strip it the same way.
          const u = lines[m].trim()
            .replace(new RegExp(`^(${LABELS})\\s*:\\s*`), '')
            .replace(new RegExp(`^(${LABELS})\\s{2,}`), '').trim();
          if (!u || isCmd(u)) break;
          desc += ' ' + u;
          if (/[.!?]$/.test(u)) break;
        }
      }
    }

    // Both vendors open every description the same way; drop the boilerplate.
    desc = desc.replace(/^Th(is|e) (command|query command|query) (is used )?to\s+/i, '')
               .replace(/^Th(is|e) (command|query command|query)\s+/i, '');
    // A parameter list picked up in place of prose — "0 | 1 | OFF | ON." — describes
    // nothing. Some entries have no prose of their own because the paragraph above
    // the heading covers the whole group, so look there before giving up.
    if (desc.includes('|') && !/[a-z]{4,}/.test(desc)) desc = '';
    if (!desc) {
      // Walk back to the paragraph above the heading and take its *first* line —
      // landing mid-paragraph yields fragments like "Equivalent. The CURRent,
      // RESistance and VOLTage commands program...".
      let k = start - 1;
      while (k >= 0 && !lines[k].trim()) k--;                 // skip the blank gap
      if (k >= 0 && !isCmd(lines[k].trim())) {
        let first = k;
        while (first > 0 && lines[first - 1].trim() && !isCmd(lines[first - 1].trim())) first--;
        const t = lines[first].trim();
        if (t.length >= 25 && !/^\d/.test(t)) desc = t;
      }
    }
    if (desc.length < 12) continue;
    desc = desc[0].toUpperCase() + desc.slice(1);

    // Drop the bare heading when a parameterised form of the *same* command is present,
    // keeping the set and query forms apart. Grouping on the header alone could not:
    // "DIGitizing:WAVeform:DATA?" and "DIGitizing:WAVeform:DATA? <V | I>" are one entry
    // and the guide documents the second, while the same command's setting form is a
    // different entry that has to survive alongside it. A group of *distinct* commands
    // sharing one description keeps all of them, because their headers differ.
    const byForm = new Map();
    for (const s of [...new Set(syntaxes)]) {
      const k = norm(s) + (s.includes('?') ? '?' : '');
      const cur = byForm.get(k);
      if (!cur || s.length > cur.length) byForm.set(k, s);
    }
    pushEntry([...byForm.values()], desc, null);
  }
}

// ---- Command-index style: "COMMAND ......... page" ---------------------
// Yields the complete command list for a guide whose body resists parsing.
function parseToc() {
  for (const line of lines) {
    let m = line.match(/^\s*(\S.*?)\s*[.\s]{4,}\s*[\dA-Za-z-]+\s*$/);
    // A sectioned page number — the FSL numbers its pages "6.6", "2.87" — but only behind a
    // real dotted leader. Accepting one after a run of spaces instead pulled
    // "OUTPut:TRACk:STATe        0.01" out of a Keysight parameter table, where 0.01 is the
    // default value and not a page at all. Without the leader there is nothing to tell an
    // index entry from a table row.
    if (!m) m = line.match(/^\s*(\S.*?)\s*\.{4,}[.\s]*\d+(?:\.\d+)+\s*$/);
    if (!m) continue;
    const t = m[1].trim();
    if (!isCmd(t) || t.length > 110) continue;
    if (/\b(the|and|for|with|this|chapter|section)\b/i.test(t)) continue;
    pushEntry([t], '', null);
  }
}

const src = lines.join('\n');
if (style === 'rigol' || (style === 'auto' && /^\s*Syntax\s/m.test(src))) parseRigol();
else if (style === 'siglent' || (style === 'auto' && /Command\s*Format/i.test(src))) parseSiglent();
else if (style === 'heading') parseHeading();
 else if (style === 'tek') parseTek();
 else if (style === 'keithley') parseKeithley();
 else if (style === 'keysight2') parseKeysight2();
 else if (style === 'block') parseBlock();
 else if (style === 'rs') parseRs();
 else if (style === 'sds') parseSds();
 else if (style === 'gw') parseGwInstek();
 else if (style === 'fluke') parseFluke();
 else if (style === 'plain') parsePlain();
else if (style === 'toc') parseToc();
else if (style === 'both') { parseHeading(); parseToc(); }
else parseKeysight();

// De-duplicate, keeping the entry that carries the most description.
const best = new Map();
for (const r of out) {
  const k = r.syntax.toUpperCase();
  const prev = best.get(k);
  if (!prev || (r.description || '').length > (prev.description || '').length) best.set(k, r);
}
// A command index legitimately yields entries with no prose — keep those.
const keepBare = style === 'toc' || style === 'both';
const result = [...best.values()].filter(r => keepBare || r.description || r.example);
console.log(JSON.stringify(result, null, 1));
console.error(`${file}: ${result.length} commands`);
