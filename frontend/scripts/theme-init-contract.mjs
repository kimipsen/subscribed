// Static contract checks for public/theme-init.js. The script is only read as
// text and matched against the constants in src/app/theme/theme.ts; it is never
// executed here. Runtime behaviour is covered by src/app/theme/theme.spec.ts.

const escape = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, String.raw`\$&`);

function capture(source, pattern, what) {
  const match = source.match(pattern);
  if (!match) throw new Error(`${what} not found in src/app/theme/theme.ts`);
  return match[1];
}

/** Reads the shared theme constants from the text of src/app/theme/theme.ts. */
export function readThemeContract(themeTs) {
  const modeList = capture(themeTs, /THEME_MODES\s*=\s*\[([^\]]*)\]/, 'THEME_MODES');
  return {
    storageKey: capture(themeTs, /THEME_STORAGE_KEY\s*=\s*'([^']+)'/, 'THEME_STORAGE_KEY'),
    modes: [...modeList.matchAll(/'([^']*)'/g)].map((match) => match[1]),
    defaultMode: capture(themeTs, /DEFAULT_THEME_MODE\s*:\s*ThemeMode\s*=\s*'([^']+)'/, 'DEFAULT_THEME_MODE'),
    darkQuery: capture(themeTs, /matchMedia\?\.\(\s*'([^']+)'\s*\)/, 'color scheme query'),
  };
}

const withoutComments = (source) =>
  source.replace(/\/\*[^*\r\n]*\*\//g, '').replace(/^[ \t]*\/\/[^\r\n]*$/gm, '');

const count = (code, pattern) => [...code.matchAll(new RegExp(pattern.source, 'g'))].length;

/**
 * Returns one `{ name, ok, detail }` entry per contract rule for the init script text.
 * @param {string} source text of public/theme-init.js
 * @param {ReturnType<typeof readThemeContract>} contract
 */
export function initContractChecks(source, { storageKey, modes, defaultMode, darkQuery }) {
  const code = withoutComments(source);
  const trimmed = code.trim();
  const guarded = [...code.matchAll(/stored\s*===\s*'([^']*)'/g)].map((match) => match[1]);
  const getItemAt = code.search(/\.getItem\(/);
  const matchMediaAt = code.search(/\.matchMedia\(/);
  const writeAt = code.search(/\.dataset\.theme\s*=/);

  return [
    {
      name: 'is a self-contained classic script (IIFE, no import/export)',
      ok:
        trimmed.startsWith('(function () {') &&
        trimmed.endsWith('})();') &&
        !/^[ \t]*(?:import|export)\b/m.test(code),
      detail: 'theme-init.js must stay a classic, render-blocking IIFE',
    },
    {
      name: 'uses let/const only',
      ok: !/\bvar\b/.test(code),
      detail: 'theme-init.js must not declare var',
    },
    {
      name: 'executes no code strings and writes no markup',
      ok: !/\b(?:eval|Function|setTimeout|setInterval)\s*\(|\bnew\s+Function\b|\.innerHTML\b|document\.write/.test(code),
      detail: 'theme-init.js must stay CSP-compatible (no eval-like calls or markup writes)',
    },
    {
      name: 'never writes storage or inline style',
      ok: !/\b(?:setItem|removeItem|clear)\s*\(|\.style\b/.test(code),
      detail: 'theme-init.js may only read storage and set data-theme',
    },
    {
      name: `starts in the default '${defaultMode}' mode`,
      ok: new RegExp(String.raw`\blet\s+mode\s*=\s*'${escape(defaultMode)}'\s*;`).test(code),
      detail: `mode must start as DEFAULT_THEME_MODE '${defaultMode}'`,
    },
    {
      name: `reads '${storageKey}' once, inside try/catch`,
      ok:
        count(code, /\.getItem\(/) === 1 &&
        new RegExp(String.raw`try\s*\{\s*const\s+stored\s*=\s*window\.localStorage\.getItem\(\s*'${escape(storageKey)}'\s*\)\s*;`).test(code) &&
        /\}\s*catch\s*(?:\(\s*\w+\s*\)\s*)?\{\s*\}/.test(code),
      detail: `storage must be read from THEME_STORAGE_KEY '${storageKey}' and failures ignored`,
    },
    {
      name: `accepts exactly the modes ${modes.join('/')}`,
      ok:
        JSON.stringify([...guarded].sort()) === JSON.stringify([...modes].sort()) &&
        /\)\s*mode\s*=\s*stored\s*;/.test(code),
      detail: `stored values must be validated against THEME_MODES (found ${guarded.join(', ') || 'none'})`,
    },
    {
      name: 'explicit modes apply directly; system resolves via matchMedia, falling back to light',
      ok: new RegExp(
        [
          String.raw`\blet\s+theme\s*=\s*mode\s*;`,
          String.raw`\s*if\s*\(\s*mode\s*===\s*'system'\s*\)\s*\{`,
          String.raw`\s*try\s*\{\s*theme\s*=\s*window\.matchMedia\(\s*'${escape(darkQuery)}'\s*\)\.matches\s*\?\s*'dark'\s*:\s*'light'\s*;\s*\}`,
          String.raw`\s*catch\s*(?:\(\s*\w+\s*\)\s*)?\{\s*theme\s*=\s*'light'\s*;\s*\}`,
          String.raw`\s*\}`,
        ].join(''),
      ).test(code) && count(code, /\.matchMedia\(/) === 1,
      detail: `resolution must mirror resolveTheme() using '${darkQuery}'`,
    },
    {
      name: 'applies the theme once via documentElement.dataset.theme, last',
      ok:
        count(code, /\bdocument\.documentElement\.dataset\.theme\s*=\s*theme\s*;/) === 1 &&
        count(code, /\.dataset\b/) === 1 &&
        !/\bsetAttribute\b/.test(code) &&
        getItemAt !== -1 && getItemAt < matchMediaAt && matchMediaAt < writeAt,
      detail: 'theme must be written once, after resolution, through dataset.theme',
    },
  ];
}
