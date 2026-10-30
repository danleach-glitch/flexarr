// Keeps the Apple apps' pinned packages in view. Dependabot can't read clients/apple/project.yml (XcodeGen), so this
// does its job for the packages pinned there with `exactVersion`: when a package's latest GitHub release is newer than
// the pin, it opens a pull request on `pins/<package>` that moves the pin, with the release notes in between. A pinned
// release reaches the TV only after it has been tried on one, so the pull request waits for that; nothing lands itself.
//
// scripts/pins/watch.json lists issues we're waiting on upstream (a feature we asked for, a bug we reported) and what
// they unblock here. A pull request says when a release in it mentions one, or when the issue has closed.
//
// Run weekly by .github/workflows/pins.yml. `--dry-run` only reports, and needs nothing but `gh` signed in.
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../..');
const projectFile = 'clients/apple/project.yml';
const dryRun = process.argv.includes('--dry-run');
const bot = 'github-actions[bot]';
const label = 'needs device test';

const run = (cmd, args, opts = {}) => execFileSync(cmd, args, { cwd: root, encoding: 'utf8', ...opts }).trim();
const gh = (...args) => run('gh', args);
const ghJson = (...args) => JSON.parse(gh(...args));
const git = (...args) => run('git', args);

const escapeRegExp = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const parse = v => v.replace(/^v/, '').split('.').map(Number);
const newer = (a, b) => {
  const [x, y] = [parse(a), parse(b)];
  for (let i = 0; i < Math.max(x.length, y.length); i++) if ((x[i] ?? 0) !== (y[i] ?? 0)) return (x[i] ?? 0) > (y[i] ?? 0);
  return false;
};

// Each `packages:` entry with a GitHub url and an exactVersion.
function pins() {
  const text = fs.readFileSync(path.join(root, projectFile), 'utf8');
  const found = [];
  const entry = /^ {2}(\w[\w-]*):\n((?: {4}.*\n)+)/gm;
  for (const [, name, body] of text.matchAll(entry)) {
    const url = body.match(/url: https:\/\/github\.com\/([\w.-]+\/[\w.-]+?)(?:\.git)?\s*$/m)?.[1];
    const version = body.match(/exactVersion: ([\w.-]+)/)?.[1];
    if (url && version) found.push({ name, repo: url, version });
  }
  return found;
}

const watch = JSON.parse(fs.readFileSync(path.join(root, 'scripts/pins/watch.json'), 'utf8'));

// The releases after the pin up to the latest, newest first. Drafts and pre-releases don't count.
function releasesSince(repo, version) {
  const all = ghJson('api', `repos/${repo}/releases?per_page=50`);
  return all.filter(r => !r.draft && !r.prerelease && newer(r.tag_name, version))
    .sort((a, b) => (newer(a.tag_name, b.tag_name) ? -1 : 1));
}

// Watched issues this update bears on: mentioned in one of the releases, or closed since we asked.
function watched(repo, releases) {
  return watch.filter(w => w.repo === repo).flatMap(w => {
    const ref = new RegExp(`(#|issues/)${w.issue}\\b`);
    const release = releases.find(r => ref.test(r.body ?? ''));
    const state = ghJson('api', `repos/${w.repo}/issues/${w.issue}`, '--jq', '{state: .state, title: .title}');
    if (!release && state.state !== 'closed') return [];
    const where = release ? `mentioned in ${release.tag_name}` : 'closed upstream';
    return [`- **${w.repo}#${w.issue}** (${state.title}): ${where}. Unblocks: ${w.unblocks}`];
  });
}

function body(pin, latest, releases, notes) {
  const limit = 50000;
  let text = `Moves **${pin.name}** in \`${projectFile}\` from ${pin.version} to ${latest}.\n\n`;
  text += 'Before landing: CI builds both apps, then try it on the Apple TV and the iPhone (`/devices`), above all ' +
    'playback. Remove the `needs device test` label once it has been.\n\n';
  if (notes.length) text += `### Waiting on upstream\n\n${notes.join('\n')}\n\n`;
  text += '### Release notes\n\n';
  for (const r of releases) {
    const section = `<details${r === releases[0] ? ' open' : ''}><summary><b>${r.name || r.tag_name}</b></summary>\n\n` +
      `${(r.body ?? '').trim()}\n\n[${r.tag_name}](${r.html_url})</details>\n\n`;
    if (text.length + section.length > limit) {
      text += `…and earlier: see https://github.com/${pin.repo}/releases\n`;
      break;
    }
    text += section;
  }
  return text + '\n_Opened by `scripts/pins/check.mjs` (weekly, `.github/workflows/pins.yml`)._\n';
}

// Moves the pin on a fresh branch from main and pushes it, unless someone has added their own commits to it.
function push(pin, latest, branch) {
  const exists = git('ls-remote', '--heads', 'origin', branch) !== '';
  if (exists) {
    git('fetch', '-q', 'origin', branch);
    const authors = git('log', '--format=%an', `origin/main..origin/${branch}`).split('\n').filter(Boolean);
    if (authors.some(a => a !== bot)) return false;
  }
  git('checkout', '-q', '-B', branch, 'origin/main');
  const file = path.join(root, projectFile);
  const text = fs.readFileSync(file, 'utf8');
  const entry = new RegExp(`(^ {2}${escapeRegExp(pin.name)}:\\n(?: {4}.*\\n)*? {4}exactVersion: )${escapeRegExp(pin.version)}`, 'm');
  fs.writeFileSync(file, text.replace(entry, `$1${latest}`));
  git('-c', `user.name=${bot}`, '-c', 'user.email=41898282+github-actions[bot]@users.noreply.github.com',
    'commit', '-q', '-am', `Apple: ${pin.name} ${latest}`);
  git('push', '-q', '--force', 'origin', branch);
  git('checkout', '-q', '--detach', 'origin/main');
  return true;
}

let failed = false;
for (const pin of pins()) {
  try {
    const releases = releasesSince(pin.repo, pin.version);
    if (!releases.length) {
      console.log(`${pin.name} ${pin.version}: up to date`);
      continue;
    }
    const latest = releases[0].tag_name.replace(/^v/, '');
    const notes = watched(pin.repo, releases);
    console.log(`${pin.name} ${pin.version}: ${latest} is out (${releases.length} release${releases.length > 1 ? 's' : ''} behind)`);
    notes.forEach(n => console.log(`  ${n}`));
    if (dryRun) continue;

    const branch = `pins/${pin.name}`;
    const title = `Apple: ${pin.name} ${latest}`;
    const open = ghJson('pr', 'list', '--head', branch, '--state', 'open', '--json', 'number,title');
    if (open[0]?.title === title) {
      console.log(`  #${open[0].number} already moves it`);
      continue;
    }
    const text = body(pin, latest, releases, notes);
    if (!push(pin, latest, branch)) {
      gh('pr', 'comment', String(open[0]?.number ?? branch), '--body',
        `${latest} is out. This branch has commits of its own, so it wasn't moved: update the pin here by hand.`);
      console.log(`  ${branch} has its own commits: commented instead`);
      continue;
    }
    if (open[0]) {
      gh('pr', 'edit', String(open[0].number), '--title', title, '--body', text);
      console.log(`  updated #${open[0].number}`);
    } else {
      const url = gh('pr', 'create', '--base', 'main', '--head', branch, '--title', title, '--body', text,
        '--label', 'dependencies', '--label', label, '--label', 'area: apple');
      console.log(`  opened ${url}`);
    }
  } catch (e) {
    failed = true;
    console.error(`${pin.name}: ${e.message}`);
  }
}
process.exit(failed ? 1 : 0);
