// Trakt's token calls for Flexarr servers. Everything else a server says to Trakt needs only Flexarr's public client
// id and the person's own token, so the servers talk to Trakt directly. These three calls also need the client secret,
// which can't ship in a release, so a server sends them here and we add it. Nothing is stored or logged but the result.
// See docs/watchlist-trakt.md ("Trakt specifics").
//
// Environment: TRAKT_CLIENT_ID and TRAKT_CLIENT_SECRET, Flexarr's Trakt app.
//
// GET                                         → { clientId }: the app's public id, for every other call
// POST { grant: "device_token", code }        → Trakt's /oauth/device/token, answered as Trakt answers it
// POST { grant: "refresh_token", refreshToken } → /oauth/token (refresh)
// POST { grant: "revoke", token }              → /oauth/revoke

const crypto = require('node:crypto');

const TRAKT = 'https://api.trakt.tv';
const TIMEOUT_MS = 10_000;
// A connect polls every 5 seconds for up to 10 minutes; a household refreshes once a day per profile. Generous, as a
// few homes may share an address.
const RATE_LIMIT = 120; // requests per minute per IP
const TOKEN_SHAPE = /^[A-Za-z0-9_-]{16,256}$/;

// --- Limits. Anyone can reach this, and it holds Flexarr's Trakt secret, so each kind of call has a ceiling: per
// address, per device code (a connect polls every 5 s for 10 minutes) and per refresh or revoked token (a server
// refreshes once a day). Counted across every instance in the Upstash store when there is one (as push.js does);
// otherwise each warm instance counts on its own. A store that's down falls back to this instance's count.

const LIMITS = {
  ip: { max: RATE_LIMIT, seconds: 60 },
  device_token: { max: 200, seconds: 3600 },
  refresh_token: { max: 10, seconds: 3600 },
  revoke: { max: 10, seconds: 3600 },
};
const KV_TIMEOUT_MS = 1500;
const hits = new Map();

function localCount(key, seconds, now) {
  const window = Math.floor(now / 1000 / seconds);
  const entry = hits.get(key);
  if (!entry || entry.window !== window) {
    // Oldest first: a flood of new keys drops the stalest, never everyone's counts at once.
    if (hits.size >= 10_000) for (const old of [...hits.keys()].slice(0, 1000)) hits.delete(old);
    hits.set(key, { window, count: 1 });
    return 1;
  }
  return ++entry.count;
}

async function count(kind, value, now = Date.now()) {
  const { max, seconds } = LIMITS[kind];
  const key = `trakt:${kind}:${crypto.createHash('sha256').update(String(value)).digest('hex').slice(0, 32)}:${Math.floor(now / 1000 / seconds)}`;
  const { KV_REST_API_URL, KV_REST_API_TOKEN } = process.env;
  if (KV_REST_API_URL && KV_REST_API_TOKEN) {
    try {
      const response = await fetch(`${KV_REST_API_URL}/pipeline`, {
        method: 'POST',
        headers: { authorization: `Bearer ${KV_REST_API_TOKEN}`, 'content-type': 'application/json' },
        body: JSON.stringify([['INCR', key], ['EXPIRE', key, String(seconds * 2)]]),
        signal: AbortSignal.timeout(KV_TIMEOUT_MS),
      });
      const [incr] = await response.json();
      if (response.ok && typeof incr?.result === 'number') return incr.result > max;
    } catch {
      // Fall back to this instance's own count.
    }
  }
  return localCount(key, seconds, now) > max;
}

function limited(ip, now = Date.now()) {
  return localCount(`ip:${ip}`, 60, now) > RATE_LIMIT;
}

function reply(res, status, body) {
  res.statusCode = status;
  res.setHeader('content-type', 'application/json');
  res.setHeader('cache-control', 'no-store');
  res.end(JSON.stringify(body));
}

async function readBody(req) {
  if (req.body !== undefined) {
    return typeof req.body === 'string' || Buffer.isBuffer(req.body) ? JSON.parse(req.body.toString()) : req.body;
  }
  let raw = '';
  for await (const chunk of req) {
    raw += chunk;
    if (raw.length > 4 * 1024) throw new Error('too big');
  }
  return JSON.parse(raw);
}

// What goes to Trakt for each grant, or null when the request isn't one we pass on.
function traktCall(input, id, secret) {
  const { grant } = input || {};
  if (grant === 'device_token') {
    if (typeof input.code !== 'string' || !TOKEN_SHAPE.test(input.code)) return null;
    return { path: '/oauth/device/token', body: { code: input.code, client_id: id, client_secret: secret } };
  }
  if (grant === 'refresh_token') {
    if (typeof input.refreshToken !== 'string' || !TOKEN_SHAPE.test(input.refreshToken)) return null;
    return {
      path: '/oauth/token',
      body: {
        refresh_token: input.refreshToken, client_id: id, client_secret: secret,
        redirect_uri: 'urn:ietf:wg:oauth:2.0:oob', grant_type: 'refresh_token',
      },
    };
  }
  if (grant === 'revoke') {
    if (typeof input.token !== 'string' || !TOKEN_SHAPE.test(input.token)) return null;
    return { path: '/oauth/revoke', body: { token: input.token, client_id: id, client_secret: secret } };
  }
  return null;
}

// Only the fields a server needs: never anything Trakt might add that it shouldn't see.
const TOKEN_FIELDS = ['access_token', 'refresh_token', 'expires_in', 'created_at', 'token_type', 'scope', 'error', 'error_description'];

module.exports = async (req, res) => {
  if (req.method === 'GET') {
    // Public: Trakt shows it to anyone who looks. Kept here so a new app needs no new release.
    // Only once the secret is there too: offering Trakt before it is would send people to codes that never connect.
    if (!process.env.TRAKT_CLIENT_ID || !process.env.TRAKT_CLIENT_SECRET) return reply(res, 503, { reason: 'NotConfigured' });
    res.setHeader('cache-control', 'public, max-age=3600');
    res.statusCode = 200;
    res.setHeader('content-type', 'application/json');
    return res.end(JSON.stringify({ clientId: process.env.TRAKT_CLIENT_ID }));
  }
  if (req.method !== 'POST') {
    res.setHeader('allow', 'GET, POST');
    return reply(res, 405, { reason: 'MethodNotAllowed' });
  }
  if (!String(req.headers['content-type'] || '').toLowerCase().startsWith('application/json')) {
    return reply(res, 415, { reason: 'UnsupportedMediaType' });
  }

  const ip = String(req.headers['x-forwarded-for'] || req.socket?.remoteAddress || '').split(',')[0].trim();
  if (limited(ip) || await count('ip', ip)) {
    console.log('trakt: address limit reached');
    return reply(res, 429, { reason: 'TooManyRequests' });
  }

  // The relay's own refusals are 422, never 400: servers read Trakt's 400 (with invalid_grant) as "connect again".
  let input;
  try { input = await readBody(req); } catch { return reply(res, 422, { reason: 'BadRequest' }); }

  const { TRAKT_CLIENT_ID, TRAKT_CLIENT_SECRET } = process.env;
  if (!TRAKT_CLIENT_ID || !TRAKT_CLIENT_SECRET) return reply(res, 503, { reason: 'NotConfigured' });

  const call = traktCall(input, TRAKT_CLIENT_ID, TRAKT_CLIENT_SECRET);
  if (!call) return reply(res, 422, { reason: 'BadRequest' });

  const limitedBy = { device_token: input.code, refresh_token: input.refreshToken, revoke: input.token }[input.grant];
  if (await count(input.grant, limitedBy)) {
    console.log(`trakt: ${input.grant} limit reached`);
    return reply(res, 429, { reason: 'TooManyRequests' });
  }

  let response;
  try {
    response = await fetch(TRAKT + call.path, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'user-agent': 'Flexarr relay' },
      body: JSON.stringify(call.body),
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
  } catch {
    console.log(`trakt: ${input.grant} unreachable`);
    return reply(res, 502, { reason: 'Unreachable' });
  }
  console.log(`trakt: ${input.grant} ${response.status}`);

  // Trakt's own status, so a server reads pending (400), expired (410), denied (418) and slow down (429) as Trakt
  // documents them.
  const body = await response.json().catch(() => ({}));
  const out = {};
  for (const key of TOKEN_FIELDS) if (body && body[key] !== undefined) out[key] = body[key];
  return reply(res, response.status, out);
};

module.exports._traktCall = traktCall;
module.exports._limited = limited;
module.exports._count = count;
