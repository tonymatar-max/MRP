import { useState } from 'react';
import { useDeleteRunTemplate, useRunTemplates, useSaveRunTemplate, useUpdateRunTemplate } from '../../api/hooks';
import type { RunCriteria } from '../../api/types';
import { useT } from '../../i18n';

/**
 * Saved option sets for the Run MRP wizard. Apply one to fill every step, or save the current options under a name.
 * A template never stores the "plan as of" date: it always plans from today.
 */
export function TemplateBar({ c, onApply, disabled }: { c: RunCriteria | null; onApply: (c: RunCriteria, name: string) => void; disabled?: boolean }) {
  const tr = useT();
  const list = useRunTemplates().data ?? [];
  const save = useSaveRunTemplate();
  const update = useUpdateRunTemplate();
  const del = useDeleteRunTemplate();
  const [pick, setPick] = useState('');
  const [saving, setSaving] = useState(false);
  const [name, setName] = useState('');
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);

  const picked = list.find((t) => t.id === pick);
  const same = list.find((t) => t.name.toLowerCase() === name.trim().toLowerCase());
  const busy = save.isPending || update.isPending || del.isPending;
  const fail = (e: unknown) => setMsg({ ok: false, text: (e as Error).message });

  const apply = () => { if (picked) { onApply(picked.criteria, picked.name); setMsg({ ok: true, text: tr('Loaded template "{name}".', { name: picked.name }) }); } };
  const doSave = () => {
    if (!c || !name.trim()) return;
    const done = (t: { id: string; name: string }) => { setSaving(false); setName(''); setPick(t.id); setMsg({ ok: true, text: tr('Saved template "{name}".', { name: t.name }) }); };
    if (same) update.mutate({ id: same.id, body: { criteria: c } }, { onSuccess: done, onError: fail });
    else save.mutate({ name: name.trim(), criteria: c }, { onSuccess: done, onError: fail });
  };
  const doUpdate = () => { if (picked && c) update.mutate({ id: picked.id, body: { criteria: c } }, { onSuccess: () => setMsg({ ok: true, text: tr('Updated template "{name}" with the current options.', { name: picked.name }) }), onError: fail }); };
  const doDelete = () => { if (picked) del.mutate(picked.id, { onSuccess: () => { setMsg({ ok: true, text: tr('Deleted template "{name}".', { name: picked.name }) }); setPick(''); }, onError: fail }); };

  return (
    <div className="tpl-bar" role="group" aria-label={tr('Templates')}>
      <label htmlFor="wz-tpl" className="muted">{tr('Template')}</label>
      <select id="wz-tpl" value={pick} onChange={(e) => { setPick(e.target.value); setMsg(null); }} disabled={disabled || busy}>
        <option value="">{list.length ? tr('Choose a saved template…') : tr('No saved templates yet')}</option>
        {list.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
      </select>
      <button type="button" className="btn sm" onClick={apply} disabled={!picked || disabled || busy}>{tr('Apply')}</button>
      {picked && <button type="button" className="btn sm" onClick={doUpdate} disabled={!c || disabled || busy} title={tr('Replace this template with the options currently chosen')}>{tr('Update')}</button>}
      {picked && <button type="button" className="btn sm" onClick={doDelete} disabled={disabled || busy}>{tr('Delete')}</button>}
      <span className="spacer" />
      {!saving
        ? <button type="button" className="btn sm" onClick={() => { setSaving(true); setMsg(null); }} disabled={!c || disabled || busy}>{tr('Save as template…')}</button>
        : (
          <span className="tpl-save">
            <input aria-label={tr('Template name')} placeholder={tr('Template name')} maxLength={80} value={name} autoFocus
              onChange={(e) => setName(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); doSave(); } }} />
            <button type="button" className="btn sm primary" onClick={doSave} disabled={!name.trim() || busy}>{same ? tr('Replace') : tr('Save')}</button>
            <button type="button" className="btn sm" onClick={() => setSaving(false)}>{tr('Cancel')}</button>
          </span>
        )}
      {msg && <span className={msg.ok ? 'hint' : 'hint err'} role={msg.ok ? 'status' : 'alert'} style={{ flexBasis: '100%' }}>{msg.text}</span>}
    </div>
  );
}
