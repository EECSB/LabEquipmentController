// Write a built catalog out in the exact shape Core/CommandData/*.json uses:
// one JSON object per line inside "commands", grouped by category, so the file
// stays reviewable as source rather than as a machine blob.
const fs = require('fs');

const src = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const dest = process.argv[3];

const order = [];
const byCat = new Map();
for (const c of src.commands) {
  if (!byCat.has(c.category)) { byCat.set(c.category, []); order.push(c.category); }
  byCat.get(c.category).push(c);
}
// IEEE 488.2 commands lead, matching the hand-written catalogs already shipped.
order.sort((a, b) => (a.startsWith('IEEE') ? -1 : b.startsWith('IEEE') ? 1 : 0));

const esc = s => JSON.stringify(s);
const lines = [];
lines.push('{');
lines.push(`  "instrument": ${esc(src.instrument)},`);
lines.push(`  "source": ${esc(src.source)},`);
// Which document this came from — the Command Library shows it, and it is what lets a
// reader check an entry against the guide. Every shipped catalog carries one; they were
// hand-added after emitting until the config started carrying it.
//
// The manufacturer is its own field, not a copy of the guide's vendor. The two usually
// match, but nothing forces them to — an instrument sold under one name can ship with a
// manual published under another (HAMEG under R&S, Texio over GW Instek hardware), and
// the library groups its tree by who made the instrument, not who wrote the book. The
// vendor is only the fallback for configs that never state a manufacturer.
const maker = src.manufacturer || (src.guide && src.guide.vendor);
if (maker) lines.push(`  "manufacturer": ${esc(maker)},`);
if (src.guide) {
  const g = src.guide;
  const parts = ['title', 'edition', 'vendor', 'url', 'fileName']
    .filter(k => g[k]).map(k => `${esc(k)}: ${esc(g[k])}`);
  lines.push(`  "guide": { ${parts.join(', ')} },`);
}
lines.push('  "commands": [');

const rows = [];
for (const cat of order) {
  rows.push(null);                                   // blank line between groups
  for (const c of byCat.get(cat)) {
    let o = `    { "category": ${esc(c.category)}, "syntax": ${esc(c.syntax)}, "description": ${esc(c.description)}`;
    if (c.example) o += `, "example": ${esc(c.example)}`;
    if (c.isQuery) o += `, "isQuery": true`;
    if (c.benchVerified) o += `, "benchVerified": true`;
    if (c.corroborated || c.crossChecked) o += `, "crossChecked": true`;
    o += ' }';
    rows.push(o);
  }
}
while (rows.length && rows[0] === null) rows.shift();
const rendered = [];
for (let i = 0; i < rows.length; i++) {
  if (rows[i] === null) { rendered.push(''); continue; }
  const isLast = !rows.slice(i + 1).some(r => r !== null);
  rendered.push(rows[i] + (isLast ? '' : ','));
}
lines.push(...rendered);
lines.push('  ]');
lines.push('}');

fs.writeFileSync(dest, lines.join('\n') + '\n');
console.log(`${dest}: ${src.commands.length} commands, ` +
  `${src.commands.filter(c => c.corroborated).length} cross-checked`);
