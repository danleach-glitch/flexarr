// Run with: node --test relay/test/*.test.js
const test = require('node:test');
const assert = require('node:assert');
const { Readable } = require('node:stream');
const push = require('../api/push.js');

function call({ contentType = 'application/json', body = {} } = {}) {
  const req = Readable.from([JSON.stringify(body)]);
  req.method = 'POST';
  req.headers = { 'content-type': contentType, 'x-forwarded-for': '203.0.113.9' };
  return new Promise((resolve) => {
    const res = {
      statusCode: 0,
      setHeader() {},
      end(text) { resolve({ status: this.statusCode, body: JSON.parse(text) }); },
    };
    push(req, res);
  });
}

test('refuses anything but JSON, so a web page can\'t post from visitors\' browsers', async () => {
  const answer = await call({ contentType: 'text/plain' });
  assert.equal(answer.status, 415);
});

test('a phone gets at most 12 pushes a minute', async () => {
  delete process.env.KV_REST_API_URL;
  const token = 'ab'.repeat(32);
  const now = Date.UTC(2026, 9, 28, 12, 0, 30);
  for (let i = 0; i < 12; i++) assert.equal(await push._tokenLimited(token, now), false);
  assert.equal(await push._tokenLimited(token, now), true);
  // Another phone, and the next minute, start afresh.
  assert.equal(await push._tokenLimited('cd'.repeat(32), now), false);
  assert.equal(await push._tokenLimited(token, now + 60_000), false);
});

test('a store that is down lets the send through on this instance\'s own count', async () => {
  process.env.KV_REST_API_URL = 'http://127.0.0.1:9';
  process.env.KV_REST_API_TOKEN = 'x';
  try {
    assert.equal(await push._tokenLimited('ef'.repeat(32)), false);
  } finally {
    delete process.env.KV_REST_API_URL;
    delete process.env.KV_REST_API_TOKEN;
  }
});
