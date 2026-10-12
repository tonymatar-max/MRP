/** Small CSV reader for the import dialogs: quotes, doubled quotes, BOM, and a comma, semicolon or tab delimiter. */
export function csvDelimiter(text: string): string {
  const firstLine = text.replace(/^\uFEFF/, '').split(/\r?\n/, 1)[0] ?? '';
  return [',', ';', '\t'].map((d) => ({ d, n: firstLine.split(d).length })).sort((a, b) => b.n - a.n)[0].d;
}
export function parseCsv(text: string): string[][] {
  const src = text.replace(/^\uFEFF/, '');
  const delim = csvDelimiter(text);
  const rows: string[][] = [];
  let row: string[] = [], cell = '', quoted = false;
  for (let i = 0; i < src.length; i++) {
    const c = src[i];
    if (quoted) {
      if (c === '"') { if (src[i + 1] === '"') { cell += '"'; i++; } else quoted = false; }
      else cell += c;
    } else if (c === '"') quoted = true;
    else if (c === delim) { row.push(cell); cell = ''; }
    else if (c === '\n' || c === '\r') {
      if (c === '\r' && src[i + 1] === '\n') i++;
      row.push(cell); cell = '';
      if (row.some((x) => x.trim() !== '')) rows.push(row);
      row = [];
    } else cell += c;
  }
  row.push(cell);
  if (row.some((x) => x.trim() !== '')) rows.push(row);
  return rows;
}

const norm = (h: string) => h.toLowerCase().replace(/[^a-z0-9]/g, '');

/** Finds the column of each wanted field by its header (any of several spellings). Returns -1 when missing. */
export function findColumns(header: string[], wanted: Record<string, string[]>): Record<string, number> {
  const heads = header.map(norm);
  const out: Record<string, number> = {};
  for (const [key, names] of Object.entries(wanted)) out[key] = heads.findIndex((h) => names.includes(h));
  return out;
}
