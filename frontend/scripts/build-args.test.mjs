import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { buildArgs, normalizeVersion } from './build-args.mjs';

const BASE = ['build', '--configuration', 'production', '--define'];
const define = (value) => [...BASE, `__APP_VERSION__=${JSON.stringify(value)}`];

test('unset, empty and whitespace-only values fall back to the package.json version', () => {
  assert.deepEqual(buildArgs({}, '0.0.0'), define('0.0.0'));
  assert.deepEqual(buildArgs({ APP_VERSION: '' }, '0.0.0'), define('0.0.0'));
  assert.deepEqual(buildArgs({ APP_VERSION: '   ' }, '0.0.0'), define('0.0.0'));
});

test('a set value gives a quoted define, trimmed, with one leading v removed', () => {
  assert.deepEqual(buildArgs({ APP_VERSION: '1.2.3-test' }, '0.0.0'), define('1.2.3-test'));
  assert.deepEqual(buildArgs({ APP_VERSION: '  1.2.3 ' }, '0.0.0'), define('1.2.3'));
  assert.deepEqual(buildArgs({ APP_VERSION: 'v1.2.3' }, '0.0.0'), define('1.2.3'));
});

test('prerelease and build metadata values pass', () => {
  assert.deepEqual(buildArgs({ APP_VERSION: '1.2.3-rc.1' }, '0.0.0'), define('1.2.3-rc.1'));
  assert.deepEqual(buildArgs({ APP_VERSION: '1.2.3+build' }, '0.0.0'), define('1.2.3+build'));
});

test('values with unsafe characters throw', () => {
  for (const bad of ['a b', 'a"b', 'a;b', 'a$b']) {
    assert.throws(() => buildArgs({ APP_VERSION: bad }, '0.0.0'), /Invalid APP_VERSION/, bad);
  }
  assert.throws(() => buildArgs({}, 'bad value'), /Invalid APP_VERSION/);
});

test('normalizeVersion handles unset, padded and v-prefixed input', () => {
  assert.equal(normalizeVersion(undefined), '');
  assert.equal(normalizeVersion(''), '');
  assert.equal(normalizeVersion('  1.0 '), '1.0');
  assert.equal(normalizeVersion('v1.0'), '1.0');
  assert.equal(normalizeVersion('vv1.0'), 'v1.0');
});

test('importing build-args.mjs spawns nothing and returns synchronously', async () => {
  const mod = await import('./build-args.mjs');
  assert.equal(typeof mod.buildArgs, 'function');
});

test('the launcher exits non-zero with a message for an invalid version, before spawning ng', () => {
  const launcher = new URL('./build.mjs', import.meta.url);
  const result = spawnSync(process.execPath, [fileURLToPath(launcher)], {
    env: { PATH: process.env.PATH, APP_VERSION: 'a b' },
    encoding: 'utf8',
  });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Invalid APP_VERSION/);
});
