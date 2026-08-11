// Turn parsed-manual entries into the app's CommandData JSON, rejecting
// anything that does not survive strict validation.
//
// The project rule (SPEC §10) is "never invent SCPI": a mangled two-column
// PDF extraction is an invention as surely as a guess is, so entries that
// don't parse cleanly are dropped rather than cleaned up by hand-waving.
const fs = require('fs');

// A well-formed SCPI header: optional leading ":" or "[:ROOT]", then
// mnemonics of the SHORTlong form, optionally suffixed <n> or [<n>].
const MNEMONIC = /^[A-Za-z][A-Za-z0-9_]*$/;

function validate(syntax) {
  let s = syntax.trim();
  // The longest legitimate ones are Chroma's program commands, which take sixteen
  // arguments: "PROGram:DATA:LIST <Arg1>,<Arg2>, … ,<Arg16>" is 136 characters. The limit
  // was 130, set when Keysight's fully-spelled supply commands at 101 were the longest
  // seen, and it silently rejected four entries the catalogs already carried.
  if (!s || s.length > 160) return null;

  // Split the header from its parameter clause at the first space.
  const sp = s.indexOf(' ');
  let header = sp > 0 ? s.slice(0, sp) : s;
  const params = sp > 0 ? s.slice(sp + 1).trim() : '';

  // IEEE 488.2 common commands.
  if (/^\*[A-Z]{3,4}\??$/.test(header)) return { header, params };

  // Brackets must balance. An unbalanced header is a truncated extraction —
  // "[:SOURce[<n>]]:VOLTage[:LEVel]:TRIGgered[:AMPLitude?" lost its "]" to the
  // PDF's column layout, and shipping it would put broken syntax in the UI.
  if ((s.match(/\[/g) || []).length !== (s.match(/\]/g) || []).length) return null;
  if ((s.match(/\{/g) || []).length !== (s.match(/\}/g) || []).length) return null;

  // Reject a line that ran two commands together (a two-column PDF artefact).
  //
  // An optional node is always written "[:LEVel]", "[SOURce:]" at the very front,
  // "[<n>]", or — Keithley's spelling for an optional channel — "[1]". So a "["
  // anywhere else, directly followed by a letter, means a second command was
  // concatenated onto the first: "...:FUNCtion[SOURce:]DIGital:...". Counting "[:"
  // instead would wrongly reject ":MEASure[:VOLTage][:DC]?", which legitimately
  // carries several optional nodes.
  for (let i = 1; i < header.length; i++) {
    if (header[i] !== '[') continue;
    const next = header[i + 1];
    if (next !== ':' && next !== '<' && !(next >= '0' && next <= '9')) return null;
  }
  // And a "]" closes a node, so what follows it is a colon, a "?", another bracket,
  // or the end — never a letter. "OUTPut[:STATe]COUPle:CHANNel" is two commands the
  // column layout ran together.
  //
  // The one exception is a leading optional root, "[SOURce:]VOLTage", where the
  // bracket swallows its own colon and a letter legitimately follows. Find where
  // that opening bracket closes and exempt exactly that position.
  let leadingClose = -1;
  if (header.startsWith('[')) {
    let depth = 0;
    for (let i = 0; i < header.length; i++) {
      if (header[i] === '[') depth++;
      else if (header[i] === ']' && --depth === 0) { leadingClose = i; break; }
    }
  }
  for (let i = 0; i < header.length - 1; i++) {
    if (header[i] !== ']' || i === leadingClose) continue;
    const next = header[i + 1];
    if (next !== ':' && next !== '?' && next !== '[' && next !== ']') return null;
  }
  if (/\?\S/.test(header)) return null;              // "...DATA?[SOURce:]..."

  // Reject anything with prose, or a second command, in the parameter clause.
  if (/\b(the|and|for|with|this|that|when|from|are|will|returns?|sets?)\b/i.test(params)) return null;
  if (params.includes('?')) return null;
  // Long but legitimate: the DP800's ":APPLy" parameter clause nests two optional value
  // groups and runs to 71 characters, and Chroma's program commands take sixteen arguments
  // — "<Arg1>,<Arg2>, … ,<Arg16>" is 118 — so the cap has to clear both.
  if (params.length > 125) return null;

  // Normalise, then check every mnemonic. Optional nodes are written with
  // brackets in several places — a leading root "[SOURce:]VOLTage", a middle
  // node ":MEASure:ALL[:DC]?", a trailing one ":VOLTage[:LEVel]" — so the
  // brackets come out wholesale before the header is split.
  const q = header.endsWith('?');
  let h = q ? header.slice(0, -1) : header;
  h = h.replace(/<[^>]*>/g, 'N').replace(/[\[\]]/g, '').replace(/^:/, '');
  // Tektronix search/trigger commands are the deepest seen, at 10 nodes:
  // SEARCH:SEARCH<x>:TRIGger:A:BUS:B<x>:ETHERnet:IPHeader:DESTinationaddr:VALue
  const parts = h.split(':').filter(Boolean);
  if (!parts.length || parts.length > 12) return null;
  for (const p of parts) {
    // A node may alternate two spellings — "BANDwidth|BWIDth", "PSEarch|PEAKsearch" —
    // which is how the FSU/FSL-era manuals head whole subsystems and how the catalogs
    // built from them spell it. Every alternate has to be a mnemonic on its own.
    if (!p.split('|').every(a => MNEMONIC.test(a))) return null;
  }
  return { header, params };
}

const args = process.argv.slice(2);
const cfg = JSON.parse(fs.readFileSync(args[0], 'utf8'));

// Cross-check corpora: every SCPI string mined from driver source, upper-cased
// and stripped to its header, so a manual entry can be marked "corroborated".
const corpus = new Set();
for (const f of ['pymeasure-commands.json', 'generic-commands.json']) {
  if (!fs.existsSync(f)) continue;
  for (const r of JSON.parse(fs.readFileSync(f, 'utf8'))) {
    const c = (r.cmd || '').split(/[\s,]/)[0].replace(/\?$/, '').toUpperCase();
    if (c) corpus.add(c.replace(/^\[?:?/, ''));
  }
}

// Do an example and its command share a root mnemonic? Guides put prerequisites and
// related commands in the same column as the example, and "APPLication:TYPe LIMITMask"
// under MASK:USER:AMPLitude is not an example of that command.
function sameRoot(example, syntax) {
  const root = s => (s.split(/[\s(]/)[0] || '')
    .replace(/<[^>]*>/g, '')
    .replace(/[\[\]]/g, '')
    .replace(/^[:*]/, '')
    .split(':')[0]
    .replace(/\d+$/, '')
    .toUpperCase();
  const a = root(example), b = root(syntax);
  if (!a || !b) return false;
  // Either may be the SCPI short form of the other ("MEASU" vs "MEASUrement").
  return a.startsWith(b) || b.startsWith(a);
}

// Reduce a header to the abbreviations a driver would plausibly have used:
// both the full long form and the 3-4 letter short form of each mnemonic.
function variants(header) {
  const h = header.replace(/\?$/, '').replace(/^\[?:?/, '').replace(/[\[\]]/g, '');
  const parts = h.split(':').filter(Boolean);
  const shortF = parts.map(p => (p.match(/^[A-Z]+/) || [p])[0]).join(':');
  return [h.toUpperCase(), shortF.toUpperCase()];
}

// Category is derived from the root mnemonic, so a catalog needs no per-command
// hand-labelling. Anything unmapped falls back to a title-cased root.
const CATEGORY = {
  '*': 'IEEE 488.2 Common',
  ANAL: 'Analyzer', APPL: 'Apply', CALC: 'Calculate', CALI: 'Calibration',
  CHAN: 'Channel', CONF: 'Configure', CURR: 'Source — Current', DATA: 'Data',
  DELAY: 'Delay', DISP: 'Display', FETC: 'Acquisition', FORM: 'Format',
  FREQ: 'Frequency', FUNC: 'Function', HCOP: 'Hardcopy', INIT: 'Acquisition',
  INP: 'Input', INST: 'Instrument', LIST: 'List', MEAS: 'Measure',
  MEM: 'Memory', MMEM: 'File', MON: 'Monitor', OUTP: 'Output',
  POW: 'Source — Power', PRES: 'Preset', READ: 'Acquisition', REC: 'Recorder',
  RES: 'Source — Resistance', ROUT: 'Route', SAMP: 'Acquisition',
  SENS: 'Sense', SOUR: 'Source', STAT: 'Status', SWE: 'Sweep',
  SYST: 'System', TIME: 'Timer', TRAC: 'Trace', TRIG: 'Trigger',
  UNIT: 'Unit', VOLT: 'Source — Voltage', WAV: 'Waveform', ACQ: 'Acquire',
  MARK: 'Marker', BAND: 'Bandwidth', AVER: 'Average', BWID: 'Bandwidth',
  TST: 'Test', ABOR: 'Acquisition', ARM: 'Trigger', LXI: 'System',
  BATT: 'Battery', LED: 'Display', EXT: 'External', SHOR: 'Input',
  TRAN: 'Transient', PROG: 'Program', VOLTage: 'Source — Voltage',
  // Roots the guides use that don't map onto a standard subsystem name.
  LAN: 'System', DHCP: 'System', IP: 'System', MASK: 'System', GATE: 'System',
  STOP: 'Acquisition', RUN: 'Acquisition', SING: 'Acquisition', AUT: 'Acquisition',
  COUN: 'Counter', COUP: 'Coupling', LIC: 'System', PA: 'Output',
  ROSC: 'Reference clock', TIM: 'Timebase', HOR: 'Horizontal', VERT: 'Vertical',
  BURS: 'Burst', MOD: 'Modulation', SWEEP: 'Sweep', HARM: 'Harmonics',
  TRIGger: 'Trigger', DIG: 'Digital', BUS: 'Bus', REF: 'Reference',
  CURS: 'Cursor', MATH: 'Math', DEC: 'Decode', ETAB: 'Decode', LA: 'Logic',
  QUIC: 'Quick action', SAVE: 'File', LOAD: 'File', SELF: 'Test',
};

function categorise(syntax) {
  const s = syntax.trim();
  if (s.startsWith('*')) return CATEGORY['*'];
  // Drop the channel suffix before deriving a name — a category headed
  // "Refcurve<m>" reads as a bug in the UI, not as a subsystem.
  const h = s.replace(/^\[?:?/, '').replace(/[\[\]]/g, '').replace(/<[^>]*>/g, '');
  const root = (h.split(/[:\s?]/)[0] || '').trim();
  const key = (root.match(/^[A-Z]+/) || [root])[0];
  if (CATEGORY[key]) return CATEGORY[key];
  if (CATEGORY[root.toUpperCase()]) return CATEGORY[root.toUpperCase()];
  return root ? root[0].toUpperCase() + root.slice(1).toLowerCase() : 'Other';
}

// "restrictTo" — a parsed command index from the same guide, used as the authority on what
// the guide actually documents. Anything the extraction produced that the index does not
// list is not a command; it is something that looked like one.
//
// The FSW manual needs this. Its programming examples are written in the abbreviated
// spelling, sit at the same indent as a heading and are followed by a comment line, so they
// read as entries — "INIT:CONT OFF" described as "Switches the sweep mode to single sweep."
// is a real example of a real command, but it is not the command's documented form and
// nothing should offer it as one. Checking against the guide's own list drops all 46 of
// them without a hand-written blocklist that would go stale the next time the guide moves.
const indexHead = s => s.split(/\s/)[0].replace(/\?$/, '').toUpperCase();
const allowed = cfg.restrictTo
  ? new Set(JSON.parse(fs.readFileSync(cfg.restrictTo, 'utf8')).map(e => indexHead(e.syntax)))
  : null;

const commands = [];
let dropped = 0;
let offIndex = 0;
for (const group of cfg.groups) {
  const src = JSON.parse(fs.readFileSync(group.file, 'utf8'));
  for (const e of src) {
    if (group.include && !new RegExp(group.include, 'i').test(e.syntax)) continue;
    if (group.exclude && new RegExp(group.exclude, 'i').test(e.syntax)) continue;
    // Typographical errors in the guide itself. The Chroma manual prints
    // "VOLTaget:PROTection" and "PROTecton:TRIPped?" a handful of times while
    // spelling both correctly everywhere else — transcribing the slip faithfully
    // would put commands in the catalog that the instrument rejects. Corrections are
    // listed per-config so each one is a deliberate, reviewable decision.
    let syntax = e.syntax;
    for (const [from, to] of Object.entries(cfg.typos || {})) syntax = syntax.split(from).join(to);

    // Where a config lists the IEEE 488.2 commands its guide documents, those curated
    // descriptions win over whatever the extraction made of them. The common-command
    // pages are the ones vendors typeset most loosely — the B&K guide gave both *SAV
    // and *SRE the description "Save command Saves the current setup to..." — and
    // these commands mean the same thing on every instrument, so there is nothing to
    // gain from the vendor's wording.
    if (syntax.startsWith('*') && (cfg.commonCommands || []).length) continue;

    const v = validate(syntax);
    if (!v) { dropped++; continue; }
    // Compare the set and query forms as one header — the index lists a command once,
    // under whichever form the guide prints, and the query is derived rather than listed.
    if (allowed && !allowed.has(indexHead(v.header))) { offIndex++; continue; }
    let desc = (e.description || '').replace(/\s+/g, ' ').trim();

    // A description that opens with a SCPI path and a colon belongs to a *different*
    // command: the guide's cross-reference pages list command tails under a parent
    // header, and the extraction pairs the tail with the next entry's prose. The
    // result — "HAR<1-400>:FREQuency?" described as "POWer:HARMonics:RESults: The IEC
    // standard specifies..." — is wrong in both halves, so drop it rather than ship it.
    if (/^[A-Za-z][A-Za-z0-9]*(:[A-Za-z0-9<>\[\]|-]+)+:\s/.test(desc)) { dropped++; continue; }
    if (/Programmer Manual|Series Oscilloscopes/i.test(desc)) { dropped++; continue; }
    // R&S prints an example response or a bare parameter name where the description
    // would go when the entry has none — "<-- 0,\"No error\"", "<PositiveTransition>".
    // Neither describes anything, so drop the entry rather than ship the noise.
    if (/^(<--|-->|<)/.test(desc)) { dropped++; continue; }
    // A table header caught instead of prose: "Commands Description Default OFF".
    if (/^Commands?\s+Description\b/i.test(desc)) { dropped++; continue; }
    // The Chroma guides head each entry "Type: Channel-Specific" and put the prose under a
    // "Description:" label below it, which the plain parser normally picks up. Where the
    // description column is empty the label is all it finds, and "VOLTage:STATic:RESponse"
    // arrives described as "Type: Channel-Specific." — which says nothing about the command.
    //
    // Reject only that: the label standing alone. An earlier version of this rejected any
    // description opening with "Type:", which threw away the 305 entries whose prose the
    // parser had read correctly and left a 9-entry catalog. The shape is not the fault; the
    // absence of anything after it is.
    if (/^Type:\s*(Channel-Specific|Global)\s*\.?$/i.test(desc)) { dropped++; continue; }
    if (/^(Figure|Table)\s+\d/i.test(desc)) { dropped++; continue; }
    // The tail of a cross-reference, with the sentence it belonged to on the previous page:
    // "On page 1212." A page number is not a description. A reference *ending* a real
    // description is fine and stays — this only rejects one standing alone.
    if (/^(On|At|See)\s+page\s+[\d.]+\.?$/i.test(desc)) { dropped++; continue; }
    if (desc.length > 240) desc = desc.slice(0, 237).replace(/\s\S*$/, '') + '…';
    if (!desc) { dropped++; continue; }
    // The guides document a set/query pair under one description written for the
    // set form, so the query inherits "Set the ...". Re-voice just that opening —
    // this rewords the prose only, never the command syntax. Anything less
    // clear-cut ("Enable or disable ...") is left exactly as the guide has it,
    // because a mechanical rewrite of those reads worse than the original.
    if (e.syntax.includes('?') && /^Sets?\s+(the|a|an)\b/i.test(desc)) {
      desc = desc.replace(/^Sets?\b/i, 'Query');
    }
    if (!/[.!?]$/.test(desc)) desc += '.';
    desc = desc[0].toUpperCase() + desc.slice(1);
    // An example is only kept if it actually looks like a command. The guides
    // wrap explanatory prose into the same column, and "please refer to Table
    // 1-6" is not something anyone should be able to click Insert on.
    let example = (e.example || '').trim();
    if (example) {
      example = example.replace(/\s*\/\*.*$/, '').trim();   // strip Rigol's trailing comment
      const head = example.split(/\s/)[0];
      if (!validate(example) || /\b(the|please|refer|see|table|for|value)\b/i.test(head)) example = '';
      // A trailing ":" means the example was cut off by the column width.
      if (/:$/.test(example)) example = '';
      // An example must exercise the command it sits under, not a neighbour's.
      if (example && !sameRoot(example, syntax)) example = '';
    }

    // A category named by the guide itself beats one guessed from the root
    // mnemonic — the Tektronix manuals label every command with its Group.
    const named = group.category || e.category;
    const corroborated = variants(v.header).some(x => corpus.has(x));
    commands.push({
      category: named || categorise(syntax),
      categoryGuessed: !named,
      syntax: syntax.trim(),
      description: desc,
      example: example || undefined,
      isQuery: syntax.includes('?') || undefined,
      corroborated,
    });
  }
}

// Where the guide named the category for some commands under a root but not others
// — pdftotext drops the odd "Group" line — let the named ones speak for their
// siblings. Without this a handful of Tektronix commands land in categories like
// "Har<1-400>" or "Ch2", derived from their own mnemonic and useful to nobody.
{
  const rootOf = syntax => syntax
    .split(/[\s?]/)[0]
    .replace(/<[^>]*>/g, '')
    .replace(/[\[\]:]/g, ':')
    .replace(/^:+/, '')
    .split(':')[0]
    .toUpperCase();

  const votes = new Map();
  for (const c of commands) {
    if (c.categoryGuessed) continue;
    const root = rootOf(c.syntax);
    if (!votes.has(root)) votes.set(root, new Map());
    const m = votes.get(root);
    m.set(c.category, (m.get(c.category) || 0) + 1);
  }
  for (const c of commands) {
    if (!c.categoryGuessed) continue;
    const m = votes.get(rootOf(c.syntax));
    if (!m) continue;
    c.category = [...m.entries()].sort((a, b) => b[1] - a[1])[0][0];
    c.categoryGuessed = false;
  }
}

// The IEEE 488.2 common commands. Every guide processed here documents these, but
// several print them in a column layout pdftotext renders inside-out — the DG1000Z
// puts the description on the line labelled "Syntax" — so they are listed once
// rather than recovered per-guide. A config opts in with "commonCommands", and
// only the mnemonics its own manual documents are emitted (checked by the caller).
const COMMON = {
  '*CLS':  'Clear all the event registers and the error queue.',
  '*ESE':  'Set the enable register of the standard event register.',
  '*ESE?': 'Query the enable register of the standard event register.',
  '*ESR?': 'Query and clear the event register of the standard event register.',
  '*IDN?': 'Identify the instrument: manufacturer, model, serial number, firmware version.',
  '*OPC':  'Set the operation-complete bit once all pending operations finish.',
  '*OPC?': 'Return 1 once all pending operations complete.',
  '*OPT?': 'Query the options installed in the instrument.',
  '*PSC':  'Enable or disable clearing the enable registers at power-on.',
  '*PSC?': 'Query whether the enable registers are cleared at power-on.',
  '*RCL':  'Recall the instrument state saved in the specified memory location.',
  '*RST':  'Reset the instrument to its factory default state.',
  '*SAV':  'Save the current instrument state to the specified memory location.',
  '*SRE':  'Set the enable register of the status byte register.',
  '*SRE?': 'Query the enable register of the status byte register.',
  '*STB?': 'Query the event register of the status byte register.',
  '*TRG':  'Trigger the instrument over the bus.',
  '*TST?': 'Run the instrument self-test and return the result.',
  '*WAI':  'Wait for all pending operations to complete before executing further commands.',
  // Keithley-specific, and the most consequential command on the instrument: a 2450
  // or DMM6500 set to TSP answers none of the SCPI below until it is switched over,
  // and the change only takes effect after a reboot.
  '*LANG':  'Set the command language, SCPI or TSP. Takes effect after a power cycle.',
  '*LANG?': 'Query the command language the instrument is set to.',
};

if (cfg.commonCommands) {
  const documented = new Set(cfg.commonCommands);
  for (const [syntax, description] of Object.entries(COMMON)) {
    if (!documented.has(syntax)) continue;
    commands.push({
      category: 'IEEE 488.2 Common',
      syntax,
      description,
      isQuery: syntax.endsWith('?') || undefined,
      corroborated: corpus.has(syntax.replace(/^\*/, '*').replace(/\?$/, '')) ||
                    corpus.has(syntax.replace(/\?$/, '')),
    });
  }
}

// Hand-transcribed supplements. A few entries sit in a table whose columns
// pdftotext interleaves beyond recovery — the DP800's ":APPLy" is printed with
// its three syntax forms and three descriptions in separate column blocks. Those
// are read off the guide by eye and listed in the config, still transcribed from
// the vendor manual and still validated by the same rules as everything else.
for (const e of cfg.supplements || []) {
  if (!validate(e.syntax)) throw new Error('supplement fails validation: ' + e.syntax);
  commands.push({
    category: e.category || categorise(e.syntax),
    syntax: e.syntax,
    description: e.description,
    example: e.example || undefined,
    isQuery: e.syntax.includes('?') || undefined,
    corroborated: variants(e.syntax.split(' ')[0]).some(x => corpus.has(x)),
  });
}

// An existing hand-written catalog wins over anything extracted here: its wording
// was written by a person and some of its entries are bench-verified. Extraction
// only ever *adds* commands the hand-written file doesn't already document.
let existing = [];
if (cfg.mergeInto && fs.existsSync(cfg.mergeInto)) {
  existing = JSON.parse(fs.readFileSync(cfg.mergeInto, 'utf8')).commands;
}

// Match on the normalised header, so ":MEASure:VPP? [<src>]" in the hand-written
// file is recognised as the same command as the guide's ":MEASure:VPP? <chan>".
function key(syntax) {
  const q = syntax.includes('?') ? '?' : '';
  let h = syntax.split(' ')[0].replace(/\?$/, '');
  h = h.replace(/<[^>]*>/g, 'N').replace(/[\[\]]/g, '').replace(/^:/, '');
  return h.toUpperCase() + q;
}

const seen = new Set(existing.map(c => key(c.syntax)));
const added = commands.filter(c => {
  const k = key(c.syntax);
  if (seen.has(k)) return false;
  seen.add(k);
  return true;
});
const final = existing.concat(added);
if (cfg.mergeInto) {
  console.error(`  merged: ${existing.length} existing kept, ${added.length} added from the guide`);
}

// Say what the index turned away rather than only what survived: a silent restriction
// reads as "the guide documents exactly this much" when it does not.
if (allowed) console.error(`  index: ${offIndex} extracted headers are not in the guide's own command list`);
console.error(`${cfg.instrument}: ${final.length} kept, ${dropped} rejected, ` +
  `${final.filter(c => c.corroborated).length} corroborated by driver source`);
console.log(JSON.stringify(
  { instrument: cfg.instrument, source: cfg.source, manufacturer: cfg.manufacturer,
    guide: cfg.guide, commands: final }, null, 1));
