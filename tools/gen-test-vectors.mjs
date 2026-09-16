/**
 * Генерирует тестовые векторы протокола PC MATE.
 *
 * Векторы считает node:crypto — реализация, независимая и от .NET, и от @noble.
 * Если C#-агент и мобильное приложение сходятся на этих числах, значит
 * телефон и компьютер точно поймут друг друга.
 *
 *   node tools/gen-test-vectors.mjs
 */
import { createHash, createPrivateKey, createPublicKey, diffieHellman, hkdfSync, createCipheriv } from 'node:crypto';
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = dirname(dirname(fileURLToPath(import.meta.url)));

// Фиксированные «сиды» — векторы должны быть воспроизводимыми.
const PC_PRIVATE = Buffer.from('77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a', 'hex');
const PHONE_PRIVATE = Buffer.from('5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb', 'hex');

const PKCS8_PREFIX = Buffer.from('302e020100300506032b656e042204 20'.replace(/\s/g, ''), 'hex');
const SPKI_PREFIX = Buffer.from('302a300506032b656e032100', 'hex');

function privateKeyFromRaw(raw) {
  return createPrivateKey({
    key: Buffer.concat([PKCS8_PREFIX, raw]),
    format: 'der',
    type: 'pkcs8'
  });
}

function rawPublicKey(privateKey) {
  const spki = createPublicKey(privateKey).export({ format: 'der', type: 'spki' });
  return spki.subarray(SPKI_PREFIX.length);
}

function publicKeyFromRaw(raw) {
  return createPublicKey({
    key: Buffer.concat([SPKI_PREFIX, raw]),
    format: 'der',
    type: 'spki'
  });
}

function b64url(buffer) {
  return Buffer.from(buffer).toString('base64').replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}

function hkdf(sharedSecret, salt, info, length = 32) {
  return Buffer.from(hkdfSync('sha256', sharedSecret, salt, Buffer.from(info, 'utf8'), length));
}

function fingerprint(pubA, pubB) {
  const [first, second] = Buffer.compare(pubA, pubB) <= 0 ? [pubA, pubB] : [pubB, pubA];
  const hash = createHash('sha256')
    .update(Buffer.from('pcmate-fp/v1', 'utf8'))
    .update(first)
    .update(second)
    .digest();
  return formatFingerprint(hash, 6);
}

function keyFingerprint(pub, groups = 3) {
  const hash = createHash('sha256')
    .update(Buffer.from('pcmate-key/v1', 'utf8'))
    .update(pub)
    .digest();
  return formatFingerprint(hash, groups);
}

function formatFingerprint(hash, groups) {
  const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
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
      out += alphabet[(acc >> bits) & 0x1f];
      produced++;
    }
    if (produced >= chars) break;
  }
  return out;
}

const pcPriv = privateKeyFromRaw(PC_PRIVATE);
const phonePriv = privateKeyFromRaw(PHONE_PRIVATE);
const pcPub = rawPublicKey(pcPriv);
const phonePub = rawPublicKey(phonePriv);

const sharedFromPhone = diffieHellman({ privateKey: phonePriv, publicKey: publicKeyFromRaw(pcPub) });
const sharedFromPc = diffieHellman({ privateKey: pcPriv, publicKey: publicKeyFromRaw(phonePub) });

if (!sharedFromPhone.equals(sharedFromPc)) throw new Error('X25519: стороны получили разный общий секрет');

const pairId = '6f1c2a54-1f0e-4b07-9a60-0a2b7f5c1d33';
const salt = Buffer.from(pairId, 'utf8');
const phoneToPc = hkdf(sharedFromPhone, salt, 'pcmate/v1/phone->pc');
const pcToPhone = hkdf(sharedFromPhone, salt, 'pcmate/v1/pc->phone');

// Конверт с фиксированными полями: nonce задан, поэтому шифротекст детерминирован.
const envelope = {
  v: 1,
  id: '2f8a1c44-0b6e-4c1f-9d33-77e0a5b41c92',
  type: 'cmd',
  from: 'c0ffee00-1111-2222-3333-444455556666',
  to: 'deadbeef-aaaa-bbbb-cccc-ddddeeeeffff',
  ts: 1757894400
};

const payload = {
  cmd: 'power.shutdown',
  args: { delaySec: 30, force: false },
  nonce: 'QUJDREVGR0hJSktMTU5PUA',
  ts: envelope.ts
};

const aad = Buffer.from(`${envelope.v}|${envelope.id}|${envelope.from}|${envelope.to}|${envelope.ts}`, 'utf8');
const gcmNonce = Buffer.from('000102030405060708090a0b', 'hex');
const plaintext = Buffer.from(JSON.stringify(payload), 'utf8');

const cipher = createCipheriv('aes-256-gcm', phoneToPc, gcmNonce);
cipher.setAAD(aad);
const encrypted = Buffer.concat([cipher.update(plaintext), cipher.final()]);
const tag = cipher.getAuthTag();

const vectors = {
  note: 'Сгенерировано tools/gen-test-vectors.mjs (node:crypto). Проверяется тестами агента и мобильного приложения.',
  protocolVersion: 1,
  x25519: {
    pcPrivate: b64url(PC_PRIVATE),
    pcPublic: b64url(pcPub),
    phonePrivate: b64url(PHONE_PRIVATE),
    phonePublic: b64url(phonePub),
    sharedSecret: b64url(sharedFromPhone)
  },
  hkdf: {
    pairId,
    infoPhoneToPc: 'pcmate/v1/phone->pc',
    infoPcToPhone: 'pcmate/v1/pc->phone',
    keyPhoneToPc: b64url(phoneToPc),
    keyPcToPhone: b64url(pcToPhone)
  },
  fingerprints: {
    pair: fingerprint(pcPub, phonePub),
    pcKey: keyFingerprint(pcPub)
  },
  aesGcm: {
    envelope,
    payload,
    aad: aad.toString('utf8'),
    nonce: b64url(gcmNonce),
    cipher: b64url(Buffer.concat([encrypted, tag])),
    plaintextJson: plaintext.toString('utf8')
  },
  magicPacket: {
    mac: 'AA:BB:CC:DD:EE:FF',
    lengthBytes: 102,
    firstBytesHex: 'ffffffffffffaabbccddeeff'
  }
};

const outputs = [
  join(ROOT, 'docs', 'test-vectors.json'),
  join(ROOT, 'agent', 'tests', 'PcMate.Core.Tests', 'test-vectors.json'),
  join(ROOT, 'mobile', 'test', 'test-vectors.json')
];

for (const path of outputs) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify(vectors, null, 2) + '\n', 'utf8');
  console.log('written ->', path);
}
