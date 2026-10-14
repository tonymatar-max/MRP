import type { RunCriteria, RunTemplate } from '../api/types';
import * as adm from './admin';

const items: RunTemplate[] = [];
let n = 0;

export function listTemplates(): RunTemplate[] {
  adm.requireSignedIn();
  return [...items].sort((a, b) => a.name.localeCompare(b.name)).map((t) => structuredClone(t));
}

const clean = (c: RunCriteria): RunCriteria => ({ ...structuredClone(c), horizon: { ...c.horizon, startDate: null } });

export function saveTemplate(name: string, criteria: RunCriteria): RunTemplate {
  adm.requireSignedIn();
  const nm = name.trim();
  if (!nm || nm.length > 80) throw new adm.MockHttpError(400, 'Give the template a name of 1 to 80 characters.');
  if (items.some((t) => t.name.toLowerCase() === nm.toLowerCase())) throw new adm.MockHttpError(409, 'A template with this name already exists.');
  const t: RunTemplate = { id: `tpl-${++n}`, name: nm, createdBy: adm.currentUserName(), updatedAt: new Date().toISOString(), criteria: clean(criteria) };
  items.push(t);
  return structuredClone(t);
}

export function updateTemplate(id: string, body: { name?: string; criteria?: RunCriteria }): RunTemplate {
  adm.requireSignedIn();
  const t = items.find((x) => x.id === id);
  if (!t) throw new adm.MockHttpError(404, 'Template not found.');
  if (body.name !== undefined) {
    const nm = body.name.trim();
    if (!nm || nm.length > 80) throw new adm.MockHttpError(400, 'Give the template a name of 1 to 80 characters.');
    if (items.some((x) => x.id !== id && x.name.toLowerCase() === nm.toLowerCase())) throw new adm.MockHttpError(409, 'A template with this name already exists.');
    t.name = nm;
  }
  if (body.criteria) t.criteria = clean(body.criteria);
  t.updatedAt = new Date().toISOString();
  return structuredClone(t);
}

export function deleteTemplate(id: string): void {
  adm.requireSignedIn();
  const i = items.findIndex((x) => x.id === id);
  if (i < 0) throw new adm.MockHttpError(404, 'Template not found.');
  items.splice(i, 1);
}
