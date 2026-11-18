// po-dev.js — SAME FILE IN EVERY PO WEB APP (wwwroot/js/po-dev.js). Drives po-dev.html.
// Polls the server every 2 s and reads the opener page's live stats (window.__poStats).
// A separate file because some Po apps forbid inline script in their CSP.
(function () {
  var DIAG_CANDIDATES = ['/api/diag/status', '/api/diag', '/health/json', '/health'];
  var diagUrl = null;
  var $ = function (id) { return document.getElementById(id); };

  function badge(status) {
    var s = String(status || '').toLowerCase();
    var cls = /healthy|ok|up|reachable|pass|working|ready/.test(s) ? 'ok'
            : /degraded|warn|slow|not.?configured|unknown|skipped/.test(s) ? 'warn' : 'bad';
    return '<span class="po-badge po-badge--' + cls + '">' + escapeHtml(status || '?') + '</span>';
  }
  function escapeHtml(s) { return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  // Collect {name, status, ms} entries from whatever shape the app's diag JSON has.
  function collect(node, out, nameHint) {
    if (!node || typeof node !== 'object' || out.length > 60) return;
    if (Array.isArray(node)) { node.forEach(function (n) { collect(n, out); }); return; }
    var status = node.status || node.Status || node.state || node.verdict;
    var name = node.name || node.Name || node.key || node.title || node.label || nameHint;
    if (status && name && typeof status === 'string') {
      var ms = node.durationMs || node.latencyMs || node.duration || node.elapsedMs || node.ms;
      out.push({ name: name, status: status, ms: typeof ms === 'number' ? Math.round(ms) : (typeof ms === 'string' ? ms : '') });
    }
    Object.keys(node).forEach(function (k) {
      var v = node[k];
      if (v && typeof v === 'object') collect(v, out, k);
    });
  }

  // The first candidate that answers OK with JSON holding at least one named check. An app may
  // answer an unknown /api/* path with a JSON 404 body, so content type alone is not enough.
  async function findDiag() {
    for (var i = 0; i < DIAG_CANDIDATES.length; i++) {
      try {
        var r = await fetch(DIAG_CANDIDATES[i], { headers: { Accept: 'application/json' }, cache: 'no-store' });
        var ct = r.headers.get('content-type') || '';
        if (!r.ok || ct.indexOf('json') < 0) continue;
        var found = []; collect(await r.json(), found);
        if (found.length) return DIAG_CANDIDATES[i];
      } catch (_) { }
    }
    return null;
  }

  async function pollServer() {
    var t0 = performance.now();
    try {
      var r = await fetch('/health/live', { cache: 'no-store' });
      $('latency').textContent = Math.round(performance.now() - t0);
      $('live').textContent = r.ok ? 'UP' : r.status;
      var c = r.headers.get('x-commit');
      $('commit').textContent = c ? c.slice(0, 7) : '–';
    } catch (e) {
      $('live').textContent = 'DOWN'; $('latency').textContent = '–';
    }
    if (diagUrl === null) diagUrl = (await findDiag()) || '';
    if (!diagUrl) { $('deps').innerHTML = '<p class="empty">This app exposes no JSON diagnostics endpoint.</p>'; return; }
    $('source').textContent = diagUrl;
    try {
      var d = await fetch(diagUrl, { headers: { Accept: 'application/json' }, cache: 'no-store' });
      var json = await d.json();
      var items = []; collect(json, items);
      var seen = {}; items = items.filter(function (i) { var k = i.name + '|' + i.status; if (seen[k]) return false; seen[k] = 1; return true; });
      $('deps').innerHTML = items.length
        ? items.map(function (i) { return '<div class="row"><span>' + escapeHtml(i.name) + '</span><code>' + (i.ms !== '' ? escapeHtml(i.ms) + ' ms' : '') + '</code>' + badge(i.status) + '</div>'; }).join('')
        : '<p class="empty">' + escapeHtml(diagUrl) + ' answered ' + d.status + ' with no named checks.</p>';
    } catch (e) {
      $('deps').innerHTML = '<p class="empty">Could not read ' + escapeHtml(diagUrl) + ': ' + escapeHtml(e.message) + '</p>';
    }
  }

  function readOpener() {
    var o = null;
    try { o = window.opener && !window.opener.closed ? window.opener : null; } catch (_) { }
    var s = o && o.__poStats;
    if (!s) { $('route').textContent = 'The app window is closed, so page stats are paused.'; return; }
    try {
      var title = o.document.title;
      $('app').textContent = title ? title + ' · developer view' : 'Developer view';
      document.title = $('app').textContent;
      var stamp = o.document.querySelector('.po-version');
      $('build').textContent = stamp ? stamp.textContent.trim() : '–';
      $('route').textContent = 'Route ' + o.location.pathname + ' · ' + s.navigations + ' navigations';
      var mem = o.performance && o.performance.memory;
      $('mem').textContent = mem ? (mem.usedJSHeapSize / 1048576).toFixed(1) : 'n/a';
    } catch (_) { }
    $('fps').textContent = s.fps;
    $('longtasks').textContent = s.longTasks;
    var secs = Math.round((Date.now() - s.startedAt) / 1000);
    $('uptime').textContent = secs < 120 ? secs + 's' : Math.round(secs / 60) + 'm';
    $('errors').innerHTML = s.errors.length
      ? s.errors.slice(-15).reverse().map(function (e) { return '<li>' + escapeHtml(e.at.slice(11, 19) + ' ' + e.msg) + '</li>'; }).join('')
      : '<li class="empty" style="list-style:none;margin-left:-18px">No errors since the page opened.</li>';
  }

  function loop() {
    readOpener();
    pollServer().finally(function () { $('updated').textContent = 'updated ' + new Date().toLocaleTimeString(); });
  }
  loop();
  setInterval(readOpener, 1000);
  setInterval(function () { pollServer().finally(function () { $('updated').textContent = 'updated ' + new Date().toLocaleTimeString(); }); }, 2000);
})();
