// Production output check run by `npm run build` after the build: the injected version is in the bundle.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { test } from 'node:test';
import { normalizeVersion, readPackageVersion } from './build-args.mjs';

const dist = new URL('../dist/subscribed/browser/', import.meta.url);

function bundles() {
  assert.ok(existsSync(dist), 'dist/subscribed/browser is missing; run the build first');
  return readdirSync(dist)
    .filter((name) => name.endsWith('.js'))
    .map((name) => ({ name, text: readFileSync(new URL(name, dist), 'utf8') }));
}

const expected = normalizeVersion(process.env.APP_VERSION) || readPackageVersion();

test('no production bundle contains the __APP_VERSION__ placeholder', () => {
  const leaked = bundles().filter(({ text }) => text.includes('__APP_VERSION__')).map(({ name }) => name);
  assert.deepEqual(leaked, []);
});

test('a bundle contains the expected version as a quoted literal', () => {
  const quoted = JSON.stringify(expected);
  const found = bundles().filter(({ text }) => text.includes(quoted)).map(({ name }) => name);
  assert.ok(found.length > 0, `no bundle contains the literal ${quoted}`);
});

test('the package.json import did not leak into the bundle', () => {
  const leaked = bundles().filter(({ text }) => text.includes('"devDependencies"')).map(({ name }) => name);
  assert.deepEqual(leaked, []);
});
