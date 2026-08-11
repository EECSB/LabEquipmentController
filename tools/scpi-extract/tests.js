// Tests for parse-manual.js and build-catalog.js. No framework, no dependencies:
//
//   node tests.js
//
// Not part of the app build — the C# suite guards the *catalogs*; this guards the tool
// that writes them. Every case here is a behaviour that regressed, or nearly did, while
// the FSW and Chroma 63600 catalogs were being built, and each was originally caught by
// hand-diffing old parser output against new across forty manuals. That diff only works
// when someone thinks to run it; this runs in two seconds.
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const PARSE = path.join(__dirname, 'parse-manual.js');
const BUILD = path.join(__dirname, 'build-catalog.js');
const EMIT = path.join(__dirname, 'emit.js');
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'scpi-tests-'));
let serial = 0;
let failures = 0;

function check(name, cond, detail) {
  if (cond) console.log(`  ok    ${name}`);
  else { failures++; console.error(`  FAIL  ${name}${detail ? '\n        ' + detail : ''}`); }
}

function parse(text, style) {
  const file = path.join(tmp, `fixture-${serial++}.txt`);
  fs.writeFileSync(file, text);
  const out = execFileSync(process.execPath, [PARSE, file, style],
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  return JSON.parse(out);
}

const bySyntax = (entries, syntax) => entries.find(e => e.syntax === syntax);

// ---------------------------------------------------------------- plain: Chroma labels
//
// The 63600 closes every label with a colon ("Description:", one space after "Setting
// Syntax:") where the pattern expected a column of spaces — 134 entries came out described
// as "Type: Channel-Specific" and every query form was lost.
{
  const r = parse([
    'CONFigure:FOO',
    '',
    '      Type:            Channel-Specific',
    '',
    '      Description:     Set the foo of the widget.',
    '',
    '      Setting Syntax: CONFigure:FOO<space><NRf+>[suffix]',
    '',
    '      Query Syntax: CONFigure:FOO?[<space><MAX | MIN>]',
    '',
  ].join('\n'), 'plain');

  const set = bySyntax(r, 'CONFigure:FOO <NRf+>[suffix]');
  check('plain: colon-closed labels — set form with <space> normalised', !!set,
    JSON.stringify(r.map(e => e.syntax)));
  check('plain: description read from the Description label, not the Type one',
    set && set.description === 'Set the foo of the widget.',
    set && set.description);
  check('plain: query form with the bracket pulled outside the space',
    !!bySyntax(r, 'CONFigure:FOO? [<MAX | MIN>]'));
}

// -------------------------------------------------- plain: wrapped sixteen-argument line
//
// A syntax line too long for the column wraps, blank line between the halves. Read a half
// at a time, PROGram:DATA:LIST took four arguments where the guide documents sixteen.
{
  const r = parse([
    'PROGram:DATA:LIST',
    '',
    'Type:              Channel-Specific',
    '',
    'Description:       Set the list parameters in program.',
    '',
    'Setting Syntax: PROGram:DATA:LIST<space><Arg1>,<Arg2>,<Arg3>,<Arg4>,',
    '',
    '                   <Arg5>,<Arg6>,<Arg7>,<Arg8>',
    '',
  ].join('\n'), 'plain');

  const set = r.find(e => e.syntax.startsWith('PROGram:DATA:LIST '));
  check('plain: wrapped syntax line joined across the blank', !!set && set.syntax.includes('<Arg8>'),
    set && set.syntax);
  check('plain: column-0 labels are not read as headings',
    !r.some(e => /^(Type|Description|Setting Syntax)/.test(e.syntax)),
    JSON.stringify(r.map(e => e.syntax)));
}

// ---------------------------------------------------- plain: set and query stay distinct
//
// "DIGitizing:WAVeform:DATA?" and "DIGitizing:WAVeform:DATA? <V | I>" are one entry and
// the fuller spelling wins; the setting form beside them is a different entry and stays.
{
  const r = parse([
    'DIGitizing:WAVeform:DATA?',
    '',
    '      Type:         Channel-Specific',
    '',
    '      Description:  Returns waveform data from the load.',
    '',
    '      Query Syntax: DIGitizing:WAVeform:DATA?<space><V | I>',
    '',
  ].join('\n'), 'plain');

  check('plain: bare query heading superseded by its parameterised form',
    !!bySyntax(r, 'DIGitizing:WAVeform:DATA? <V | I>') && !bySyntax(r, 'DIGitizing:WAVeform:DATA?'),
    JSON.stringify(r.map(e => e.syntax)));
}

// ------------------------------------------------------------ rs: wrapped placeholder
//
// A heading whose parameter does not fit the column wraps it onto its own indented line;
// read as prose it became the description, was too short to keep, and the entry vanished.
// 47 FSW commands went that way.
{
  const r = parse([
    '',
    '     SENSe:LONG:PATH:INSert',
    '             <FilePath>',
    '',
    '     Loads a frequency response file to the current configuration.',
    '',
  ].join('\n'), 'rs');

  check('rs: wrapped placeholder joined back onto its heading',
    !!bySyntax(r, 'SENSe:LONG:PATH:INSert <FilePath>'),
    JSON.stringify(r.map(e => e.syntax)));
}

// ------------------------------------------------------------ rs: the query-form rule
//
// Newer R&S manuals mark exceptions in a Usage field; older ones say it in prose. Reading
// only the field invented a query for everything — *RST? included. 92 would have shipped.
{
  const r = parse([
    '',
    '     SENSe:FOO:BAR <Value>',
    '',
    '     Sets the bar. This command is an event and therefore has no *RST value and no query.',
    '',
    '     Example:                 SENS:FOO:BAR 1',
    '',
    '     SENSe:FOO:BAZ <Value>',
    '',
    '     Sets the baz level used by the measurement.',
    '',
    '     Parameters:',
    '     <Value>                  ON | OFF',
    '',
  ].join('\n'), 'rs');

  check('rs: no query form for a command whose prose says it has none',
    !bySyntax(r, 'SENSe:FOO:BAR?'), JSON.stringify(r.map(e => e.syntax)));
  check('rs: the query form still generated where nothing forbids it',
    !!bySyntax(r, 'SENSe:FOO:BAZ?'));
}

// ------------------------------------------------------------------- de-hyphenation
//
// Justified text hyphenates at the margin and pdftotext keeps the hyphen: "com- mand".
// Join only when the same document uses the joined word; keep a compound's hyphen; leave
// a suspended hyphen ("x- and y-axes") exactly as printed.
{
  const r = parse([
    '',
    '     SENSe:HYPH:ONE <Value>',
    '',
    '     This com- mand is only available in single sweep mode.',
    '',
    '     SENSe:HYPH:TWO <Value>',
    '',
    '     Starts the self- alignment routine for the command path.',
    '',
    '     SENSe:HYPH:THRee <Value>',
    '',
    '     Scales the x- and y-axes of the self-alignment display.',
    '',
  ].join('\n'), 'rs');

  const one = bySyntax(r, 'SENSe:HYPH:ONE <Value>');
  const two = bySyntax(r, 'SENSe:HYPH:TWO <Value>');
  const three = bySyntax(r, 'SENSe:HYPH:THRee <Value>');
  check('dehyphenate: margin break joined when the document knows the word',
    one && one.description.includes('command'), one && one.description);
  check('dehyphenate: compound keeps its hyphen and loses the space',
    two && two.description.includes('self-alignment'), two && two.description);
  check('dehyphenate: a suspended hyphen is left exactly as printed',
    three && three.description.includes('x- and y-axes'), three && three.description);
}

// ----------------------------------------------------------------------- toc pages
//
// The FSL numbers its pages "6.6" — a sectioned number counts, but only behind a real
// dotted leader. Behind spaces it is a table row: "OUTPut:TRACk:STATe    0.01" is a
// default value out of a Keysight parameter table, not an index entry.
{
  const r = parse([
    'CALCulate:FOO:BAR ........................................ 6.6',
    'OUTPut:TRACk:STATe                                 0.01',
    'SYSTem:BAZ ............................................ 123',
    '',
  ].join('\n'), 'toc');

  const got = r.map(e => e.syntax);
  check('toc: sectioned page number behind a dotted leader parses', got.includes('CALCulate:FOO:BAR'),
    JSON.stringify(got));
  check('toc: plain page number still parses', got.includes('SYSTem:BAZ'));
  check('toc: a table row behind spaces does not', !got.includes('OUTPut:TRACk:STATe'));
}

// --------------------------------------------------------------- build-catalog rules
//
// The filters and the index restriction, exercised through the real CLI: a page-pointer
// description, a Type label standing alone, a header the guide's own list does not name —
// all dropped — and the sixteen-argument command the old 120/100-character caps rejected,
// kept.
{
  const sixteen = 'PROGram:DATA:LIST ' + Array.from({ length: 16 }, (_, i) => `<Arg${i + 1}>`).join(',');
  const parsed = [
    { syntax: sixteen, description: 'Set the list parameters in program.', isQuery: false },
    { syntax: 'FOO:BAR <NR1>', description: 'On page 1212.', isQuery: false },
    { syntax: 'FOO:BAZ <NR1>', description: 'Type: Channel-Specific.', isQuery: false },
    { syntax: 'OFF:INDEX <NR1>', description: 'A real description, of a header the index does not list.', isQuery: false },
  ];
  const list = [{ syntax: 'PROGram:DATA:LIST' }, { syntax: 'FOO:BAR' }, { syntax: 'FOO:BAZ' }];

  const parsedFile = path.join(tmp, 'parsed.json');
  const listFile = path.join(tmp, 'list.json');
  const cfgFile = path.join(tmp, 'cfg.json');
  fs.writeFileSync(parsedFile, JSON.stringify(parsed));
  fs.writeFileSync(listFile, JSON.stringify(list));
  fs.writeFileSync(cfgFile, JSON.stringify({
    instrument: 'Fixture instrument',
    source: 'Fixture source.',
    groups: [{ file: parsedFile }],
    restrictTo: listFile,
  }));

  const built = JSON.parse(execFileSync(process.execPath, [BUILD, cfgFile],
    { encoding: 'utf8', cwd: tmp, stdio: ['ignore', 'pipe', 'ignore'] }));
  const got = built.commands.map(c => c.syntax);

  check('build: sixteen-argument command survives the length caps', got.includes(sixteen),
    JSON.stringify(got));
  check('build: a page-pointer description is not a description', !got.includes('FOO:BAR <NR1>'));
  check('build: a Type label standing alone is not one either', !got.includes('FOO:BAZ <NR1>'));
  check('build: a header the guide\'s own index does not list is dropped', !got.includes('OFF:INDEX <NR1>'));
}

// ------------------------------------------- plain: the skewed 63200A block, end to end
//
// pdftotext floats the value column against the labels in the 63200A, so the heading
// carries the Type value and every value sits a row off. The labels are singular there
// too ("Setting Parameter:"), the query wrap splits inside the "<space>" token, and the
// rows below the split are the return parameter and example values — which must NOT be
// glued to the syntax, however argument-shaped they look.
{
  const r = parse([
    'PROGram:DATA:STEP?         Channel-Specific',
    '      Type:               Returns the step parameters in program.',
    '      Description:',
    '      Query Syntax 2:     PROGram:DATA:STEP?<space><Arg1>,<Arg2><s',
    '                          pace><MAX | MIN>',
    '      Return Parameter:   <aard>',
    '      Return Example:     2,1,AUTO,CC,HIGH',
    '',
    'VOLTage:STATic:LFOO         Channel-Specific',
    '      Type:',
    '      Description:        Set the static load voltage in constant voltage mode.',
    '      Setting Syntax:',
    '      Setting Parameter:  VOLTage:STATic:LFOO<space><NRf+>[suffix]',
    '      Setting Example:',
    '                          VOLT:STAT:LFOO 8       Set voltage of load as 8V.',
    '',
  ].join('\n'), 'plain');

  const step = r.find(e => e.syntax.startsWith('PROGram:DATA:STEP?'));
  check('plain: mid-token <space> split glued and normalised',
    step && step.syntax === 'PROGram:DATA:STEP? <Arg1>,<Arg2> <MAX | MIN>',
    step && step.syntax);
  check('plain: return parameter and example rows never join the syntax',
    step && !/aard|AUTO/.test(step.syntax));
  check('plain: skewed heading — Type line read as the description',
    step && step.description === 'Returns the step parameters in program.',
    step && step.description);

  const lfoo = bySyntax(r, 'VOLTage:STATic:LFOO <NRf+>[suffix]');
  check('plain: singular "Setting Parameter:" label carries the syntax', !!lfoo,
    JSON.stringify(r.map(e => e.syntax)));
}

// -------------------------------------------- plain: the split-colon heading, both ways
//
// "[ADVance:]OCP: LATCh" is a misprinted command heading — the stray space took the
// whole entry down. "Configuration: Capacitance" is a Keysight chapter title of exactly
// the same shape, and the first version of the mend welded it into a command. The head's
// depth and casing are what tell them apart.
{
  const r = parse([
    '[ADVance:]OCP: LATCh',
    '',
    'Type:         Channel-Specific',
    '',
    'Description:  Set load latch function for OCP test mode.',
    '',
    'Setting Syntax: [ADVance:]OCP:LATCh<space><CRD | NR1>',
    '',
    'Query Syntax: [ADVance:]OCP:LATCh?',
    '',
    'Configuration: Capacitance',
    '',
    'To configure capacitance measurements, remove all connections first.',
    '',
  ].join('\n'), 'plain');

  check('plain: misprinted split-colon heading mended into its entry',
    !!bySyntax(r, '[ADVance:]OCP:LATCh <CRD | NR1>') && !!bySyntax(r, '[ADVance:]OCP:LATCh?'),
    JSON.stringify(r.map(e => e.syntax)));
  check('plain: a chapter title of the same shape is not a command',
    !r.some(e => /Configuration/.test(e.syntax)));
}

// ----------------------------------------------- plain: page furniture never describes
{
  const r = parse([
    '[SENSe:]CURRent[:AC]:BANDwidth',
    '',
    '66 / 158 SDM Series Programming guide',
    '',
  ].join('\n'), 'plain');

  check('plain: a running footer is not a description',
    !r.some(e => /66 \/ 158|Programming guide/.test(e.description || '')),
    JSON.stringify(r));
}

// -------------------------------------------------- rs: FSU-era bare parameter clauses
//
// The FSU-generation manuals print the parameter clause in plain words on the heading
// line — an alternation, or a range whose bounds carry units or the symbolic fmax. The
// long-form example lines of newer manuals look almost the same, and the word "to" is
// most of what separates them.
{
  const r = parse([
    '',
    'SENSe:FREQ:FOO:STARt 0 to fmax',
    '',
    '    This command defines the start frequency of the widget sweep.',
    '',
    'SENSe:FREQ:FOO:RESolution 10Hz to 10MHz',
    '',
    '    This command defines the resolution bandwidth of the widget.',
    '',
    'SENSe:FREQ:FOO:MODE HCONtinuous ON | OFF',
    '',
    '    This command switches the continuous mode of the widget on and off.',
    '',
    'SENS:FREQ:FOO:COUNt 7',
    '',
    '    An example line in the abbreviated spelling, never a heading.',
    '',
  ].join('\n'), 'rs');

  const got = r.map(e => e.syntax);
  check('rs: symbolic range bound (0 to fmax) accepted as a heading',
    got.includes('SENSe:FREQ:FOO:STARt 0 to fmax'), JSON.stringify(got));
  check('rs: unit-bearing range (10Hz to 10MHz) accepted',
    got.includes('SENSe:FREQ:FOO:RESolution 10Hz to 10MHz'));
  check('rs: an abbreviated example with a bare number still is not one',
    !got.some(s => s.startsWith('SENS:FREQ:FOO:COUNt')));
}

// ------------------------------------------------------- rs: the option lead-in, only
//
// The FSP introduces option commands with "Command for option …:" directly above the
// heading, no blank between. The RTB2000 and FPC carry a boilerplate annex introduced
// "commands for this section:" over standardized SCPI that is not theirs — the word
// "option" is what separates the two.
{
  const r = parse([
    '',
    'Command for option FS-K82 cdma2000 BTS:',
    'CALCulate:LIMit:ESPectrum:VALue <numeric_value>',
    '',
    '    This command switches to manual limit line selection for the widget.',
    '',
    'commands for this section:',
    'SENSe:FREQuency:STOPgap <numeric_value>',
    '',
    '    A standardized command out of the conformance annex, not this instrument.',
    '',
  ].join('\n'), 'rs');

  const got = r.map(e => e.syntax);
  check('rs: a heading after a "Command for option …:" lead-in is admitted',
    got.includes('CALCulate:LIMit:ESPectrum:VALue <numeric_value>'), JSON.stringify(got));
  check('rs: the conformance annex\'s lead-in is not one',
    !got.some(s => s.startsWith('SENSe:FREQuency:STOPgap')));
}

// ------------------------------------------------------ rs: the FSP's spaced suffixes
//
// "DELTamarker<1 to 4>" — the space splits the header token in every splitter, so the
// heading dies at parse or at build. Carried as "<1...4>", the spelling the other R&S
// catalogs use.
{
  const r = parse([
    '',
    'CALCulate<1|2>:DELTamarker<1 to 4>:MODE ABSolute | RELative',
    '',
    '    This command switches between relative and absolute delta marker measurement.',
    '',
  ].join('\n'), 'rs');

  check('rs: a spaced suffix range is normalised and the heading survives',
    r.some(e => e.syntax === 'CALCulate<1|2>:DELTamarker<1...4>:MODE ABSolute | RELative'),
    JSON.stringify(r.map(e => e.syntax)));
}

// ------------------------------------------------- emit: manufacturer is its own field
//
// A catalog's manufacturer usually equals its guide's vendor, but nothing forces it to —
// an instrument sold under one name can ship with a manual published under another. emit
// used to overwrite the config's manufacturer with the vendor, which is invisible for as
// long as the two happen to match everywhere.
{
  const parsedFile = path.join(tmp, 'emit-parsed.json');
  const cfgFile = path.join(tmp, 'emit-cfg.json');
  const builtFile = path.join(tmp, 'emit-built.json');
  const outFile = path.join(tmp, 'emit-out.json');
  fs.writeFileSync(parsedFile, JSON.stringify([
    { syntax: 'FOO:BAR <NR1>', description: 'Sets the bar of the widget.', isQuery: false },
  ]));
  fs.writeFileSync(cfgFile, JSON.stringify({
    instrument: 'Fixture instrument',
    source: 'Fixture source.',
    manufacturer: 'Badge Brand',
    guide: { title: 'Fixture Guide', vendor: 'Manual Publisher', fileName: 'fixture.pdf' },
    groups: [{ file: parsedFile }],
  }));
  fs.writeFileSync(builtFile, execFileSync(process.execPath, [BUILD, cfgFile],
    { encoding: 'utf8', cwd: tmp, stdio: ['ignore', 'pipe', 'ignore'] }));
  execFileSync(process.execPath, [EMIT, builtFile, outFile],
    { encoding: 'utf8', cwd: tmp, stdio: ['ignore', 'pipe', 'ignore'] });
  const emitted = JSON.parse(fs.readFileSync(outFile, 'utf8'));

  check('emit: the config\'s manufacturer survives over the guide\'s vendor',
    emitted.manufacturer === 'Badge Brand', emitted.manufacturer);
  check('emit: the guide block still carries the vendor',
    emitted.guide && emitted.guide.vendor === 'Manual Publisher');
}

// -------------------------------------------- rs: the indented heading's wrapped clause
//
// The FPC breaks "CALCulate<n>:LIMit<k>:DEFine …, <Y-" at the margin, mid-token, and the
// tail row read as the entry's description; the truncated head shipped once. The join is
// gated on the continuation reading as arguments: the FSV's "MMEMory:STORe<n>:PEAK
// <FileName" is open the same way, but below it sits the entry's prose, and the first
// draft of the join ate it sentence by sentence — the placeholder never closes — taking
// the neighbouring entry's description along.
{
  const r = parse([
    '',
    '  CALCulate<n>:LIMit<k>:DEFine <Name>, <Description>, <X-unit>, <X-scale>, <Y-',
    '  unit>, <X0>, <Y0>, <X1>, <Y1>[, <Xn>, <Yn>]',
    '',
    '  This command defines the shape of a limit line.',
    '',
    '  MMEMory:STORe<n>:PEAK <FileName',
    '',
    '  This command stores the current marker peak list in a dat file.',
    '',
  ].join('\n'), 'rs');

  const whole = 'CALCulate<n>:LIMit<k>:DEFine <Name>, <Description>, <X-unit>, '
    + '<X-scale>, <Y-unit>, <X0>, <Y0>, <X1>, <Y1>[, <Xn>, <Yn>]';
  const set = bySyntax(r, whole);
  check('rs: an indented heading\'s wrapped clause is joined, mid-token break glued',
    !!set, JSON.stringify(r.map(e => e.syntax)));
  check('rs: the joined entry reads the prose below the clause, not the clause',
    set && set.description === 'This command defines the shape of a limit line.',
    set && set.description);
  const peak = bySyntax(r, 'MMEMory:STORe<n>:PEAK <FileName');
  check('rs: an open head above prose keeps its description rather than absorbing it',
    peak && peak.description === 'This command stores the current marker peak list in a dat file.',
    JSON.stringify(r.map(e => [e.syntax, e.description])));
}

fs.rmSync(tmp, { recursive: true, force: true });
if (failures) { console.error(`\n${failures} failure(s)`); process.exitCode = 1; }
else console.log('\nall green');
