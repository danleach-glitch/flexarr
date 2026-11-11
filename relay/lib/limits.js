// What the relay's functions share for their rate limits: who's asking, kept counts that can't grow without end, and
// counts that reach the Upstash store only when they need to.

const net = require('node:net');

const MAX_KEYS = 10_000;
const KV_TIMEOUT_MS = 1500;

/**
 * Who a request is from, for counting: the first X-Forwarded-For address (Vercel's own), else the socket's. An IPv6
 * address counts as its /64, the block one home or one machine is given, so walking through it is one address.
 */
function clientAddress(req) {
  const ip = String(req.headers?.['x-forwarded-for'] || req.socket?.remoteAddress || '').split(',')[0].trim();
  return addressKey(ip);
}

function addressKey(raw) {
  let ip = String(raw || '').trim().replace(/^\[(.*)\]$/, '$1').replace(/%.*$/, '');
  const mapped = ip.match(/^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/i);
  if (mapped) return mapped[1];
  if (!net.isIPv6(ip)) return ip;
  // A dotted IPv4 tail (::1.2.3.4) is the last two groups.
  const dotted = ip.match(/^(.*:)(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/);
  if (dotted) {
    const [, head, a, b, c, d] = dotted.map((x, i) => (i > 1 ? Number(x) : x));
    ip = `${head}${((a << 8) | b).toString(16)}:${((c << 8) | d).toString(16)}`;
  }
  const [head, tail] = ip.split('::');
  const front = head ? head.split(':') : [];
  const back = tail ? tail.split(':') : [];
  const groups = tail === undefined ? front : [...front, ...Array(8 - front.length - back.length).fill('0'), ...back];
  return `${groups.slice(0, 4).map((g) => parseInt(g, 16).toString(16)).join(':')}::/64`;
}

/** Sets key in map, newest last; when the map is full, the oldest tenth goes, never everyone's counts at once. */
function remember(map, key, value, max = MAX_KEYS) {
  map.delete(key);
  if (map.size >= max) {
    let drop = Math.ceil(max / 10);
    for (const old of map.keys()) {
      map.delete(old);
      if (--drop <= 0) break;
    }
  }
  map.set(key, value);
}

/** This instance's count for key in a window of `seconds` (counting this one). */
function localCount(map, key, seconds, now = Date.now(), max = MAX_KEYS) {
  const window = Math.floor(now / 1000 / seconds);
  const entry = map.get(key);
  if (!entry || entry.window !== window) {
    remember(map, key, { window, count: 1, sent: 0 }, max);
    return 1;
  }
  return ++entry.count;
}

/**
 * The count for `id` (already hashed) in its window, across instances when there's a store. The first time this
 * instance sees an id in a window costs the store nothing: most ids come once (a household's handful a minute, or a
 * flood's random tokens, which never come again), and only one that comes back is worth counting everywhere. When it
 * does, what this instance has counted so far goes to the store in one INCRBY, so a real repeat loses nothing but a
 * single uncounted hit on each other instance. A store that's down or slow falls back to this instance's own count.
 */
async function sharedCount(map, id, storeKey, seconds, now = Date.now()) {
  const count = localCount(map, id, seconds, now);
  const entry = map.get(id);
  const { KV_REST_API_URL, KV_REST_API_TOKEN } = process.env;
  if (count === 1 || !KV_REST_API_URL || !KV_REST_API_TOKEN) return count;
  // Taken before the store answers, so requests here at the same time don't send the same hits twice.
  const by = entry.count - entry.sent;
  entry.sent = entry.count;
  const key = `${storeKey}:${entry.window}`;
  try {
    const response = await fetch(`${KV_REST_API_URL}/pipeline`, {
      method: 'POST',
      headers: { authorization: `Bearer ${KV_REST_API_TOKEN}`, 'content-type': 'application/json' },
      body: JSON.stringify([['INCRBY', key, String(by)], ['EXPIRE', key, String(seconds * 2)]]),
      signal: AbortSignal.timeout(KV_TIMEOUT_MS),
    });
    const [incr] = await response.json();
    if (response.ok && typeof incr?.result === 'number') return Math.max(incr.result, entry.count);
  } catch {
    // Fall back to this instance's own count.
  }
  return entry.count;
}

module.exports = { clientAddress, addressKey, remember, localCount, sharedCount, MAX_KEYS };
