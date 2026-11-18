// Dependency-free theme checks run by `npm run test:ci`:
// T-I static init-script contract, T-C WCAG contrast of the daisyUI light/dark themes (stock files plus the
// overrides in src/styles.css), T-H no hard-coded colours in src/**/*.css. Colour maths lives in theme-palette.mjs.
// Template scans (palette utilities, `dark:`, alpha utilities used in templates, component allow-list) are in
// styles-contract.test.mjs because they need the Angular template parser.
// Theme resolution behaviour is covered by src/app/theme/theme.spec.ts; the init script is only read as text.
import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import { test } from 'node:test';
import { initContractChecks, readThemeContract } from './theme-init-contract.mjs';
import {
  ALPHA_TABLE, blendAlpha, colorMixOklab, contrastRatio, loadPalette, oklchInGamut, oklchToSrgb, parseOklch,
  read, readOverrides, stockColors,
} from './theme-palette.mjs';

const initSource = read('public/theme-init.js');
const themeContract = readThemeContract(read('src/app/theme/theme.ts'));
const stylesCss = read('src/styles.css');
const THEMES = ['light', 'dark'];

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

// ---------- self-tests for the test code (scripts/** is not covered by eslint) ----------

const near = (actual, expected, tolerance, label) =>
  actual.forEach((v, i) => assert.ok(Math.abs(v - expected[i]) <= tolerance,
    `${label}: channel ${i} is ${v.toFixed(2)}, expected ${expected[i]} (+/- ${tolerance})`));

test('T-S oklch converter: white, black and a mid grey', () => {
  near(oklchToSrgb(1, 0, 0), [255, 255, 255], 0.01, 'white');
  near(oklchToSrgb(0, 0, 0), [0, 0, 0], 0.01, 'black');
  // oklab(50% 0 0) is sRGB rgb(99 99 99) (cube of L is the linear value 0.125, which encodes to 0.388).
  near(oklchToSrgb(0.5, 0, 0), [99, 99, 99], 1, 'mid grey');
});

test('T-S oklch converter against independent references (tolerance 1/255)', () => {
  // CSS Color 4 / Bjorn Ottosson oklab published values for the sRGB primaries.
  near(oklchToSrgb(0.62796, 0.25768, 29.2339), [255, 0, 0], 1, 'sRGB red');
  near(oklchToSrgb(0.86644, 0.29483, 142.495), [0, 255, 0], 1, 'sRGB lime');
  near(oklchToSrgb(0.452, 0.31321, 264.052), [0, 0, 255], 1, 'sRGB blue');
  // Tailwind CSS 4 colour reference documents zinc-900 as both #18181b and oklch(21% 0.006 285.885), and
  // zinc-950 as #09090b / oklch(14% 0.005 285.823). daisyUI's light base-content and neutral are those two tokens.
  near(oklchToSrgb(0.21, 0.006, 285.885), [0x18, 0x18, 0x1b], 1, 'zinc-900 (daisyUI light base-content)');
  near(oklchToSrgb(0.14, 0.005, 285.823), [0x09, 0x09, 0x0b], 1, 'zinc-950 (daisyUI neutral)');
});

test('T-S out-of-gamut colours are chroma-reduced, not channel-clipped', () => {
  // daisyUI success is oklch(76% 0.177 163.223), outside sRGB. A mapped colour keeps lightness and hue, so its
  // oklab lightness stays near 0.76 whereas channel clipping would shift it.
  const [r, g, b] = oklchToSrgb(0.76, 0.177, 163.223);
  assert.ok([r, g, b].every((c) => c >= 0 && c <= 255));
  assert.equal(oklchInGamut(0.76, 0.177, 163.223), false);
  assert.equal(oklchInGamut(0.5, 0.05, 20), true);
  assert.ok(g > r && g > b, 'a green hue stays green after mapping');
});

test('T-S alpha blend (gamma-encoded sRGB, per channel)', () => {
  near(blendAlpha([0, 0, 0], 0.5, [255, 255, 255]), [127.5, 127.5, 127.5], 1e-9, '50% black on white');
  near(blendAlpha([10, 20, 30], 1, [255, 255, 255]), [10, 20, 30], 1e-9, '100% alpha returns the foreground');
  near(blendAlpha([10, 20, 30], 0, [200, 100, 50]), [200, 100, 50], 1e-9, '0% alpha returns the background');
});

test('T-S WCAG contrast ratio', () => {
  assert.ok(Math.abs(contrastRatio([0, 0, 0], [255, 255, 255]) - 21) < 1e-9);
  assert.equal(contrastRatio([12, 34, 56], [12, 34, 56]), 1);
});

test('T-S colorMixOklab (separate from the alpha blend model)', () => {
  const white = [255, 255, 255];
  const black = [0, 0, 0];
  // 50% white with black in oklab has oklab L 0.5 (cube 0.125 linear), which is sRGB about 99 per channel.
  near(colorMixOklab(white, 50, black), [99, 99, 99], 1, '50% white with black');
  near(colorMixOklab([10, 100, 200], 100, black), [10, 100, 200], 1e-3, 'p=100 returns A');
  near(colorMixOklab([10, 100, 200], 0, black), [0, 0, 0], 1e-3, 'p=0 returns B');
  // Transparent second colour: A with alpha p% composited by the alpha blend rule.
  near(colorMixOklab(black, 10, null, white), blendAlpha(black, 0.1, white), 1e-9, 'transparent B');
  // The two models differ on purpose: a 50% oklab mix is not the gamma blend.
  assert.ok(Math.abs(colorMixOklab(white, 50, black)[0] - blendAlpha(white, 0.5, black)[0]) > 20);
});

// ---------- T-C: contrast of the daisyUI themes (stock plus overrides) ----------

const rgbOf = (palette, name) => {
  const value = palette.get(name);
  assert.ok(value, `colour ${name} missing from theme`);
  return value;
};

/** The pair checker used by every T-C test; the negative test below runs it on a known failure. */
function checkPair(fg, bg, min) {
  const ratio = contrastRatio(fg, bg);
  return { ratio, ok: ratio >= min };
}

const black = [0, 0, 0];
const BTN_DARKEN = [5, 7]; // daisyUI 5.8.1 button.css mixes the button colour with #000 at 5% (border/rest) and 7% (hover/active)

/**
 * Text and UI pairs per theme: [description, foreground, background, minimum ratio].
 * Plain pairs use theme tokens directly. State pairs encode the exact expressions read from daisyUI 5.8.1
 * (button.css, menu.css, tooltip.css, table.css), see the T-D guards further down that the files still match.
 */
function pairsFor(palette) {
  const c = (name) => rgbOf(palette, name);
  const pairs = [];
  const add = (description, fg, bg, min) => pairs.push([description, fg, bg, min]);

  for (const on of ['base-100', 'base-200', 'base-300']) add(`base-content on ${on}`, c('base-content'), c(on), 4.5);
  add('primary-content on primary', c('primary-content'), c('primary'), 4.5);
  for (const on of ['base-100', 'base-200']) add(`primary on ${on} (links, brand, active text)`, c('primary'), c(on), 4.5);
  for (const on of ['base-100', 'base-200']) add(`error on ${on} (error text)`, c('error'), c(on), 4.5);
  add('error-content on error', c('error-content'), c('error'), 4.5);
  add('warning-content on warning', c('warning-content'), c('warning'), 4.5);
  add('info-content on info', c('info-content'), c('info'), 4.5);
  add('success-content on success', c('success-content'), c('success'), 4.5);
  add('neutral-content on neutral (btn-neutral, badge-neutral, menu-active, tooltip bubble)', c('neutral-content'), c('neutral'), 4.5);
  for (const on of ['base-100', 'base-200']) add(`focus ring (primary) on ${on}`, c('primary'), c(on), 3);

  // btn: rest, hover and active. Hover/active bg is color-mix(in oklab, <btn colour>, #000 5% or 7%), text is --btn-fg.
  const buttons = [
    ['btn (default, also btn-outline hover)', 'base-content', 'base-200'],
    ['btn-neutral', 'neutral-content', 'neutral'],
    ['btn-primary', 'primary-content', 'primary'],
  ];
  for (const [name, fg, bg] of buttons) {
    add(`${name} rest`, c(fg), c(bg), 4.5);
    for (const darken of BTN_DARKEN) {
      add(`${name} hover/active (${darken}% black)`, c(fg), colorMixOklab(c(bg), 100 - darken, black), 4.5);
    }
  }
  for (const on of ['base-100', 'base-200']) add(`btn-outline rest text on ${on}`, c('base-content'), c(on), 4.5);

  // menu and btn-ghost hover/focus: color-mix(in oklab, base-content 10%, transparent) over the surface, text base-content.
  for (const on of ['base-100', 'base-200']) {
    add(`menu / btn-ghost hover on ${on}`, c('base-content'), colorMixOklab(c('base-content'), 10, null, c(on)), 4.5);
  }

  // daisyUI built-in alpha resolved to "allow" (see DAISY_BUILTIN_ALPHA): table head text.
  for (const { token, alpha, on, min, source } of DAISY_BUILTIN_ALPHA.filter((e) => e.resolution === 'allow')) {
    for (const surface of on) add(`${source} (${token}/${alpha}) on ${surface}`, blendAlpha(c(token), alpha / 100, c(surface)), c(surface), min);
  }

  // The approved alpha table (muted text, control borders). Templates may only use these (styles-contract).
  for (const { token, alpha, role, on, min } of ALPHA_TABLE) {
    for (const surface of on) add(`${role} ${token}/${alpha} on ${surface}`, blendAlpha(c(token), alpha / 100, c(surface)), c(surface), min);
  }
  return pairs;
}

/**
 * daisyUI components that bake in a low-alpha `base-content` and how each one is resolved (the three allowed
 * answers: allow-list with the pair measured, override with a tested value, or forbid in templates).
 * Values read from node_modules/daisyui/components/*.css (5.8.1); T-D guards that they have not changed.
 */
export const DAISY_BUILTIN_ALPHA = [
  { source: 'table head/foot text', file: 'table.css', expr: 'color-mix(in oklab, var(--color-base-content) 60%, transparent)', token: 'base-content', alpha: 60, on: ['base-100', 'base-200'], min: 4.5, resolution: 'allow' },
  { source: 'input/select/textarea placeholder', file: 'input.css', expr: 'color-mix(in oklab, var(--color-base-content) 40%, transparent)', resolution: 'placeholder utility', note: 'fails 4.5:1 in light; every element with a placeholder attribute must carry placeholder:text-base-content/NN from ALPHA_TABLE (styles-contract check 11)' },
  { source: 'input/select/textarea/checkbox/radio border', file: 'input.css', expr: 'color-mix(in oklab, var(--color-base-content) 20%, #0000)', resolution: 'border utility', note: 'fails 3:1 in light; every control carries border-base-content/NN from ALPHA_TABLE (styles-contract check 11)' },
  { source: 'menu-title text', file: 'menu.css', expr: 'color-mix(in oklab, var(--color-base-content) 40%, transparent)', resolution: 'forbid', note: 'menu-title is on the forbidden list' },
  { source: 'menu disabled item', file: 'menu.css', expr: 'color-mix(in oklab, var(--color-base-content) 20%, transparent)', resolution: 'exempt', note: 'disabled controls are exempt from WCAG 1.4.3' },
  { source: 'stats title/description', resolution: 'forbid', note: 'stats and stat-* are on the forbidden list; use plain markup plus the tested muted alpha' },
  { source: 'label', resolution: 'forbid', note: 'label (daisyUI) is on the forbidden list; use a plain <label> element' },
];

for (const theme of THEMES) {
  const palette = loadPalette(theme, stylesCss);
  for (const [description, fg, bg, min] of pairsFor(palette)) {
    test(`T-C ${theme}: ${description} >= ${min}:1`, () => {
      const { ratio, ok } = checkPair(fg, bg, min);
      assert.ok(ok, `${description} in ${theme} is ${ratio.toFixed(2)}:1, below ${min}:1`);
    });
  }
}

test('T-C negative: the pair checker reports the stock light error on base-100 (2.87:1) as failing', () => {
  const stock = loadPalette('light', '');
  const { ratio, ok } = checkPair(rgbOf(stock, 'error'), rgbOf(stock, 'base-100'), 4.5);
  assert.equal(ok, false);
  assert.ok(Math.abs(ratio - 2.87) < 0.01, `ratio ${ratio}`);
  assert.equal(checkPair(rgbOf(stock, 'primary'), rgbOf(stock, 'base-100'), 4.5).ok, true);
});

test('T-C the same override-free check fails on the stock themes where overrides exist (overrides are needed)', () => {
  const stockDark = loadPalette('dark', '');
  assert.equal(checkPair(rgbOf(stockDark, 'primary'), rgbOf(stockDark, 'base-100'), 4.5).ok, false);
  const stockLight = loadPalette('light', '');
  assert.equal(checkPair(rgbOf(stockLight, 'error-content'), rgbOf(stockLight, 'error'), 4.5).ok, true);
});

// ---------- overrides in src/styles.css stay minimal ----------

const ALLOWED_OVERRIDES = {
  light: ['--color-error', '--color-error-content'],
  dark: ['--color-primary', '--color-primary-content'],
};

function declarations(text) {
  return new Map([...text.matchAll(/(--[a-z0-9-]+):\s*([^;]+);/g)].map(([, key, value]) => [key, value.trim()]));
}

test('T-C styles.css has one override block per theme with the right daisyUI flags', () => {
  const blocks = readOverrides(stylesCss);
  assert.deepEqual([...blocks.keys()].sort(), [...THEMES].sort());
  assert.match(blocks.get('light').body, /default:\s*true;/);
  assert.match(blocks.get('light').body, /prefersdark:\s*false;/);
  assert.match(blocks.get('dark').body, /prefersdark:\s*true;/);
  assert.match(blocks.get('dark').body, /default:\s*false;/);
  assert.match(stylesCss, /@plugin\s+"daisyui"\s*\{\s*themes:\s*false;(\s*include:[^;]+;)?\s*\}/, 'built-in themes are replaced by the two override blocks');
});

for (const theme of THEMES) {
  test(`T-C ${theme}: override block restates the stock theme and differs only in ${ALLOWED_OVERRIDES[theme].join(', ')}`, () => {
    const stock = declarations(read(`node_modules/daisyui/theme/${theme}.css`));
    const override = declarations(readOverrides(stylesCss).get(theme).body);
    assert.deepEqual([...override.keys()].sort(), [...stock.keys()].sort(), 'same variables as the stock theme');
    const changed = [...override.keys()].filter((key) => override.get(key) !== stock.get(key)).sort();
    assert.deepEqual(changed, ALLOWED_OVERRIDES[theme]);
  });

  test(`T-C ${theme}: overridden colours are inside the sRGB gamut`, () => {
    const colors = readOverrides(stylesCss).get(theme).colors;
    for (const key of ALLOWED_OVERRIDES[theme]) {
      const name = key.replace('--color-', '');
      assert.ok(colors.has(name), `${key} missing`);
      assert.ok(oklchInGamut(...parseOklch(colors.get(name))), `${key} ${colors.get(name)} is outside sRGB`);
    }
  });
}

test('T-C every ALPHA_TABLE entry names a real token and backgrounds', () => {
  const stock = stockColors('light');
  for (const { token, alpha, on } of ALPHA_TABLE) {
    assert.ok(stock.has(token));
    assert.ok(alpha > 0 && alpha < 100);
    for (const surface of on) assert.ok(stock.has(surface));
  }
});

// ---------- T-D: the daisyUI 5.8.1 expressions encoded above still match node_modules ----------

const normalise = (text) => text.replace(/\s+/g, ' ');

test('T-D daisyUI version is the pinned one the pair table was written for', () => {
  const pkg = JSON.parse(read('node_modules/daisyui/package.json'));
  assert.equal(pkg.version, '5.8.1', 'daisyUI changed: re-read button.css, menu.css, input.css, table.css and tooltip.css and update pairsFor()');
});

for (const entry of DAISY_BUILTIN_ALPHA.filter((e) => e.file)) {
  test(`T-D daisyUI ${entry.file} still contains the ${entry.source} expression`, () => {
    assert.ok(normalise(read(`node_modules/daisyui/components/${entry.file}`)).includes(entry.expr), entry.expr);
  });
}

test('T-D daisyUI button, menu and tooltip expressions behind the state pairs are unchanged', () => {
  const button = normalise(read('node_modules/daisyui/components/button.css'));
  assert.ok(button.includes('--btn-bg:color-mix(in oklab, var(--btn-color,var(--color-base-200)), #000 7%)'), 'btn hover');
  assert.ok(button.includes('--btn-bg:color-mix(in oklab, var(--btn-color,var(--color-base-200)), #000 5%)'), 'btn 5% state');
  assert.ok(button.includes('.btn-neutral{@layer daisyui.l1.l2{--btn-color:var(--color-neutral);--btn-fg:var(--color-neutral-content)'));
  const menu = normalise(read('node_modules/daisyui/components/menu.css'));
  assert.ok(menu.includes('--menu-active-fg:var(--color-neutral-content);--menu-active-bg:var(--color-neutral)'));
  assert.ok(menu.includes('background-color:color-mix(in oklab, var(--color-base-content) 10%, transparent)'));
  const tooltip = normalise(read('node_modules/daisyui/components/tooltip.css'));
  assert.ok(tooltip.includes('--tt-bg:var(--color-neutral)'));
  assert.ok(tooltip.includes('color:var(--color-neutral-content)'));
});

// ---------- T-H: no hard-coded colours in src/**/*.css outside the two override blocks ----------

const NAMED = 'white|black|red|green|blue|yellow|orange|purple|pink|gray|grey|silver|navy|teal|maroon|olive|aqua|fuchsia|lime|cyan|magenta|brown|gold|indigo|violet';
const HARD_CODED = new RegExp(`#[0-9a-fA-F]{3,8}\\b|\\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch|color)\\(|\\b(?:${NAMED})\\b(?!-)`, 'i');

// Comments are removed outside string literals (an @source glob such as "./**/*.html" contains /* and */).
const withoutComments = (source) => source.replace(/("(?:[^"\\]|\\.)*")|\/\*[\s\S]*?\*\//g, '$1');
const withoutOverrideBlocks = (source) => source.replace(/@plugin\s+"daisyui\/theme"\s*\{[^}]*\}/g, '');
export const offendingCss = (source) => withoutOverrideBlocks(withoutComments(source)).split('\n').filter((line) => HARD_CODED.test(line));

function cssFiles(directory) {
  return readdirSync(new URL(`../${directory}`, import.meta.url), { withFileTypes: true }).flatMap((entry) =>
    entry.isDirectory() ? cssFiles(`${directory}/${entry.name}`) :
      entry.name.endsWith('.css') ? [`${directory}/${entry.name}`] : []);
}

test('T-H src/styles.css uses no hard-coded colours outside the two override blocks', () => {
  assert.ok(cssFiles('src').includes('src/styles.css'));
  for (const file of cssFiles('src')) assert.deepEqual(offendingCss(read(file)), [], file);
});

test('T-H negative: the scan reports hex, rgb() and named colours but not override blocks or comments', () => {
  assert.equal(offendingCss('.a { color: #fff; }').length, 1);
  assert.equal(offendingCss('.a { color: rgb(1 2 3); }').length, 1);
  assert.equal(offendingCss('.a { color: red; }').length, 1);
  assert.equal(offendingCss('/* #fff red */ .a { display: block; }').length, 0);
  assert.equal(offendingCss('@plugin "daisyui/theme" {\n name: "light";\n --color-x: oklch(1 0 0);\n}\n').length, 0);
});
