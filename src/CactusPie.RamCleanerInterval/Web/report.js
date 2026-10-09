// Session report page: refreshes itself (every 10 s while a raid is in progress, 30 s otherwise) so the raid in
// progress grows without F5. Only the content below the nav is swapped; charts are redrawn with window.rcChart.
(function () {
  var box = document.getElementById('autoOn'), msg = document.getElementById('autoMsg');
  if (!box) return;
  try { if (localStorage.getItem('rcReportAuto') === '0') box.checked = false; } catch (e) { }
  box.addEventListener('change', function () {
    try { localStorage.setItem('rcReportAuto', box.checked ? '1' : '0'); } catch (e) { }
    if (box.checked) schedule(500);
  });

  var timer = null, busy = false;
  function keep(n) {
    return n.nodeType === 1 && (n.tagName === 'NAV' || n.tagName === 'SCRIPT' || n.tagName === 'STYLE' || n.id === 'auto' || n.id === 'tip');
  }
  function live() { return !!document.querySelector('main .live'); }
  function schedule(ms) {
    clearTimeout(timer);
    timer = setTimeout(tick, ms != null ? ms : (document.hidden ? 30000 : live() ? 10000 : 30000));
  }
  function tick() {
    if (!box.checked || busy) return;
    if (document.hidden) { schedule(); return; }
    busy = true;
    fetch('report', { cache: 'no-store' }).then(function (r) {
      if (!r.ok) throw new Error(r.status);
      return r.text();
    }).then(function (html) {
      var fresh = new DOMParser().parseFromString(html, 'text/html').querySelector('main.viz-root');
      if (!fresh) throw new Error('page');
      var main = document.querySelector('main.viz-root'), y = window.scrollY;
      var open = {};
      main.querySelectorAll('details[open]').forEach(function (d, i) { open[i] = true; });
      Array.prototype.slice.call(main.childNodes).forEach(function (n) { if (!keep(n)) main.removeChild(n); });
      var tip = document.getElementById('tip');
      Array.prototype.slice.call(fresh.childNodes).forEach(function (n) {
        if (!keep(n)) main.insertBefore(document.importNode(n, true), tip);
      });
      main.querySelectorAll('details').forEach(function (d, i) { if (open[i]) d.open = true; });
      main.querySelectorAll('.plot[data-chart]').forEach(function (p) {
        try { window.rcChart(p, JSON.parse(p.getAttribute('data-chart'))); } catch (e) { }
      });
      window.scrollTo(0, y);
      msg.textContent = window.RC.updated + ' ' + new Date().toLocaleTimeString();
      msg.classList.remove('err');
    }).catch(function () {
      msg.textContent = window.RC.offline;
      msg.classList.add('err');
    }).then(function () { busy = false; schedule(); });
  }
  document.addEventListener('visibilitychange', function () { if (!document.hidden && box.checked) schedule(300); });
  // This script runs above the report body: wait for it before checking for a raid in progress.
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', function () { schedule(); }); else schedule();
})();
