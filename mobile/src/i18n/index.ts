import { ru } from './ru';
import { en } from './en';

export type Language = 'ru' | 'en';
export type Dictionary = typeof ru;

const dictionaries: Record<Language, Dictionary> = { ru, en };

let current: Language = 'ru';

export function setLanguage(language: Language): void {
  current = language;
}

export function getLanguage(): Language {
  return current;
}

/** Перевод по ключу вида "home.title" с подстановкой {{name}}. */
export function t(key: string, params?: Record<string, string | number>): string {
  const dictionary = dictionaries[current] ?? ru;
  const value = lookup(dictionary, key) ?? lookup(ru, key) ?? key;
  if (!params) return value;

  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) =>
    params[name] === undefined ? `{{${name}}}` : String(params[name])
  );
}

function lookup(dictionary: unknown, key: string): string | undefined {
  const parts = key.split('.');
  let node: unknown = dictionary;

  for (const part of parts) {
    if (typeof node !== 'object' || node === null) return undefined;
    node = (node as Record<string, unknown>)[part];
  }

  return typeof node === 'string' ? node : undefined;
}

/** Правильная форма существительного: 1 минута, 2 минуты, 5 минут. */
export function plural(count: number, one: string, few: string, many: string): string {
  const n = Math.abs(count) % 100;
  const n1 = n % 10;
  if (n > 10 && n < 20) return many;
  if (n1 > 1 && n1 < 5) return few;
  if (n1 === 1) return one;
  return many;
}

export { ru, en };
