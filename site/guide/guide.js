// The guide: search over search.json, copy buttons on code, and the contents open on the page you're on.

// Copy buttons, with the old way as a fallback.
for (const pre of document.querySelectorAll('pre')) {
  const button = Object.assign(document.createElement('button'), { type: 'button', className: 'copy', textContent: 'Copy' });
  button.addEventListener('click', async () => {
    const text = pre.querySelector('code')?.textContent ?? pre.textContent;
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      const area = Object.assign(document.createElement('textarea'), { value: text });
      document.body.append(area); area.select(); document.execCommand('copy'); area.remove();
    }
    button.textContent = 'Copied';
    setTimeout(() => (button.textContent = 'Copy'), 1600);
  });
  pre.append(button);
}

// On wide screens the contents are always open; on a phone they fold away.
const contents = document.querySelector('.contents');
if (contents && matchMedia('(min-width: 921px)').matches) contents.open = true;

// Search: every word must appear; titles and headings count for more than body text.
const input = document.getElementById('guide-search');
const list = document.getElementById('guide-results');
let pages;
let active = -1;

const escape = s => s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const mark = (text, words) => words.reduce((html, w) => html.replace(new RegExp(`(${w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')})`, 'gi'), '<mark>$1</mark>'), escape(text));

async function load() {
  if (!pages) {
    try { pages = await (await fetch('/guide/search.json')).json(); } catch { pages = []; }
  }
  return pages;
}

function score(page, words) {
  let total = 0;
  for (const w of words) {
    const title = page.title.toLowerCase().includes(w) ? 10 : 0;
    const heading = page.headings.some(([, h]) => h.toLowerCase().includes(w)) ? 5 : 0;
    const desc = page.description.toLowerCase().includes(w) ? 3 : 0;
    const body = page.text.toLowerCase().includes(w) ? 1 : 0;
    if (!(title || heading || desc || body)) return 0;
    total += title + heading + desc + body;
  }
  return total;
}

function show(items, words) {
  active = -1;
  if (!words.length) { list.hidden = true; input.setAttribute('aria-expanded', 'false'); return; }
  list.innerHTML = items.length
    ? items.map(({ page, anchor }) => {
        const href = `/guide/${page.slug}${anchor ? '#' + anchor[0] : ''}`;
        const where = anchor ? `${page.title} › ${anchor[1]}` : page.title;
        return `<li><a href="${href}">${mark(where, words)}<small>${mark(page.description, words)}</small></a></li>`;
      }).join('')
    : '<li class="none">Nothing found. Try fewer words, or look in Troubleshooting.</li>';
  list.hidden = false;
  input.setAttribute('aria-expanded', 'true');
}

if (input && list) {
  input.setAttribute('aria-expanded', 'false');
  input.addEventListener('focus', load, { once: true });
  input.addEventListener('input', async () => {
    const words = input.value.toLowerCase().split(/\s+/).filter(w => w.length > 1);
    const all = await load();
    const found = all
      .map(page => ({ page, score: score(page, words), anchor: page.headings.find(([, h]) => words.every(w => h.toLowerCase().includes(w))) }))
      .filter(r => r.score)
      .sort((a, b) => b.score - a.score)
      .slice(0, 8);
    show(found, words);
  });
  input.addEventListener('keydown', e => {
    const links = [...list.querySelectorAll('a')];
    if (e.key === 'Escape') { input.value = ''; show([], []); return; }
    if (!links.length || !['ArrowDown', 'ArrowUp', 'Enter'].includes(e.key)) return;
    e.preventDefault();
    if (e.key === 'Enter') { (links[active] ?? links[0]).click(); return; }
    active = (active + (e.key === 'ArrowDown' ? 1 : -1) + links.length) % links.length;
    links.forEach((a, i) => a.setAttribute('aria-selected', i === active));
    links[active].scrollIntoView({ block: 'nearest' });
  });
  document.addEventListener('click', e => { if (!e.target.closest('.search')) list.hidden = true; });
}
