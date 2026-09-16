import { config } from '../config.js';

type Level = 'debug' | 'info' | 'warn' | 'error';

const ORDER: Record<Level, number> = { debug: 10, info: 20, warn: 30, error: 40 };
const threshold = ORDER[(config.logLevel as Level) in ORDER ? (config.logLevel as Level) : 'info'];

function write(level: Level, context: unknown, message?: string): void {
  if (ORDER[level] < threshold) return;

  const line = {
    t: new Date().toISOString(),
    level,
    msg: message ?? (typeof context === 'string' ? context : ''),
    ...(typeof context === 'object' && context !== null ? sanitize(context as Record<string, unknown>) : {})
  };

  const out = level === 'error' || level === 'warn' ? process.stderr : process.stdout;
  out.write(JSON.stringify(line) + '\n');
}

/** Из журнала не должны утекать токены и шифротексты. */
function sanitize(context: Record<string, unknown>): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(context)) {
    if (/token|secret|password|pub|cipher/i.test(key)) {
      result[key] = typeof value === 'string' && value.length > 8 ? `${value.slice(0, 4)}…` : '…';
    } else if (value instanceof Error) {
      result[key] = { name: value.name, message: value.message };
    } else {
      result[key] = value;
    }
  }
  return result;
}

export const logger = {
  debug: (context: unknown, message?: string) => write('debug', context, message),
  info: (context: unknown, message?: string) => write('info', context, message),
  warn: (context: unknown, message?: string) => write('warn', context, message),
  error: (context: unknown, message?: string) => write('error', context, message)
};
