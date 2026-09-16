import { plural, t } from '../i18n';
import type { Schedule } from './types';

/** Форматирование дат, длительностей и описаний расписаний. */

export function formatUptime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return '—';

  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);

  if (days > 0) return `${days} д ${hours} ч`;
  if (hours > 0) return `${hours} ч ${minutes} м`;
  return `${minutes} м`;
}

export function formatRelative(iso: string | null | undefined): string {
  if (!iso) return t('common.never');

  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return t('common.never');

  const diffSec = Math.round((Date.now() - then) / 1000);
  if (diffSec < 45) return 'только что';
  if (diffSec < 90) return 'минуту назад';

  const minutes = Math.round(diffSec / 60);
  if (minutes < 60) return `${minutes} ${plural(minutes, 'минуту', 'минуты', 'минут')} назад`;

  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} ${plural(hours, 'час', 'часа', 'часов')} назад`;

  const days = Math.round(hours / 24);
  if (days < 7) return `${days} ${plural(days, 'день', 'дня', 'дней')} назад`;

  return new Date(iso).toLocaleDateString();
}

export function formatTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '—';
  return `${date.toLocaleDateString([], { day: '2-digit', month: '2-digit' })} ${formatTime(iso)}`;
}

const DAY_NAMES = ['Вс', 'Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб'];

export function describeDays(schedule: Schedule): string {
  const days = schedule.days ?? [];
  if (days.length === 0) return schedule.date ?? t('schedules.once');
  if (days.length === 7) return t('schedules.everyday');
  if (days.length === 5 && days.every((d) => d >= 1 && d <= 5)) return t('schedules.weekdays');
  if (days.length === 2 && days.includes(0) && days.includes(6)) return t('schedules.weekend');

  return days
    .slice()
    .sort((a, b) => ((a + 6) % 7) - ((b + 6) % 7))
    .map((d) => DAY_NAMES[d] ?? '')
    .join(', ');
}

/** Ближайшее срабатывание расписания в локальном времени телефона. */
export function nextOccurrence(schedule: Schedule, from = new Date()): Date | null {
  if (!schedule.enabled) return null;

  const [hoursText, minutesText] = (schedule.time ?? '').split(':');
  const hours = Number.parseInt(hoursText ?? '', 10);
  const minutes = Number.parseInt(minutesText ?? '', 10);
  if (!Number.isFinite(hours) || !Number.isFinite(minutes)) return null;

  const offsetMs = (schedule.action === 'wake' ? (schedule.wakeBeforeSec ?? 0) : 0) * 1000;

  if (!schedule.days || schedule.days.length === 0) {
    if (!schedule.date) return null;
    const [year, month, day] = schedule.date.split('-').map((v) => Number.parseInt(v, 10));
    if (!year || !month || !day) return null;

    const once = new Date(year, month - 1, day, hours, minutes, 0, 0);
    once.setTime(once.getTime() - offsetMs);
    return once > from ? once : null;
  }

  for (let i = 0; i <= 7; i++) {
    const candidate = new Date(from);
    candidate.setDate(candidate.getDate() + i);
    candidate.setHours(hours, minutes, 0, 0);
    candidate.setTime(candidate.getTime() - offsetMs);

    if (schedule.days.includes(candidate.getDay()) && candidate > from) return candidate;
  }

  return null;
}

export function formatNextOccurrence(schedule: Schedule): string | null {
  const next = nextOccurrence(schedule);
  if (!next) return null;

  const isToday = next.toDateString() === new Date().toDateString();
  const time = next.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

  if (isToday) return `сегодня в ${time}`;

  const tomorrow = new Date();
  tomorrow.setDate(tomorrow.getDate() + 1);
  if (next.toDateString() === tomorrow.toDateString()) return `завтра в ${time}`;

  return `${DAY_NAMES[next.getDay()]}, ${next.toLocaleDateString([], { day: '2-digit', month: '2-digit' })} в ${time}`;
}

export function formatBytesMb(mb: number): string {
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} ГБ`;
  return `${Math.round(mb)} МБ`;
}
