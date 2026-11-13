// Keep in sync with src/app/theme/theme.ts (isThemeMode/resolveTheme). Checked statically by scripts/theme.test.mjs.
(function () {
  let mode = 'system';
  try {
    const stored = window.localStorage.getItem('subscribed.theme');
    if (stored === 'light' || stored === 'dark' || stored === 'system') mode = stored;
  } catch { /* storage unavailable: behave as system */ }
  let theme = mode;
  if (mode === 'system') {
    try { theme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'; }
    catch { theme = 'light'; }
  }
  document.documentElement.dataset.theme = theme;
})();
