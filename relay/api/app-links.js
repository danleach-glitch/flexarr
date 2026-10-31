// The files that let flexarr.io/join links open the Flexarr app (family invites, docs/remote-guests.md), served here
// because they name the app's signing team and certificate, which live in this project's environment rather than the
// public repository. flexarr.io rewrites /.well-known/apple-app-site-association and /.well-known/assetlinks.json here.
//
// Environment: APNS_TEAM_ID and APNS_TOPIC (already set for notifications: the team and the iPhone app's bundle id);
// ANDROID_CERT_SHA256 (the Android app's signing certificate fingerprints, comma-separated, as Google Play shows
// them) and optionally ANDROID_PACKAGE (default com.danleach.flexarr).

function reply(res, status, body) {
  res.statusCode = status;
  res.setHeader('content-type', 'application/json');
  res.setHeader('cache-control', 'public, max-age=3600');
  res.end(JSON.stringify(body));
}

function apple() {
  const { APNS_TEAM_ID: team, APNS_TOPIC: bundle } = process.env;
  const appIDs = team && bundle ? [`${team}.${bundle}`] : [];
  return { applinks: { details: [{ appIDs, components: [{ '/': '/join', comment: 'Family invites' }] }] } };
}

function android() {
  const fingerprints = String(process.env.ANDROID_CERT_SHA256 || '')
    .split(',')
    .map((f) => f.trim().toUpperCase())
    .filter((f) => /^([0-9A-F]{2}:){31}[0-9A-F]{2}$/.test(f));
  if (fingerprints.length === 0) return [];
  return [{
    relation: ['delegate_permission/common.handle_all_urls'],
    target: { namespace: 'android_app', package_name: process.env.ANDROID_PACKAGE || 'com.danleach.flexarr', sha256_cert_fingerprints: fingerprints },
  }];
}

module.exports = (req, res) => {
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    res.setHeader('allow', 'GET');
    return reply(res, 405, { reason: 'MethodNotAllowed' });
  }
  const platform = new URL(req.url || '/', 'http://relay').searchParams.get('platform');
  if (platform === 'apple') return reply(res, 200, apple());
  if (platform === 'android') return reply(res, 200, android());
  return reply(res, 404, { reason: 'UnknownPlatform' });
};
