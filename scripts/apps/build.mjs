// Builds flexarr.io/apps: site/apps.json (every app and platform, its status and version) into site/apps.html.
// Run with `make apps`. The output is committed, so Vercel serves it as a plain file, and apps.json is served as is
// for anything that wants the same list. `--check` fails when apps.html is out of date; `--server-version x.y.z`
// (from scripts/release.sh) first writes that version into every entry marked "release": "server".
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../..');
const source = path.join(root, 'site/apps.json');
const out = path.join(root, 'site/apps.html');
const repo = 'https://github.com/danleach-glitch/flexarr';
const args = process.argv.slice(2);
const check = args.includes('--check');
const serverVersion = args.includes('--server-version') ? args[args.indexOf('--server-version') + 1] : null;

const escape = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

// The status column's icons, in the order the legend lists them.
const statuses = {
  available: { icon: '✅', label: 'Available' },
  beta: { icon: '🧪', label: 'Beta' },
  coming: { icon: '🔜', label: 'Coming soon' },
  development: { icon: '🛠️', label: 'In development' },
  planned: { icon: '🗓️', label: 'Planned' },
};

const stroke = d => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${d}</svg>`;
const icons = {
  phone: stroke('<rect x="6" y="2.5" width="12" height="19" rx="3"/><path d="M10.5 18.5h3"/>'),
  tv: stroke('<rect x="2.5" y="4" width="19" height="12.5" rx="2"/><path d="M8 20h8M12 16.5V20"/>'),
  web: stroke('<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.6 2.7 3.9 5.7 3.9 9s-1.3 6.3-3.9 9c-2.6-2.7-3.9-5.7-3.9-9S9.4 5.7 12 3Z"/>'),
  server: stroke('<rect x="3" y="3.5" width="18" height="7" rx="2"/><rect x="3" y="13.5" width="18" height="7" rx="2"/><path d="M7 7h.01M7 17h.01"/>'),
  home: stroke('<path d="M3 11.5 12 4l9 7.5"/><path d="M5.5 9.5V20h13V9.5"/><path d="M10 20v-5.5h4V20"/>'),
};
const stores = {
  apple: '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M16.4 12.6c0-2.4 2-3.6 2.1-3.7a4.5 4.5 0 0 0-3.5-1.9c-1.5-.2-2.9.9-3.7.9-.8 0-1.9-.9-3.2-.8a4.7 4.7 0 0 0-4 2.4c-1.7 3-.4 7.4 1.2 9.8.8 1.2 1.8 2.5 3 2.4 1.2 0 1.7-.8 3.1-.8 1.5 0 1.9.8 3.2.8 1.3 0 2.2-1.2 3-2.4a10 10 0 0 0 1.4-2.8 4.3 4.3 0 0 1-2.6-3.9ZM14 5.4a4.3 4.3 0 0 0 1-3.1 4.4 4.4 0 0 0-2.9 1.5 4.1 4.1 0 0 0-1 3 3.6 3.6 0 0 0 2.9-1.4Z"/></svg>',
  google: '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M5 3.5v17l14-8.5L5 3.5Z"/></svg>',
};

function load() {
  const data = JSON.parse(fs.readFileSync(source, 'utf8'));
  const problems = [];
  const groups = new Set((data.groups ?? []).map(g => g.id));
  const ids = new Set();
  for (const [i, p] of (data.platforms ?? []).entries()) {
    const at = `platforms[${i}]${p.id ? ` (${p.id})` : ''}`;
    for (const key of ['id', 'group', 'name', 'icon', 'status', 'summary']) if (!p[key]) problems.push(`${at}: needs "${key}"`);
    if (ids.has(p.id)) problems.push(`${at}: id used twice`);
    ids.add(p.id);
    if (p.group && !groups.has(p.group)) problems.push(`${at}: no group "${p.group}"`);
    if (p.icon && !icons[p.icon]) problems.push(`${at}: icon must be one of ${Object.keys(icons).join(', ')}`);
    if (p.status && !statuses[p.status]) problems.push(`${at}: status must be one of ${Object.keys(statuses).join(', ')}`);
    if (['available', 'beta', 'coming'].includes(p.status) && !p.version) problems.push(`${at}: a ${p.status} app needs a version`);
    if (p.version && !/^\d+\.\d+\.\d+$/.test(p.version)) problems.push(`${at}: version "${p.version}" isn't x.y.z`);
    if (p.release && p.release !== 'server') problems.push(`${at}: "release" can only be "server"`);
    for (const link of p.links ?? []) {
      if (!link.label || !/^(https:\/\/|\/)/.test(link.href ?? '')) problems.push(`${at}: each link needs a label and an https:// or /path href`);
      if (link.store && !stores[link.store]) problems.push(`${at}: store must be one of ${Object.keys(stores).join(', ')}`);
    }
  }
  if (problems.length) throw new Error(`site/apps.json:\n  ${problems.join('\n  ')}`);
  return data;
}

if (serverVersion) {
  if (!/^\d+\.\d+\.\d+$/.test(serverVersion)) throw new Error(`--server-version "${serverVersion}" isn't x.y.z`);
  // Rewrites only the version lines of server-released entries, so the file keeps its hand-made layout.
  let text = fs.readFileSync(source, 'utf8');
  for (const p of JSON.parse(text).platforms.filter(p => p.release === 'server')) {
    const start = text.indexOf(`"id": "${p.id}"`, text.indexOf('"platforms"'));
    const end = text.indexOf('"id": "', start + 1);
    const entry = text.slice(start, end < 0 ? undefined : end);
    if (!/"version": "[^"]*"/.test(entry)) throw new Error(`site/apps.json: ${p.id} has "release": "server" but no "version" to update`);
    text = text.slice(0, start) + entry.replace(/"version": "[^"]*"/, `"version": "${serverVersion}"`) + (end < 0 ? '' : text.slice(end));
  }
  fs.writeFileSync(source, text);
}

const data = load();

const version = p => (p.version ? `<span class="version">${escape(p.version)}</span>` : '<span class="none">Not yet</span>');
const status = p => `<span class="status ${p.status}"><span aria-hidden="true">${statuses[p.status].icon}</span> ${statuses[p.status].label}</span>`;
const link = (l, first) => {
  const external = /^https:/.test(l.href);
  const kind = l.store || l.primary ? 'button primary' : first ? 'button quiet' : 'more';
  return `<a class="${kind}" href="${escape(l.href)}"${external ? ' rel="noopener"' : ''}>${l.store ? stores[l.store] : ''}${escape(l.label)}</a>`;
};

const table = `<div class="table"><table>
          <thead><tr><th scope="col"><span class="visually-hidden">Status</span></th><th scope="col">Platform</th><th scope="col">Status</th><th scope="col">Version</th></tr></thead>
          <tbody>
${data.platforms.map(p => `            <tr><td class="mark" aria-hidden="true">${statuses[p.status].icon}</td><th scope="row"><a href="#${escape(p.id)}">${escape(p.name)}</a></th><td>${statuses[p.status].label}</td><td>${version(p)}</td></tr>`).join('\n')}
          </tbody>
        </table></div>`;

const card = p => `<article class="app" id="${escape(p.id)}">
            <div class="app-head">
              <span class="app-icon">${icons[p.icon]}</span>
              <div>
                <h3>${escape(p.name)}</h3>
                <p class="meta">${status(p)}${p.version ? ` <span class="dot" aria-hidden="true">·</span> Version ${version(p)}` : ''}</p>
              </div>
            </div>
            <p>${escape(p.summary)}</p>
${p.note ? `            <p class="note">${escape(p.note)}</p>\n` : ''}${p.requires ? `            <p class="requires"><span>Needs</span> ${escape(p.requires)}</p>\n` : ''}${p.links?.length ? `            <div class="links">${p.links.map((l, i) => link(l, i === 0)).join('')}</div>\n` : ''}          </article>`;

const groups = data.groups.map(g => {
  const members = data.platforms.filter(p => p.group === g.id);
  return members.length ? `<section class="group" aria-labelledby="group-${escape(g.id)}">
        <h2 id="group-${escape(g.id)}">${escape(g.title)}</h2>
${g.intro ? `        <p class="intro">${escape(g.intro)}</p>\n` : ''}        <div class="apps">
          ${members.map(card).join('\n          ')}
        </div>
      </section>` : '';
}).filter(Boolean).join('\n      ');

const title = 'Flexarr apps: Apple TV, iPhone, iPad, web and Android';
const description = 'Every Flexarr app and platform, with its version and status: what you can download today, what\'s coming soon and what\'s on the way.';

const html = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${escape(title)}</title>
  <meta name="description" content="${escape(description)}">
  <meta name="theme-color" content="#1b1720">
  <meta name="color-scheme" content="dark">
  <meta name="apple-itunes-app" content="app-id=6826618084">
  <link rel="canonical" href="https://flexarr.io/apps">
  <link rel="alternate" type="application/json" href="/apps.json">
  <link rel="icon" href="/favicon.svg" type="image/svg+xml">
  <link rel="apple-touch-icon" href="/apple-touch-icon.png">
  <meta property="og:title" content="${escape(title)}">
  <meta property="og:description" content="${escape(description)}">
  <meta property="og:image" content="https://flexarr.io/shots/og.jpg">
  <meta property="og:url" content="https://flexarr.io/apps">
  <meta name="twitter:card" content="summary_large_image">
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link href="https://fonts.googleapis.com/css2?family=Atkinson+Hyperlegible+Next:wght@400;500;700&family=Big+Shoulders+Display:wght@700;800;900&display=swap" rel="stylesheet">
  <link rel="stylesheet" href="/base.css">
  <style>
    .visually-hidden { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
    .hero { padding-block: clamp(32px, 6vw, 72px) 0; }
    .kicker { color: var(--silver); font-weight: 700; letter-spacing: 0.08em; text-transform: uppercase; font-size: 0.8rem; margin: 0 0 12px; }
    .hero h1 { font-size: clamp(2.8rem, 9vw, 5.5rem); font-weight: 900; }
    .lede { max-width: 680px; margin: 18px 0 0; color: var(--usher); font-size: clamp(1.1rem, 2.4vw, 1.25rem); }

    /* At a glance */
    .legend { display: flex; flex-wrap: wrap; gap: 4px 18px; margin: 36px 0 0; padding: 0; list-style: none; color: var(--usher); font-size: 0.92rem; }
    .table { margin-top: 12px; overflow-x: auto; border: 1px solid var(--aisle); border-radius: 16px; background: var(--seat); }
    table { width: 100%; border-collapse: collapse; }
    th, td { padding: 13px 18px; text-align: left; border-top: 1px solid var(--aisle); white-space: nowrap; }
    thead th { border-top: 0; color: var(--usher); font-size: 0.85rem; font-weight: 700; letter-spacing: 0.04em; text-transform: uppercase; }
    tbody th { font-weight: 500; }
    tbody th a { color: var(--screen); text-decoration: none; }
    tbody th a:hover { color: var(--silver); text-decoration: underline; }
    td { color: var(--usher); }
    .mark { width: 1%; padding-right: 0; font-size: 1.1rem; }
    .version { font-variant-numeric: tabular-nums; color: var(--screen); }
    .none { color: var(--usher); opacity: 0.7; }
    /* On a phone the icon column carries the status, so the table fits without scrolling. */
    @media (max-width: 640px) {
      th, td { padding: 12px 14px; white-space: normal; }
      th:nth-child(3), td:nth-child(3) { display: none; }
    }

    /* Each platform */
    .group { padding-block: clamp(48px, 7vw, 88px) 0; }
    .group h2 { font-size: clamp(2.2rem, 5vw, 3.3rem); }
    .intro { color: var(--usher); margin: 12px 0 0; font-size: 1.05rem; }
    .apps { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 8px 32px; margin-top: 20px; }
    .app { padding: 24px 0 8px; border-top: 1px solid var(--aisle); display: flex; flex-direction: column; gap: 12px; scroll-margin-top: 96px; }
    .app:target .app-icon { outline: 2px solid var(--silver); outline-offset: 3px; }
    .app-head { display: flex; align-items: center; gap: 14px; }
    .app h3 { font-size: 1.9rem; }
    .app-icon { width: 44px; height: 44px; border-radius: 13px; display: grid; place-items: center; flex: none; background: rgb(169 194 236 / 0.14); color: var(--silver); }
    .app:nth-child(3n+2) .app-icon { background: rgb(232 160 191 / 0.14); color: var(--rose); }
    .app:nth-child(3n) .app-icon { background: rgb(242 238 231 / 0.1); color: var(--screen); }
    .app-icon svg { width: 22px; height: 22px; }
    .app p { margin: 0; color: var(--usher); }
    .app .meta { margin-top: 4px; font-size: 0.95rem; }
    .status { color: var(--screen); font-weight: 500; }
    .dot { margin: 0 4px; }
    .note { font-size: 0.95rem; }
    .requires { font-size: 0.92rem; }
    .requires span { color: var(--screen); font-weight: 700; margin-right: 4px; }
    .links { display: flex; flex-wrap: wrap; align-items: center; gap: 10px 18px; margin-top: 4px; }
    .links .button { min-height: 44px; padding: 0 18px; font-size: 0.95rem; }
    .links .button.primary { box-shadow: none; }
    .links .button svg { width: 18px; height: 18px; }
    .more { font-weight: 700; text-decoration: none; }
    .more:hover { text-decoration: underline; }

    .closing { padding-block: clamp(56px, 8vw, 96px); color: var(--usher); }
    .closing p { margin: 0; max-width: 680px; }
    .closing p + p { margin-top: 10px; font-size: 0.95rem; }
  </style>
</head>
<body>
  <a class="skip" href="#main">Skip to the content</a>
  <div class="glow" aria-hidden="true"></div>
  <header class="site-header">
    <div class="wrap bar">
      <a class="wordmark" href="/" aria-label="Flexarr home"><img src="/logo-mark.svg" alt="">flexarr</a>
      <nav class="site-nav" aria-label="Main">
        <a href="/#features">Features</a>
        <a class="keep" href="/apps" aria-current="page">Apps</a>
        <a href="/#setup">Set up</a>
        <a class="keep" href="/guide">Guide</a>
        <a class="github keep" href="${repo}"><svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 .5a11.5 11.5 0 0 0-3.64 22.41c.58.1.79-.25.79-.56v-2c-3.2.7-3.88-1.37-3.88-1.37-.52-1.33-1.28-1.69-1.28-1.69-1.05-.72.08-.7.08-.7 1.16.08 1.77 1.19 1.77 1.19 1.03 1.77 2.7 1.26 3.36.96.1-.75.4-1.26.73-1.55-2.55-.29-5.24-1.28-5.24-5.69 0-1.26.45-2.29 1.19-3.09-.12-.29-.52-1.46.11-3.05 0 0 .97-.31 3.17 1.18a11 11 0 0 1 5.77 0c2.2-1.49 3.17-1.18 3.17-1.18.63 1.59.23 2.76.11 3.05.74.8 1.19 1.83 1.19 3.09 0 4.42-2.69 5.39-5.26 5.68.41.36.78 1.06.78 2.14v3.17c0 .31.21.67.8.56A11.5 11.5 0 0 0 12 .5Z"/></svg>GitHub</a>
      </nav>
    </div>
  </header>

  <main id="main">
    <div class="wrap">
      <div class="hero">
        <p class="kicker">Apps</p>
        <h1>Flexarr on every screen.</h1>
        <p class="lede">What you can download today, what's coming soon and what's on the way. Every app connects to your own Flexarr server at home.</p>
      </div>

      <section aria-label="Every platform at a glance">
        <ul class="legend">
${Object.values(statuses).map(s => `          <li><span aria-hidden="true">${s.icon}</span> ${s.label}</li>`).join('\n')}
        </ul>
        ${table}
      </section>

      ${groups}

      <div class="closing">
        <p>Want Flexarr somewhere else? <a href="${repo}/issues">Tell us on GitHub</a>.</p>
        <p>This list is also at <a href="/apps.json">flexarr.io/apps.json</a>, and its source is <a href="${repo}/blob/main/site/apps.json">site/apps.json</a> on GitHub.</p>
      </div>
    </div>
  </main>

  <footer class="site-footer">
    <div class="wrap bar">
      <span>Flexarr is free for noncommercial use under the PolyForm Noncommercial License.<br>Not affiliated with the *arr projects.</span>
      <nav aria-label="Project">
        <a href="/guide">Guide</a>
        <a href="/apps">Apps</a>
        <a href="${repo}">GitHub</a>
        <a href="${repo}/releases">Releases</a>
        <a href="${repo}/blob/main/CHANGELOG.md">Changelog</a>
        <a href="${repo}/issues">Report a problem</a>
        <a href="/privacy">Privacy</a>
      </nav>
    </div>
  </footer>
</body>
</html>
`;

if (check) {
  const current = fs.existsSync(out) ? fs.readFileSync(out, 'utf8') : '';
  if (current !== html) {
    console.error('site/apps.html is out of date with site/apps.json: run `make apps` and commit the result.');
    process.exit(1);
  }
  console.log('site/apps.html is up to date.');
} else {
  fs.writeFileSync(out, html);
  console.log(`site/apps.html: ${data.platforms.length} platforms`);
}
