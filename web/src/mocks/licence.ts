import type { BackupSettings, BackupView, LicenseStatus } from '../api/types';
import { MockHttpError, requireAdmin } from './admin';

// Mock-mode stand-ins for the licence and backup endpoints (dev without a backend). The real checks are server side.
const HW = '3F9A0C5E7B1D42A8C6E0F1B2D3A49587';
let lic: LicenseStatus = {
  productCode: 'CobaltMRP', hardwareKey: HW, state: 'Valid', isValid: true, licenseKeySet: true,
  expiresOn: new Date(Date.now() + 200 * 864e5).toISOString().slice(0, 10), customer: 'Demo customer', maxSeats: null, message: 'Licensed.',
};
export const licenseStatus = (): LicenseStatus => ({ ...lic });
export function saveLicense(key: string): LicenseStatus {
  requireAdmin();
  if (!key.trim().startsWith('ICL1.')) throw new MockHttpError(400, 'That is not a Cobalt licence key. Paste the whole key exactly as generated (it starts with ICL1).');
  lic = { ...lic, state: 'Valid', isValid: true, licenseKeySet: true, message: 'Licensed.' };
  return { ...lic };
}

const DEFAULT_FOLDER = 'C:\\ProgramData\\Cobalt\\MRP\\Backups\\Scheduled';
let bk: BackupSettings = { enabled: false, frequency: 'daily', time: '02:00', dayOfWeek: 1, folder: '', keepLast: 14 };
let last: BackupView['last'] = null;
const view = (): BackupView => ({
  settings: { ...bk }, folder: bk.folder || DEFAULT_FOLDER, defaultFolder: DEFAULT_FOLDER,
  dataFolder: 'C:\\CobaltMRP\\data', contents: ['settings.json', 'users.json', 'automation-state.json', 'backup-schedule.json', 'license.json', 'keys/*'],
  nextRunLocal: bk.enabled ? '2026-10-25 02:00' : null, running: false, last, backups: [],
});
export const backupView = (): BackupView => { requireAdmin(); return view(); };
export function saveBackup(s: BackupSettings): BackupView {
  requireAdmin();
  if (s.folder && !/^[a-zA-Z]:\\|^\\\\/.test(s.folder)) throw new MockHttpError(400, 'The backup folder must be a full absolute path, for example D:\\CobaltBackups.');
  bk = { ...s };
  return view();
}
export function runBackup(): { result: NonNullable<BackupView['last']>; view: BackupView } {
  requireAdmin();
  const now = new Date().toISOString();
  last = { startedAtUtc: now, finishedAtUtc: now, trigger: 'manual', success: true, file: 'cobalt-mrp-backup-auto-mock.zip', sizeBytes: 4096, pruned: 0, error: null };
  return { result: last, view: view() };
}
