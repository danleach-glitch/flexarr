// Flexarr's push relay. Flexarr servers can't hold an APNs key, so they post an encrypted payload here and we hand
// it to Apple. We never see what's inside: the phone decrypts it. See docs/notifications.md ("Relay API").
//
// Environment: APNS_KEY (the .p8 file's contents), APNS_KEY_ID, APNS_TEAM_ID, APNS_TOPIC (the app's bundle id).
// Optional: KV_REST_API_URL and KV_REST_API_TOKEN (an Upstash Redis store, as Vercel's marketplace sets them), so the
// per-phone limit holds across every instance; without them each warm instance counts on its own.

const crypto = require('node:crypto');
const http2 = require('node:http2');

const HOSTS = { production: 'https://api.push.apple.com', sandbox: 'https://api.sandbox.push.apple.com' };
// Apple takes 4 KB in all; base64 grows the sealed payload by a third, and the alert around it needs room too.
const MAX_PAYLOAD = 2800;
const TIMEOUT_MS = 10_000;
const RATE_LIMIT = 120; // requests per minute per IP
// Per phone: a household's server sends a handful a minute at most (a season arriving). More is someone else's server,
// or someone who has the phone's token, filling it with alerts.
const TOKEN_LIMIT = 12; // per minute per device token
const KV_TIMEOUT_MS = 1500;

// --- Provider token: Apple wants it refreshed between 20 and 60 minutes.

let cachedJwt = null;

function b64url(input) {
  return Buffer.from(input).toString('base64url');
}

function jwt(now = Date.now()) {
  if (cachedJwt && now - cachedJwt.at < 50 * 60 * 1000) return cachedJwt.token;
  const key = process.env.APNS_KEY.replace(/\\n/g, '\n');
  const header = b64url(JSON.stringify({ alg: 'ES256', kid: process.env.APNS_KEY_ID }));
  const claims = b64url(JSON.stringify({ iss: process.env.APNS_TEAM_ID, iat: Math.floor(now / 1000) }));
  const signature = crypto.sign('sha256', Buffer.from(`${header}.${claims}`), { key, dsaEncoding: 'ieee-p1363' });
  cachedJwt = { token: `${header}.${claims}.${signature.toString('base64url')}`, at: now };
  return cachedJwt.token;
}

// --- One HTTP/2 connection per Apple host, kept across warm invocations.

const sessions = {};

function session(origin) {
  const existing = sessions[origin];
  if (existing && !existing.closed && !existing.destroyed) return existing;
  const s = http2.connect(origin);
  s.on('error', () => s.destroy());
  s.on('goaway', () => s.close());
  s.unref();
  sessions[origin] = s;
  return s;
}

function sendToApple(environment, token, body, collapseId) {
  return new Promise((resolve, reject) => {
    const headers = {
      ':method': 'POST',
      ':path': `/3/device/${token}`,
      authorization: `bearer ${jwt()}`,
      'apns-push-type': 'alert',
      'apns-topic': process.env.APNS_TOPIC,
      'apns-priority': '10',
      'content-type': 'application/json',
    };
    if (collapseId) headers['apns-collapse-id'] = collapseId;

    const req = session(HOSTS[environment]).request(headers);
    let status = 0;
    let data = '';
    req.setEncoding('utf8');
    req.setTimeout(TIMEOUT_MS, () => req.close(http2.constants.NGHTTP2_CANCEL));
    req.on('response', (h) => { status = h[':status']; });
    req.on('data', (chunk) => { data += chunk; });
    req.on('end', () => {
      let reason;
      try { reason = JSON.parse(data).reason; } catch { /* empty body on success */ }
      resolve({ status, reason });
    });
    req.on('close', () => { if (!status) reject(new Error('timeout or reset')); });
    req.on('error', reject);
    req.end(body);
  });
}

// --- Best-effort rate limit. Each warm instance counts on its own, which is fine for keeping out the noise.

const hits = new Map();

function limited(ip, now = Date.now()) {
  const minute = Math.floor(now / 60_000);
  const entry = hits.get(ip);
  if (!entry || entry.minute !== minute) {
    if (hits.size > 10_000) hits.clear();
    hits.set(ip, { minute, count: 1 });
    return false;
  }
  return ++entry.count > RATE_LIMIT;
}

// --- Per phone, across instances when there's a store. A store that's down or slow lets the send through: the
// limit is there for floods, and every phone missing its notifications would be worse.

const tokenHits = new Map();

async function tokenLimited(token, now = Date.now()) {
  const minute = Math.floor(now / 60_000);
  const key = `push:${crypto.createHash('sha256').update(token).digest('hex').slice(0, 32)}:${minute}`;
  const { KV_REST_API_URL, KV_REST_API_TOKEN } = process.env;
  if (KV_REST_API_URL && KV_REST_API_TOKEN) {
    try {
      const response = await fetch(`${KV_REST_API_URL}/pipeline`, {
        method: 'POST',
        headers: { authorization: `Bearer ${KV_REST_API_TOKEN}`, 'content-type': 'application/json' },
        body: JSON.stringify([['INCR', key], ['EXPIRE', key, '120']]),
        signal: AbortSignal.timeout(KV_TIMEOUT_MS),
      });
      const [incr] = await response.json();
      if (response.ok && typeof incr?.result === 'number') return incr.result > TOKEN_LIMIT;
    } catch {
      // Fall back to this instance's own count.
    }
  }
  if (tokenHits.size > 10_000) tokenHits.clear();
  const count = (tokenHits.get(key) || 0) + 1;
  tokenHits.set(key, count);
  return count > TOKEN_LIMIT;
}

// --- The handler.

function reply(res, status, body) {
  res.statusCode = status;
  res.setHeader('content-type', 'application/json');
  res.end(JSON.stringify(body));
}

async function readBody(req) {
  if (req.body !== undefined) {
    return typeof req.body === 'string' || Buffer.isBuffer(req.body) ? JSON.parse(req.body.toString()) : req.body;
  }
  let raw = '';
  for await (const chunk of req) {
    raw += chunk;
    if (raw.length > 16 * 1024) throw new Error('too big');
  }
  return JSON.parse(raw);
}

module.exports = async (req, res) => {
  if (req.method !== 'POST') {
    res.setHeader('allow', 'POST');
    return reply(res, 405, { reason: 'MethodNotAllowed' });
  }

  // JSON only. A form or text/plain post is one any web page can make from its visitors' browsers without asking.
  if (!String(req.headers['content-type'] || '').toLowerCase().startsWith('application/json')) {
    return reply(res, 415, { reason: 'UnsupportedMediaType' });
  }

  const ip = String(req.headers['x-forwarded-for'] || req.socket?.remoteAddress || '').split(',')[0].trim();
  if (limited(ip)) return reply(res, 429, { reason: 'TooManyRequests' });

  let input;
  try { input = await readBody(req); } catch { return reply(res, 400, { reason: 'BadRequest' }); }
  const { token, environment, payload, collapseId } = input || {};

  if (typeof token !== 'string' || !/^[0-9a-fA-F]{64,200}$/.test(token)) return reply(res, 400, { reason: 'BadToken' });
  if (environment !== 'production' && environment !== 'sandbox') return reply(res, 400, { reason: 'BadEnvironment' });
  if (typeof payload !== 'string' || !/^[A-Za-z0-9+/]+={0,2}$/.test(payload) || payload.length % 4 !== 0) {
    return reply(res, 400, { reason: 'BadPayload' });
  }
  if (Buffer.from(payload, 'base64').length > MAX_PAYLOAD) return reply(res, 413, { reason: 'PayloadTooLarge' });
  if (collapseId !== undefined && collapseId !== null &&
      (typeof collapseId !== 'string' || collapseId.length === 0 || Buffer.byteLength(collapseId) > 64)) {
    return reply(res, 400, { reason: 'BadCollapseId' });
  }

  if (await tokenLimited(token)) return reply(res, 429, { reason: 'TooManyRequests' });

  const { APNS_KEY, APNS_KEY_ID, APNS_TEAM_ID, APNS_TOPIC } = process.env;
  if (!APNS_KEY || !APNS_KEY_ID || !APNS_TEAM_ID || !APNS_TOPIC) return reply(res, 503, { reason: 'NotConfigured' });

  const body = JSON.stringify({
    aps: { alert: { title: 'Flexarr', body: 'Something new is ready.' }, sound: 'default', 'mutable-content': 1 },
    e: payload,
  });

  let apple;
  try {
    apple = await sendToApple(environment, token, body, collapseId || undefined);
  } catch {
    console.log('push: apple unreachable');
    return reply(res, 502, { reason: 'Unreachable' });
  }
  console.log(`push: apple ${apple.status} ${apple.reason || ''}`.trim());

  if (apple.status === 200) return reply(res, 200, { ok: true });
  if (apple.status === 410) return reply(res, 410, { reason: 'Unregistered' });
  if (apple.status === 400 && (apple.reason === 'BadDeviceToken' || apple.reason === 'DeviceTokenNotForTopic')) {
    return reply(res, 400, { reason: apple.reason });
  }
  if (apple.status === 429) return reply(res, 429, { reason: apple.reason || 'TooManyRequests' });
  if (apple.status === 403 && apple.reason === 'ExpiredProviderToken') cachedJwt = null;
  return reply(res, 502, { reason: apple.reason || 'Unknown' });
};

module.exports._jwt = jwt;
module.exports._tokenLimited = tokenLimited;
