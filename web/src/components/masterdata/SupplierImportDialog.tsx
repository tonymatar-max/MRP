import { useState } from 'react';
import { useImportSuppliers } from '../../api/hooks';
import type { DataSource, SupplierImportResponse, SupplierImportRow } from '../../api/types';
import { Dialog, Pill } from '../ui';
import { csvDelimiter, findColumns, parseCsv } from './csv';

const COLS = {
  cardCode: ['cardcode', 'code', 'supplier', 'suppliercode', 'vendor', 'vendorcode'],
  leadTimeDays: ['leadtimedays', 'leadtime', 'days', 'leaddays'],
  notes: ['notes', 'note', 'comment', 'comments'],
};
const TEMPLATE = 'CardCode,LeadTimeDays,Notes\r\nV-STEELCO,21,Mill lead time\r\nV-SKFGULF,9,\r\n';

function downloadTemplate() {
  const url = URL.createObjectURL(new Blob([TEMPLATE], { type: 'text/csv;charset=utf-8' }));
  const a = document.createElement('a');
  a.href = url; a.download = 'supplier-lead-times-template.csv';
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Turns the file text into rows, or explains why it cannot. */
function toRows(text: string): { rows: SupplierImportRow[]; warning?: string } | { error: string } {
  const table = parseCsv(text);
  if (table.length < 2) return { error: 'The file needs a header row and at least one supplier.' };
  const c = findColumns(table[0], COLS);
  if (c.cardCode < 0) return { error: 'No supplier code column found. The header should include CardCode (or Code / Supplier).' };
  if (c.leadTimeDays < 0 && c.notes < 0) return { error: 'Nothing to import: add a LeadTimeDays and/or Notes column.' };
  const cell = (r: string[], i: number) => (i >= 0 ? (r[i] ?? '').trim() : '');
  // A row with more cells than the header has an unquoted delimiter inside its last column (typically Notes): glue it back
  // together instead of silently cutting the text off. When the last column is not Notes we cannot know, so we only warn.
  const width = table[0].length, delim = csvDelimiter(text);
  const wide: number[] = [];
  const body = table.slice(1).map((r) => {
    if (r.length <= width) return r;
    wide.push(0);
    return c.notes === width - 1 ? [...r.slice(0, width - 1), r.slice(width - 1).join(delim)] : r;
  });
  const dname = delim === '\t' ? 'tab' : delim;
  const warning = wide.length
    ? c.notes === width - 1
      ? `${wide.length} row(s) had extra ${dname} characters in the Notes text; the pieces were joined back into the note. Put quotes around such notes to be safe.`
      : `${wide.length} row(s) have more cells than the header, so the extra cells were ignored. Check for a stray ${dname} or put quotes around the text.`
    : undefined;
  return { rows: body.map((r, i) => ({ line: i + 2, cardCode: cell(r, c.cardCode), leadTimeDays: cell(r, c.leadTimeDays), notes: cell(r, c.notes) })), warning };
}

export function SupplierImportDialog({ source, onClose }: { source: DataSource; onClose: () => void }) {
  const imp = useImportSuppliers();
  const [rows, setRows] = useState<SupplierImportRow[] | null>(null);
  const [fileName, setFileName] = useState('');
  const [error, setError] = useState('');
  const [warning, setWarning] = useState('');
  const [check, setCheck] = useState<SupplierImportResponse | null>(null);
  const [done, setDone] = useState<SupplierImportResponse | null>(null);

  const pick = async (file: File | undefined) => {
    setError(''); setWarning(''); setCheck(null); setDone(null); setRows(null);
    if (!file) return;
    setFileName(file.name);
    const parsed = toRows(await file.text());
    if ('error' in parsed) { setError(parsed.error); return; }
    setRows(parsed.rows);
    setWarning(parsed.warning ?? '');
    imp.mutate({ source, rows: parsed.rows, dryRun: true }, { onSuccess: setCheck, onError: (e) => setError((e as Error).message) });
  };
  const apply = () => rows && imp.mutate({ source, rows, dryRun: false }, { onSuccess: setDone, onError: (e) => setError((e as Error).message) });

  const shown = (done ?? check)?.results.filter((r) => r.status !== 'same').slice(0, 300) ?? [];
  return (
    <Dialog
      title="Import supplier lead times"
      onClose={onClose}
      footer={<>
        <button type="button" className="btn" onClick={onClose}>{done ? 'Close' : 'Cancel'}</button>
        {!done && <button type="button" className="btn primary" disabled={!check || check.willChange === 0 || imp.isPending} onClick={apply}>
          {check?.willChange ? `Apply ${check.willChange} change${check.willChange === 1 ? '' : 's'}` : 'Apply'}
        </button>}
      </>}
    >
      <p className="hint" style={{ marginBottom: 10 }}>
        A CSV with the columns <b>CardCode</b>, <b>LeadTimeDays</b> and optionally <b>Notes</b>. A blank cell leaves that value as it is.
        Values are stored in Cobalt MRP only; SAP Business One is not changed. <button type="button" className="btn sm" onClick={downloadTemplate}>Download template</button>
      </p>
      <div className="field"><label htmlFor="sup-import-file">CSV file</label>
        <input id="sup-import-file" type="file" accept=".csv,.txt,text/csv" onChange={(e) => pick(e.target.files?.[0])} /></div>
      {error && <div className="alert danger" role="alert" style={{ marginTop: 10 }}>{error}</div>}
      {warning && <div className="alert warn" role="status" style={{ marginTop: 10 }}>{warning}</div>}
      {imp.isPending && <p className="muted" style={{ marginTop: 10 }}>Working on {fileName}…</p>}
      {(done ?? check) && (
        <div style={{ marginTop: 12 }}>
          <div className={`alert ${done ? 'ok' : 'info-note'}`} role="status">
            {done
              ? `Applied ${done.applied} change${done.applied === 1 ? '' : 's'}.`
              : `${check!.willChange} will change, ${check!.same} already match, ${check!.errors} have problems${check!.errors ? ' (those are skipped)' : ''}.`}
          </div>
          {shown.length > 0 && (
            <div className="table-wrap" style={{ maxHeight: 260, overflow: 'auto', marginTop: 8 }}>
              <table className="data">
                <thead><tr><th className="nosort num">Line</th><th className="nosort">Supplier</th><th className="nosort num">Lead time</th><th className="nosort">Result</th></tr></thead>
                <tbody>
                  {shown.map((r) => (
                    <tr key={r.line}>
                      <td className="num">{r.line}</td>
                      <td className="mono">{r.cardCode || '—'}</td>
                      <td className="num">{r.status === 'error' ? '—' : `${r.before ?? '-'} → ${r.after ?? '-'}`}</td>
                      <td style={{ whiteSpace: 'normal' }}><Pill tone={r.status === 'error' ? 'danger' : 'ok'}>{r.status === 'error' ? 'Problem' : 'Change'}</Pill> {r.message}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </Dialog>
  );
}
