// Builds flexarr.io/guide: docs/guide/*.md (in the order docs/guide/index.json gives) into
// site/guide/<slug>.html, site/guide/index.html and site/guide/search.json.
// Run with `make guide`. The output is committed, so Vercel serves it as plain files.
import fs from 'node:fs';
import path from 'node:path';
import { Marked } from 'marked';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../..');
const source = path.join(root, 'docs/guide');
const out = path.join(root, 'site/guide');
const repo = 'https://github.com/danleach-glitch/flexarr';
const check = process.argv.includes('--check');

const { sections } = JSON.parse(fs.readFileSync(path.join(source, 'index.json'), 'utf8'));
const order = sections.flatMap(s => s.pages.map(slug => ({ slug, section: s.title })));
const slugs = new Set(order.map(p => p.slug));

const escape = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const plain = s => s.replace(/<[^>]+>/g, '').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'");
const slugify = s => plain(s).toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '');

function frontMatter(text, file) {
  const match = /^---\n([\s\S]*?)\n---\n/.exec(text);
  if (!match) throw new Error(`${file}: no front matter`);
  const meta = Object.fromEntries(match[1].split('\n').filter(Boolean).map(line => {
    const at = line.indexOf(':');
    return [line.slice(0, at).trim(), line.slice(at + 1).trim().replace(/^["']|["']$/g, '')];
  }));
  if (!meta.title || !meta.description) throw new Error(`${file}: front matter needs title and description`);
  return { meta, body: text.slice(match[0].length) };
}

// Links between articles are written as `other-page.md#part` so they work on GitHub too.
function rewrite(href, file, missing) {
  if (/^([a-z]+:|\/|#)/i.test(href)) return href;
  const [target, hash = ''] = href.split('#');
  const slug = target.replace(/\.md$/, '');
  if (!slugs.has(slug)) { missing.push(`${file}: link to "${href}"`); return href; }
  return `/guide/${slug}${hash ? '#' + hash : ''}`;
}

const pages = order.map(({ slug, section }) => {
  const file = `${slug}.md`;
  const full = path.join(source, file);
  if (!fs.existsSync(full)) return { slug, section, missing: true };
  const { meta, body } = frontMatter(fs.readFileSync(full, 'utf8'), file);
  const toc = [];
  const missing = [];
  const used = new Set();
  const marked = new Marked({
    gfm: true,
    renderer: {
      heading({ tokens, depth }) {
        const html = this.parser.parseInline(tokens);
        let id = slugify(html);
        for (let n = 2; used.has(id); n++) id = `${slugify(html)}-${n}`;
        used.add(id);
        if (depth === 2) toc.push({ id, text: plain(html) });
        return `<h${depth} id="${id}"><a class="anchor" href="#${id}" aria-hidden="true" tabindex="-1">#</a>${html}</h${depth}>\n`;
      },
      link({ href, title, tokens }) {
        const to = rewrite(href, file, missing);
        const external = /^https?:/.test(to) && !to.startsWith('https://flexarr.io');
        return `<a href="${escape(to)}"${title ? ` title="${escape(title)}"` : ''}${external ? ' rel="noopener"' : ''}>${this.parser.parseInline(tokens)}</a>`;
      },
    },
  });
  // Tables scroll sideways on a phone instead of widening the page.
  let html = marked.parse(body).replace(/<table>/g, '<div class="table"><table>').replace(/<\/table>/g, '</table></div>');
  // > **Tip** … becomes a callout.
  html = html.replace(/<blockquote>\s*<p><strong>(Tip|Note|Important)<\/strong>:?\s*/g,
    (_, kind) => `<blockquote class="callout ${kind.toLowerCase()}"><p><strong>${kind}</strong> `);
  if (missing.length) console.warn(missing.join('\n'));
  const text = plain(html).replace(/\s+/g, ' ').trim();
  return { slug, section, meta, html, toc, text, words: text.split(' ').length };
});

const ready = pages.filter(p => !p.missing);
for (const p of pages.filter(p => p.missing)) console.warn(`missing: docs/guide/${p.slug}.md`);

const fonts = '<link rel="preconnect" href="https://fonts.googleapis.com">\n  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>\n  <link href="https://fonts.googleapis.com/css2?family=Atkinson+Hyperlegible+Next:wght@400;500;700&family=Big+Shoulders+Display:wght@700;800;900&display=swap" rel="stylesheet">';

const head = ({ title, description, url }) => `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${escape(title)}</title>
  <meta name="description" content="${escape(description)}">
  <meta name="theme-color" content="#1b1720">
  <meta name="color-scheme" content="dark">
  <meta name="apple-itunes-app" content="app-id=6826618084">
  <link rel="canonical" href="https://flexarr.io${url}">
  <link rel="icon" href="/favicon.svg" type="image/svg+xml">
  <link rel="apple-touch-icon" href="/apple-touch-icon.png">
  <meta property="og:title" content="${escape(title)}">
  <meta property="og:description" content="${escape(description)}">
  <meta property="og:image" content="https://flexarr.io/shots/og.jpg">
  <meta property="og:url" content="https://flexarr.io${url}">
  <meta name="twitter:card" content="summary_large_image">
  ${fonts}
  <link rel="stylesheet" href="/base.css">
  <link rel="stylesheet" href="/guide/guide.css">
</head>`;

const header = `<a class="skip" href="#main">Skip to the article</a>
  <div class="glow" aria-hidden="true"></div>
  <header class="site-header">
    <div class="wrap bar">
      <a class="wordmark" href="/" aria-label="Flexarr home"><img src="/logo-mark.svg" alt="">flexarr</a>
      <nav class="site-nav" aria-label="Main">
        <a href="/#features">Features</a>
        <a href="/#apps">Apps</a>
        <a class="keep" href="/guide" aria-current="page">Guide</a>
        <a class="github keep" href="${repo}">GitHub</a>
      </nav>
    </div>
  </header>`;

const footer = `<footer class="site-footer">
    <div class="wrap bar">
      <span>Flexarr is free for noncommercial use under the PolyForm Noncommercial License.<br>Not affiliated with the *arr projects.</span>
      <nav aria-label="Project">
        <a href="/guide">Guide</a>
        <a href="${repo}">GitHub</a>
        <a href="${repo}/releases">Releases</a>
        <a href="${repo}/blob/main/CHANGELOG.md">Changelog</a>
        <a href="${repo}/issues">Report a problem</a>
        <a href="/privacy">Privacy</a>
      </nav>
    </div>
  </footer>
  <script src="/guide/guide.js" defer></script>
</body>
</html>
`;

const search = `<div class="search" role="search">
          <label class="visually-hidden" for="guide-search">Search the guide</label>
          <input id="guide-search" type="search" placeholder="Search the guide" autocomplete="off" spellcheck="false" aria-controls="guide-results">
          <ol id="guide-results" class="results" hidden></ol>
        </div>`;

const sidebar = current => `<aside class="sidebar" aria-label="Guide">
        ${search}
        <details class="contents"${current ? '' : ' open'}>
          <summary>Guide contents</summary>
          <nav>
${sections.map(s => `            <p class="section">${escape(s.title)}</p>
            <ul>
${s.pages.map(slug => {
  const p = ready.find(r => r.slug === slug);
  return p ? `              <li><a href="/guide/${slug}"${slug === current ? ' aria-current="page"' : ''}>${escape(p.meta.nav || p.meta.title)}</a></li>` : '';
}).filter(Boolean).join('\n')}
            </ul>`).join('\n')}
          </nav>
        </details>
      </aside>`;

function articlePage(p) {
  const i = ready.indexOf(p);
  const prev = ready[i - 1];
  const next = ready[i + 1];
  const minutes = Math.max(1, Math.round(p.words / 220));
  return `${head({ title: `${p.meta.title} · Flexarr guide`, description: p.meta.description, url: `/guide/${p.slug}` })}
<body class="guide">
  ${header}
  <div class="wrap layout">
    ${sidebar(p.slug)}
    <main id="main">
      <article>
        <p class="crumbs"><a href="/guide">Guide</a> <span aria-hidden="true">›</span> ${escape(p.section)}</p>
        <h1>${escape(p.meta.title)}</h1>
        <p class="lede">${escape(p.meta.description)}</p>
        <p class="meta">${minutes} min read</p>
${p.toc.length > 2 ? `        <nav class="toc" aria-label="On this page">
          <p>On this page</p>
          <ol>
${p.toc.map(t => `            <li><a href="#${t.id}">${escape(t.text)}</a></li>`).join('\n')}
          </ol>
        </nav>
` : ''}        <div class="prose">
${p.html}
        </div>
        <nav class="pager" aria-label="More of the guide">
${prev ? `          <a class="prev" href="/guide/${prev.slug}"><span>Previous</span>${escape(prev.meta.title)}</a>` : '          <span></span>'}
${next ? `          <a class="next" href="/guide/${next.slug}"><span>Next</span>${escape(next.meta.title)}</a>` : ''}
        </nav>
        <p class="edit"><a href="${repo}/blob/main/docs/guide/${p.slug}.md">Edit this page on GitHub</a> · <a href="${repo}/issues/new?title=${encodeURIComponent(`Guide: ${p.meta.title}`)}">Something wrong or missing?</a></p>
      </article>
    </main>
  </div>
  ${footer}`;
}

const starts = [
  ['what-you-need', 'New to this?', 'What Flexarr is, what it runs on, and whether you need Sonarr and Radarr.'],
  ['first-setup', 'Got the app?', 'Your iPhone needs a Flexarr server at home. Set one up, then connect.'],
  ['connect-arrs', 'Run the *arrs?', 'Point Flexarr at your media and add Sonarr, Radarr, Lidarr and Seerr.'],
];

function indexPage() {
  return `${head({ title: 'Guide · Flexarr', description: 'How to install Flexarr, connect your Apple TV and iPhone, and get the most from films, TV, music, podcasts and live TV.', url: '/guide' })}
<body class="guide guide-home">
  ${header}
  <main id="main">
    <div class="wrap intro">
      <p class="kicker">Guide</p>
      <h1>Everything about Flexarr, step by step.</h1>
      <p class="lede">Install the server, connect your Apple TV and iPhone, and get the most out of films, TV, music, podcasts and live TV.</p>
      ${search}
    </div>
    <div class="wrap starts">
${starts.map(([slug, kicker, text]) => {
  const p = ready.find(r => r.slug === slug);
  return p ? `      <a class="start" href="/guide/${slug}"><span class="kicker">${escape(kicker)}</span><strong>${escape(p.meta.title)}</strong><span>${escape(text)}</span></a>` : '';
}).join('\n')}
    </div>
    <div class="wrap shelves">
${sections.map(s => `      <section class="shelf">
        <h2>${escape(s.title)}</h2>
        <ul>
${s.pages.map(slug => ready.find(r => r.slug === slug)).filter(Boolean).map(p => `          <li><a href="/guide/${p.slug}"><strong>${escape(p.meta.title)}</strong><span>${escape(p.meta.description)}</span></a></li>`).join('\n')}
        </ul>
      </section>`).join('\n')}
    </div>
    <div class="wrap help">
      <h2>Still stuck?</h2>
      <p>Look through <a href="/guide/troubleshooting">Troubleshooting</a>, or <a href="${repo}/issues/new">open an issue on GitHub</a> with the zip from <strong>Settings › Server › Logs › Download diagnostics</strong> attached.</p>
    </div>
  </main>
  ${footer}`;
}

const index = ready.map(p => ({
  slug: p.slug, title: p.meta.title, section: p.section, description: p.meta.description,
  headings: p.toc.map(t => [t.id, t.text]), text: p.text.slice(0, 4000),
}));

const files = new Map([
  ...ready.map(p => [`${p.slug}.html`, articlePage(p)]),
  ['index.html', indexPage()],
  ['search.json', JSON.stringify(index)],
]);

if (check) {
  const stale = [...files].filter(([name, body]) => {
    const file = path.join(out, name);
    return !fs.existsSync(file) || fs.readFileSync(file, 'utf8') !== body;
  }).map(([name]) => name);
  if (stale.length) { console.error(`site/guide is out of date (${stale.join(', ')}); run make guide`); process.exit(1); }
  console.log('site/guide is up to date');
} else {
  fs.mkdirSync(out, { recursive: true });
  for (const [name, body] of files) fs.writeFileSync(path.join(out, name), body);
  console.log(`built ${ready.length} of ${order.length} guide pages into site/guide`);
}
