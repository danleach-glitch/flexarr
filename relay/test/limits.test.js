// Run with: node --test relay/test/*.test.js
const test = require('node:test');
const assert = require('node:assert');
const { addressKey, clientAddress, remember, sharedCount } = require('../lib/limits.js');
const push = require('../api/push.js');
const trakt = require('../api/trakt.js');

// A stand-in Upstash store: answers INCRBY as Redis would and keeps every pipeline it's sent.
function withStore(work) {
  const counts = new Map();
  const sent = [];
  const realFetch = global.fetch;
  process.env.KV_REST_API_URL = 'https://store.test';
  process.env.KV_REST_API_TOKEN = 'x';
  global.fetch = async (url, init) => {
    const commands = JSON.parse(init.body);
    sent.push(commands);
    const results = commands.map(([op, key, by]) => {
      if (op !== 'INCRBY') return { result: 1 };
      counts.set(key, (counts.get(key) || 0) + Number(by));
      return { result: counts.get(key) };
    });
    return new Response(JSON.stringify(results), { status: 200 });
  };
  return Promise.resolve(work(sent, counts)).finally(() => {
    global.fetch = realFetch;
    delete process.env.KV_REST_API_URL;
    delete process.env.KV_REST_API_TOKEN;
  });
}

test('an IPv6 address counts as its /64; IPv4 as itself', () => {
  assert.equal(addressKey('203.0.113.9'), '203.0.113.9');
  assert.equal(addressKey('::ffff:203.0.113.9'), '203.0.113.9');
  const home = addressKey('2001:db8:1:2::1');
  assert.equal(home, '2001:db8:1:2::/64');
  for (const same of ['2001:db8:1:2:aaaa:bbbb:cccc:dddd', '2001:0db8:0001:0002::ffff', '[2001:db8:1:2::9]', '2001:DB8:1:2::1%en0']) {
    assert.equal(addressKey(same), home, same);
  }
  assert.notEqual(addressKey('2001:db8:1:3::1'), home);
  assert.equal(addressKey('::1'), '0:0:0:0::/64');
  assert.equal(addressKey('2001:db8::'), '2001:db8:0:0::/64');
  assert.equal(addressKey('64:ff9b::203.0.113.9'), '64:ff9b:0:0::/64');
  assert.equal(addressKey('not an address'), 'not an address');
  assert.equal(clientAddress({ headers: { 'x-forwarded-for': '2001:db8:1:2::7, 10.0.0.1' } }), home);
});

test('a full map drops its oldest tenth, never everyone at once', () => {
  const map = new Map();
  for (let i = 0; i < 100; i++) remember(map, `k${i}`, i, 100);
  remember(map, 'k0', 'again', 100); // touched: now the newest
  remember(map, 'new', 1, 100);
  assert.equal(map.size, 91);
  assert.equal(map.has('k1'), false);
  assert.equal(map.has('k10'), false);
  assert.equal(map.has('k11'), true);
  assert.equal(map.get('k0'), 'again');
  assert.equal(map.has('new'), true);
});

test('a flood of made-up push tokens never reaches the store', () =>
  withStore(async (sent) => {
    const now = Date.UTC(2026, 10, 11, 12, 0, 30);
    for (let i = 0; i < 500; i++) {
      assert.equal(await push._tokenLimited(i.toString(16).padStart(64, '0'), now), false);
    }
    assert.equal(sent.length, 0);
  }));

test('a token that comes back is counted in the store, its first push included', () =>
  withStore(async (sent, counts) => {
    const token = '9f'.repeat(32);
    const now = Date.UTC(2026, 10, 11, 12, 1, 30);
    assert.equal(await push._tokenLimited(token, now), false);
    assert.equal(sent.length, 0);
    assert.equal(await push._tokenLimited(token, now), false);
    assert.deepEqual(sent[0][0].slice(0, 1).concat(sent[0][0][2]), ['INCRBY', '2']);
    assert.equal(sent[0][1][2], '120');
    assert.equal(await push._tokenLimited(token, now), false);
    assert.equal(sent[1][0][2], '1');
    // Other instances' pushes to the same phone count here too.
    const [key] = counts.keys();
    assert.match(key, /^push:[0-9a-f]{32}:\d+$/);
    counts.set(key, 12);
    assert.equal(await push._tokenLimited(token, now), true);
  }));

test('the store answering less than this instance has counted never lets more through', () =>
  withStore(async (sent, counts) => {
    const map = new Map();
    const now = Date.UTC(2026, 10, 11, 12, 2, 30);
    for (let i = 0; i < 3; i++) await sharedCount(map, 'id', 'test:id', 60, now);
    counts.clear(); // the store lost its keys
    assert.equal(await sharedCount(map, 'id', 'test:id', 60, now), 4);
  }));

test('a store that is down falls back to this instance\'s count, without counting a hit twice later', async () => {
  process.env.KV_REST_API_URL = 'http://127.0.0.1:9';
  process.env.KV_REST_API_TOKEN = 'x';
  try {
    const map = new Map();
    const now = Date.UTC(2026, 10, 11, 12, 3, 30);
    for (let i = 1; i <= 3; i++) assert.equal(await sharedCount(map, 'id', 'test:id', 60, now), i);
  } finally {
    delete process.env.KV_REST_API_URL;
    delete process.env.KV_REST_API_TOKEN;
  }
});

test('made-up Trakt codes and tokens never reach the store either', () =>
  withStore(async (sent) => {
    const now = Date.UTC(2026, 10, 11, 12, 4, 30);
    for (let i = 0; i < 50; i++) assert.equal(await trakt._count('revoke', `made-up-${i}`.padEnd(32, 'x'), now), false);
    assert.equal(sent.length, 0);
    for (let i = 0; i < 10; i++) await trakt._count('revoke', 'r'.repeat(32), now);
    assert.equal(await trakt._count('revoke', 'r'.repeat(32), now), true);
    assert.equal(sent.length, 10);
  }));

test('walking through an IPv6 /64 is one address to the push limit', () => {
  const now = Date.UTC(2026, 10, 11, 12, 5, 30);
  for (let i = 0; i < 600; i++) assert.equal(push._limited(addressKey(`2001:db8:9:9::${i.toString(16)}`), now), false);
  assert.equal(push._limited(addressKey('2001:db8:9:9:ffff::1'), now), true);
  assert.equal(push._limited(addressKey('2001:db8:9:a::1'), now), false);
});
