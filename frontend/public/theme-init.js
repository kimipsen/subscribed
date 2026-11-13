// Keep in sync with src/app/theme/theme.ts (isThemeMode/resolveTheme). Tested by scripts/theme.test.mjs.
(function () {
  var theme = 'light', mode = 'system';
  try {
    var stored = window.localStorage.getItem('subscribed.theme');
    if (stored === 'light' || stored === 'dark' || stored === 'system') mode = stored;
  } catch (e) { /* storage unavailable: behave as system */ }
  if (mode === 'system') {
    try { theme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'; }
    catch (e) { theme = 'light'; }
  } else { theme = mode; }
  document.documentElement.setAttribute('data-theme', theme);
})();
