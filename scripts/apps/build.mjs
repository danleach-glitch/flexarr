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
  coming: { icon: '🕒', label: 'Coming soon' },
  development: { icon: '🛠️', label: 'In development' },
  planned: { icon: '🗓️', label: 'Planned' },
};

const stroke = d => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${d}</svg>`;
// Each platform's own mark, in its colours: Apple, Android and Home Assistant from Simple Icons (CC0), and Flexarr's logo.
const icons = {
  flexarr: '<img src="/logo-mark.svg" alt="">',
  apple: '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12.152 6.896c-.948 0-2.415-1.078-3.96-1.04-2.04.027-3.91 1.183-4.961 3.014-2.117 3.675-.546 9.103 1.519 12.09 1.013 1.454 2.208 3.09 3.792 3.039 1.52-.065 2.09-.987 3.935-.987 1.831 0 2.35.987 3.96.948 1.637-.026 2.676-1.48 3.676-2.948 1.156-1.688 1.636-3.325 1.662-3.415-.039-.013-3.182-1.221-3.22-4.857-.026-3.04 2.48-4.494 2.597-4.559-1.429-2.09-3.623-2.324-4.39-2.376-2-.156-3.675 1.09-4.61 1.09zM15.53 3.83c.843-1.012 1.4-2.427 1.245-3.83-1.207.052-2.662.805-3.532 1.818-.78.896-1.454 2.338-1.273 3.714 1.338.104 2.715-.688 3.559-1.701"/></svg>',
  android: '<svg viewBox="0 0 24 24" fill="#3ddc84" aria-hidden="true"><path d="M18.4395 5.5586c-.675 1.1664-1.352 2.3318-2.0274 3.498-.0366-.0155-.0742-.0286-.1113-.043-1.8249-.6957-3.484-.8-4.42-.787-1.8551.0185-3.3544.4643-4.2597.8203-.084-.1494-1.7526-3.021-2.0215-3.4864a1.1451 1.1451 0 0 0-.1406-.1914c-.3312-.364-.9054-.4859-1.379-.203-.475.282-.7136.9361-.3886 1.5019 1.9466 3.3696-.0966-.2158 1.9473 3.3593.0172.031-.4946.2642-1.3926 1.0177C2.8987 12.176.452 14.772 0 18.9902h24c-.119-1.1108-.3686-2.099-.7461-3.0683-.7438-1.9118-1.8435-3.2928-2.7402-4.1836a12.1048 12.1048 0 0 0-2.1309-1.6875c.6594-1.122 1.312-2.2559 1.9649-3.3848.2077-.3615.1886-.7956-.0079-1.1191a1.1001 1.1001 0 0 0-.8515-.5332c-.5225-.0536-.9392.3128-1.0488.5449zm-.0391 8.461c.3944.5926.324 1.3306-.1563 1.6503-.4799.3197-1.188.0985-1.582-.4941-.3944-.5927-.324-1.3307.1563-1.6504.4727-.315 1.1812-.1086 1.582.4941zM7.207 13.5273c.4803.3197.5506 1.0577.1563 1.6504-.394.5926-1.1038.8138-1.584.4941-.48-.3197-.5503-1.0577-.1563-1.6504.4008-.6021 1.1087-.8106 1.584-.4941z"/></svg>',
  'home-assistant': '<svg viewBox="0 0 24 24" fill="#18bcf2" aria-hidden="true"><path d="M22.939 10.627 13.061.749a1.505 1.505 0 0 0-2.121 0l-9.879 9.878C.478 11.21 0 12.363 0 13.187v9c0 .826.675 1.5 1.5 1.5h9.227l-4.063-4.062a2.034 2.034 0 0 1-.664.113c-1.13 0-2.05-.92-2.05-2.05s.92-2.05 2.05-2.05 2.05.92 2.05 2.05c0 .233-.041.456-.113.665l3.163 3.163V9.928a2.05 2.05 0 0 1-1.15-1.84c0-1.13.92-2.05 2.05-2.05s2.05.92 2.05 2.05a2.05 2.05 0 0 1-1.15 1.84v8.127l3.146-3.146A2.051 2.051 0 0 1 18 12.239c1.13 0 2.05.92 2.05 2.05s-.92 2.05-2.05 2.05c-.25 0-.488-.047-.709-.13L12.9 20.602v3.088h9.6c.825 0 1.5-.675 1.5-1.5v-9c0-.825-.477-1.977-1.061-2.561z"/></svg>',
};
// What the server runs on. Brand marks from Simple Icons (CC0); Synology's is a wordmark too small to read, so it gets
// a drive bay, and Windows (not in Simple Icons) four panes.
const systems = {
  Docker: '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M13.983 11.078h2.119a.186.186 0 00.186-.185V9.006a.186.186 0 00-.186-.186h-2.119a.185.185 0 00-.185.185v1.888c0 .102.083.185.185.185m-2.954-5.43h2.118a.186.186 0 00.186-.186V3.574a.186.186 0 00-.186-.185h-2.118a.185.185 0 00-.185.185v1.888c0 .102.082.185.185.185m0 2.716h2.118a.187.187 0 00.186-.186V6.29a.186.186 0 00-.186-.185h-2.118a.185.185 0 00-.185.185v1.887c0 .102.082.185.185.186m-2.93 0h2.12a.186.186 0 00.184-.186V6.29a.185.185 0 00-.185-.185H8.1a.185.185 0 00-.185.185v1.887c0 .102.083.185.185.186m-2.964 0h2.119a.186.186 0 00.185-.186V6.29a.185.185 0 00-.185-.185H5.136a.186.186 0 00-.186.185v1.887c0 .102.084.185.186.186m5.893 2.715h2.118a.186.186 0 00.186-.185V9.006a.186.186 0 00-.186-.186h-2.118a.185.185 0 00-.185.185v1.888c0 .102.082.185.185.185m-2.93 0h2.12a.185.185 0 00.184-.185V9.006a.185.185 0 00-.184-.186h-2.12a.185.185 0 00-.184.185v1.888c0 .102.083.185.185.185m-2.964 0h2.119a.185.185 0 00.185-.185V9.006a.185.185 0 00-.184-.186h-2.12a.186.186 0 00-.186.186v1.887c0 .102.084.185.186.185m-2.92 0h2.12a.185.185 0 00.184-.185V9.006a.185.185 0 00-.184-.186h-2.12a.185.185 0 00-.184.185v1.888c0 .102.082.185.185.185M23.763 9.89c-.065-.051-.672-.51-1.954-.51-.338.001-.676.03-1.01.087-.248-1.7-1.653-2.53-1.716-2.566l-.344-.199-.226.327c-.284.438-.49.922-.612 1.43-.23.97-.09 1.882.403 2.661-.595.332-1.55.413-1.744.42H.751a.751.751 0 00-.75.748 11.376 11.376 0 00.692 4.062c.545 1.428 1.355 2.48 2.41 3.124 1.18.723 3.1 1.137 5.275 1.137.983.003 1.963-.086 2.93-.266a12.248 12.248 0 003.823-1.389c.98-.567 1.86-1.288 2.61-2.136 1.252-1.418 1.998-2.997 2.553-4.4h.221c1.372 0 2.215-.549 2.68-1.009.309-.293.55-.65.707-1.046l.098-.288Z"/></svg>',
  Synology: stroke('<rect x="4" y="3" width="16" height="18" rx="2.5"/><path d="M8 7.5h8M8 11.5h8"/><path d="M8.5 17h.01M12 17h.01"/>'),
  Unraid: '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M11.406 8.528h1.17v6.926h-1.17zM1.17 15.454H0V8.528h1.17zm4.534.828h1.17v2.645h-1.17zm-2.86-2.969h1.169v4.282h-1.17zm5.703 0h1.17v4.282h-1.17zM22.83 8.528H24v6.926h-1.17zm-4.534-.81h-1.17V5.073h1.17zm2.86 2.95h-1.169V6.406h1.17zm-5.72 0h-1.17V6.406h1.17z"/></svg>',
  Linux: '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12.504 0c-.155 0-.315.008-.48.021-4.226.333-3.105 4.807-3.17 6.298-.076 1.092-.3 1.953-1.05 3.02-.885 1.051-2.127 2.75-2.716 4.521-.278.832-.41 1.684-.287 2.489a.424.424 0 00-.11.135c-.26.268-.45.6-.663.839-.199.199-.485.267-.797.4-.313.136-.658.269-.864.68-.09.189-.136.394-.132.602 0 .199.027.4.055.536.058.399.116.728.04.97-.249.68-.28 1.145-.106 1.484.174.334.535.47.94.601.81.2 1.91.135 2.774.6.926.466 1.866.67 2.616.47.526-.116.97-.464 1.208-.946.587-.003 1.23-.269 2.26-.334.699-.058 1.574.267 2.577.2.025.134.063.198.114.333l.003.003c.391.778 1.113 1.132 1.884 1.071.771-.06 1.592-.536 2.257-1.306.631-.765 1.683-1.084 2.378-1.503.348-.199.629-.469.649-.853.023-.4-.2-.811-.714-1.376v-.097l-.003-.003c-.17-.2-.25-.535-.338-.926-.085-.401-.182-.786-.492-1.046h-.003c-.059-.054-.123-.067-.188-.135a.357.357 0 00-.19-.064c.431-1.278.264-2.55-.173-3.694-.533-1.41-1.465-2.638-2.175-3.483-.796-1.005-1.576-1.957-1.56-3.368.026-2.152.236-6.133-3.544-6.139zm.529 3.405h.013c.213 0 .396.062.584.198.19.135.33.332.438.533.105.259.158.459.166.724 0-.02.006-.04.006-.06v.105a.086.086 0 01-.004-.021l-.004-.024a1.807 1.807 0 01-.15.706.953.953 0 01-.213.335.71.71 0 00-.088-.042c-.104-.045-.198-.064-.284-.133a1.312 1.312 0 00-.22-.066c.05-.06.146-.133.183-.198.053-.128.082-.264.088-.402v-.02a1.21 1.21 0 00-.061-.4c-.045-.134-.101-.2-.183-.333-.084-.066-.167-.132-.267-.132h-.016c-.093 0-.176.03-.262.132a.8.8 0 00-.205.334 1.18 1.18 0 00-.09.4v.019c.002.089.008.179.02.267-.193-.067-.438-.135-.607-.202a1.635 1.635 0 01-.018-.2v-.02a1.772 1.772 0 01.15-.768c.082-.22.232-.406.43-.533a.985.985 0 01.594-.2zm-2.962.059h.036c.142 0 .27.048.399.135.146.129.264.288.344.465.09.199.14.4.153.667v.004c.007.134.006.2-.002.266v.08c-.03.007-.056.018-.083.024-.152.055-.274.135-.393.2.012-.09.013-.18.003-.267v-.015c-.012-.133-.04-.2-.082-.333a.613.613 0 00-.166-.267.248.248 0 00-.183-.064h-.021c-.071.006-.13.04-.186.132a.552.552 0 00-.12.27.944.944 0 00-.023.33v.015c.012.135.037.2.08.334.046.134.098.2.166.268.01.009.02.018.034.024-.07.057-.117.07-.176.136a.304.304 0 01-.131.068 2.62 2.62 0 01-.275-.402 1.772 1.772 0 01-.155-.667 1.759 1.759 0 01.08-.668 1.43 1.43 0 01.283-.535c.128-.133.26-.2.418-.2zm1.37 1.706c.332 0 .733.065 1.216.399.293.2.523.269 1.052.468h.003c.255.136.405.266.478.399v-.131a.571.571 0 01.016.47c-.123.31-.516.643-1.063.842v.002c-.268.135-.501.333-.775.465-.276.135-.588.292-1.012.267a1.139 1.139 0 01-.448-.067 3.566 3.566 0 01-.322-.198c-.195-.135-.363-.332-.612-.465v-.005h-.005c-.4-.246-.616-.512-.686-.71-.07-.268-.005-.47.193-.6.224-.135.38-.271.483-.336.104-.074.143-.102.176-.131h.002v-.003c.169-.202.436-.47.839-.601.139-.036.294-.065.466-.065zm2.8 2.142c.358 1.417 1.196 3.475 1.735 4.473.286.534.855 1.659 1.102 3.024.156-.005.33.018.513.064.646-1.671-.546-3.467-1.089-3.966-.22-.2-.232-.335-.123-.335.59.534 1.365 1.572 1.646 2.757.13.535.16 1.104.021 1.67.067.028.135.06.205.067 1.032.534 1.413.938 1.23 1.537v-.043c-.06-.003-.12 0-.18 0h-.016c.151-.467-.182-.825-1.065-1.224-.915-.4-1.646-.336-1.77.465-.008.043-.013.066-.018.135-.068.023-.139.053-.209.064-.43.268-.662.669-.793 1.187-.13.533-.17 1.156-.205 1.869v.003c-.02.334-.17.838-.319 1.35-1.5 1.072-3.58 1.538-5.348.334a2.645 2.645 0 00-.402-.533 1.45 1.45 0 00-.275-.333c.182 0 .338-.03.465-.067a.615.615 0 00.314-.334c.108-.267 0-.697-.345-1.163-.345-.467-.931-.995-1.788-1.521-.63-.4-.986-.87-1.15-1.396-.165-.534-.143-1.085-.015-1.645.245-1.07.873-2.11 1.274-2.763.107-.065.037.135-.408.974-.396.751-1.14 2.497-.122 3.854a8.123 8.123 0 01.647-2.876c.564-1.278 1.743-3.504 1.836-5.268.048.036.217.135.289.202.218.133.38.333.59.465.21.201.477.335.876.335.039.003.075.006.11.006.412 0 .73-.134.997-.268.29-.134.52-.334.74-.4h.005c.467-.135.835-.402 1.044-.7zm2.185 8.958c.037.6.343 1.245.882 1.377.588.134 1.434-.333 1.791-.765l.211-.01c.315-.007.577.01.847.268l.003.003c.208.199.305.53.391.876.085.4.154.78.409 1.066.486.527.645.906.636 1.14l.003-.007v.018l-.003-.012c-.015.262-.185.396-.498.595-.63.401-1.746.712-2.457 1.57-.618.737-1.37 1.14-2.036 1.191-.664.053-1.237-.2-1.574-.898l-.005-.003c-.21-.4-.12-1.025.056-1.69.176-.668.428-1.344.463-1.897.037-.714.076-1.335.195-1.814.12-.465.308-.797.641-.984l.045-.022zm-10.814.049h.01c.053 0 .105.005.157.014.376.055.706.333 1.023.752l.91 1.664.003.003c.243.533.754 1.064 1.189 1.637.434.598.77 1.131.729 1.57v.006c-.057.744-.48 1.148-1.125 1.294-.645.135-1.52.002-2.395-.464-.968-.536-2.118-.469-2.857-.602-.369-.066-.61-.2-.723-.4-.11-.2-.113-.602.123-1.23v-.004l.002-.003c.117-.334.03-.752-.027-1.118-.055-.401-.083-.71.043-.94.16-.334.396-.4.69-.533.294-.135.64-.202.915-.47h.002v-.002c.256-.268.445-.601.668-.838.19-.201.38-.336.663-.336zm7.159-9.074c-.435.201-.945.535-1.488.535-.542 0-.97-.267-1.28-.466-.154-.134-.28-.268-.373-.335-.164-.134-.144-.333-.074-.333.109.016.129.134.199.2.096.066.215.2.36.333.292.2.68.467 1.167.467.485 0 1.053-.267 1.398-.466.195-.135.445-.334.648-.467.156-.136.149-.267.279-.267.128.016.034.134-.147.332a8.097 8.097 0 01-.69.468zm-1.082-1.583V5.64c-.006-.02.013-.042.029-.05.074-.043.18-.027.26.004.063 0 .16.067.15.135-.006.049-.085.066-.135.066-.055 0-.092-.043-.141-.068-.052-.018-.146-.008-.163-.065zm-.551 0c-.02.058-.113.049-.166.066-.047.025-.086.068-.14.068-.05 0-.13-.02-.136-.068-.01-.066.088-.133.15-.133.08-.031.184-.047.259-.005.019.009.036.03.03.05v.02h.003z"/></svg>',
  Windows: '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M3 3h8.5v8.5H3zM12.5 3H21v8.5h-8.5zM3 12.5h8.5V21H3zM12.5 12.5H21V21h-8.5z"/></svg>',
  Mac: '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12.152 6.896c-.948 0-2.415-1.078-3.96-1.04-2.04.027-3.91 1.183-4.961 3.014-2.117 3.675-.546 9.103 1.519 12.09 1.013 1.454 2.208 3.09 3.792 3.039 1.52-.065 2.09-.987 3.935-.987 1.831 0 2.35.987 3.96.948 1.637-.026 2.676-1.48 3.676-2.948 1.156-1.688 1.636-3.325 1.662-3.415-.039-.013-3.182-1.221-3.22-4.857-.026-3.04 2.48-4.494 2.597-4.559-1.429-2.09-3.623-2.324-4.39-2.376-2-.156-3.675 1.09-4.61 1.09zM15.53 3.83c.843-1.012 1.4-2.427 1.245-3.83-1.207.052-2.662.805-3.532 1.818-.78.896-1.454 2.338-1.273 3.714 1.338.104 2.715-.688 3.559-1.701"/></svg>',
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
    if (p.runsOn && !(Array.isArray(p.runsOn) && p.runsOn.every(r => systems[r]))) problems.push(`${at}: runsOn can list ${Object.keys(systems).join(', ')}`);
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
  return `<a class="${kind}" href="${escape(l.href)}"${external(l.href) ? ' target="_blank" rel="noopener"' : ''}>${l.store ? stores[l.store] : ''}${escape(l.label)}</a>`;
};

// At a glance: one row each, with the first link as the way to get it.
const get = p => {
  const l = p.links?.[0];
  if (!l) return '<span class="none">Not yet</span>';
  return `<a class="get${l.store || l.primary ? ' ready' : ''}" href="${escape(l.href)}"${external(l.href) ? ' target="_blank" rel="noopener"' : ''}>${l.store ? stores[l.store] : ''}${escape(l.short || l.label)}${external(l.href) ? arrow : ''}</a>`;
};
const row = p => `            <tr>
              <th scope="row"><div class="platform"><span class="row-icon">${icons[p.icon]}</span><div><a href="#${escape(p.id)}">${escape(p.name)}</a>${p.version ? `<span class="sub">${escape(p.version)}</span>` : ''}</div></div></th>
              <td class="state"><span class="mark" aria-hidden="true">${statuses[p.status].icon}</span><span class="state-label">${statuses[p.status].label}</span></td>
              <td class="ver">${p.version ? `<span class="version">${escape(p.version)}</span>` : '<span class="none">–</span>'}</td>
              <td class="go">${get(p)}</td>
            </tr>`;
const table = `<div class="table"><table>
          <thead><tr><th scope="col">Platform</th><th scope="col" class="state"><span class="state-label">Status</span></th><th scope="col" class="ver">Version</th><th scope="col" class="go">Get it</th></tr></thead>
          <tbody>
${data.platforms.map(row).join('\n')}
          </tbody>
        </table></div>`;

const card = p => `<article class="card${p.runsOn ? ' wide' : ''}" id="${escape(p.id)}">
            <div class="card-body">
              <div class="card-top"><span class="app-icon">${icons[p.icon]}</span>${pill(p)}</div>
              <h3>${escape(p.name)}</h3>
${p.version ? `              <p class="version-line">Version <span class="version">${escape(p.version)}</span></p>\n` : ''}              <p class="summary">${escape(p.summary)}</p>
${p.requires ? `              <p class="requires">${escape(p.requires)}</p>\n` : ''}${p.note || p.links?.length ? `              <div class="foot">
${p.note ? `                <p class="note">${escape(p.note)}</p>\n` : ''}${p.links?.length ? `                <div class="links">${p.links.map(button).join('')}</div>\n` : ''}              </div>\n` : ''}            </div>
${p.runsOn ? `            <div class="runs-on">
              <p>Runs on</p>
              <ul>${p.runsOn.map(r => `<li>${systems[r]}${escape(r)}</li>`).join('')}</ul>
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
    .platform { display: flex; align-items: center; gap: 14px; }
    .row-icon { width: 36px; height: 36px; border-radius: 11px; display: grid; place-items: center; flex: none; background: var(--seat-raised); border: 1px solid var(--aisle); color: var(--screen); }
    .row-icon svg, .row-icon img { width: 19px; height: 19px; }
    td.state { white-space: nowrap; }
    .mark { margin-right: 9px; font-size: 1.05rem; }
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
      .ver, .state-label { display: none; }
      .platform { gap: 10px; }
      .row-icon { width: 30px; height: 30px; border-radius: 9px; }
      .row-icon svg, .row-icon img { width: 16px; height: 16px; }
      td.state { padding-inline: 4px; text-align: center; }
      .mark { margin: 0; }
      .sub { display: block; margin-top: 2px; color: var(--usher); font-weight: 400; font-size: 0.88rem; font-variant-numeric: tabular-nums; }
      td.go { padding-left: 4px; }
      .get { padding: 6px 11px; font-size: 0.85rem; gap: 5px; }
      .get .arrow { display: none; }
    }

    /* On a phone the cards below say it all, so the table goes. */
    @media (max-width: 640px) { .glance { display: none; } }

    /* Cards */
    .cards + .cards { margin-top: 20px; }
    .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 340px), 1fr)); gap: 20px; margin-top: 32px; }
    .card { position: relative; display: grid; border: 1px solid var(--aisle); border-radius: 24px; overflow: hidden; scroll-margin-top: 100px;
      background: radial-gradient(420px 260px at 0% 0%, rgb(var(--accent) / 0.14), transparent 70%), linear-gradient(180deg, var(--seat), rgb(37 32 44 / 0.4)); }
    .card:target { border-color: rgb(var(--accent) / 0.7); }
    .card-body { display: flex; flex-direction: column; gap: 12px; padding: clamp(24px, 3vw, 34px); }
    .card { --accent: 169 194 236; }
    .card:nth-child(3n+2) { --accent: 232 160 191; }
    .card:nth-child(3n) { --accent: 241 194 125; }
    .card-top { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 8px; }
    .app-icon { width: 56px; height: 56px; border-radius: 16px; display: grid; place-items: center; flex: none; background: var(--seat-raised); border: 1px solid var(--aisle); color: var(--screen); }
    .app-icon svg, .app-icon img { width: 28px; height: 28px; }
    .card h3 { font-size: clamp(2rem, 3.4vw, 2.6rem); }
    .card p { margin: 0; }
    .version-line { color: var(--usher); font-size: 0.95rem; margin-top: -4px !important; }
    .summary { color: #cfc8d8; }
    .note, .requires { color: var(--usher); font-size: 0.93rem; }
    .requires::before { content: "Needs "; color: var(--screen); font-weight: 700; }
    /* The note and buttons sit at the bottom, so cards in a row line up. */
    .foot { margin-top: auto; padding-top: 10px; display: flex; flex-direction: column; gap: 12px; }
    .links { display: flex; flex-wrap: wrap; align-items: center; gap: 10px 20px; }
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
    .runs-on li { display: flex; align-items: center; gap: 12px; padding: 12px 16px; border-radius: 14px; background: var(--seat-raised); border: 1px solid var(--aisle); font-weight: 700; }
    .runs-on li svg { width: 22px; height: 22px; flex: none; color: var(--silver); }
    @media (max-width: 760px) {
      .card.wide { grid-template-columns: minmax(0, 1fr); }
      .runs-on { border-left: 0; border-top: 1px solid var(--aisle); }
      .runs-on ul { grid-template-columns: repeat(3, minmax(0, 1fr)); }
      .runs-on li { flex-direction: column; gap: 6px; padding: 12px 6px; font-size: 0.88rem; }
    }

    /* On the way: lighter, dashed. */
    .soon .card { background: transparent; border-style: dashed; }
    .soon .card h3 { font-size: clamp(1.8rem, 3vw, 2.2rem); }
    .soon .requires { display: none; }
    .soon .foot { border-top: 1px dashed var(--aisle); padding-top: 16px; margin-top: auto; }
    .soon .card-body > .summary { margin-bottom: 8px; }
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
        <a class="keep" href="/apps" aria-current="page">The Apps</a>
        <a href="/#setup">Install</a>
        <a class="keep" href="/guide">Guide</a>
        <a class="github keep" href="${repo}" target="_blank" rel="noopener"><svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 .5a11.5 11.5 0 0 0-3.64 22.41c.58.1.79-.25.79-.56v-2c-3.2.7-3.88-1.37-3.88-1.37-.52-1.33-1.28-1.69-1.28-1.69-1.05-.72.08-.7.08-.7 1.16.08 1.77 1.19 1.77 1.19 1.03 1.77 2.7 1.26 3.36.96.1-.75.4-1.26.73-1.55-2.55-.29-5.24-1.28-5.24-5.69 0-1.26.45-2.29 1.19-3.09-.12-.29-.52-1.46.11-3.05 0 0 .97-.31 3.17 1.18a11 11 0 0 1 5.77 0c2.2-1.49 3.17-1.18 3.17-1.18.63 1.59.23 2.76.11 3.05.74.8 1.19 1.83 1.19 3.09 0 4.42-2.69 5.39-5.26 5.68.41.36.78 1.06.78 2.14v3.17c0 .31.21.67.8.56A11.5 11.5 0 0 0 12 .5Z"/></svg>GitHub</a>
      </nav>
    </div>
  </header>

  <main id="main">
    <div class="wrap">
      <div class="hero">
        <p class="kicker">Apps</p>
        <h1>Flexarr on every screen.</h1>
        <p class="lede">One server at home, and an app for every screen in it. Here's what you can get today and what's on the way.</p>
        <picture><source media="(max-width: 640px)" srcset="/shots/screens-m.webp" width="1100" height="902"><img src="/shots/screens.webp" width="2000" height="1206" srcset="/shots/screens-1000.webp 1000w, /shots/screens.webp 2000w" sizes="(min-width: 1260px) 1180px, 92vw" alt="Flexarr in a web browser showing Only Murders in the Building on Home, on an iPad showing Home, and on an iPhone showing Barbie's page." fetchpriority="high"></picture>
      </div>

      <section class="glance" aria-labelledby="glance">
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
          ${now.filter(p => p.runsOn).map(card).join('\n          ')}
        </div>
        <div class="cards">
          ${now.filter(p => !p.runsOn).map(card).join('\n          ')}
        </div>
      </section>

      <section class="soon" aria-labelledby="soon">
        <h2 id="soon">On the way</h2>
        <div class="cards">
          ${soon.map(card).join('\n          ')}
          <article class="card ask">
            <div class="card-body">
              <div><h3>Somewhere else?</h3><p>Tell us where you'd like to watch Flexarr next.</p></div>
              <div class="links"><a class="button quiet" href="${repo}/discussions/categories/ideas" target="_blank" rel="noopener">Ask on GitHub</a></div>
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
        <a href="${repo}" target="_blank" rel="noopener">GitHub</a>
        <a href="${repo}/releases" target="_blank" rel="noopener">Releases</a>
        <a href="${repo}/blob/main/CHANGELOG.md" target="_blank" rel="noopener">Changelog</a>
        <a href="${repo}/issues" target="_blank" rel="noopener">Report a problem</a>
        <a href="/donate">Donate</a>
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
