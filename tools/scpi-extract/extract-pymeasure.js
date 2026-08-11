// Mine pymeasure drivers for (SCPI command, human description) pairs.
// pymeasure declares properties as:
//   name = Instrument.control("QUERY?", "SET %g", """docstring...""", ...)
//   name = Instrument.measurement("QUERY?", """docstring""", ...)
//   name = Instrument.setting("SET %g", """docstring""", ...)
const fs = require('fs');
const path = require('path');

const root = process.argv[2];
const out = [];

function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p);
    else if (e.name.endsWith('.py') && !e.name.startsWith('__')) parse(p);
  }
}

// Pull the first sentence-ish line out of a triple-quoted docstring.
function firstLine(doc) {
  if (!doc) return '';
  const lines = doc.split('\n').map(s => s.trim()).filter(Boolean);
  let s = '';
  for (const l of lines) {
    if (/^(\.\.|:param|:type|====|---)/.test(l)) break;
    s += (s ? ' ' : '') + l;
    if (/[.!?]$/.test(l)) break;
  }
  return s.replace(/\s+/g, ' ').trim();
}

function parse(file) {
  const src = fs.readFileSync(file, 'utf8');
  const rel = path.relative(root, file).replace(/\\/g, '/');
  const parts = rel.split('/');
  const vendor = parts[parts.length - 2] || '';
  const model = path.basename(file, '.py');

  const re = /(\w+)\s*=\s*(?:Instrument|Channel)\.(control|measurement|setting)\s*\(([\s\S]*?)\n\s*\)/g;
  let m;
  while ((m = re.exec(src)) !== null) {
    const [, prop, kind, body] = m;
    // The command strings are the arguments *before* the triple-quoted docstring.
    const docStart = body.indexOf('"""');
    const head = docStart >= 0 ? body.slice(0, docStart) : body;
    const strs = [...head.matchAll(/(['"])((?:\\.|(?!\1)[^\\])*)\1/g)].map(x => x[2]);
    const docM = body.match(/"""([\s\S]*?)"""/);
    const doc = firstLine(docM ? docM[1] : '');

    let get = null, set = null;
    if (kind === 'control') { get = strs[0]; set = strs[1]; }
    else if (kind === 'measurement') { get = strs[0]; }
    else { set = strs[0]; }

    for (const [cmd, isQuery] of [[get, true], [set, false]]) {
      if (!cmd) continue;
      if (!/[A-Za-z:*]/.test(cmd)) continue;
      if (cmd.length > 90) continue;
      out.push({ vendor, model, prop, cmd, isQuery, doc });
    }
  }
}

walk(root);
// De-duplicate on vendor+model+command.
const seen = new Set();
const uniq = out.filter(r => {
  const k = r.vendor + '|' + r.model + '|' + r.cmd;
  if (seen.has(k)) return false;
  seen.add(k);
  return true;
});
fs.writeFileSync(process.argv[3], JSON.stringify(uniq, null, 1));
console.log('extracted', uniq.length, 'commands from', new Set(uniq.map(r => r.vendor + '/' + r.model)).size, 'drivers');
