import { x25519 } from '@noble/curves/ed25519.js';
import { hkdf } from '@noble/hashes/hkdf.js';
import { sha256 } from '@noble/hashes/sha2.js';
import { randomBytes, concatBytes } from '@noble/hashes/utils.js';
import { gcm } from '@noble/ciphers/aes.js';
import { toBase64Url, fromBase64Url, utf8ToBytes, bytesToUtf8 } from './base64';

/**
 * Криптография телефона — зеркало PcMate.Core.Crypto на стороне ПК.
 * X25519 → HKDF-SHA256 → два направленных ключа AES-256-GCM.
 * Приватный ключ не покидает Keystore/Keychain; сервер видит только шифротекст.
 */

export const NONCE_SIZE = 12;
export const KEY_SIZE = 32;
export const ALGORITHM = 'aes-256-gcm';

export const INFO_PHONE_TO_PC = 'pcmate/v1/phone->pc';
export const INFO_PC_TO_PHONE = 'pcmate/v1/pc->phone';

export interface KeyPair {
  privateKey: Uint8Array;
  publicKey: Uint8Array;
}

export interface SessionKeys {
  /** Чем телефон шифрует исходящие команды. */
  sendKey: Uint8Array;
  /** Чем телефон расшифровывает ответы и события ПК. */
  receiveKey: Uint8Array;
}

export function generateKeyPair(): KeyPair {
  const privateKey = randomBytes(KEY_SIZE);
  return { privateKey, publicKey: x25519.getPublicKey(privateKey) };
}

export function publicKeyFrom(privateKey: Uint8Array): Uint8Array {
  return x25519.getPublicKey(privateKey);
}

export function deriveSessionKeys(
  privateKey: Uint8Array,
  peerPublicKey: Uint8Array,
  pairId: string
): SessionKeys {
  const shared = x25519.getSharedSecret(privateKey, peerPublicKey);

  // Общий секрет из одних нулей означает ключ малого порядка — такому собеседнику доверять нельзя.
  if (shared.every((b) => b === 0)) throw new Error('Небезопасный общий секрет X25519.');

  const salt = utf8ToBytes(pairId);
  return {
    sendKey: hkdf(sha256, shared, salt, utf8ToBytes(INFO_PHONE_TO_PC), KEY_SIZE),
    receiveKey: hkdf(sha256, shared, salt, utf8ToBytes(INFO_PC_TO_PHONE), KEY_SIZE)
  };
}

export function buildAad(v: number, id: string, from: string, to: string, ts: number): Uint8Array {
  return utf8ToBytes(`${v}|${id}|${from}|${to}|${ts}`);
}

export function seal(
  key: Uint8Array,
  plaintext: Uint8Array,
  aad: Uint8Array
): { nonce: string; cipher: string } {
  const nonce = randomBytes(NONCE_SIZE);
  const sealed = gcm(key, nonce, aad).encrypt(plaintext);
  return { nonce: toBase64Url(nonce), cipher: toBase64Url(sealed) };
}

export function open(key: Uint8Array, nonceB64: string, cipherB64: string, aad: Uint8Array): Uint8Array {
  const nonce = fromBase64Url(nonceB64);
  if (nonce.length !== NONCE_SIZE) throw new Error('Некорректная длина nonce.');
  return gcm(key, nonce, aad).decrypt(fromBase64Url(cipherB64));
}

export function sealJson(key: Uint8Array, payload: unknown, aad: Uint8Array) {
  return seal(key, utf8ToBytes(JSON.stringify(payload)), aad);
}

export function openJson<T>(key: Uint8Array, nonce: string, cipher: string, aad: Uint8Array): T {
  return JSON.parse(bytesToUtf8(open(key, nonce, cipher, aad))) as T;
}

export function newNonce(): string {
  return toBase64Url(randomBytes(16));
}

const FP_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'; // Crockford Base32

/**
 * Отпечаток пары: ПК и телефон обязаны показать одинаковую строку.
 * Если они разошлись — между устройствами кто-то встал.
 */
export function pairFingerprint(pubA: Uint8Array, pubB: Uint8Array): string {
  const [first, second] = compareBytes(pubA, pubB) <= 0 ? [pubA, pubB] : [pubB, pubA];
  const hash = sha256(concatBytes(utf8ToBytes('pcmate-fp/v1'), first, second));
  return formatFingerprint(hash, 6);
}

export function keyFingerprint(pub: Uint8Array, groups = 3): string {
  const hash = sha256(concatBytes(utf8ToBytes('pcmate-key/v1'), pub));
  return formatFingerprint(hash, groups);
}

function formatFingerprint(hash: Uint8Array, groups: number): string {
  const chars = groups * 4;
  let out = '';
  let acc = 0;
  let bits = 0;
  let produced = 0;

  for (const byte of hash) {
    acc = (acc << 8) | byte;
    bits += 8;
    while (bits >= 5 && produced < chars) {
      bits -= 5;
      if (produced > 0 && produced % 4 === 0) out += '-';
      out += FP_ALPHABET[(acc >> bits) & 0x1f];
      produced++;
    }
    if (produced >= chars) break;
  }

  return out;
}

function compareBytes(a: Uint8Array, b: Uint8Array): number {
  const n = Math.min(a.length, b.length);
  for (let i = 0; i < n; i++) {
    if (a[i]! !== b[i]!) return a[i]! - b[i]!;
  }
  return a.length - b.length;
}

export { toBase64Url, fromBase64Url, utf8ToBytes, bytesToUtf8 };
