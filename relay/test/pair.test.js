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
  pair._memory.clear();
  await call({ method: 'POST', body: { code: 'ABCDEFGH', publicKey: publicKey(), secret } });
  for (let i = 0; i < 60; i++) assert.equal((await call({ url: '/api/pair?code=ABCDEFGH', ip: '198.51.100.7' })).status, 200);
  assert.equal((await call({ url: '/api/pair?code=ABCDEFGH', ip: '198.51.100.7' })).status, 429);
});

// --- Budgets: what a flood does all day and a household hardly ever.

function fresh() {
  pair._memory.clear();
  pair._hits.clear();
  pair._spent.clear();
}

/** An eight-character code nobody is showing, different for each n. */
function unknownCode(n) {
  const alphabet = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';
  return `ZZZZZZ${alphabet[Math.floor(n / alphabet.length)]}${alphabet[n % alphabet.length]}`;
}

/** Runs work against a stand-in for the Upstash store's /pipeline: just the commands pair.js sends. */
async function withStore(work, { failing = () => false } = {}) {
  const data = new Map();
  const sent = [];
  const realFetch = global.fetch;
  process.env.KV_REST_API_URL = 'https://store.example';
  process.env.KV_REST_API_TOKEN = 'token';
  global.fetch = async (url, options) => {
    const commands = JSON.parse(options.body);
    if (failing(commands)) throw new Error('store down');
    sent.push(...commands);
    const results = commands.map(([name, key, ...rest]) => {
      if (name === 'GET') return data.has(key) ? data.get(key) : null;
      if (name === 'SET') {
        if (data.has(key)) return null;
        data.set(key, rest[0]);
        return 'OK';
      }
      if (name === 'INCR') {
        data.set(key, (data.get(key) || 0) + 1);
        return data.get(key);
      }
      if (name === 'EXPIRE') return 1;
      if (name === 'DEL') return [key, ...rest].filter((k) => data.delete(k)).length;
      throw new Error(`unexpected command ${name}`);
    });
    return { ok: true, status: 200, json: async () => results.map((result) => ({ result })) };
  };
  try {
    await work({ sent, data });
  } finally {
    global.fetch = realFetch;
    delete process.env.KV_REST_API_URL;
    delete process.env.KV_REST_API_TOKEN;
  }
}

test('an address that keeps asking about codes nobody is showing is refused, and others are not', async () => {
  fresh();
  const ip = '198.51.100.20';
  for (let i = 0; i < 20; i++) assert.equal((await call({ url: `/api/pair?code=${unknownCode(i)}`, ip })).status, 404);
  assert.equal((await call({ url: `/api/pair?code=${unknownCode(20)}`, ip })).status, 429);
  assert.equal((await call({ method: 'POST', url: `/api/pair?code=${unknownCode(21)}&action=invite`, body: {}, ip })).status, 429);
  assert.equal((await call({ url: `/api/pair?code=${unknownCode(22)}`, ip: '198.51.100.21' })).status, 404);

  // Ten minutes on, it may ask again.
  for (const entry of pair._spent.values()) entry.window -= 1;
  assert.equal((await call({ url: `/api/pair?code=${unknownCode(23)}`, ip })).status, 404);
});

test('a TV\'s polls of its own code, and a server\'s lookups of it, use none of that budget', async () => {
  fresh();
  const ip = '198.51.100.22';
  assert.equal((await call({ method: 'POST', body: { code: 'ABCDEFGH', publicKey: publicKey(), secret }, ip })).status, 201);
  for (let i = 0; i < 25; i++) assert.equal((await call({ url: `/api/pair?code=ABCDEFGH&secret=${secret}`, ip })).status, 204);
  for (let i = 0; i < 25; i++) assert.equal((await call({ url: '/api/pair?code=ABCDEFGH', ip })).status, 200);
  assert.equal(pair._spent.has(`unknown:${ip}`), false);
});

test('an address registers only so many codes in ten minutes', async () => {
  fresh();
  const ip = '198.51.100.23';
  for (let i = 0; i < 20; i++) {
    assert.equal((await call({ method: 'POST', body: { code: unknownCode(i), publicKey: publicKey(), secret }, ip })).status, 201);
  }
  assert.equal((await call({ method: 'POST', body: { code: unknownCode(20), publicKey: publicKey(), secret }, ip })).status, 429);
  assert.equal(pair._memory.has(`pair:${unknownCode(20)}`), false);
  // Its codes still work, and so does another address.
  assert.equal((await call({ url: `/api/pair?code=${unknownCode(0)}&secret=${secret}`, ip })).status, 204);
  assert.equal((await call({ method: 'POST', body: { code: unknownCode(21), publicKey: publicKey(), secret }, ip: '198.51.100.24' })).status, 201);
});

test('with a store, the budget is one across instances and a refused address costs the store nothing', () =>
  withStore(async ({ sent }) => {
    fresh();
    const ip = '198.51.100.25';
    for (let i = 0; i < 20; i++) assert.equal((await call({ url: `/api/pair?code=${unknownCode(i)}`, ip })).status, 404);

    // Another instance: nothing in its memory, but the store has the count.
    pair._spent.clear();
    assert.equal((await call({ url: `/api/pair?code=${unknownCode(20)}`, ip })).status, 404);
    sent.length = 0;
    assert.equal((await call({ url: `/api/pair?code=${unknownCode(21)}`, ip })).status, 429);
    assert.equal((await call({ url: `/api/pair?code=${unknownCode(22)}&secret=${secret}`, ip })).status, 429);
    assert.deepEqual(sent, []);

    // Registering is counted the same way, before the code is kept.
    for (let i = 0; i < 20; i++) {
      assert.equal((await call({ method: 'POST', body: { code: unknownCode(100 + i), publicKey: publicKey(), secret }, ip })).status, 201);
    }
    pair._spent.clear();
    sent.length = 0;
    assert.equal((await call({ method: 'POST', body: { code: unknownCode(120), publicKey: publicKey(), secret }, ip })).status, 429);
    assert.equal(sent.some(([name]) => name === 'SET'), false);
  }));

test('with a store, a TV\'s ordinary polls only read: nothing is counted for them', () =>
  withStore(async ({ sent }) => {
    fresh();
    const ip = '198.51.100.26';
    assert.equal((await call({ method: 'POST', body: { code: 'ABCDEFGH', publicKey: publicKey(), secret }, ip })).status, 201);
    sent.length = 0;
    for (let i = 0; i < 5; i++) assert.equal((await call({ url: `/api/pair?code=ABCDEFGH&secret=${secret}`, ip })).status, 204);
    assert.equal((await call({ url: '/api/pair?code=ABCDEFGH', ip })).status, 200);
    assert.deepEqual([...new Set(sent.map(([name]) => name))], ['GET']);
    assert.equal(sent.length, 11);
  }));

test('a store that can\'t count falls back to this instance\'s count, and the answer is the same', () =>
  withStore(async () => {
    fresh();
    const ip = '198.51.100.27';
    for (let i = 0; i < 20; i++) assert.equal((await call({ url: `/api/pair?code=${unknownCode(i)}`, ip })).status, 404);
    assert.equal((await call({ url: `/api/pair?code=${unknownCode(20)}`, ip })).status, 429);
  }, { failing: (commands) => commands.some(([name]) => name === 'INCR') }));
