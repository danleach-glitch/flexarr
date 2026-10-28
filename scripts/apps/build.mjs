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
  const ids = new Set();
  for (const [i, p] of (data.platforms ?? []).entries()) {
    const at = `platforms[${i}]${p.id ? ` (${p.id})` : ''}`;
    for (const key of ['id', 'name', 'icon', 'status', 'summary']) if (!p[key]) problems.push(`${at}: needs "${key}"`);
    if (ids.has(p.id)) problems.push(`${at}: id used twice`);
    ids.add(p.id);
    if (p.runsOn && !(Array.isArray(p.runsOn) && p.runsOn.every(r => typeof r === 'string'))) problems.push(`${at}: runsOn must be a list of names`);
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

const external = href => /^https:/.test(href);
const arrow = '<svg class="arrow" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M7 17 17 7M8 7h9v9"/></svg>';
const pill = p => `<span class="pill ${p.status}">${statuses[p.status].label}</span>`;
const button = (l, i) => {
  const kind = l.store || l.primary ? 'button primary' : i === 0 ? 'button quiet' : 'more';
  return `<a class="${kind}" href="${escape(l.href)}"${external(l.href) ? ' rel="noopener"' : ''}>${l.store ? stores[l.store] : ''}${escape(l.label)}</a>`;
};

// At a glance: one row each, with the first link as the way to get it.
const get = p => {
  const l = p.links?.[0];
  if (!l) return '<span class="none">Not yet</span>';
  return `<a class="get${l.store || l.primary ? ' ready' : ''}" href="${escape(l.href)}"${external(l.href) ? ' rel="noopener"' : ''}>${l.store ? stores[l.store] : ''}${escape(l.short || l.label)}${external(l.href) ? arrow : ''}</a>`;
};
const row = p => `            <tr>
              <td class="mark" aria-hidden="true">${statuses[p.status].icon}</td>
              <th scope="row"><a href="#${escape(p.id)}">${escape(p.name)}</a>${p.version ? `<span class="sub">${escape(p.version)}</span>` : ''}</th>
              <td class="state">${statuses[p.status].label}</td>
              <td class="ver">${p.version ? `<span class="version">${escape(p.version)}</span>` : '<span class="none">–</span>'}</td>
              <td class="go">${get(p)}</td>
            </tr>`;
const table = `<div class="table"><table>
          <thead><tr><th scope="col"><span class="visually-hidden">Status icon</span></th><th scope="col">Platform</th><th scope="col" class="state">Status</th><th scope="col" class="ver">Version</th><th scope="col" class="go">Get it</th></tr></thead>
          <tbody>
${data.platforms.map(row).join('\n')}
          </tbody>
        </table></div>`;

const card = p => `<article class="card${p.runsOn ? ' wide' : ''}" id="${escape(p.id)}">
            <div class="card-body">
              <div class="card-top"><span class="app-icon">${icons[p.icon]}</span>${pill(p)}</div>
              <h3>${escape(p.name)}</h3>
${p.version ? `              <p class="version-line">Version <span class="version">${escape(p.version)}</span></p>\n` : ''}              <p class="summary">${escape(p.summary)}</p>
${p.note ? `              <p class="note">${escape(p.note)}</p>\n` : ''}${p.requires ? `              <p class="requires">${escape(p.requires)}</p>\n` : ''}${p.links?.length ? `              <div class="links">${p.links.map(button).join('')}</div>\n` : ''}            </div>
${p.runsOn ? `            <div class="runs-on">
              <p>Runs on</p>
              <ul>${p.runsOn.map(r => `<li>${escape(r)}</li>`).join('')}</ul>
            </div>
` : ''}          </article>`;

const now = data.platforms.filter(p => ['available', 'beta'].includes(p.status));
const soon = data.platforms.filter(p => !['available', 'beta'].includes(p.status));

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
    :root { --mint: #9fdcb4; }
    .visually-hidden { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
    .kicker { color: var(--silver); font-weight: 700; letter-spacing: 0.08em; text-transform: uppercase; font-size: 0.8rem; margin: 0 0 14px; }
    section { padding-top: clamp(64px, 9vw, 112px); }
    section > h2, .section-head h2 { font-size: clamp(2.4rem, 6vw, 4rem); }

    /* Hero: the headline, then the devices straight on the page. */
    .hero { padding-top: clamp(40px, 7vw, 88px); text-align: center; }
    .hero h1 { font-size: clamp(3rem, 11vw, 7rem); font-weight: 900; }
    .hero .lede { max-width: 620px; margin: 22px auto 0; color: var(--usher); font-size: clamp(1.1rem, 2.4vw, 1.3rem); text-wrap: balance; }
    .hero picture { display: block; margin-top: clamp(32px, 6vw, 64px); }
    .hero img { width: 100%; }

    /* At a glance */
    .section-head { display: flex; flex-wrap: wrap; align-items: end; justify-content: space-between; gap: 16px 32px; }
    .legend { display: flex; flex-wrap: wrap; gap: 6px 18px; margin: 0; padding: 0; list-style: none; color: var(--usher); font-size: 0.92rem; }
    .table { margin-top: 28px; border: 1px solid var(--aisle); border-radius: 22px; background: linear-gradient(180deg, var(--seat), rgb(37 32 44 / 0.55)); overflow: hidden; }
    table { width: 100%; border-collapse: collapse; }
    th, td { padding: 18px 22px; text-align: left; border-top: 1px solid var(--aisle); vertical-align: middle; }
    thead th { border-top: 0; padding-block: 16px; color: var(--usher); font-size: 0.78rem; font-weight: 700; letter-spacing: 0.08em; text-transform: uppercase; }
    tbody tr { transition: background 0.15s; }
    tbody tr:hover { background: rgb(242 238 231 / 0.03); }
    tbody th { font-weight: 700; font-size: 1.05rem; }
    tbody th a { color: var(--screen); text-decoration: none; }
    tbody th a:hover { color: var(--silver); }
    td { color: var(--usher); }
    .mark { width: 1%; padding-right: 0; font-size: 1.15rem; }
    .version { font-variant-numeric: tabular-nums; color: var(--screen); }
    .none { color: var(--usher); opacity: 0.6; }
    .sub { display: none; }
    th.go, td.go { text-align: right; }
    .get { display: inline-flex; align-items: center; gap: 7px; padding: 7px 14px; border-radius: 999px; border: 1px solid var(--aisle); color: var(--screen); font-size: 0.92rem; font-weight: 700; text-decoration: none; white-space: nowrap; }
    .get:hover { border-color: var(--usher); background: var(--seat-raised); }
    .get.ready { background: var(--screen); border-color: var(--screen); color: var(--room); }
    .get.ready:hover { background: #fff; }
    .get svg { width: 15px; height: 15px; flex: none; }
    .get .arrow { width: 13px; height: 13px; opacity: 0.7; }
    @media (max-width: 720px) {
      th, td { padding: 14px 14px; }
      .state, .ver { display: none; }
      .sub { display: block; margin-top: 2px; color: var(--usher); font-weight: 400; font-size: 0.88rem; font-variant-numeric: tabular-nums; }
      .mark { padding-right: 4px; }
      td.go { padding-left: 4px; }
      .get { padding: 6px 11px; font-size: 0.85rem; gap: 5px; }
      .get .arrow { display: none; }
    }

    /* Cards */
    .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 340px), 1fr)); gap: 20px; margin-top: 32px; }
    .card { position: relative; display: grid; border: 1px solid var(--aisle); border-radius: 24px; overflow: hidden; scroll-margin-top: 100px;
      background: radial-gradient(420px 260px at 0% 0%, rgb(var(--accent) / 0.14), transparent 70%), linear-gradient(180deg, var(--seat), rgb(37 32 44 / 0.4)); }
    .card:target { border-color: rgb(var(--accent) / 0.7); }
    .card-body { display: flex; flex-direction: column; gap: 12px; padding: clamp(24px, 3vw, 34px); }
    .card { --accent: 169 194 236; }
    .card:nth-child(3n+2) { --accent: 232 160 191; }
    .card:nth-child(3n) { --accent: 241 194 125; }
    .card-top { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 8px; }
    .app-icon { width: 56px; height: 56px; border-radius: 16px; display: grid; place-items: center; flex: none; background: rgb(var(--accent) / 0.16); color: rgb(var(--accent)); }
    .app-icon svg { width: 26px; height: 26px; }
    .card h3 { font-size: clamp(2rem, 3.4vw, 2.6rem); }
    .card p { margin: 0; }
    .version-line { color: var(--usher); font-size: 0.95rem; margin-top: -4px !important; }
    .summary { color: #cfc8d8; }
    .note, .requires { color: var(--usher); font-size: 0.93rem; }
    .requires::before { content: "Needs "; color: var(--screen); font-weight: 700; }
    .links { display: flex; flex-wrap: wrap; align-items: center; gap: 10px 20px; margin-top: auto; padding-top: 10px; }
    .links .button { min-height: 46px; padding: 0 20px; font-size: 0.95rem; }
    .links .button.primary { box-shadow: none; }
    .links .button svg { width: 18px; height: 18px; }
    .more { font-weight: 700; text-decoration: none; }
    .more:hover { text-decoration: underline; }

    .pill { display: inline-flex; align-items: center; gap: 7px; padding: 5px 12px; border-radius: 999px; font-size: 0.85rem; font-weight: 700; white-space: nowrap; color: var(--c); background: color-mix(in srgb, var(--c) 13%, transparent); }
    .pill::before { content: ""; width: 7px; height: 7px; border-radius: 50%; background: currentColor; }
    .pill.available { --c: var(--mint); }
    .pill.beta { --c: var(--rose); }
    .pill.coming { --c: var(--amber); }
    .pill.development { --c: var(--silver); }
    .pill.planned { --c: var(--usher); }

    /* The server spans the row, with what it runs on beside it. */
    .card.wide { grid-column: 1 / -1; grid-template-columns: minmax(0, 7fr) minmax(0, 5fr); }
    .runs-on { padding: clamp(24px, 3vw, 34px); border-left: 1px solid var(--aisle); background: rgb(27 23 32 / 0.35); display: flex; flex-direction: column; justify-content: center; }
    .runs-on p { margin: 0 0 14px; color: var(--usher); font-size: 0.78rem; font-weight: 700; letter-spacing: 0.08em; text-transform: uppercase; }
    .runs-on ul { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; margin: 0; padding: 0; list-style: none; }
    .runs-on li { padding: 12px 16px; border-radius: 14px; background: var(--seat-raised); border: 1px solid var(--aisle); font-weight: 700; text-align: center; }
    @media (max-width: 760px) {
      .card.wide { grid-template-columns: minmax(0, 1fr); }
      .runs-on { border-left: 0; border-top: 1px solid var(--aisle); }
      .runs-on ul { grid-template-columns: repeat(3, minmax(0, 1fr)); }
      .runs-on li { padding: 10px 6px; font-size: 0.92rem; }
    }

    /* On the way: lighter, dashed. */
    .soon .card { background: transparent; border-style: dashed; }
    .soon .card h3 { font-size: clamp(1.8rem, 3vw, 2.2rem); }
    /* A slim strip across the row, after the cards. */
    .card.ask { grid-column: 1 / -1; }
    .card.ask .card-body { flex-direction: row; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 12px 24px; padding-block: 24px; }
    .card.ask h3 { font-size: clamp(1.7rem, 3vw, 2.1rem); }
    .card.ask p { color: var(--usher); margin-top: 4px; }
    .card.ask .links { margin: 0; padding: 0; }

    .source { margin: clamp(56px, 8vw, 96px) 0 clamp(48px, 6vw, 72px); color: var(--usher); font-size: 0.92rem; text-align: center; }
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
        <p class="lede">One server at home, and an app for every screen in it. Here's what you can get today and what's on the way.</p>
        <picture><source media="(max-width: 640px)" srcset="/shots/screens-m.webp"><img src="/shots/screens.webp" srcset="/shots/screens-1000.webp 1000w, /shots/screens.webp 2000w" sizes="(min-width: 1260px) 1180px, 92vw" alt="Flexarr in a web browser showing Only Murders in the Building on Home, on an iPad showing Home, and on an iPhone showing Barbie's page." fetchpriority="high"></picture>
      </div>

      <section aria-labelledby="glance">
        <div class="section-head">
          <h2 id="glance">At a glance</h2>
          <ul class="legend">
${Object.values(statuses).map(s => `            <li><span aria-hidden="true">${s.icon}</span> ${s.label}</li>`).join('\n')}
          </ul>
        </div>
        ${table}
      </section>

      <section aria-labelledby="now">
        <h2 id="now">Available now</h2>
        <div class="cards">
          ${now.map(card).join('\n          ')}
        </div>
      </section>

      <section class="soon" aria-labelledby="soon">
        <h2 id="soon">On the way</h2>
        <div class="cards">
          ${soon.map(card).join('\n          ')}
          <article class="card ask">
            <div class="card-body">
              <div><h3>Somewhere else?</h3><p>Tell us where you'd like to watch Flexarr next.</p></div>
              <div class="links"><a class="button quiet" href="${repo}/issues" rel="noopener">Ask on GitHub</a></div>
            </div>
          </article>
        </div>
      </section>

      <p class="source">This list is also at <a href="/apps.json">flexarr.io/apps.json</a>, built from <a href="${repo}/blob/main/site/apps.json">site/apps.json</a>.</p>
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
