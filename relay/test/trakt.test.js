// Run with: node --test relay/test/*.test.js
const test = require('node:test');
const assert = require('node:assert');
const { Readable } = require('node:stream');
const trakt = require('../api/trakt.js');

function call({ contentType = 'application/json', body = {}, ip = '203.0.113.20', method = 'POST' } = {}) {
  const req = Readable.from([JSON.stringify(body)]);
  req.method = method;
  req.headers = { 'content-type': contentType, 'x-forwarded-for': ip };
  return new Promise((resolve) => {
    const res = {
      statusCode: 0,
      setHeader() {},
      end(text) { resolve({ status: this.statusCode, body: JSON.parse(text) }); },
    };
    trakt(req, res);
  });
}

function withTrakt(answer, work) {
  const sent = [];
  const realFetch = global.fetch;
  process.env.TRAKT_CLIENT_ID = 'id';
  process.env.TRAKT_CLIENT_SECRET = 'secret';
  global.fetch = async (url, options) => {
    sent.push({ url, body: JSON.parse(options.body) });
    return { status: answer.status, json: async () => answer.body };
  };
  return work(sent).finally(() => {
    global.fetch = realFetch;
    delete process.env.TRAKT_CLIENT_ID;
    delete process.env.TRAKT_CLIENT_SECRET;
  });
}

const code = 'a'.repeat(64);

test('refuses anything but JSON', async () => {
  assert.equal((await call({ contentType: 'text/plain' })).status, 415);
});

test('answers its own refusals with 422, so servers never read them as Trakt signing someone out', () =>
  withTrakt({ status: 200, body: {} }, async () => {
    assert.equal((await call({ body: { grant: 'password' } })).status, 422);
  }));

test('a refresh token can be refreshed only so often', () =>
  withTrakt({ status: 200, body: {} }, async () => {
    const refreshToken = 'q'.repeat(64);
    for (let i = 0; i < 10; i++) assert.equal((await call({ body: { grant: 'refresh_token', refreshToken }, ip: `203.0.113.${100 + i}` })).status, 200);
    assert.equal((await call({ body: { grant: 'refresh_token', refreshToken }, ip: '203.0.113.200' })).status, 429);
  }));

test('offers no client id until the secret is there too', async () => {
  process.env.TRAKT_CLIENT_ID = 'id';
  try {
    assert.equal((await call({ method: 'GET' })).status, 503);
  } finally {
    delete process.env.TRAKT_CLIENT_ID;
  }
});

test('says so when it has no Trakt app', async () => {
  assert.equal((await call({ body: { grant: 'device_token', code } })).status, 503);
});

test('adds the secret to a device-token call and passes Trakt\'s answer back', () =>
  withTrakt({ status: 200, body: { access_token: 'at', refresh_token: 'rt', expires_in: 86400, created_at: 1, extra: 'x' } }, async (sent) => {
    const answer = await call({ body: { grant: 'device_token', code } });
    assert.equal(answer.status, 200);
    assert.deepEqual(answer.body, { access_token: 'at', refresh_token: 'rt', expires_in: 86400, created_at: 1 });
    assert.equal(sent[0].url, 'https://api.trakt.tv/oauth/device/token');
    assert.deepEqual(sent[0].body, { code, client_id: 'id', client_secret: 'secret' });
  }));

test('keeps Trakt\'s status, so a server can tell pending from expired', () =>
  withTrakt({ status: 400, body: {} }, async () => {
    assert.equal((await call({ body: { grant: 'device_token', code } })).status, 400);
  }));

test('refreshes and revokes', () =>
  withTrakt({ status: 200, body: {} }, async (sent) => {
    await call({ body: { grant: 'refresh_token', refreshToken: 'r'.repeat(64) } });
    await call({ body: { grant: 'revoke', token: 't'.repeat(64) } });
    assert.equal(sent[0].url, 'https://api.trakt.tv/oauth/token');
    assert.equal(sent[0].body.grant_type, 'refresh_token');
    assert.equal(sent[0].body.client_secret, 'secret');
    assert.equal(sent[1].url, 'https://api.trakt.tv/oauth/revoke');
  }));

test('passes on only the three grants, with token-shaped values', () => {
  assert.equal(trakt._traktCall({ grant: 'authorization_code', code }, 'i', 's'), null);
  assert.equal(trakt._traktCall({ grant: 'device_token', code: 'short' }, 'i', 's'), null);
  assert.equal(trakt._traktCall({ grant: 'device_token', code: '../../x'.repeat(4) }, 'i', 's'), null);
  assert.equal(trakt._traktCall(null, 'i', 's'), null);
});


test('an address gets at most 120 calls a minute', () => {
  const now = Date.UTC(2026, 9, 31, 12, 0, 30);
  for (let i = 0; i < 120; i++) assert.equal(trakt._limited('198.51.100.1', now), false);
  assert.equal(trakt._limited('198.51.100.1', now), true);
  assert.equal(trakt._limited('198.51.100.1', now + 60_000), false);
});

test('gives out the public client id, never the secret', () =>
  withTrakt({ status: 200, body: {} }, async () => {
    const answer = await call({ method: 'GET' });
    assert.equal(answer.status, 200);
    assert.deepEqual(answer.body, { clientId: 'id' });
  }));
