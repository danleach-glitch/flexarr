// Run with: node --test relay/test/*.test.js
const test = require('node:test');
const assert = require('node:assert');
const appLinks = require('../api/app-links.js');

function call(url, method = 'GET') {
  return new Promise((resolve) => {
    const res = { statusCode: 0, setHeader() {}, end(text) { resolve({ status: this.statusCode, body: JSON.parse(text) }); } };
    appLinks({ method, url }, res);
  });
}

test('the Apple file names the app from the notification settings, for /join only', async () => {
  process.env.APNS_TEAM_ID = 'TEAM123456';
  process.env.APNS_TOPIC = 'com.example.flexarr.ios';
  const { status, body } = await call('/api/app-links?platform=apple');
  assert.equal(status, 200);
  assert.deepEqual(body.applinks.details[0].appIDs, ['TEAM123456.com.example.flexarr.ios']);
  assert.deepEqual(body.applinks.details[0].components.map((c) => c['/']), ['/join']);
});

test('the Android file lists only well-formed fingerprints, and none without them', async () => {
  const fingerprint = Array.from({ length: 32 }, () => 'AB').join(':');
  process.env.ANDROID_CERT_SHA256 = `${fingerprint.toLowerCase()}, not-a-fingerprint`;
  const { body } = await call('/api/app-links?platform=android');
  assert.deepEqual(body[0].target.sha256_cert_fingerprints, [fingerprint]);
  assert.equal(body[0].target.package_name, 'com.danleach.flexarr');
  delete process.env.ANDROID_CERT_SHA256;
  assert.deepEqual((await call('/api/app-links?platform=android')).body, []);
});

test('unknown platforms and methods are refused', async () => {
  assert.equal((await call('/api/app-links?platform=windows')).status, 404);
  assert.equal((await call('/api/app-links?platform=apple', 'POST')).status, 405);
});
