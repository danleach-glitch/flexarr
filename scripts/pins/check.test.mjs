// node --test scripts/pins/check.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { movePin, pins, versionOf } from './check.mjs';

const a = 'a'.repeat(40);
const b = 'b'.repeat(40);
const yml = `packages:
  FlexarrKit:
    path: FlexarrKit
  Engine:
    url: https://github.com/someone/Engine
    revision: ${a} # 1.2.3
  Sentry:
    url: https://github.com/getsentry/sentry-cocoa.git
    revision: ${b} # 9.32.0
targets:
  App:
    type: application
`;

test('reads each pinned package with its commit and version', () => {
  assert.deepEqual(pins(yml), [
    { name: 'Engine', repo: 'someone/Engine', revision: a, version: '1.2.3' },
    { name: 'Sentry', repo: 'getsentry/sentry-cocoa', revision: b, version: '9.32.0' },
  ]);
});

test('the real project.yml pins AetherEngine and Sentry by commit', () => {
  const found = pins(fs.readFileSync(new URL('../../clients/apple/project.yml', import.meta.url), 'utf8'));
  assert.deepEqual(found.map(p => p.name).sort(), ['AetherEngine', 'Sentry']);
});

test('moves only the named pin, commit and version together', () => {
  const c = 'c'.repeat(40);
  const out = movePin(yml, pins(yml)[0], '1.3.0', c);
  assert.match(out, new RegExp(`revision: ${c} # 1\\.3\\.0\\n`));
  assert.match(out, new RegExp(`revision: ${b} # 9\\.32\\.0\\n`));
  assert.equal(pins(out)[0].version, '1.3.0');
});

test('a `$` in a value is never read as a replacement pattern', () => {
  assert.throws(() => movePin(yml, pins(yml)[0], "$'1.3.0", 'c'.repeat(40)));
  assert.throws(() => movePin(yml, pins(yml)[0], '1.3.0', '$1'));
});

test('only plain release tags count', () => {
  assert.equal(versionOf('7.54.0'), '7.54.0');
  assert.equal(versionOf('v9.33.0'), '9.33.0');
  for (const tag of ['7.54.0-beta.1', '7.54', 'x7.54.0', '7.54.0\nrevision: x', '../7.54.0']) assert.equal(versionOf(tag), null);
});

test('a short or upper-case commit is refused', () => {
  assert.throws(() => movePin(yml, pins(yml)[0], '1.3.0', 'abc123'));
  assert.throws(() => movePin(yml, pins(yml)[0], '1.3.0', 'C'.repeat(40)));
});
