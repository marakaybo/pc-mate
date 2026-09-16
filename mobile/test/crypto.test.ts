import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  buildAad,
  deriveSessionKeys,
  fromBase64Url,
  keyFingerprint,
  open,
  pairFingerprint,
  publicKeyFrom,
  seal,
  toBase64Url,
  utf8ToBytes,
  bytesToUtf8
} from '../src/lib/crypto';
import { buildMagicPacket, parseMac, sameSubnet24, broadcastFor } from '../src/lib/magic-packet';
import { parsePairingUri, ReplayGuard, unixNow } from '../src/lib/protocol';

/**
 * Совместимость телефона с агентом. Векторы посчитаны node:crypto
 * (tools/gen-test-vectors.mjs); те же самые проверяет C#-тест InteropTests.
 */
const vectors = JSON.parse(
  readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'test-vectors.json'), 'utf8')
) as {
  x25519: Record<string, string>;
  hkdf: Record<string, string>;
  fingerprints: Record<string, string>;
  aesGcm: {
    envelope: { v: number; id: string; from: string; to: string; ts: number };
    aad: string;
    nonce: string;
    cipher: string;
    plaintextJson: string;
  };
  magicPacket: { mac: string; lengthBytes: number; firstBytesHex: string };
};

describe('Совместимость с эталонными векторами', () => {
  test('X25519 даёт те же публичные ключи и общий секрет', () => {
    const pcPriv = fromBase64Url(vectors.x25519.pcPrivate!);
    const phonePriv = fromBase64Url(vectors.x25519.phonePrivate!);

    assert.equal(toBase64Url(publicKeyFrom(pcPriv)), vectors.x25519.pcPublic);
    assert.equal(toBase64Url(publicKeyFrom(phonePriv)), vectors.x25519.phonePublic);
  });

  test('HKDF выводит те же направленные ключи', () => {
    const phonePriv = fromBase64Url(vectors.x25519.phonePrivate!);
    const pcPub = fromBase64Url(vectors.x25519.pcPublic!);

    const keys = deriveSessionKeys(phonePriv, pcPub, vectors.hkdf.pairId!);

    assert.equal(toBase64Url(keys.sendKey), vectors.hkdf.keyPhoneToPc);
    assert.equal(toBase64Url(keys.receiveKey), vectors.hkdf.keyPcToPhone);
  });

  test('отпечатки совпадают с эталонными', () => {
    const pcPub = fromBase64Url(vectors.x25519.pcPublic!);
    const phonePub = fromBase64Url(vectors.x25519.phonePublic!);

    assert.equal(pairFingerprint(pcPub, phonePub), vectors.fingerprints.pair);
    assert.equal(pairFingerprint(phonePub, pcPub), vectors.fingerprints.pair, 'порядок ключей не важен');
    assert.equal(keyFingerprint(pcPub), vectors.fingerprints.pcKey);
  });

  test('AES-GCM расшифровывает эталонный шифротекст', () => {
    const { envelope, aad, nonce, cipher, plaintextJson } = vectors.aesGcm;
    const key = fromBase64Url(vectors.hkdf.keyPhoneToPc!);

    const aadBytes = buildAad(envelope.v, envelope.id, envelope.from, envelope.to, envelope.ts);
    assert.equal(bytesToUtf8(aadBytes), aad);

    const plain = open(key, nonce, cipher, aadBytes);
    assert.equal(bytesToUtf8(plain), plaintextJson);

    const payload = JSON.parse(bytesToUtf8(plain)) as { cmd: string; args: { delaySec: number } };
    assert.equal(payload.cmd, 'power.shutdown');
    assert.equal(payload.args.delaySec, 30);
  });

  test('подменённые маршрутные поля ломают расшифровку', () => {
    const { envelope, nonce, cipher } = vectors.aesGcm;
    const key = fromBase64Url(vectors.hkdf.keyPhoneToPc!);
    const forged = buildAad(envelope.v, envelope.id, envelope.from, 'другой-пк', envelope.ts);

    assert.throws(() => open(key, nonce, cipher, forged));
  });

  test('магический пакет совпадает с эталоном', () => {
    const packet = buildMagicPacket(vectors.magicPacket.mac);
    assert.equal(packet.length, vectors.magicPacket.lengthBytes);

    const prefix = Array.from(packet.slice(0, 12), (b) => b.toString(16).padStart(2, '0')).join('');
    assert.equal(prefix, vectors.magicPacket.firstBytesHex);
  });
});

describe('Криптография телефона', () => {
  test('шифрование и расшифровка ходят по кругу', () => {
    const phonePriv = fromBase64Url(vectors.x25519.phonePrivate!);
    const pcPub = fromBase64Url(vectors.x25519.pcPublic!);
    const keys = deriveSessionKeys(phonePriv, pcPub, 'pair-1');

    const aad = buildAad(1, 'id', 'phone', 'pc', unixNow());
    const message = utf8ToBytes('Привет, компьютер! 🖥');

    const { nonce, cipher } = seal(keys.sendKey, message, aad);
    assert.deepEqual(open(keys.sendKey, nonce, cipher, aad), message);
  });

  test('разные pairId дают разные ключи', () => {
    const phonePriv = fromBase64Url(vectors.x25519.phonePrivate!);
    const pcPub = fromBase64Url(vectors.x25519.pcPublic!);

    const first = deriveSessionKeys(phonePriv, pcPub, 'pair-1');
    const second = deriveSessionKeys(phonePriv, pcPub, 'pair-2');

    assert.notDeepEqual(first.sendKey, second.sendKey);
  });

  test('base64url ходит по кругу без паддинга', () => {
    for (let length = 1; length <= 40; length++) {
      const bytes = new Uint8Array(length);
      for (let i = 0; i < length; i++) bytes[i] = (i * 37 + length) % 256;

      const encoded = toBase64Url(bytes);
      assert.ok(!encoded.includes('='));
      assert.ok(!encoded.includes('+'));
      assert.ok(!encoded.includes('/'));
      assert.deepEqual(fromBase64Url(encoded), bytes);
    }
  });
});

describe('Защита от повтора', () => {
  test('второй раз тот же nonce не проходит', () => {
    const guard = new ReplayGuard();
    assert.equal(guard.check('abc', unixNow()), 'ok');
    assert.equal(guard.check('abc', unixNow()), 'duplicate');
  });

  test('старое и будущее время отвергаются', () => {
    const guard = new ReplayGuard();
    assert.equal(guard.check('n1', unixNow() - 600), 'stale');
    assert.equal(guard.check('n2', unixNow() + 600), 'stale');
  });
});

describe('Разбор QR-кода сопряжения', () => {
  test('корректный код читается', () => {
    const offer = {
      v: 1,
      pcId: 'pc-1',
      pcName: 'DESKTOP',
      pub: vectors.x25519.pcPublic,
      token: 'token',
      exp: unixNow() + 300,
      lan: ['192.168.1.50:8760']
    };

    const uri = 'pcmate://pair?d=' + toBase64Url(utf8ToBytes(JSON.stringify(offer)));
    const parsed = parsePairingUri(uri);

    assert.ok(parsed);
    assert.equal(parsed!.pcId, 'pc-1');
    assert.deepEqual(parsed!.lan, ['192.168.1.50:8760']);
  });

  test('мусор отбрасывается', () => {
    assert.equal(parsePairingUri('просто текст'), null);
    assert.equal(parsePairingUri('pcmate://pair?d=!!!!'), null);
  });
});

describe('Wake-on-LAN', () => {
  test('MAC разбирается в любом формате', () => {
    const expected = new Uint8Array([0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff]);
    assert.deepEqual(parseMac('AA:BB:CC:DD:EE:FF'), expected);
    assert.deepEqual(parseMac('aa-bb-cc-dd-ee-ff'), expected);
    assert.deepEqual(parseMac('AABBCCDDEEFF'), expected);
    assert.throws(() => parseMac('AA:BB'));
  });

  test('подсеть и широковещательный адрес считаются верно', () => {
    assert.equal(sameSubnet24('192.168.1.10', '192.168.1.50'), true);
    assert.equal(sameSubnet24('192.168.1.10', '10.0.0.5'), false);
    assert.equal(broadcastFor('192.168.1.50'), '192.168.1.255');
  });
});
