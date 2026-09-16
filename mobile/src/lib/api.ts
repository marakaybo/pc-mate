import type { EventRecord, Schedule } from './types';

/** HTTP-клиент: сервер-ретранслятор и прямые запросы к агенту в локальной сети. */

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code?: string
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export function httpBase(relayUrl: string): string {
  let url = relayUrl.trim().replace(/\/+$/, '');
  if (url.endsWith('/ws')) url = url.slice(0, -3);
  if (url.startsWith('wss://')) return 'https://' + url.slice(6);
  if (url.startsWith('ws://')) return 'http://' + url.slice(5);
  if (url.startsWith('http')) return url;
  return 'https://' + url;
}

export function wsUrl(relayUrl: string, token: string): string {
  let url = relayUrl.trim().replace(/\/+$/, '');
  if (url.startsWith('http://')) url = 'ws://' + url.slice(7);
  else if (url.startsWith('https://')) url = 'wss://' + url.slice(8);
  else if (!url.startsWith('ws')) url = 'wss://' + url;
  if (!url.endsWith('/ws')) url += '/ws';
  return `${url}?token=${encodeURIComponent(token)}`;
}

async function request<T>(url: string, init: RequestInit & { timeoutMs?: number } = {}): Promise<T> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), init.timeoutMs ?? 15000);

  let response: Response;
  try {
    response = await fetch(url, {
      ...init,
      signal: controller.signal,
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json',
        ...(init.headers ?? {})
      }
    });
  } catch (error) {
    clearTimeout(timeout);
    const message = (error as Error).name === 'AbortError' ? 'Сервер не ответил вовремя.' : (error as Error).message;
    throw new ApiError(message, 0);
  }
  clearTimeout(timeout);

  const text = await response.text();
  let body: unknown = null;
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = { message: text };
    }
  }

  if (!response.ok) {
    const payload = (body ?? {}) as { message?: string; error?: string };
    throw new ApiError(
      payload.message ?? payload.error ?? `Ошибка ${response.status}`,
      response.status,
      payload.error
    );
  }

  return body as T;
}

// ---------------- Сервер-ретранслятор ----------------

export interface RegisterResult {
  deviceId: string;
  deviceToken: string;
}

export function registerDevice(
  relayUrl: string,
  input: { deviceId: string; name: string; pub: string; secret?: string }
): Promise<RegisterResult> {
  return request<RegisterResult>(`${httpBase(relayUrl)}/api/v1/devices/register`, {
    method: 'POST',
    body: JSON.stringify({ ...input, role: 'phone' })
  });
}

export interface PairClaimResult {
  ok: boolean;
  pairId: string;
  pcId: string;
  pcName?: string | null;
  pcPub?: string | null;
  mac?: string | null;
  lanIps?: string[] | null;
  fingerprint?: string | null;
  error?: string;
}

export function claimPairingViaRelay(
  relayUrl: string,
  body: { token: string; phoneId: string; phonePub: string; phoneName: string }
): Promise<PairClaimResult> {
  return request<PairClaimResult>(`${httpBase(relayUrl)}/api/v1/pair/claim`, {
    method: 'POST',
    body: JSON.stringify(body),
    timeoutMs: 30000
  });
}

/** Сопряжение напрямую с агентом — работает без сервера, внутри домашней сети. */
export function claimPairingViaLan(
  address: string,
  body: { token: string; phoneId: string; phonePub: string; phoneName: string }
): Promise<PairClaimResult> {
  return request<PairClaimResult>(`http://${address}/pair/claim`, {
    method: 'POST',
    body: JSON.stringify(body),
    timeoutMs: 8000
  });
}

export function probeAgent(address: string): Promise<{ ok: boolean; pcId: string; pcName: string; version: string }> {
  return request(`http://${address}/health`, { method: 'GET', timeoutMs: 2500 });
}

export function relayHealth(relayUrl: string): Promise<{ ok: boolean; version: string; protocol: number }> {
  return request(`${httpBase(relayUrl)}/api/v1/health`, { method: 'GET', timeoutMs: 8000 });
}

export function requestWake(relayUrl: string, token: string, pcId: string) {
  return request<{ ok: boolean; via: string; message?: string; error?: string }>(
    `${httpBase(relayUrl)}/api/v1/wake`,
    {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ pcId }),
      timeoutMs: 25000
    }
  );
}

export function fetchEvents(relayUrl: string, token: string, pcId: string, limit = 100) {
  return request<{ pcId: string; events: EventRecord[] }>(
    `${httpBase(relayUrl)}/api/v1/events?pcId=${encodeURIComponent(pcId)}&limit=${limit}`,
    { method: 'GET', headers: { Authorization: `Bearer ${token}` } }
  );
}

export function fetchSchedulesFromRelay(relayUrl: string, token: string, pcId: string) {
  return request<{ pcId: string; schedules: Schedule[] }>(
    `${httpBase(relayUrl)}/api/v1/schedules?pcId=${encodeURIComponent(pcId)}`,
    { method: 'GET', headers: { Authorization: `Bearer ${token}` } }
  );
}

export function pushScheduleToRelay(relayUrl: string, token: string, pcId: string, schedule: Schedule) {
  return request<{ ok: boolean; synced: boolean; pcOnline: boolean }>(
    `${httpBase(relayUrl)}/api/v1/schedules/${encodeURIComponent(schedule.id)}`,
    {
      method: 'PUT',
      headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ pcId, schedule })
    }
  );
}

export function deleteScheduleFromRelay(relayUrl: string, token: string, pcId: string, id: string) {
  return request<{ ok: boolean }>(
    `${httpBase(relayUrl)}/api/v1/schedules/${encodeURIComponent(id)}?pcId=${encodeURIComponent(pcId)}`,
    { method: 'DELETE', headers: { Authorization: `Bearer ${token}` } }
  );
}

export function registerPushToken(relayUrl: string, token: string, platform: string, pushToken: string) {
  return request<{ ok: boolean }>(`${httpBase(relayUrl)}/api/v1/push/register`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}` },
    body: JSON.stringify({ platform, token: pushToken })
  });
}
