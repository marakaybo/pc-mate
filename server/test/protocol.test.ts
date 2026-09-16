import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { parseEnvelope, sysEnvelope, isEncrypted, PROTOCOL_VERSION } from '../src/lib/protocol.js';
import { hashToken, newDeviceToken, tokensMatch, base64url, extractBearer } from '../src/lib/auth.js';

describe('Разбор конвертов', () => {
  const valid = {
    v: PROTOCOL_VERSION,
    id: 'msg-1',
    type: 'cmd',
    from: 'phone-1',
    to: 'pc-1',
    ts: Math.floor(Date.now() / 1000),
    enc: { alg: 'aes-256-gcm', n: 'nonce', c: 'cipher' }
  };

  test('принимает корректный конверт', () => {
    const envelope = parseEnvelope(JSON.stringify(valid));
    assert.ok(envelope);
    assert.equal(envelope?.from, 'phone-1');
    assert.equal(isEncrypted(envelope!), true);
  });

  test('отбрасывает мусор', () => {
    assert.equal(parseEnvelope('не json'), null);
    assert.equal(parseEnvelope('null'), null);
    assert.equal(parseEnvelope('[]'), null);
    assert.equal(parseEnvelope('123'), null);
  });

  test('отбрасывает конверты без обязательных полей', () => {
    for (const field of ['id', 'from', 'to', 'ts', 'type', 'v']) {
      const broken: Record<string, unknown> = { ...valid };
      delete broken[field];
      assert.equal(parseEnvelope(JSON.stringify(broken)), null, `поле ${field} обязательно`);
    }
  });

  test('отбрасывает неизвестный тип и слишком длинный id', () => {
    assert.equal(parseEnvelope(JSON.stringify({ ...valid, type: 'hack' })), null);
    assert.equal(parseEnvelope(JSON.stringify({ ...valid, id: 'x'.repeat(100) })), null);
  });

  test('sys-конверт формируется корректно', () => {
    const envelope = sysEnvelope('ping', { t: 1 }, 'pc-1');
    assert.equal(envelope.type, 'sys');
    assert.equal(envelope.from, 'server');
    assert.equal(envelope.to, 'pc-1');
    assert.equal(isEncrypted(envelope), false);
  });
});

describe('Токены устройств', () => {
  test('токен сходится только сам с собой', () => {
    const token = newDeviceToken();
    const hash = hashToken(token);

    assert.equal(tokensMatch(token, hash), true);
    assert.equal(tokensMatch(newDeviceToken(), hash), false);
    assert.equal(tokensMatch(token, 'не хеш'), false);
  });

  test('токен не содержит символов, ломающих URL', () => {
    for (let i = 0; i < 20; i++) {
      const token = newDeviceToken();
      assert.match(token, /^[A-Za-z0-9_-]+$/);
    }
  });

  test('base64url без паддинга', () => {
    assert.equal(base64url(Buffer.from([251, 255, 190])), '-_--');
    assert.doesNotMatch(base64url(Buffer.from('привет')), /=/);
  });

  test('заголовок Authorization разбирается', () => {
    assert.equal(extractBearer('Bearer abc123'), 'abc123');
    assert.equal(extractBearer('bearer abc123'), 'abc123');
    assert.equal(extractBearer('Basic abc123'), null);
    assert.equal(extractBearer(undefined), null);
  });
});
