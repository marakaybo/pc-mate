import Database from 'better-sqlite3';
import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { config } from '../config.js';
import { hashToken, newDeviceToken, tokensMatch } from '../lib/auth.js';
import type { DeviceRole, PeerState } from '../lib/protocol.js';

export interface DeviceRow {
  id: string;
  role: DeviceRole;
  name: string | null;
  pub: string;
  token_hash: string;
  mac: string | null;
  lan_ips: string | null;
  state: string;
  created_at: string;
  last_seen: string | null;
}

export interface PairRow {
  pair_id: string;
  pc_id: string;
  phone_id: string;
  created_at: string;
  revoked: number;
}

export interface EventRow {
  id: number;
  pc_id: string;
  kind: string;
  summary: string;
  at: string;
  source: string;
}

const SCHEMA = `
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS devices (
  id          TEXT PRIMARY KEY,
  role        TEXT NOT NULL,
  name        TEXT,
  pub         TEXT NOT NULL,
  token_hash  TEXT NOT NULL,
  mac         TEXT,
  lan_ips     TEXT,
  state       TEXT NOT NULL DEFAULT 'unknown',
  created_at  TEXT NOT NULL,
  last_seen   TEXT
);

CREATE TABLE IF NOT EXISTS pairs (
  pair_id    TEXT PRIMARY KEY,
  pc_id      TEXT NOT NULL,
  phone_id   TEXT NOT NULL,
  created_at TEXT NOT NULL,
  revoked    INTEGER NOT NULL DEFAULT 0,
  UNIQUE (pc_id, phone_id)
);

CREATE TABLE IF NOT EXISTS pair_offers (
  token      TEXT PRIMARY KEY,
  pc_id      TEXT NOT NULL,
  exp        INTEGER NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS bridges (
  bridge_id  TEXT PRIMARY KEY,
  pc_id      TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS schedules (
  id         TEXT NOT NULL,
  pc_id      TEXT NOT NULL,
  payload    TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (pc_id, id)
);

CREATE TABLE IF NOT EXISTS events (
  id      INTEGER PRIMARY KEY AUTOINCREMENT,
  pc_id   TEXT NOT NULL,
  kind    TEXT NOT NULL,
  summary TEXT NOT NULL,
  at      TEXT NOT NULL,
  source  TEXT NOT NULL DEFAULT 'server'
);

CREATE TABLE IF NOT EXISTS push_tokens (
  device_id TEXT NOT NULL,
  platform  TEXT NOT NULL,
  token     TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (device_id, platform)
);

CREATE INDEX IF NOT EXISTS idx_events_pc ON events (pc_id, id DESC);
CREATE INDEX IF NOT EXISTS idx_pairs_phone ON pairs (phone_id);
CREATE INDEX IF NOT EXISTS idx_pairs_pc ON pairs (pc_id);
`;

export class Store {
  private readonly db: Database.Database;

  constructor(path: string = config.databasePath) {
    if (path !== ':memory:') mkdirSync(dirname(path), { recursive: true });
    this.db = new Database(path);
    this.db.exec(SCHEMA);
  }

  close(): void {
    this.db.close();
  }

  // ---------------- Устройства ----------------

  getDevice(id: string): DeviceRow | undefined {
    return this.db.prepare('SELECT * FROM devices WHERE id = ?').get(id) as DeviceRow | undefined;
  }

  /**
   * Регистрирует устройство. Повторная регистрация того же id разрешена только
   * владельцу действующего токена — иначе чужой мог бы перехватить идентификатор.
   */
  registerDevice(input: {
    deviceId?: string;
    role: DeviceRole;
    name?: string;
    pub: string;
    mac?: string | null;
    lanIps?: string[] | null;
    existingToken?: string | null;
  }): { deviceId: string; deviceToken: string } | { error: string } {
    const now = new Date().toISOString();
    const deviceId = input.deviceId?.trim() || crypto.randomUUID();
    const existing = this.getDevice(deviceId);

    if (existing) {
      const authorized = input.existingToken && tokensMatch(input.existingToken, existing.token_hash);
      if (!authorized && existing.pub !== input.pub) {
        return { error: 'Устройство с таким идентификатором уже зарегистрировано.' };
      }

      const token = newDeviceToken();
      this.db
        .prepare(
          `UPDATE devices
              SET role = ?, name = ?, pub = ?, token_hash = ?, mac = ?, lan_ips = ?, last_seen = ?
            WHERE id = ?`
        )
        .run(
          input.role,
          input.name ?? existing.name,
          input.pub,
          hashToken(token),
          input.mac ?? existing.mac,
          input.lanIps ? JSON.stringify(input.lanIps) : existing.lan_ips,
          now,
          deviceId
        );
      return { deviceId, deviceToken: token };
    }

    const token = newDeviceToken();
    this.db
      .prepare(
        `INSERT INTO devices (id, role, name, pub, token_hash, mac, lan_ips, state, created_at, last_seen)
         VALUES (?, ?, ?, ?, ?, ?, ?, 'unknown', ?, ?)`
      )
      .run(
        deviceId,
        input.role,
        input.name ?? null,
        input.pub,
        hashToken(token),
        input.mac ?? null,
        input.lanIps ? JSON.stringify(input.lanIps) : null,
        now,
        now
      );

    return { deviceId, deviceToken: token };
  }

  authenticate(deviceId: string, token: string): DeviceRow | null {
    const device = this.getDevice(deviceId);
    if (!device) return null;
    return tokensMatch(token, device.token_hash) ? device : null;
  }

  /** Находит устройство только по токену — используется на HTTP-эндпоинтах. */
  authenticateByToken(token: string): DeviceRow | null {
    const hash = hashToken(token);
    const row = this.db.prepare('SELECT * FROM devices WHERE token_hash = ?').get(hash) as DeviceRow | undefined;
    return row ?? null;
  }

  updateDeviceMeta(id: string, patch: { name?: string; mac?: string | null; lanIps?: string[] | null }): void {
    const device = this.getDevice(id);
    if (!device) return;

    this.db
      .prepare('UPDATE devices SET name = ?, mac = ?, lan_ips = ? WHERE id = ?')
      .run(
        patch.name ?? device.name,
        patch.mac ?? device.mac,
        patch.lanIps ? JSON.stringify(patch.lanIps) : device.lan_ips,
        id
      );
  }

  touchDevice(id: string, state?: string): void {
    const now = new Date().toISOString();
    if (state) this.db.prepare('UPDATE devices SET last_seen = ?, state = ? WHERE id = ?').run(now, state, id);
    else this.db.prepare('UPDATE devices SET last_seen = ? WHERE id = ?').run(now, id);
  }

  deleteDevice(id: string): void {
    this.db.prepare('DELETE FROM devices WHERE id = ?').run(id);
    this.db.prepare('DELETE FROM pairs WHERE pc_id = ? OR phone_id = ?').run(id, id);
    this.db.prepare('DELETE FROM bridges WHERE bridge_id = ? OR pc_id = ?').run(id, id);
  }

  // ---------------- Пары ----------------

  createPair(pairId: string, pcId: string, phoneId: string): void {
    this.db
      .prepare(
        `INSERT INTO pairs (pair_id, pc_id, phone_id, created_at, revoked)
         VALUES (?, ?, ?, ?, 0)
         ON CONFLICT (pc_id, phone_id) DO UPDATE SET pair_id = excluded.pair_id, revoked = 0`
      )
      .run(pairId, pcId, phoneId, new Date().toISOString());
  }

  arePaired(a: string, b: string): boolean {
    const row = this.db
      .prepare(
        `SELECT 1 FROM pairs
          WHERE revoked = 0 AND ((pc_id = ? AND phone_id = ?) OR (pc_id = ? AND phone_id = ?))`
      )
      .get(a, b, b, a);
    return !!row;
  }

  revokePair(pcId: string, phoneId: string): boolean {
    const result = this.db
      .prepare('UPDATE pairs SET revoked = 1 WHERE pc_id = ? AND phone_id = ?')
      .run(pcId, phoneId);
    return result.changes > 0;
  }

  /** Все спаренные устройства для данного (телефон → его ПК, ПК → его телефоны). */
  listPeers(deviceId: string): DeviceRow[] {
    return this.db
      .prepare(
        `SELECT d.* FROM devices d
           JOIN pairs p ON (p.pc_id = d.id AND p.phone_id = ?) OR (p.phone_id = d.id AND p.pc_id = ?)
          WHERE p.revoked = 0`
      )
      .all(deviceId, deviceId) as DeviceRow[];
  }

  // ---------------- Коды сопряжения ----------------

  saveOffer(token: string, pcId: string, exp: number): void {
    this.db
      .prepare(
        `INSERT INTO pair_offers (token, pc_id, exp, created_at) VALUES (?, ?, ?, ?)
         ON CONFLICT (token) DO UPDATE SET pc_id = excluded.pc_id, exp = excluded.exp`
      )
      .run(token, pcId, exp, new Date().toISOString());

    // Заодно чистим протухшие.
    this.db.prepare('DELETE FROM pair_offers WHERE exp < ?').run(Math.floor(Date.now() / 1000));
  }

  takeOffer(token: string): { pcId: string } | null {
    const row = this.db.prepare('SELECT * FROM pair_offers WHERE token = ?').get(token) as
      | { token: string; pc_id: string; exp: number }
      | undefined;

    if (!row) return null;
    if (row.exp < Math.floor(Date.now() / 1000)) {
      this.db.prepare('DELETE FROM pair_offers WHERE token = ?').run(token);
      return null;
    }

    return { pcId: row.pc_id };
  }

  consumeOffer(token: string): void {
    this.db.prepare('DELETE FROM pair_offers WHERE token = ?').run(token);
  }

  // ---------------- Мосты ----------------

  bindBridge(bridgeId: string, pcId: string): void {
    this.db
      .prepare(
        `INSERT INTO bridges (bridge_id, pc_id, created_at) VALUES (?, ?, ?)
         ON CONFLICT (bridge_id) DO UPDATE SET pc_id = excluded.pc_id`
      )
      .run(bridgeId, pcId, new Date().toISOString());
  }

  findBridgesForPc(pcId: string): string[] {
    const rows = this.db.prepare('SELECT bridge_id FROM bridges WHERE pc_id = ?').all(pcId) as {
      bridge_id: string;
    }[];
    return rows.map((r) => r.bridge_id);
  }

  // ---------------- Расписания ----------------

  listSchedules(pcId: string): unknown[] {
    const rows = this.db.prepare('SELECT payload FROM schedules WHERE pc_id = ?').all(pcId) as {
      payload: string;
    }[];
    return rows.map((r) => JSON.parse(r.payload));
  }

  saveSchedule(pcId: string, id: string, payload: unknown): void {
    this.db
      .prepare(
        `INSERT INTO schedules (id, pc_id, payload, updated_at) VALUES (?, ?, ?, ?)
         ON CONFLICT (pc_id, id) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at`
      )
      .run(id, pcId, JSON.stringify(payload), new Date().toISOString());
  }

  deleteSchedule(pcId: string, id: string): boolean {
    const result = this.db.prepare('DELETE FROM schedules WHERE pc_id = ? AND id = ?').run(pcId, id);
    return result.changes > 0;
  }

  // ---------------- События ----------------

  addEvent(pcId: string, kind: string, summary: string, source = 'server'): EventRow {
    const at = new Date().toISOString();
    const result = this.db
      .prepare('INSERT INTO events (pc_id, kind, summary, at, source) VALUES (?, ?, ?, ?, ?)')
      .run(pcId, kind, summary, at, source);

    // Держим историю ограниченной — сервер не архив.
    this.db
      .prepare(
        `DELETE FROM events
          WHERE pc_id = ? AND id NOT IN (
            SELECT id FROM events WHERE pc_id = ? ORDER BY id DESC LIMIT ?
          )`
      )
      .run(pcId, pcId, config.eventsPerPc);

    return { id: Number(result.lastInsertRowid), pc_id: pcId, kind, summary, at, source };
  }

  listEvents(pcId: string, limit = 100): EventRow[] {
    const rows = this.db
      .prepare('SELECT * FROM events WHERE pc_id = ? ORDER BY id DESC LIMIT ?')
      .all(pcId, Math.min(Math.max(limit, 1), 500)) as EventRow[];
    return rows.reverse();
  }

  // ---------------- Push ----------------

  savePushToken(deviceId: string, platform: string, token: string): void {
    this.db
      .prepare(
        `INSERT INTO push_tokens (device_id, platform, token, updated_at) VALUES (?, ?, ?, ?)
         ON CONFLICT (device_id, platform) DO UPDATE SET token = excluded.token, updated_at = excluded.updated_at`
      )
      .run(deviceId, platform, token, new Date().toISOString());
  }

  getPushTokens(deviceId: string): { platform: string; token: string }[] {
    return this.db
      .prepare('SELECT platform, token FROM push_tokens WHERE device_id = ?')
      .all(deviceId) as { platform: string; token: string }[];
  }
}

export function toPeerState(row: DeviceRow, online: boolean): PeerState {
  return {
    deviceId: row.id,
    role: row.role,
    name: row.name,
    online,
    state: online ? row.state : row.state === 'running' ? 'unknown' : row.state,
    lastSeen: row.last_seen,
    mac: row.mac,
    lanIps: row.lan_ips ? (JSON.parse(row.lan_ips) as string[]) : null
  };
}
