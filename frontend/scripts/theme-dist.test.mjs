// Production output checks run by `npm run build` after `ng build` (T-D).
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
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

// ---------- stylesheet wiring (Tailwind 4 + daisyUI 5) ----------

const read = (path) => readFileSync(new URL(path, dist), 'utf8');
const html = () => read('index.html');
const stylesheetName = () => {
  const links = [...html().matchAll(/<link\b[^>]*rel=["']?stylesheet["']?[^>]*>/gi)];
  assert.equal(links.length, 1, 'expected exactly one <link rel="stylesheet">');
  const href = /href=["']?(styles-[^"'\s>]+\.css)/.exec(links[0][0])?.[1];
  assert.ok(href, `stylesheet link does not point at styles-*.css: ${links[0][0]}`);
  const head = html().slice(0, html().indexOf('</head>'));
  assert.ok(head.includes(links[0][0]), 'the stylesheet link must be inside <head>');
  return href;
};
const css = () => read(stylesheetName());

// Whitespace and quote style differ between minifier versions: compare with both stripped.
const normalise = (text) => text.replace(/\s+/g, '').replace(/["']/g, '');

test('T-D one external stylesheet, no <style> element in index.html (CSP style-src has no unsafe-inline)', () => {
  stylesheetName();
  assert.doesNotMatch(html(), /<style[\s>]/i, 'index.html must not contain a <style> element');
  assert.doesNotMatch(html(), /\sstyle=/i, 'index.html must not contain an inline style attribute');
});

test('T-D built stylesheet carries the theme selectors and the prefers-color-scheme fallback', () => {
  const text = normalise(css());
  assert.ok(text.includes('[data-theme=light]'), '[data-theme=light] rule is missing');
  assert.ok(text.includes('[data-theme=dark]'), '[data-theme=dark] rule is missing');
  // daisyUI `default: true` / `prefersdark: true` => `:root:not([data-theme])` inside the dark media query.
  // First production build emitted: @media(prefers-color-scheme:dark){:root:not([data-theme]){color-scheme:dark;--color-base-100: ...
  assert.ok(text.includes('@media(prefers-color-scheme:dark){:root:not([data-theme]){'), 'prefers-color-scheme dark fallback rule is missing');
  // The theme overrides in src/styles.css are the winning declarations (stock values were replaced, not duplicated).
  assert.equal((text.match(/\[data-theme=light\]\{color-scheme:light/g) ?? []).length, 1, 'one winning [data-theme=light] rule');
  assert.equal((text.match(/\[data-theme=dark\]\{color-scheme:dark/g) ?? []).length, 1, 'one winning [data-theme=dark] rule');
});

// ---------- negative source check: guards the @source narrowing in src/styles.css ----------

// Each sentinel utility exists only where Tailwind must not look. If automatic detection is switched back on, or
// @source is widened, one of them is emitted into the stylesheet and this test fails.
const SENTINELS = [
  { file: 'src/app/tailwind-sources.spec.ts', utility: 'w-[137px]', selector: '.w-\\[137px\\]' },
  { file: 'scripts/theme-dist.test.mjs', utility: 'h-[139px]', selector: '.h-\\[139px\\]' },
  { file: 'src/app/testing/query.ts', utility: 'p-[149px]', selector: '.p-\\[149px\\]' },
];

test('T-D sentinel utilities exist in their (excluded) source files, so the negative check is not vacuous', () => {
  for (const { file, utility } of SENTINELS) {
    assert.ok(readFileSync(new URL(`../${file}`, import.meta.url), 'utf8').includes(utility), `${file} must contain ${utility}`);
  }
});

test('T-D built stylesheet contains none of the sentinel utilities (specs, testing/ and scripts are not scanned)', () => {
  const text = normalise(css());
  for (const { file, selector } of SENTINELS) {
    assert.ok(!text.includes(normalise(selector)), `${selector} from ${file} is in the built CSS: @source is too wide`);
  }
});

// ---------- built-CSS proofs for behaviour-protecting utilities ----------

// These need the migrated templates (WP4 shell, WP5 pages), so each proof is enforced once the template that
// provides it is in styles-contract.test.mjs MIGRATED_FILES, or everywhere once ENABLE_TEMPLATE_CHECKS is true.
const contractSource = readFileSync(new URL('./styles-contract.test.mjs', import.meta.url), 'utf8');
const templateChecksEnabled = /const ENABLE_TEMPLATE_CHECKS = true;/.test(contractSource);
const migratedFiles = new Set(
  [...(/const MIGRATED_FILES = \[([\s\S]*?)\];/.exec(contractSource)?.[1] ?? '').matchAll(/'([^']+)'/g)].map(([, file]) => file));
const enforced = (files) => templateChecksEnabled || files.some((file) => migratedFiles.has(file));

/** Every `@media(...) { ... }` rule in the stylesheet, found by brace matching, with whitespace/quotes stripped. */
function mediaBlocks(text) {
  const source = normalise(text);
  const blocks = [];
  for (const match of source.matchAll(/@media([^{]*)\{/g)) {
    let depth = 1;
    let end = match.index + match[0].length;
    while (depth > 0 && end < source.length) depth += (source[end] === '{') - (source[end++] === '}');
    blocks.push({ query: match[1], body: source.slice(match.index + match[0].length, end - 1) });
  }
  return blocks;
}

const NAV_MIN = /^\(?(?:width>=900px|min-width:900px)\)?$/;
const NAV_MAX = /^\(?(?:width<900px|max-width:899\.98px)\)?$|^notall(?:and)?\(min-width:900px\)$/;
const guardedRule = (utility) => `:is(:where(.group)[data-collapsed=true].nav\\:group-data-\\[collapsed\\=true\\]\\:${utility})`;

// Real strings emitted by the first production build (Tailwind 4.3.5, daisyUI 5.8.1, --breakpoint-nav: 900px):
//   @media(width>=900px){.nav\:flex{display:flex}:is(:where(.group)[data-collapsed=true] .nav\:group-data-\[collapsed\=true\]\:sr-only){position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip-path:inset(50%);white-space:nowrap;border-width:0}...}
//   @media(width<900px){.max-nav\:hidden{display:none}}
//   .sr-only{position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip-path:inset(50%);white-space:nowrap;border-width:0}
//   .overflow-x-auto{overflow-x:auto}   .wrap-anywhere{overflow-wrap:anywhere}
//   .tooltip>.tooltip-content,.tooltip[data-tip]:before{position:absolute;...}  (the guarded form is :is(:where(.group)[data-collapsed=true] .nav\:group-data-\[collapsed\=true\]\:tooltip)>.tooltip-content)
const RAIL_UTILITIES = ['w-16', 'justify-center', 'px-2']; // adjust with the sidebar template in WP4 if the rail uses other classes

export const PROOFS = [
  {
    name: 'collapsed label is sr-only only behind the nav breakpoint (nav:group-data-[collapsed=true]:sr-only)',
    files: ['src/app/app.html'],
    check: (text) => {
      const guarded = mediaBlocks(text).filter(({ query }) => NAV_MIN.test(query)).map(({ body }) => body).join('');
      assert.ok(guarded.includes(`${guardedRule('sr-only')}{position:absolute;width:1px;height:1px`),
        'the nav-guarded group-data collapsed sr-only rule must sit inside the 900px min-width media block (an unguarded rule is not accepted)');
    },
  },
  {
    name: 'rail width, centring and padding are guarded the same way',
    files: ['src/app/app.html'],
    check: (text) => {
      const guarded = mediaBlocks(text).filter(({ query }) => NAV_MIN.test(query)).map(({ body }) => body).join('');
      for (const utility of RAIL_UTILITIES) assert.ok(guarded.includes(guardedRule(utility)), `nav-guarded collapsed ${utility} is missing`);
    },
  },
  {
    name: 'the nav breakpoint is 900px for both the sidebar (nav:) and the Menu button (max-nav:)',
    files: ['src/app/app.html'],
    check: (text) => {
      const queries = mediaBlocks(text).map(({ query }) => query);
      assert.ok(queries.some((q) => NAV_MIN.test(q)), `no 900px min-width media query (found: ${[...new Set(queries)].join(' | ')})`);
      assert.ok(queries.some((q) => NAV_MAX.test(q)), `no 900px max-width media query (found: ${[...new Set(queries)].join(' | ')})`);
    },
  },
  {
    name: '.sr-only exists',
    files: ['src/app/app.html'],
    check: (text) => assert.ok(normalise(text).includes('.sr-only{position:absolute;width:1px;height:1px'), '.sr-only rule is missing'),
  },
  {
    name: '.overflow-x-auto exists (scrollable table region)',
    files: ['src/app/subscriptions/subscription-overview.html'],
    check: (text) => assert.ok(normalise(text).includes('.overflow-x-auto{overflow-x:auto}'), '.overflow-x-auto rule is missing'),
  },
  {
    name: 'the collapsed sidebar tooltip rules exist (aria-hidden .tooltip-content child, not data-tip)',
    files: ['src/app/app.html'],
    check: (text) => {
      const source = normalise(text);
      assert.ok(source.includes('>.tooltip-content'), '.tooltip-content rule is missing');
      assert.ok(source.includes('\\:tooltip)'), 'the nav-guarded tooltip variant is missing');
    },
  },
  {
    name: 'wrap-anywhere exists (long Danish words wrap)',
    files: ['src/app/subscriptions/subscription-overview.html', 'src/app/foundation-page.html'],
    check: (text) => assert.ok(normalise(text).includes('.wrap-anywhere{overflow-wrap:anywhere}'), '.wrap-anywhere rule is missing'),
  },
];

for (const { name, files, check } of PROOFS) {
  test(`T-D built-CSS proof: ${name}`, (t) => {
    if (!enforced(files)) {
      t.diagnostic(`not enforced yet: ${files.join(', ')} not migrated (MIGRATED_FILES in styles-contract.test.mjs)`);
      return;
    }
    check(css());
  });
}

// Include-side check (guards the opposite direction of the sentinels): a typo in an @source path or a dropped
// directory must fail instead of shipping an unstyled page. Entries are structural utilities that are used by
// exactly one template; each records the file and line so a later edit that removes it points here.
// One entry for src/app/app.html and one for a single page template; keep the line numbers in step when the line moves.
export const INCLUDE_SIDE = [
  { file: 'src/app/app.html', line: 32, utility: 'nav:grid-cols-[auto_1fr]' },
  { file: 'src/app/foundation-page.html', line: 25, utility: 'tracking-wide' },
];

test('T-D include-side utilities are in the built CSS', (t) => {
  if (!templateChecksEnabled) {
    t.diagnostic('not enforced yet: ENABLE_TEMPLATE_CHECKS is false');
    return;
  }
  assert.ok(INCLUDE_SIDE.length >= 2, 'record at least two include-side utilities (one from app.html, one from a page template)');
  const text = normalise(css());
  for (const { file, line, utility } of INCLUDE_SIDE) {
    const selector = `.${utility.replace(/[:[\]=/.]/g, '\\$&')}`;
    assert.ok(text.includes(normalise(selector)), `${utility} (${file}:${line}) is missing from the built CSS`);
  }
});

test('T-D there is exactly one built stylesheet in dist', () => {
  const files = readdirSync(new URL('./', dist)).filter((name) => /^styles-.*\.css$/.test(name));
  assert.equal(files.length, 1, files.join(', '));
});
