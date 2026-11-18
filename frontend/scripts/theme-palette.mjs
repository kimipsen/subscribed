// Colour maths and palette loading shared by scripts/theme.test.mjs (contrast) and
// scripts/styles-contract.test.mjs (alpha scan). Pure functions only; nothing here runs a test.
//
// Colour source decision: colours are read from source, not from built CSS. The stock daisyUI theme files
// (node_modules/daisyui/theme/light.css and dark.css, exact pinned version) are the base and the override
// blocks in src/styles.css win per colour. No build is needed first, and a diff in the overrides is reviewable.
import { readFileSync } from 'node:fs';

const root = new URL('../', import.meta.url);
export const read = (path) => readFileSync(new URL(path, root), 'utf8');

// ---------- oklch -> sRGB ----------

const toLinear = (L, C, h) => {
  const hr = (h * Math.PI) / 180;
  const a = C * Math.cos(hr);
  const b = C * Math.sin(hr);
  const l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const m = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s = (L - 0.0894841775 * a - 1.291485548 * b) ** 3;
  return [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ];
};

const linearToOklab = ([r, g, b]) => {
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  return [
    0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s,
    1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s,
    0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s,
  ];
};

const oklabToLinear = ([L, a, b]) => {
  const l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const m = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s = (L - 0.0894841775 * a - 1.291485548 * b) ** 3;
  return [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ];
};

const encode = (c) => (c <= 0.0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - 0.055);
const decode = (v) => (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
const clip01 = (c) => Math.min(1, Math.max(0, c));
const EPS = 1e-6;
const inGamut = (linear) => linear.every((c) => c >= -EPS && c <= 1 + EPS);
const clipLinear = (linear) => linear.map(clip01);
const deltaEOK = (p, q) => Math.hypot(p[0] - q[0], p[1] - q[1], p[2] - q[2]);

/**
 * oklch(L C h) with L in 0..1 to sRGB channels on 0..255.
 * Out-of-gamut colours are mapped the way browsers do it, with the CSS Color 4 gamut mapping algorithm
 * (chroma reduction by bisection, accepting a clipped colour within a deltaE OK of 0.02), not by clipping
 * channels. This replaces a separate "every colour must be inside the sRGB gamut" rule.
 */
export function oklchToSrgb(L, C, h) {
  if (L >= 1) return [255, 255, 255];
  if (L <= 0) return [0, 0, 0];
  const toRgb255 = (linear) => clipLinear(linear).map((c) => encode(c) * 255);
  const start = toLinear(L, C, h);
  if (inGamut(start)) return toRgb255(start);
  const JND = 0.02;
  const EPSILON = 0.0001;
  let clipped = clipLinear(start);
  if (deltaEOK(linearToOklab(clipped), linearToOklab(start)) < JND) return toRgb255(clipped);
  let min = 0;
  let max = C;
  let minInGamut = true;
  while (max - min > EPSILON) {
    const chroma = (min + max) / 2;
    const current = toLinear(L, chroma, h);
    if (minInGamut && inGamut(current)) {
      min = chroma;
      continue;
    }
    clipped = clipLinear(current);
    const e = deltaEOK(linearToOklab(clipped), linearToOklab(current));
    if (e < JND) {
      if (JND - e < EPSILON) return toRgb255(clipped);
      minInGamut = false;
      min = chroma;
    } else {
      max = chroma;
    }
  }
  return toRgb255(clipped);
}

/** True when the colour is inside the sRGB gamut without any mapping. */
export function oklchInGamut(L, C, h) {
  return inGamut(toLinear(L, C, h));
}

export function parseOklch(text) {
  const match = /^oklch\(\s*([\d.]+)%\s+([\d.]+)\s+([\d.]+)\s*\)$/.exec(text.trim());
  if (!match) throw new Error(`unsupported colour value: ${text}`);
  return [Number(match[1]) / 100, Number(match[2]), Number(match[3])];
}

export const toSrgb = (oklch) => oklchToSrgb(...oklch);

// ---------- WCAG ----------

export function luminance([r, g, b]) {
  return 0.2126 * decode(r / 255) + 0.7152 * decode(g / 255) + 0.0722 * decode(b / 255);
}

export function contrastRatio(a, b) {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

// ---------- blending ----------

/**
 * Alpha blend rule: computed in gamma-encoded sRGB, per channel on 0..255, c = a*fg + (1-a)*bg. This matches how
 * browsers resolve the colour daisyUI emits for `text-base-content/NN` (a `color-mix(in oklab, ... NN%, transparent)`
 * that resolves to the same colour with alpha NN%, composited in sRGB). It must not blend in linear light or oklab.
 */
export function blendAlpha(fg, alpha, bg) {
  return fg.map((c, i) => alpha * c + (1 - alpha) * bg[i]);
}

/**
 * color-mix(in oklab, A p%, B) for the two shapes daisyUI 5.8.1 uses:
 *  - B opaque (an sRGB triplet), for example `#000` in the btn hover/active mix: mix in oklab, then to sRGB;
 *  - B transparent (null), for example `transparent` in the menu hover: the result keeps A's colour and carries
 *    alpha p%, so it is composited over `onto` (an sRGB triplet) with the alpha blend rule above.
 * Both inputs are sRGB triplets on 0..255. `pct` is 0..100.
 */
export function colorMixOklab(a, pct, b, onto) {
  const p = pct / 100;
  if (b === null) {
    if (!onto) throw new Error('colorMixOklab with a transparent second colour needs the colour it sits on');
    return blendAlpha(a, p, onto);
  }
  const lab = (srgb) => linearToOklab(srgb.map((c) => decode(c / 255)));
  const la = lab(a);
  const lb = lab(b);
  const mixed = la.map((v, i) => p * v + (1 - p) * lb[i]);
  return clipLinear(oklabToLinear(mixed)).map((c) => encode(c) * 255);
}

// ---------- palette loading ----------

export const COLOR_NAMES = [
  'base-100', 'base-200', 'base-300', 'base-content', 'primary', 'primary-content', 'secondary', 'secondary-content',
  'accent', 'accent-content', 'neutral', 'neutral-content', 'info', 'info-content', 'success', 'success-content',
  'warning', 'warning-content', 'error', 'error-content',
];

const COLOR_DECLARATION = /--color-([a-z0-9-]+):\s*(oklch\([^)]*\))/g;

function stockTheme(name) {
  const css = read(`node_modules/daisyui/theme/${name}.css`);
  const colors = new Map();
  for (const [, key, value] of css.matchAll(COLOR_DECLARATION)) colors.set(key, value);
  return colors;
}

/** Override blocks in src/styles.css: `@plugin "daisyui/theme" { name: "light"; ... --color-x: oklch(...); }`. */
export function readOverrides(stylesCss) {
  const blocks = new Map();
  for (const match of stylesCss.matchAll(/@plugin\s+"daisyui\/theme"\s*\{([^}]*)\}/g)) {
    const body = match[1];
    const name = /name:\s*"([a-z]+)"/.exec(body)?.[1];
    if (!name) throw new Error('a daisyui/theme override block has no name');
    const colors = new Map();
    for (const [, key, value] of body.matchAll(COLOR_DECLARATION)) colors.set(key, value);
    blocks.set(name, { colors, body, text: match[0] });
  }
  return blocks;
}

/** Stock colours with overrides winning per colour; values are oklch strings. */
export function loadThemeColors(theme, stylesCss) {
  const colors = stockTheme(theme);
  const override = readOverrides(stylesCss).get(theme);
  if (override) for (const [key, value] of override.colors) colors.set(key, value);
  return colors;
}

export const stockColors = stockTheme;

/** sRGB triplets (0..255) for every colour of a theme. */
export function loadPalette(theme, stylesCss) {
  const palette = new Map();
  for (const [key, value] of loadThemeColors(theme, stylesCss)) palette.set(key, toSrgb(parseOklch(value)));
  return palette;
}

// ---------- the approved alpha table ----------

/**
 * Every `text-base-content/NN` and `border-base-content/NN` (and `placeholder:text-base-content/NN`) used in
 * templates must appear here, and each entry must pass its minimum on every background listed, in both themes.
 * `role` is what the alpha is for. Muted text and the placeholder need 4.5:1 (WCAG 1.4.3), control borders 3:1
 * (WCAG 1.4.11). Measured with the alpha blend rule below, the smallest alphas that pass in both themes are 60
 * (muted text: 4.64 / 4.55 in light on base-100 / base-200) and 50 (border: 3.38 / 3.34 in light). The table uses
 * 70 and 55 to leave a margin for anti-aliasing and for base-300 style surfaces. The implementation plan expected
 * larger values (85 to 90 and 70 to 75); its sample ratios were measured differently, the test is the authority.
 */
export const ALPHA_TABLE = [
  { token: 'base-content', alpha: 70, role: 'muted text', on: ['base-100', 'base-200'], min: 4.5 },
  { token: 'base-content', alpha: 55, role: 'control border', on: ['base-100', 'base-200'], min: 3 },
];

export const ALPHA_UTILITY = /(?:^|:)((?:placeholder:)?(?:text|border))-base-content\/(\d+)$/;
