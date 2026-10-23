import type { Weekday } from './types';

export const WEEK: Weekday[] = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

/** "Daily", "Mon-Fri", "Mon-Wed, Sat": consecutive days are joined into ranges. */
export function describeDays(days: readonly string[]): string {
  const on = WEEK.map((d) => days.includes(d));
  if (on.every(Boolean)) return 'Daily';
  const parts: string[] = [];
  for (let i = 0; i < 7; i++) {
    if (!on[i]) continue;
    let j = i;
    while (j + 1 < 7 && on[j + 1]) j++;
    parts.push(j - i >= 2 ? `${WEEK[i]}-${WEEK[j]}` : WEEK.slice(i, j + 1).join(', '));
    i = j;
  }
  return parts.join(', ') || 'No days';
}

/** "Mon-Fri 02:00" */
export const describeSchedule = (s: { days: readonly string[]; time: string }) => `${describeDays(s.days)} ${s.time}`;

/** The saved id of a template's enabled schedules, formatted for the "Scheduled: ..." badge, or null. */
export function scheduledBadge(t: { schedules?: { enabled: boolean; days: string[]; time: string }[] }): string | null {
  const on = (t.schedules ?? []).filter((s) => s.enabled);
  return on.length ? on.map(describeSchedule).join('; ') : null;
}
