import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';

/** Токены устройств: в базе лежит только SHA-256, сам токен знает лишь устройство. */

export function newDeviceToken(): string {
  return base64url(randomBytes(32));
}

export function hashToken(token: string): string {
  return createHash('sha256').update(token, 'utf8').digest('hex');
}

export function tokensMatch(token: string, expectedHash: string): boolean {
  const actual = Buffer.from(hashToken(token), 'hex');
  let expected: Buffer;
  try {
    expected = Buffer.from(expectedHash, 'hex');
  } catch {
    return false;
  }
  return actual.length === expected.length && timingSafeEqual(actual, expected);
}

export function base64url(buffer: Buffer): string {
  return buffer.toString('base64').replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}

export function extractBearer(header: string | undefined): string | null {
  if (!header) return null;
  const match = /^Bearer\s+(.+)$/i.exec(header.trim());
  return match?.[1] ?? null;
}

/** Секрет регистрации (если задан) — защита от чужих устройств на публичном сервере. */
export function registrationSecretMatches(provided: string | undefined, expected: string): boolean {
  if (!expected) return true;
  if (!provided) return false;

  const a = Buffer.from(provided, 'utf8');
  const b = Buffer.from(expected, 'utf8');
  return a.length === b.length && timingSafeEqual(a, b);
}
