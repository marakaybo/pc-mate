/**
 * base64url без паддинга — единый формат бинарных значений в протоколе PC MATE.
 * Своя реализация, потому что в Hermes нет ни Buffer, ни гарантированного atob.
 */

const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';

const LOOKUP = (() => {
  const table = new Uint8Array(128).fill(255);
  for (let i = 0; i < ALPHABET.length; i++) table[ALPHABET.charCodeAt(i)] = i;
  // Принимаем и обычный base64 — на случай данных из чужих реализаций.
  table['+'.charCodeAt(0)] = 62;
  table['/'.charCodeAt(0)] = 63;
  return table;
})();

export function toBase64Url(bytes: Uint8Array): string {
  let out = '';
  let i = 0;

  for (; i + 2 < bytes.length; i += 3) {
    const n = (bytes[i]! << 16) | (bytes[i + 1]! << 8) | bytes[i + 2]!;
    out += ALPHABET[(n >> 18) & 63]! + ALPHABET[(n >> 12) & 63]! + ALPHABET[(n >> 6) & 63]! + ALPHABET[n & 63]!;
  }

  const remaining = bytes.length - i;
  if (remaining === 1) {
    const n = bytes[i]! << 16;
    out += ALPHABET[(n >> 18) & 63]! + ALPHABET[(n >> 12) & 63]!;
  } else if (remaining === 2) {
    const n = (bytes[i]! << 16) | (bytes[i + 1]! << 8);
    out += ALPHABET[(n >> 18) & 63]! + ALPHABET[(n >> 12) & 63]! + ALPHABET[(n >> 6) & 63]!;
  }

  return out;
}

export function fromBase64Url(value: string): Uint8Array {
  const clean = value.replace(/=+$/, '').trim();
  const length = clean.length;
  if (length % 4 === 1) throw new Error('Некорректная длина base64url-строки.');

  const byteLength = Math.floor((length * 3) / 4);
  const out = new Uint8Array(byteLength);

  let outIndex = 0;
  let buffer = 0;
  let bits = 0;

  for (let i = 0; i < length; i++) {
    const code = clean.charCodeAt(i);
    const value6 = code < 128 ? LOOKUP[code]! : 255;
    if (value6 === 255) throw new Error(`Недопустимый символ base64url: ${clean[i]}`);

    buffer = (buffer << 6) | value6;
    bits += 6;

    if (bits >= 8) {
      bits -= 8;
      out[outIndex++] = (buffer >> bits) & 0xff;
    }
  }

  return out.subarray(0, outIndex);
}

const encoder = new TextEncoder();
const decoder = new TextDecoder();

export function utf8ToBytes(text: string): Uint8Array {
  return encoder.encode(text);
}

export function bytesToUtf8(bytes: Uint8Array): string {
  return decoder.decode(bytes);
}
