// flexarr.io/join#<invite>: the page a family invite opens when the Flexarr app isn't on the device yet (with the app,
// the link opens it straight away). The invite stays in the page: it's after the #, which browsers never send.
// See docs/remote-guests.md.
(function () {
  const show = (id) => {
    for (const each of ['invite', 'expired', 'missing']) document.getElementById(each).hidden = each !== id;
  };

  const raw = location.hash.slice(1);
  let invite = null;
  try {
    const base64 = raw.replace(/-/g, '+').replace(/_/g, '/');
    const json = new TextDecoder().decode(Uint8Array.from(atob(base64 + '='.repeat((4 - (base64.length % 4)) % 4)), (c) => c.charCodeAt(0)));
    invite = JSON.parse(json);
  } catch {
    invite = null;
  }

  if (!invite || invite.v !== 1 || typeof invite.t !== 'string' || typeof invite.k !== 'string') {
    show('missing');
    return;
  }

  const owner = typeof invite.n === 'string' && invite.n.trim() ? invite.n.trim() : 'Someone';
  if (invite.e && Date.parse(invite.e) < Date.now()) {
    document.getElementById('expired-lede').textContent = `Ask ${owner} for a new one.`;
    show('expired');
    return;
  }

  document.title = `${owner} invited you to watch on Flexarr`;
  document.getElementById('title').textContent = `${owner} invited you to watch.`;
  if (typeof invite.p === 'string' && invite.p.trim()) {
    document.getElementById('lede').textContent = `Watch ${owner}'s films and shows as ${invite.p.trim()}, from wherever you are.`;
  }

  // The app's own address for the invite, for when the link was opened somewhere that doesn't hand it to the app.
  document.getElementById('open').href = `flexarr://join#${raw}`;

  const agent = navigator.userAgent;
  const apple = /iPhone|iPad|iPod/.test(agent) || (/Macintosh/.test(agent) && navigator.maxTouchPoints > 1);
  const android = /Android/.test(agent);
  if (android) document.getElementById('app-store').hidden = true;
  if (apple) document.getElementById('android').hidden = true;
  if (!apple && !android) document.getElementById('desktop').hidden = false;

  // Copied only when asked: the link carries the invite's key, which shouldn't sit in clipboard history (or a
  // cloud clipboard) for the three days it works.
  const copied = document.getElementById('copied');
  document.getElementById('copy').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(location.href);
      copied.textContent = 'Copied. Once Flexarr is installed, open the link again.';
    } catch {
      copied.textContent = "Couldn't copy here. Use Open in Flexarr instead.";
    }
  });

  show('invite');
})();
