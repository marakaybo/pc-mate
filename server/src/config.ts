/** Конфигурация сервера. Всё настраивается переменными окружения — см. .env.example. */

function int(name: string, fallback: number): number {
  const raw = process.env[name];
  if (!raw) return fallback;
  const value = Number.parseInt(raw, 10);
  return Number.isFinite(value) ? value : fallback;
}

function bool(name: string, fallback: boolean): boolean {
  const raw = process.env[name];
  if (raw === undefined) return fallback;
  return ['1', 'true', 'yes', 'on'].includes(raw.toLowerCase());
}

export const config = {
  /** Порт HTTP/WebSocket. За TLS отвечает обратный прокси (Caddy/nginx/Traefik). */
  port: int('PCMATE_PORT', 8080),
  host: process.env.PCMATE_HOST ?? '0.0.0.0',

  /** Файл базы SQLite. В Docker — том /data. */
  databasePath: process.env.PCMATE_DB ?? './data/pcmate.db',

  /** Интервал ping и таймаут «молчания» соединения. */
  pingIntervalMs: int('PCMATE_PING_INTERVAL_MS', 30_000),
  connectionTimeoutMs: int('PCMATE_CONNECTION_TIMEOUT_MS', 90_000),

  /** Сколько ждать ответ ПК при сопряжении. */
  pairTimeoutMs: int('PCMATE_PAIR_TIMEOUT_MS', 20_000),

  /** Сколько ждать подтверждения от моста после отправки магического пакета. */
  wolTimeoutMs: int('PCMATE_WOL_TIMEOUT_MS', 15_000),

  /** Сколько событий хранить на один ПК. */
  eventsPerPc: int('PCMATE_EVENTS_PER_PC', 500),

  /** Максимальный размер кадра WebSocket (защита от мусора). */
  maxFrameBytes: int('PCMATE_MAX_FRAME_BYTES', 512 * 1024),

  /** Ограничение частоты запросов к HTTP API. */
  rateLimitMax: int('PCMATE_RATE_LIMIT_MAX', 120),
  rateLimitWindowMs: int('PCMATE_RATE_LIMIT_WINDOW_MS', 60_000),

  /** Разрешить регистрацию новых устройств (можно закрыть после настройки). */
  allowRegistration: bool('PCMATE_ALLOW_REGISTRATION', true),

  /** Необязательный общий секрет: без него регистрация новых устройств отклоняется. */
  registrationSecret: process.env.PCMATE_REGISTRATION_SECRET ?? '',

  logLevel: process.env.PCMATE_LOG_LEVEL ?? 'info',
  version: '1.0.0',
  protocolVersion: 1
} as const;

export type Config = typeof config;
