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

// --- Android, through Firebase. Google is stood in for by a fetch that answers as told and keeps what it's sent.

const crypto = require('node:crypto');

const fcmToken = 'dGVzdA:APA91b' + 'x'.repeat(140);
const payload = Buffer.from('sealed').toString('base64');

function withGoogle(answer, work) {
  const { privateKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  process.env.FCM_SERVICE_ACCOUNT = JSON.stringify({
    project_id: 'flexarr-test',
    client_email: 'relay@flexarr-test.iam.gserviceaccount.com',
    private_key: privateKey.export({ type: 'pkcs8', format: 'pem' }),
  });
  const sent = [];
  const realFetch = global.fetch;
  global.fetch = async (url, init) => {
    sent.push({ url: String(url), init });
    if (String(url).startsWith('https://oauth2.googleapis.com/')) {
      return new Response(JSON.stringify({ access_token: 'ya29.test', expires_in: 3599 }), { status: 200 });
    }
    const [status, body] = answer;
    return new Response(JSON.stringify(body), { status });
  };
  push._forget();
  return work(sent).finally(() => {
    global.fetch = realFetch;
    delete process.env.FCM_SERVICE_ACCOUNT;
  });
}

const fcmError = (status, code, errorCode) => [code, {
  error: { code, status, details: errorCode ? [{ '@type': 'type.googleapis.com/google.firebase.fcm.v1.FcmError', errorCode }] : [] },
}];

test('an Android phone\'s push goes to Firebase as a data message, sealed as it came', async () => {
  await withGoogle([200, { name: 'projects/flexarr-test/messages/1' }], async (sent) => {
    const answer = await call({ body: { token: fcmToken, environment: 'production', payload, collapseId: 'film:1', platform: 'android' } });
    assert.equal(answer.status, 200);
    const [auth, message] = sent;
    assert.match(new URLSearchParams(auth.init.body.toString()).get('assertion'), /^[\w-]+\.[\w-]+\.[\w-]+$/);
    assert.equal(message.url, 'https://fcm.googleapis.com/v1/projects/flexarr-test/messages:send');
    assert.equal(message.init.headers.authorization, 'Bearer ya29.test');
    assert.deepEqual(JSON.parse(message.init.body), {
      message: { token: fcmToken, data: { e: payload }, android: { priority: 'HIGH', collapse_key: 'film:1' } },
    });
  });
});

test('Google\'s refusals come back in the words servers know from Apple', async () => {
  const cases = [
    [fcmError('NOT_FOUND', 404, 'UNREGISTERED'), 410, 'Unregistered'],
    [fcmError('INVALID_ARGUMENT', 400, 'INVALID_ARGUMENT'), 400, 'BadDeviceToken'],
    [fcmError('PERMISSION_DENIED', 403, 'SENDER_ID_MISMATCH'), 400, 'BadDeviceToken'],
    [fcmError('RESOURCE_EXHAUSTED', 429, 'QUOTA_EXCEEDED'), 429, 'TooManyRequests'],
    [fcmError('UNAVAILABLE', 503, 'UNAVAILABLE'), 502, 'UNAVAILABLE'],
  ];
  for (const [google, status, reason] of cases) {
    await withGoogle(google, async () => {
      const token = crypto.randomBytes(80).toString('base64url');
      const answer = await call({ body: { token, environment: 'production', payload, platform: 'android' } });
      assert.equal(answer.status, status, reason);
      assert.equal(answer.body.reason, reason);
    });
  }
});

test('Android tokens are checked as Firebase\'s, Apple ones as hex, and nothing else is a platform', async () => {
  await withGoogle([200, {}], async (sent) => {
    assert.equal((await call({ body: { token: 'ab'.repeat(32), environment: 'production', payload, platform: 'android' } })).status, 200);
    assert.equal((await call({ body: { token: 'not a token!', environment: 'production', payload, platform: 'android' } })).body.reason, 'BadToken');
    assert.equal((await call({ body: { token: fcmToken, environment: 'production', payload } })).body.reason, 'BadToken');
    assert.equal((await call({ body: { token: fcmToken, environment: 'production', payload, platform: 'windows' } })).body.reason, 'BadPlatform');
    assert.equal(sent.filter((s) => s.url.includes('messages:send')).length, 1);
  });
});

test('without a service account, Android pushes say so', async () => {
  delete process.env.FCM_SERVICE_ACCOUNT;
  const answer = await call({ body: { token: fcmToken.replace('x', 'y'), environment: 'production', payload, platform: 'android' } });
  assert.equal(answer.status, 503);
  assert.equal(answer.body.reason, 'NotConfigured');
});

// --- Live Activities: times only.

test('a Live Activity update carries times and nothing else', async () => {
  const now = Math.floor(Date.now() / 1000);
  const state = { paused: false, position: 120, at: now, duration: 3000, rate: 1 };
  assert.deepEqual(push._activityState(state, now), state);
  // A title, text where a number goes, or a time far off: refused.
  assert.equal(push._activityState({ ...state, title: 'Heat' }, now), null);
  assert.equal(push._activityState({ ...state, position: '120' }, now), null);
  assert.equal(push._activityState({ ...state, at: now + 3 * 86400 }, now), null);
  assert.equal(push._activityState({ ...state, rate: 40 }, now), null);
});

test('a Live Activity request with a title in it is refused before reaching Apple', async () => {
  const now = Math.floor(Date.now() / 1000);
  const answer = await call({ body: { kind: 'liveactivity', token: 'ab'.repeat(32), environment: 'sandbox', event: 'update',
    state: { paused: false, position: 1, at: now, duration: 10, rate: 1, title: 'Heat' } } });
  assert.equal(answer.status, 400);
  assert.equal(answer.body.reason, 'BadState');
  const badEvent = await call({ body: { kind: 'liveactivity', token: 'ab'.repeat(32), environment: 'sandbox', event: 'start',
    state: { paused: false, position: 1, at: now, duration: 10, rate: 1 } } });
  assert.equal(badEvent.body.reason, 'BadEvent');
});
