// The meeting point for inviting a guest's TV by the code it shows (Flexarr family invites, docs/remote-guests.md).
// A far-away TV can't reach its host's server, and the server can't reach it, so both talk to this:
//
//   TV      POST /api/pair                          {code, publicKey, secret}   registers its code for ten minutes
//   server  GET  /api/pair?code=ABCDEFGH            -> {publicKey}              the TV's public key
//   server  POST /api/pair?code=ABCDEFGH&action=invite  {serverKey, sealed}     leaves the invite, sealed to that key
//   TV      GET  /api/pair?code=ABCDEFGH&secret=…   -> {serverKey, sealed}      collects it once (204 while waiting)
//
// Only public keys and ciphertext pass through here: the invite is encrypted on the server to the TV's key (P-256
// ECDH, HKDF-SHA256, AES-256-GCM) and only the TV can open it. The TV's secret keeps anyone else from collecting it.
//
// Environment: KV_REST_API_URL and KV_REST_API_TOKEN (an Upstash Redis store, as Vercel's marketplace sets them).
// Without them each instance keeps codes in its own memory, which only works with a single instance (tests, local).

const crypto = require('node:crypto');

const TTL_SECONDS = 600;
const ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';
const CODE = new RegExp(`^[${ALPHABET}]{8}$`);
const SECRET = /^[A-Za-z0-9_-]{32,128}$/;
const BASE64 = /^[A-Za-z0-9+/]+={0,2}$/;
const MAX_SEALED = 4096; // an invite is a few hundred bytes
// A TV polls every few seconds for ten minutes; a server makes a couple of calls per invite.
const RATE_LIMIT = 60; // requests per minute per address
const KV_TIMEOUT_MS = 1500;

// --- Storage: Upstash when configured, else this instance's memory.

const memory = new Map();

function kvConfigured() {
  return Boolean(process.env.KV_REST_API_URL && process.env.KV_REST_API_TOKEN);
}

async function kv(commands) {
  const response = await fetch(`${process.env.KV_REST_API_URL}/pipeline`, {
    method: 'POST',
    headers: { authorization: `Bearer ${process.env.KV_REST_API_TOKEN}`, 'content-type': 'application/json' },
    body: JSON.stringify(commands),
    signal: AbortSignal.timeout(KV_TIMEOUT_MS),
  });
  if (!response.ok) throw new Error(`store answered ${response.status}`);
  return (await response.json()).map((r) => {
    if (r.error) throw new Error(r.error);
    return r.result;
  });
}

function memoryGet(key, now) {
  const entry = memory.get(key);
  if (!entry) return null;
  if (entry.until <= now) {
    memory.delete(key);
    return null;
  }
  return entry.value;
}

const store = {
  /** Sets key only if it's free, for TTL_SECONDS; true when set. */
  async add(key, value, now = Date.now()) {
    if (kvConfigured()) {
      const [result] = await kv([['SET', key, value, 'NX', 'EX', String(TTL_SECONDS)]]);
      return result === 'OK';
    }
    if (memoryGet(key, now) !== null) return false;
    if (memory.size > 10_000) memory.clear();
    memory.set(key, { value, until: now + TTL_SECONDS * 1000 });
    return true;
  },
  async get(key, now = Date.now()) {
    if (kvConfigured()) return (await kv([['GET', key]]))[0];
    return memoryGet(key, now);
  },
  async remove(...keys) {
    if (kvConfigured()) {
      await kv([['DEL', ...keys]]);
      return;
    }
    for (const key of keys) memory.delete(key);
  },
};

// --- Rate limit per address, on this instance.

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

// --- Checks.

function isPublicKey(value) {
  if (typeof value !== 'string' || !BASE64.test(value) || value.length % 4 !== 0) return false;
  const bytes = Buffer.from(value, 'base64');
  return bytes.length === 65 && bytes[0] === 0x04; // an uncompressed P-256 point
}

function isSealed(value) {
  return typeof value === 'string' && BASE64.test(value) && value.length % 4 === 0 && value.length <= MAX_SEALED;
}

function hash(secret) {
  return crypto.createHash('sha256').update(secret).digest('hex');
}

function sameHash(a, b) {
  const x = Buffer.from(a, 'hex');
  const y = Buffer.from(b, 'hex');
  return x.length === y.length && crypto.timingSafeEqual(x, y);
}

// --- The handler.

function reply(res, status, body) {
  res.statusCode = status;
  if (body === undefined) {
    res.end();
    return;
  }
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
    if (raw.length > 16 * 1024) throw new Error('too big');
  }
  return JSON.parse(raw);
}

function queryOf(req) {
  return new URL(req.url || '/', 'http://relay').searchParams;
}

module.exports = async (req, res) => {
  if (req.method !== 'GET' && req.method !== 'POST') {
    res.setHeader('allow', 'GET, POST');
    return reply(res, 405, { reason: 'MethodNotAllowed' });
  }

  // JSON only for posts: a form or text/plain post is one any web page can make from its visitors' browsers.
  if (req.method === 'POST' && !String(req.headers['content-type'] || '').toLowerCase().startsWith('application/json')) {
    return reply(res, 415, { reason: 'UnsupportedMediaType' });
  }

  const ip = String(req.headers['x-forwarded-for'] || req.socket?.remoteAddress || '').split(',')[0].trim();
  if (limited(ip)) return reply(res, 429, { reason: 'TooManyRequests' });

  if (!kvConfigured() && process.env.VERCEL_ENV === 'production') {
    console.log('pair: no store configured; codes only work within one instance');
  }

  const query = queryOf(req);
  const code = query.get('code');
  try {
    // The TV registers its code and key.
    if (req.method === 'POST' && code === null) {
      let input;
      try { input = await readBody(req); } catch { return reply(res, 400, { reason: 'BadRequest' }); }
      const { code: wanted, publicKey, secret } = input || {};
      if (typeof wanted !== 'string' || !CODE.test(wanted)) return reply(res, 400, { reason: 'BadCode' });
      if (!isPublicKey(publicKey)) return reply(res, 400, { reason: 'BadPublicKey' });
      if (typeof secret !== 'string' || !SECRET.test(secret)) return reply(res, 400, { reason: 'BadSecret' });
      const added = await store.add(`pair:${wanted}`, JSON.stringify({ publicKey, secretHash: hash(secret) }));
      return added ? reply(res, 201, { expiresIn: TTL_SECONDS }) : reply(res, 409, { reason: 'CodeTaken' });
    }

    if (typeof code !== 'string' || !CODE.test(code)) return reply(res, 400, { reason: 'BadCode' });
    const registered = await store.get(`pair:${code}`);
    const entry = registered ? JSON.parse(registered) : null;

    // The server leaves the sealed invite, once.
    if (req.method === 'POST' && query.get('action') === 'invite') {
      if (!entry) return reply(res, 404, { reason: 'UnknownCode' });
      let input;
      try { input = await readBody(req); } catch { return reply(res, 400, { reason: 'BadRequest' }); }
      const { serverKey, sealed } = input || {};
      if (!isPublicKey(serverKey)) return reply(res, 400, { reason: 'BadServerKey' });
      if (!isSealed(sealed)) return reply(res, 400, { reason: 'BadSealed' });
      const added = await store.add(`pair:${code}:invite`, JSON.stringify({ serverKey, sealed }));
      return added ? reply(res, 201, {}) : reply(res, 409, { reason: 'AlreadyInvited' });
    }

    if (req.method === 'POST') return reply(res, 400, { reason: 'BadAction' });

    // The TV collects its invite with its secret.
    const secret = query.get('secret');
    if (secret !== null) {
      if (!entry) return reply(res, 404, { reason: 'UnknownCode' });
      if (!SECRET.test(secret) || !sameHash(hash(secret), entry.secretHash)) return reply(res, 403, { reason: 'WrongSecret' });
      const invite = await store.get(`pair:${code}:invite`);
      if (!invite) return reply(res, 204);
      await store.remove(`pair:${code}`, `pair:${code}:invite`);
      return reply(res, 200, JSON.parse(invite));
    }

    // The server asks for the TV's key.
    return entry ? reply(res, 200, { publicKey: entry.publicKey }) : reply(res, 404, { reason: 'UnknownCode' });
  } catch (error) {
    console.log(`pair: store failed (${error.message})`);
    return reply(res, 503, { reason: 'StoreUnavailable' });
  }
};

module.exports._store = store;
module.exports._memory = memory;
module.exports._hits = hits;
