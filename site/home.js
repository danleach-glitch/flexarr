// Tabs: click or arrow keys.
const tabs = [...document.querySelectorAll('[role="tab"]')];
function select(tab) {
  for (const t of tabs) {
    const on = t === tab;
    t.setAttribute('aria-selected', on);
    t.tabIndex = on ? 0 : -1;
    document.getElementById(t.getAttribute('aria-controls')).hidden = !on;
  }
  tab.focus();
}
tabs.forEach((tab, i) => {
  tab.addEventListener('click', () => select(tab));
  tab.addEventListener('keydown', (e) => {
    const step = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
    if (step) { e.preventDefault(); select(tabs[(i + step + tabs.length) % tabs.length]); }
  });
});
async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const area = Object.assign(document.createElement('textarea'), { value: text });
    document.body.append(area); area.select(); document.execCommand('copy'); area.remove();
  }
}
// On a phone, send the set-up steps to a computer: the share sheet where there is one, otherwise copy the link.
for (const button of document.querySelectorAll('[data-share]')) {
  button.addEventListener('click', async () => {
    const url = 'https://flexarr.io/install';
    if (navigator.share) {
      try { await navigator.share({ title: 'Set up Flexarr', text: 'Install the Flexarr server on a computer at home:', url }); } catch {}
      return;
    }
    await copyText(url);
    const label = button.lastChild.textContent;
    button.lastChild.textContent = ' Link copied';
    setTimeout(() => (button.lastChild.textContent = label), 1600);
  });
}
// Arrows under the swipe: one card at a time, greyed out at either end.
const rail = document.querySelector('.rail');
const arrows = [...document.querySelectorAll('[data-step]')];
for (const arrow of arrows) {
  arrow.addEventListener('click', () => {
    const card = rail.querySelector('.card');
    rail.scrollBy({ left: Number(arrow.dataset.step) * (card.offsetWidth + parseFloat(getComputedStyle(rail).columnGap)), behavior: 'smooth' });
  });
}
function updateArrows() {
  arrows[0].disabled = rail.scrollLeft < 4;
  arrows[1].disabled = rail.scrollLeft > rail.scrollWidth - rail.clientWidth - 4;
}
rail.addEventListener('scroll', updateArrows, { passive: true });
// Copy buttons, with the old way as a fallback.
for (const pre of document.querySelectorAll('pre')) {
  const button = Object.assign(document.createElement('button'), { type: 'button', className: 'copy', textContent: 'Copy' });
  button.addEventListener('click', async () => {
    await copyText(pre.querySelector('code').textContent);
    button.textContent = 'Copied';
    setTimeout(() => (button.textContent = 'Copy'), 1600);
  });
  pre.append(button);
}
