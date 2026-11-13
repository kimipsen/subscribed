// Dependency-free theme checks run by `npm run test:ci`:
// T-I static init-script contract, T-C WCAG contrast of the token palette, T-H no hard-coded colours.
// Theme resolution behaviour is covered by src/app/theme/theme.spec.ts; the init script is only read as text.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { initContractChecks, readThemeContract } from './theme-init-contract.mjs';

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');
const initSource = read('public/theme-init.js');
const themeContract = readThemeContract(read('src/app/theme/theme.ts'));
const styles = read('src/styles.scss');
const appStyles = read('src/app/app.scss');

// ---------- T-I: public/theme-init.js (static contract, never executed) ----------

test('T-I theme.ts exposes the constants the init script mirrors', () => {
  assert.equal(themeContract.storageKey, 'subscribed.theme');
  assert.deepEqual([...themeContract.modes].sort(), ['dark', 'light', 'system']);
  assert.equal(themeContract.defaultMode, 'system');
  assert.equal(themeContract.darkQuery, '(prefers-color-scheme: dark)');
});

for (const { name, ok, detail } of initContractChecks(initSource, themeContract)) {
  test(`T-I theme-init.js: ${name}`, () => {
    assert.ok(ok, detail);
  });
}

// ---------- T-C: palette contrast ----------

function mixinBody(source, name) {
  const start = source.indexOf(`@mixin ${name} {`);
  assert.notEqual(start, -1, `@mixin ${name} not found in src/styles.scss`);
  const end = source.indexOf('}', start);
  return { start, end: end + 1, body: source.slice(start, end) };
}

function tokens(name) {
  const result = new Map();
  for (const match of mixinBody(styles, name).body.matchAll(/^\s*--color-([a-z-]+):\s*(#[0-9a-f]{6});\s*$/gm)) {
    result.set(match[1], match[2]);
  }
  return result;
}

const palettes = { light: tokens('light-tokens'), dark: tokens('dark-tokens') };
const REQUIRED_TOKENS = [
  'bg', 'surface', 'text', 'text-muted', 'border', 'control-border', 'primary', 'on-primary',
  'nav-active-bg', 'nav-active-text', 'focus', 'error', 'invalid', 'notice-bg', 'notice-border',
  'skeleton', 'empty-border',
];

function luminance(hex) {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a, b) {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const PAIRS = [
  ['text', 'bg', 4.5], ['text', 'surface', 4.5], ['text', 'nav-active-bg', 4.5], ['text', 'notice-bg', 4.5],
  ['text-muted', 'bg', 4.5], ['text-muted', 'surface', 4.5],
  ['primary', 'surface', 4.5], ['primary', 'bg', 4.5],
  ['on-primary', 'primary', 4.5],
  ['nav-active-text', 'nav-active-bg', 4.5],
  ['error', 'surface', 4.5], ['error', 'bg', 4.5],
  ['control-border', 'surface', 3], ['control-border', 'bg', 3],
  ['invalid', 'surface', 3],
  ['focus', 'bg', 3], ['focus', 'surface', 3], ['focus', 'nav-active-bg', 3], ['focus', 'notice-bg', 3],
];

test('T-C both mixins define the same, complete token set', () => {
  assert.deepEqual([...palettes.light.keys()].sort(), [...palettes.dark.keys()].sort());
  assert.deepEqual([...palettes.light.keys()].sort(), [...REQUIRED_TOKENS].sort());
});

for (const [theme, palette] of Object.entries(palettes)) {
  for (const [fg, bg, min] of PAIRS) {
    test(`T-C ${theme}: ${fg} on ${bg} >= ${min}:1`, () => {
      const ratio = contrast(palette.get(fg), palette.get(bg));
      assert.ok(ratio >= min, `--color-${fg} on --color-${bg} in ${theme} is ${ratio.toFixed(2)}:1, below ${min}:1`);
    });
  }
}

// ---------- T-H: no hard-coded colours outside the token mixins ----------

const HARD_CODED = /#[0-9a-fA-F]{3,8}\b|\b(?:white|black)\b(?!-)/;

function withoutComments(source) {
  return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/[^\n]*/g, '');
}

function withoutMixins(source) {
  let result = source;
  for (const name of ['dark-tokens', 'light-tokens']) {
    const { start, end } = mixinBody(result, name);
    result = result.slice(0, start) + result.slice(end);
  }
  return result;
}

function offending(source) {
  return withoutComments(source).split('\n').filter((line) =>
    HARD_CODED.test(line.replace(/white-space\s*:[^;]*;?/g, '')));
}

test('T-H src/app/app.scss uses only tokens', () => {
  assert.deepEqual(offending(appStyles), []);
});

test('T-H src/styles.scss uses only tokens outside the two mixins', () => {
  assert.deepEqual(offending(withoutMixins(styles)), []);
});
