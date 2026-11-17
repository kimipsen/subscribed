// Every non-null `logo` in PAYMENT_OPTION_BRANDS must exist in public/logos and have a row in LICENSES.md (R15).
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const read = (path) => readFileSync(new URL(path, root), 'utf8');

const brands = read('src/app/subscriptions/payment-option-brands.ts');
const licences = read('public/logos/LICENSES.md');
const logos = [...brands.matchAll(/logo:\s*'([^']+)'/g)].map((m) => m[1]);

test('every licensed brand logo exists in public/logos and has a LICENSES.md row', () => {
  for (const file of logos) {
    assert.ok(existsSync(new URL(`public/logos/${file}`, root)), `${file} is missing from public/logos`);
    assert.ok(licences.includes(`| ${file} |`), `${file} has no LICENSES.md row`);
  }
});

test('the generic card icon exists and has a LICENSES.md row', () => {
  assert.ok(existsSync(new URL('public/logos/generic-card.svg', root)));
  assert.ok(licences.includes('| generic-card.svg |'));
});
