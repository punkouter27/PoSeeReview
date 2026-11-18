// po-devwindow.js — SAME FILE IN EVERY PO WEB APP (wwwroot/js/po-devwindow.js).
// Pressing Tab while nothing on the page has focus opens a separate developer window
// (/po-dev.html) with live diagnostics and stats. Once a control has focus, Tab moves focus as
// normal, so keyboard navigation keeps working. The page also collects the stats that window
// reads through window.opener: frame rate, errors, long tasks, navigations.
(function () {
  if (window.__poStats) return;
  var stats = window.__poStats = {
    startedAt: Date.now(), fps: 0, errors: [], longTasks: 0, navigations: 0,
    lastRoute: location.pathname
  };

  function pushError(msg) {
    stats.errors.push({ at: new Date().toISOString(), msg: String(msg).slice(0, 300) });
    if (stats.errors.length > 50) stats.errors.shift();
  }
  window.addEventListener('error', function (e) { pushError(e.message || 'error'); });
  window.addEventListener('unhandledrejection', function (e) {
    pushError('Unhandled promise: ' + (e.reason && e.reason.message || e.reason));
  });

  // Frame rate, sampled once a second. Stops while the tab is hidden (rAF does not run).
  var frames = 0, last = performance.now();
  function tick(now) {
    frames++;
    if (now - last >= 1000) { stats.fps = Math.round(frames * 1000 / (now - last)); frames = 0; last = now; }
    requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);

  try {
    new PerformanceObserver(function (list) { stats.longTasks += list.getEntries().length; })
      .observe({ type: 'longtask', buffered: true });
  } catch (_) { /* longtask is Chromium-only */ }

  // Blazor navigations change the path without a page load.
  setInterval(function () {
    if (location.pathname !== stats.lastRoute) { stats.lastRoute = location.pathname; stats.navigations++; }
  }, 500);

  var devWin = null;
  function openDevWindow() {
    if (devWin && !devWin.closed) { devWin.focus(); return true; }
    devWin = window.open('/po-dev.html', 'po-dev', 'popup,width=560,height=820');
    return !!devWin;
  }
  window.poOpenDevWindow = openDevWindow;

  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Tab' || e.shiftKey || e.ctrlKey || e.altKey || e.metaKey) return;
    var a = document.activeElement;
    var nothingFocused = !a || a === document.body || a === document.documentElement;
    if (!nothingFocused) return;
    if (openDevWindow()) e.preventDefault();
  }, true);
})();
