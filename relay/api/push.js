// Flexarr's push relay. Flexarr servers can't hold an APNs key or a Firebase service account, so they post an
// encrypted payload here and we hand it to Apple, or to Google for an Android phone. We never see what's inside: the
// phone decrypts it. See docs/notifications.md ("Relay API").
//
// Environment: APNS_KEY (the .p8 file's contents), APNS_KEY_ID, APNS_TEAM_ID, APNS_TOPIC (the app's bundle id); for
// Android, FCM_SERVICE_ACCOUNT (the Firebase project's service account key, the JSON file's contents).
// Optional: KV_REST_API_URL and KV_REST_API_TOKEN (an Upstash Redis store, as Vercel's marketplace sets them), so the
// per-phone limit holds across every instance; without them each warm instance counts on its own.

const crypto = require('node:crypto');
const http2 = require('node:http2');

const HOSTS = { production: 'https://api.push.apple.com', sandbox: 'https://api.sandbox.push.apple.com' };
// Apple takes 4 KB in all; base64 grows the sealed payload by a third, and the alert around it needs room too.
const MAX_PAYLOAD = 2800;
const TIMEOUT_MS = 10_000;
// Per address: a backstop only, now each phone has its own limit. Generous, because servers from before 0.51 reach the
// relay through flexarr.io's rewrite and may all arrive from Vercel's own few addresses.
const RATE_LIMIT = 600; // requests per minute per IP
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

// `liveActivity`: a Live Activity's update, which goes to the app's Live Activity topic as its own push type, at
// Apple's high priority only when `urgent` (Apple budgets those for an activity).
function sendToApple(environment, token, body, collapseId, liveActivity = false, urgent = true) {
  return new Promise((resolve, reject) => {
    const headers = {
      ':method': 'POST',
      ':path': `/3/device/${token}`,
      authorization: `bearer ${jwt()}`,
      'apns-push-type': liveActivity ? 'liveactivity' : 'alert',
      'apns-topic': liveActivity ? `${process.env.APNS_TOPIC}.push-type.liveactivity` : process.env.APNS_TOPIC,
      'apns-priority': urgent ? '10' : '5',
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

// --- Firebase Cloud Messaging (HTTP v1), for Android phones. An OAuth token from the service account, kept for most of
// its hour.

const FCM_SCOPE = 'https://www.googleapis.com/auth/firebase.messaging';
let cachedAccess = null;

function serviceAccount() {
  try {
    const account = JSON.parse(process.env.FCM_SERVICE_ACCOUNT || '');
    return account.client_email && account.private_key && account.project_id ? account : null;
  } catch {
    return null;
  }
}

async function accessToken(account, now = Date.now()) {
  if (cachedAccess && cachedAccess.email === account.client_email && now < cachedAccess.until) return cachedAccess.token;
  const iat = Math.floor(now / 1000);
  const header = b64url(JSON.stringify({ alg: 'RS256', typ: 'JWT' }));
  const tokenUri = account.token_uri || 'https://oauth2.googleapis.com/token';
  const claims = b64url(JSON.stringify({ iss: account.client_email, scope: FCM_SCOPE, aud: tokenUri, iat, exp: iat + 3600 }));
  const signature = crypto.sign('sha256', Buffer.from(`${header}.${claims}`), account.private_key.replace(/\\n/g, '\n'));
  const response = await fetch(tokenUri, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer',
      assertion: `${header}.${claims}.${signature.toString('base64url')}`,
    }),
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok || typeof body.access_token !== 'string') throw new Error(`google token ${response.status}`);
  cachedAccess = { token: body.access_token, email: account.client_email, until: now + Math.min(body.expires_in || 3600, 3600) * 1000 - 5 * 60_000 };
  return cachedAccess.token;
}

// A data message, not a notification one: the app unseals it and shows the notification itself, as the iPhone's
// extension does. High priority, so a phone that's dozing wakes for it.
async function sendToGoogle(account, token, payload, collapseId) {
  const android = { priority: 'HIGH' };
  if (collapseId) android.collapse_key = collapseId;
  const response = await fetch(`https://fcm.googleapis.com/v1/projects/${encodeURIComponent(account.project_id)}/messages:send`, {
    method: 'POST',
    headers: { authorization: `Bearer ${await accessToken(account)}`, 'content-type': 'application/json' },
    body: JSON.stringify({ message: { token, data: { e: payload }, android } }),
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
  const body = await response.json().catch(() => ({}));
  const error = body.error || {};
  const fcm = (error.details || []).find((d) => d && typeof d.errorCode === 'string');
  return { status: response.status, reason: fcm?.errorCode || error.status };
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
  if (limited(ip)) {
    console.log('push: address limit reached');
    return reply(res, 429, { reason: 'TooManyRequests' });
  }

  let input;
  try { input = await readBody(req); } catch { return reply(res, 400, { reason: 'BadRequest' }); }
  if (input && input.kind === 'liveactivity') return liveActivity(res, input);
  const { token, environment, payload, collapseId, platform } = input || {};

  // Apple's hex device tokens, unless the server says it's an Android phone's Firebase registration token.
  const android = platform === 'android';
  if (platform !== undefined && platform !== null && !android) return reply(res, 400, { reason: 'BadPlatform' });
  const tokenShape = android ? /^[A-Za-z0-9_:-]{32,250}$/ : /^[0-9a-fA-F]{64,200}$/;
  if (typeof token !== 'string' || !tokenShape.test(token)) return reply(res, 400, { reason: 'BadToken' });
  if (environment !== 'production' && environment !== 'sandbox') return reply(res, 400, { reason: 'BadEnvironment' });
  if (typeof payload !== 'string' || !/^[A-Za-z0-9+/]+={0,2}$/.test(payload) || payload.length % 4 !== 0) {
    return reply(res, 400, { reason: 'BadPayload' });
  }
  if (Buffer.from(payload, 'base64').length > MAX_PAYLOAD) return reply(res, 413, { reason: 'PayloadTooLarge' });
  if (collapseId !== undefined && collapseId !== null &&
      (typeof collapseId !== 'string' || collapseId.length === 0 || Buffer.byteLength(collapseId) > 64)) {
    return reply(res, 400, { reason: 'BadCollapseId' });
  }

  if (await tokenLimited(token)) {
    console.log('push: phone limit reached');
    return reply(res, 429, { reason: 'TooManyRequests' });
  }

  if (android) return forwardToGoogle(res, token, payload, collapseId);

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

// --- Live Activities: what a TV is playing, on an iPhone's Lock Screen. Apple's system reads these itself, so unlike
// notifications they can't be sealed: they carry times and nothing else (the phone put the title on when it started
// the activity), and anything more is refused.

const DAY = 24 * 60 * 60;

function activityState(state, now) {
  if (!state || typeof state !== 'object' || Array.isArray(state)) return null;
  const keys = Object.keys(state).sort().join(',');
  if (keys !== 'at,duration,paused,position,rate') return null;
  const { paused, position, at, duration, rate } = state;
  const number = (n, min, max) => typeof n === 'number' && Number.isFinite(n) && n >= min && n <= max;
  if (typeof paused !== 'boolean' || !number(position, 0, 2 * DAY) || !number(duration, 0, 2 * DAY) || !number(rate, 0.25, 4) ||
      !number(at, now - DAY, now + DAY)) return null;
  return { paused, position, at, duration, rate };
}

async function liveActivity(res, { token, environment, event, state, urgent }) {
  const now = Math.floor(Date.now() / 1000);
  // Longer than a notification's: a Live Activity's token is 128 bytes.
  if (typeof token !== 'string' || !/^[0-9a-fA-F]{64,512}$/.test(token)) return reply(res, 400, { reason: 'BadToken' });
  if (environment !== 'production' && environment !== 'sandbox') return reply(res, 400, { reason: 'BadEnvironment' });
  if (event !== 'update' && event !== 'end') return reply(res, 400, { reason: 'BadEvent' });
  if (urgent !== undefined && typeof urgent !== 'boolean') return reply(res, 400, { reason: 'BadUrgent' });
  const content = activityState(state, now);
  if (!content) return reply(res, 400, { reason: 'BadState' });

  if (await tokenLimited(token)) {
    console.log('push: activity limit reached');
    return reply(res, 429, { reason: 'TooManyRequests' });
  }

  const { APNS_KEY, APNS_KEY_ID, APNS_TEAM_ID, APNS_TOPIC } = process.env;
  if (!APNS_KEY || !APNS_KEY_ID || !APNS_TEAM_ID || !APNS_TOPIC) return reply(res, 503, { reason: 'NotConfigured' });

  // Stale (dimmed) once it should have ended, or a while after a pause with no word: the TV may have gone.
  const left = content.paused ? 20 * 60 : (content.duration - content.position) / content.rate + 5 * 60;
  const aps = { timestamp: now, event, 'content-state': content, 'stale-date': Math.round(now + Math.max(60, left)) };
  if (event === 'end') aps['dismissal-date'] = now;

  let apple;
  try {
    apple = await sendToApple(environment, token, JSON.stringify({ aps }), undefined, true, urgent !== false || event === 'end');
  } catch {
    console.log('push: apple unreachable');
    return reply(res, 502, { reason: 'Unreachable' });
  }
  console.log(`push: activity ${apple.status} ${apple.reason || ''}`.trim());
  if (apple.status === 200) return reply(res, 200, { ok: true });
  if (apple.status === 410) return reply(res, 410, { reason: 'Unregistered' });
  if (apple.status === 400 && (apple.reason === 'BadDeviceToken' || apple.reason === 'DeviceTokenNotForTopic')) {
    return reply(res, 400, { reason: apple.reason });
  }
  if (apple.status === 429) return reply(res, 429, { reason: apple.reason || 'TooManyRequests' });
  if (apple.status === 403 && apple.reason === 'ExpiredProviderToken') cachedJwt = null;
  return reply(res, 502, { reason: apple.reason || 'Unknown' });
}

// Google's answers, in the words the servers already know from Apple: Unregistered and BadDeviceToken make a server
// forget the phone.
async function forwardToGoogle(res, token, payload, collapseId) {
  const account = serviceAccount();
  if (!account) return reply(res, 503, { reason: 'NotConfigured' });

  let google;
  try {
    google = await sendToGoogle(account, token, payload, collapseId || undefined);
  } catch {
    console.log('push: google unreachable');
    return reply(res, 502, { reason: 'Unreachable' });
  }
  console.log(`push: google ${google.status} ${google.reason || ''}`.trim());

  if (google.status === 200) return reply(res, 200, { ok: true });
  if (google.reason === 'UNREGISTERED') return reply(res, 410, { reason: 'Unregistered' });
  // Everything else in the message was checked here, so a refused argument is the token. A token from another
  // Firebase project is no more use to this one.
  if (google.reason === 'INVALID_ARGUMENT' || google.reason === 'SENDER_ID_MISMATCH') return reply(res, 400, { reason: 'BadDeviceToken' });
  if (google.status === 429) return reply(res, 429, { reason: 'TooManyRequests' });
  if (google.status === 401) cachedAccess = null;
  return reply(res, 502, { reason: google.reason || 'Unknown' });
}

module.exports._jwt = jwt;
module.exports._forget = () => { cachedAccess = null; };
module.exports._tokenLimited = tokenLimited;
module.exports._activityState = activityState;
