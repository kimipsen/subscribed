// Production output checks run by `npm run build` after `ng build` (T-D).
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { test } from 'node:test';
import { initContractChecks, readThemeContract } from './theme-init-contract.mjs';

const dist = new URL('../dist/subscribed/browser/', import.meta.url);
const indexUrl = new URL('index.html', dist);

test('T-D production output keeps the external, render-blocking theme script', () => {
  assert.ok(existsSync(indexUrl), 'dist/subscribed/browser/index.html is missing; run ng build first');
  const html = readFileSync(indexUrl, 'utf8');
  const head = html.slice(0, html.indexOf('</head>'));
  assert.notEqual(html.indexOf('</head>'), -1, 'index.html has no </head>');

  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)];
  const themeScripts = scripts.filter(([, attrs]) => /\bsrc=["']?theme-init\.js["'\s>]?/.test(attrs));
  assert.equal(themeScripts.length, 1, 'expected exactly one <script src="theme-init.js">');
  const [tag, attrs] = themeScripts[0];
  assert.ok(head.includes(tag), 'theme-init.js must be inside <head>');
  assert.doesNotMatch(attrs, /\b(async|defer)\b/, 'theme-init.js must be render-blocking');
  assert.doesNotMatch(attrs, /type=["']?module/, 'theme-init.js must be a classic script');

  for (const [whole, scriptAttrs, body] of scripts) {
    assert.match(scriptAttrs, /\bsrc=/, `inline script is not allowed by CSP: ${whole.slice(0, 80)}`);
    assert.equal(body.trim(), '', `script with src must have no inline body: ${whole.slice(0, 80)}`);
  }

  // Angular's production build lowercases the attribute name (ngcspnonce); the placeholder value must stay exact.
  assert.match(
    html,
    /\s[nN][gG][cC][sS][pP][nN][oO][nN][cC][eE]="__CSP_NONCE__"/,
    'ngCspNonce="__CSP_NONCE__" placeholder is missing',
  );
});

test('T-D dist theme-init.js is byte-identical to public/ and meets the static contract', () => {
  const built = readFileSync(new URL('theme-init.js', dist));
  const source = readFileSync(new URL('../public/theme-init.js', import.meta.url));
  assert.ok(built.equals(source), 'dist theme-init.js differs from public/theme-init.js');

  const contract = readThemeContract(readFileSync(new URL('../src/app/theme/theme.ts', import.meta.url), 'utf8'));
  const failed = initContractChecks(built.toString('utf8'), contract).filter(({ ok }) => !ok);
  assert.deepEqual(failed.map(({ name }) => name), [], failed.map(({ detail }) => detail).join('; '));
});
