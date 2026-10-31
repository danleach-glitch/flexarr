// Shared by the /brief and /audit pages. Data comes from the JSON block; ticks from the db collection `checks`
// (doc id = item id, {done, note}); the optional doc `_stage` ({label, kind, note}) overrides the stage pill.
const DATA = JSON.parse(document.getElementById('data').textContent);
let ticks = {}, stageDoc = null, db = null, canWrite = false, hideDone = false;

// Tiny inline markup for data strings: **bold**, *em*, `code`. Everything else is escaped.
function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
function md(s) {
  return esc(s)
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^*]+)\*/g, '<em>$1</em>');
}
const plain = s => String(s ?? '').replace(/[*`]/g, '');
const $ = id => document.getElementById(id);

const isDone = id => !!ticks[id]?.done;
const isDropped = id => { const t = ticks[id]; return !!t && !t.done && typeof t.note === 'string' && t.note.startsWith('dropped'); };
const isResolved = id => isDone(id) || isDropped(id);

// A row: checkbox, body (title, detail lines, note), and on the right the id and a Copy button.
// opts: {lines: [html], pill, sevClass, copyText}
function rowHtml(item, opts = {}) {
  const lines = (opts.lines || []).filter(Boolean).map(l => `<small>${l}</small>`).join('');
  return `<li id="${esc(item.id)}" data-item="${esc(item.id)}"${opts.sevClass ? ` class="sev ${opts.sevClass}"` : ''}>
    <input type="checkbox" class="check" id="c-${esc(item.id)}" data-id="${esc(item.id)}" aria-label="Done: ${esc(plain(item.title))}" disabled>
    <div><b>${opts.pill ? `<span class="pill">${esc(opts.pill)}</span>` : ''}${md(item.title)}</b>${item.you ? '<span class="you">You</span>' : ''}${lines}<small class="note" data-note="${esc(item.id)}" hidden></small></div>
    <div class="side"><span class="idtag">${esc(item.id)}</span><button type="button" class="copy" data-copy="${esc(opts.copyText || item.id)}" aria-label="Copy a prompt for ${esc(item.id)}">Copy</button></div>
  </li>`;
}

// Applies ticks to every row on the page.
function paintRows() {
  for (const li of document.querySelectorAll('li[data-item]')) {
    const id = li.dataset.item, d = isDone(id);
    li.classList.toggle('done', d);
    li.hidden = hideDone && isResolved(id);
    const c = $('c-' + id); c.checked = d; c.disabled = !canWrite;
    const n = li.querySelector('[data-note]'), t = ticks[id]?.note;
    n.hidden = !t; n.textContent = t ? (d ? '✓ ' : '') + t : '';
    n.classList.toggle('dropped', isDropped(id));
  }
}

function setProgress(ids, areas) {
  const done = ids.filter(isResolved).length;
  $('p-done').textContent = done;
  $('p-total').textContent = ids.length;
  $('p-bar').style.width = ids.length ? (100 * done / ids.length) + '%' : '0';
  $('p-areas').innerHTML = areas.map(a =>
    `<a href="#${a.anchor}">${esc(a.name)} <b>${a.ids.filter(isResolved).length}/${a.ids.length}</b></a>`).join('');
  return done;
}

// stage = {label, kind: waiting|building|done|info, note}; a `_stage` doc in checks wins.
function setStage(stage) {
  const s = stageDoc?.label ? stageDoc : stage;
  const pill = $('stage');
  pill.className = 'stage ' + (s.kind || 'info');
  pill.innerHTML = esc(s.label) + (s.note ? ` <small>${md(s.note)}</small>` : '');
}

function sectionHtml(id, title, inner, { intro, count } = {}) {
  return `<section id="${id}"><div class="sec-head"><h2>${md(title)}</h2>${count != null ? `<span class="count" data-count="${id}">${count}</span>` : ''}</div>
    ${intro ? `<p class="intro">${md(intro)}</p>` : ''}${inner}</section>`;
}
const listHtml = items => `<div class="box"><ul class="plain">${items.map(i => `<li>${md(i)}</li>`).join('')}</ul></div>`;

function parityHtml(parity) {
  const rows = parity.rows.map(r => `<tr><td class="mark">${esc(r.mark)}</td><td class="client">${md(r.client)}</td><td>${md(r.what)}</td></tr>`).join('');
  return sectionHtml('parity', parity.heading || 'Where it lands',
    `<div class="tablebox"><table><thead><tr><th></th><th>Client</th><th>What changes</th></tr></thead><tbody>${rows}</tbody></table></div>
     <p class="legend">${md(parity.legend || '✅ built here · 🟡 works, with small touches · ❌ not in this branch')}</p>`);
}

function wireControls(render) {
  $('filter').addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    document.querySelectorAll('#filter button').forEach(x => x.setAttribute('aria-pressed', String(x === b)));
    hideDone = b.dataset.h === '1'; render();
  });
  document.addEventListener('click', async e => {
    const b = e.target.closest('button.copy'); if (!b) return;
    try { await navigator.clipboard.writeText(b.dataset.copy); b.textContent = 'Copied'; }
    catch { b.textContent = 'Copy blocked'; }
    setTimeout(() => { b.textContent = 'Copy'; }, 1600);
  });
  document.addEventListener('change', async e => {
    const c = e.target.closest('.check[data-id]'); if (!c || !db) return;
    const id = c.dataset.id, prev = ticks[id];
    ticks[id] = { ...(prev || {}), done: c.checked }; render();
    try { await db.collection('checks').doc(id).set({ ...(prev || {}), done: c.checked, by: 'page' }); }
    catch { ticks[id] = prev; render(); $('p-sync').textContent = 'Couldn’t save that tick. You may only have view access.'; }
  });
}

function loadTicks(render) {
  const sync = $('p-sync');
  sync.textContent = 'Loading saved progress…';
  const timer = setTimeout(() => { if (!db) sync.textContent = 'Saved progress isn’t available in this view.'; }, 10000);
  (async () => {
    try {
      db = await window.claude?.use?.('db');
      const user = await window.claude?.use?.('user');
      canWrite = !!db && (user ? user.can?.('data.write') !== false : true);
    } catch { db = null; }
    clearTimeout(timer);
    if (!db) { sync.textContent = 'Saved progress isn’t available in this view.'; return; }
    db.collection('checks').onSnapshot(snap => {
      ticks = {}; stageDoc = null;
      snap.docs.forEach(d => { if (d.id === '_stage') stageDoc = d.data(); else ticks[d.id] = d.data() || {}; });
      sync.textContent = 'Live · ticks itself as work lands';
      render();
    }, () => { sync.textContent = 'Lost the connection to saved progress. Reload to try again.'; });
  })();
}
