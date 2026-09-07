// Same-origin blocking script: resolve appearance before CSS and application rendering.
(function () {
  var preference = 'system';
  try { var stored = localStorage.getItem('workbench.appearance'); if (stored === 'light' || stored === 'dark') preference = stored; } catch { /* System fallback. */ }
  var dark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
  var theme = preference === 'system' ? (dark ? 'dark' : 'light') : preference;
  document.documentElement.dataset.theme = theme;
  document.documentElement.style.colorScheme = theme;
  document.querySelector('meta[name="theme-color"]').setAttribute('content', theme === 'dark' ? '#191919' : '#f6f5f2');
})();
