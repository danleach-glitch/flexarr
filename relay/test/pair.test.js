// Run with: node --test relay/test/*.test.js
const test = require('node:test');
const assert = require('node:assert');
const crypto = require('node:crypto');
const { Readable } = require('node:stream');
const pair = require('../api/pair.js');

delete process.env.KV_REST_API_URL;
delete process.env.KV_REST_API_TOKEN;

let address = 0;

function call({ method = 'GET', url = '/api/pair', body, contentType = 'application/json', ip } = {}) {
  const req = Readable.from(body === undefined ? [] : [JSON.stringify(body)]);
  req.method = method;
  req.url = url;
  req.headers = { 'content-type': contentType, 'x-forwarded-for': ip || `203.0.113.${++address % 250}` };
  return new Promise((resolve) => {
    const res = {
      statusCode: 0,
      setHeader() {},
      end(text) { resolve({ status: this.statusCode, body: text ? JSON.parse(text) : undefined }); },
    };
    pair(req, res);
  });
}

function publicKey() {
  const ecdh = crypto.createECDH('prime256v1');
  ecdh.generateKeys();
  return ecdh.getPublicKey('base64', 'uncompressed');
}

const secret = 'x'.repeat(43);

test('a TV registers its code, the server leaves an invite, and only the TV collects it, once', async () => {
  pair._memory.clear();
  const key = publicKey();
  assert.equal((await call({ method: 'POST', body: { code: 'ABCDEFGH', publicKey: key, secret } })).status, 201);

  const fetched = await call({ url: '/api/pair?code=ABCDEFGH' });
  assert.equal(fetched.status, 200);
  assert.equal(fetched.body.publicKey, key);

  // Nothing yet.
  assert.equal((await call({ url: `/api/pair?code=ABCDEFGH&secret=${secret}` })).status, 204);

  const invite = { serverKey: publicKey(), sealed: Buffer.from('sealed invite').toString('base64') };
  assert.equal((await call({ method: 'POST', url: '/api/pair?code=ABCDEFGH&action=invite', body: invite })).status, 201);
  // A second invite for the same code is refused.
  assert.equal((await call({ method: 'POST', url: '/api/pair?code=ABCDEFGH&action=invite', body: invite })).status, 409);

  // Someone without the secret can't collect it.
  assert.equal((await call({ url: `/api/pair?code=ABCDEFGH&secret=${'y'.repeat(43)}` })).status, 403);

  const collected = await call({ url: `/api/pair?code=ABCDEFGH&secret=${secret}` });
  assert.equal(collected.status, 200);
  assert.deepEqual(collected.body, invite);

  // Gone once collected.
  assert.equal((await call({ url: `/api/pair?code=ABCDEFGH&secret=${secret}` })).status, 404);
  assert.equal((await call({ url: '/api/pair?code=ABCDEFGH' })).status, 404);
});

test('a code already showing on another TV is refused', async () => {
  pair._memory.clear();
  assert.equal((await call({ method: 'POST', body: { code: 'JKMNPQRS', publicKey: publicKey(), secret } })).status, 201);
  assert.equal((await call({ method: 'POST', body: { code: 'JKMNPQRS', publicKey: publicKey(), secret } })).status, 409);
});

test('codes last ten minutes', async () => {
  pair._memory.clear();
  const now = Date.now();
  await pair._store.add('pair:TUVWXYZ2', JSON.stringify({ publicKey: publicKey(), secretHash: 'ab' }), now - 601_000);
  assert.equal((await call({ url: '/api/pair?code=TUVWXYZ2' })).status, 404);
});

test('an invite left in a code\'s last seconds keeps the code alive until it\'s collected', async () => {
  pair._memory.clear();
  // Registered nine minutes and fifty-five seconds ago: five seconds left.
  await pair._store.add('pair:EEEEFFFF', JSON.stringify({ publicKey: publicKey(), secretHash: crypto.createHash('sha256').update(secret).digest('hex') }), Date.now() - 595_000);
  const invite = { serverKey: publicKey(), sealed: Buffer.from('late invite').toString('base64') };
  assert.equal((await call({ method: 'POST', url: '/api/pair?code=EEEEFFFF&action=invite', body: invite })).status, 201);
  assert.ok(pair._memory.get('pair:EEEEFFFF').until > Date.now() + 590_000);
  const collected = await call({ url: `/api/pair?code=EEEEFFFF&secret=${secret}` });
  assert.equal(collected.status, 200);
});

test('bad codes, keys, secrets and invites are refused', async () => {
  pair._memory.clear();
  assert.equal((await call({ method: 'POST', body: { code: 'O0O0O0O0', publicKey: publicKey(), secret } })).status, 400);
  assert.equal((await call({ method: 'POST', body: { code: 'ABCDEFGH', publicKey: 'AAAA', secret } })).status, 400);
  assert.equal((await call({ method: 'POST', body: { code: 'ABCDEFGH', publicKey: publicKey(), secret: 'short' } })).status, 400);
  assert.equal((await call({ url: '/api/pair?code=abc' })).status, 400);
  await call({ method: 'POST', body: { code: 'ABCDEFGH', publicKey: publicKey(), secret } });
  const big = { serverKey: publicKey(), sealed: 'A'.repeat(5000) };
  assert.equal((await call({ method: 'POST', url: '/api/pair?code=ABCDEFGH&action=invite', body: big })).status, 400);
  assert.equal((await call({ method: 'POST', url: '/api/pair?code=ZZZZZZZZ&action=invite', body: { serverKey: publicKey(), sealed: 'AAAA' } })).status, 404);
});

test('refuses anything but JSON posts, so a web page can\'t post from visitors\' browsers', async () => {
  assert.equal((await call({ method: 'POST', contentType: 'text/plain', body: {} })).status, 415);
  assert.equal((await call({ method: 'DELETE' })).status, 405);
});

test('an address gets 60 requests a minute', async () => {
  pair._hits.clear();
  for (let i = 0; i < 60; i++) await call({ url: '/api/pair?code=ABCDEFGH', ip: '198.51.100.7' });
  assert.equal((await call({ url: '/api/pair?code=ABCDEFGH', ip: '198.51.100.7' })).status, 429);
});
