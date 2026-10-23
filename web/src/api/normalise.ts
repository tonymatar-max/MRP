import type { AppSettings, NotificationSettings, ScheduleSettings, Weekday } from './types';

const ALL: Weekday[] = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

export const defaultNotifications = (): NotificationSettings => ({
  enabled: false, smtpHost: '', smtpPort: 587, useSsl: true, userName: '', password: '', passwordSet: false,
  from: '', recipients: '', publicUrl: '', notifyOn: 'ScheduledRuns',
});

const asSchedule = (x: Partial<ScheduleSettings> | null | undefined, id: string): ScheduleSettings => ({
  id: typeof x?.id === 'string' && x.id ? x.id : id,
  name: typeof x?.name === 'string' ? x.name : '',
  enabled: !!x?.enabled,
  time: typeof x?.time === 'string' && /^\d\d:\d\d$/.test(x.time) ? x.time : '02:00',
  days: Array.isArray(x?.days) ? (x!.days as Weekday[]).filter((d) => ALL.includes(d)) : [...ALL],
  templateId: typeof x?.templateId === 'string' ? x.templateId : '',
});

/**
 * Accepts any settings shape a server may send (the newer `schedules` list, the older single `schedule`, or neither) and
 * returns one the Settings screen can always render. Never throws.
 */
export function normaliseSettings(raw: unknown): AppSettings {
  const r = (raw ?? {}) as Record<string, unknown> & { schedule?: Partial<ScheduleSettings> | null; schedules?: unknown; notifications?: Partial<NotificationSettings> | null };
  let schedules: ScheduleSettings[];
  if (Array.isArray(r.schedules)) schedules = r.schedules.map((x) => asSchedule(x as Partial<ScheduleSettings>, ''));
  else if (r.schedule && typeof r.schedule === 'object') schedules = [asSchedule({ ...r.schedule, id: 'default' }, 'default')];
  else schedules = [];
  const { schedule: _legacy, ...rest } = r;
  return { ...(rest as unknown as AppSettings), notifications: { ...defaultNotifications(), ...(r.notifications ?? {}) }, schedules };
}
