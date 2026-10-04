// Settings page: the same entries as F12, grouped by category. Every change is sent at once and applied in the game.
(function () {
  var T = window.RC || {};
  var $ = function (id) { return document.getElementById(id); };
  var esc = window.rcEsc;
  var API = T.api || '/api/settings', STATUS = T.statusApi || '';
  var LANGUAGE = '0. Mode|Language', CP_LANGUAGE = 'Language|Language', PRESET = '0. Mode|Preset', PORT = '18. Web page|Port', WEB = '18. Web page|Enabled';

  function control(it) {
    if (it.type === 'bool') {
      return '<label class="switch"><input type="checkbox"' + (it.v === 'true' ? ' checked' : '') + ' aria-label="' + esc(it.name) +
        '"><span></span></label>';
    }
    if (it.type === 'list') {
      return '<select aria-label="' + esc(it.name) + '">' + it.opts.map(function (o) {
        return '<option' + (o === it.v ? ' selected' : '') + '>' + esc(o) + '</option>';
      }).join('') + '</select>';
    }
    if ((it.type === 'int' || it.type === 'float') && it.min != null) {
      var step = it.type === 'int' ? 1 : (it.max - it.min <= 2 ? 0.01 : it.max - it.min <= 20 ? 0.1 : 0.5);
      return '<input type="range" min="' + it.min + '" max="' + it.max + '" step="' + step + '" value="' + esc(it.v) + '" aria-label="' + esc(it.name) +
        '"><input type="number" min="' + it.min + '" max="' + it.max + '" step="' + (it.type === 'int' ? 1 : 'any') + '" value="' + esc(it.v) + '">';
    }
    if (it.type === 'int' || it.type === 'float') {
      return '<input type="number" step="' + (it.type === 'int' ? 1 : 'any') + '" value="' + esc(it.v) + '" aria-label="' + esc(it.name) + '">';
    }
    return '<input type="text" value="' + esc(it.v) + '" aria-label="' + esc(it.name) + '">';
  }

  function setValue(row, it, v) {
    it.v = v;
    var inputs = row.querySelectorAll('input,select');
    inputs.forEach(function (el) {
      if (el.type === 'checkbox') el.checked = v === 'true'; else el.value = v;
    });
    row.querySelector('.reset').classList.toggle('hidden', v === it.def);
  }

  function save(row, it, v) {
    var state = row.querySelector('.state');
    state.className = 'state';
    state.textContent = T.saving;
    window.rcPost(API, { key: it.key, value: v }).then(function (r) {
      if (!r.ok) { state.className = 'state err'; state.textContent = r.error || T.failed; setValue(row, it, it.v); return; }
      setValue(row, it, r.v);
      state.className = 'state ok';
      state.textContent = T.saved;
      setTimeout(function () { if (state.textContent === T.saved) state.textContent = ''; }, 2500);
      if (it.key === LANGUAGE || it.key === CP_LANGUAGE) { location.reload(); return; }
      if (STATUS) { $('msg').textContent = T.cpSent; setTimeout(status, 1500); }
      if (it.key === PRESET) { $('msg').textContent = T.presetApplied; load(); return; }
      if (it.key === PORT) {
        $('msg').textContent = T.moving.replace('{0}', r.v);
        setTimeout(function () { location.href = location.protocol + '//' + location.hostname + ':' + r.v + '/settings'; }, 2500);
      }
      if (it.key === WEB && r.v === 'false') $('msg').textContent = T.webOff;
    }, function () { state.className = 'state err'; state.textContent = T.offline; });
  }

  function bind(row, it) {
    row.querySelectorAll('input,select').forEach(function (el) {
      if (el.type === 'range') {
        var num = row.querySelector('input[type=number]');
        el.addEventListener('input', function () { num.value = el.value; });
      }
      el.addEventListener('change', function () {
        save(row, it, el.type === 'checkbox' ? (el.checked ? 'true' : 'false') : el.value);
      });
    });
    row.querySelector('.reset').addEventListener('click', function () { save(row, it, it.def); });
  }

  function render(d) {
    var list = $('list');
    list.innerHTML = '';
    d.groups.forEach(function (g) {
      var sec = document.createElement('section');
      sec.className = 'card set';
      sec.innerHTML = '<h2>' + esc(g.cat) + '</h2>';
      g.items.forEach(function (it) {
        var row = document.createElement('div');
        row.className = 'row';
        row.setAttribute('data-q', (g.cat + ' ' + it.name + ' ' + it.desc + ' ' + it.key).toLowerCase());
        row.innerHTML = '<div class="info"><div class="name">' + esc(it.name) + '</div><div class="desc">' + esc(it.desc) +
          '</div><div class="key">' + esc(T.defaultIs) + ' ' + esc(it.type === 'bool' ? (it.def === 'true' ? T.on : T.off) : it.def) + '</div></div><div class="ctl">' + control(it) +
          '<button type="button" class="reset' + (it.v === it.def ? ' hidden' : '') + '">' + esc(T.reset) + '</button><span class="state"></span></div>';
        bind(row, it);
        sec.appendChild(row);
      });
      list.appendChild(sec);
    });
    filter();
  }

  function filter() {
    var q = $('q').value.trim().toLowerCase();
    document.querySelectorAll('.set').forEach(function (sec) {
      var any = false;
      sec.querySelectorAll('.row').forEach(function (row) {
        var show = !q || row.getAttribute('data-q').indexOf(q) >= 0;
        row.classList.toggle('hidden', !show);
        any = any || show;
      });
      sec.classList.toggle('hidden', !any);
    });
  }

  function load() {
    fetch(API, { cache: 'no-store' }).then(function (r) { return r.json(); })
      .then(function (d) { if (d.error) { $('msg').textContent = d.error; return; } render(d); }, function () { $('msg').textContent = T.offline; });
  }

  // CompoundingPerf tab: server status from its plugin, refreshed every 3 s.
  function status(refresh) {
    var req = refresh ? window.rcPost(STATUS, {}) : fetch(STATUS, { cache: 'no-store' }).then(function (r) { return r.json(); });
    return req.then(function (d) {
      $('cpStatus').textContent = d.error || d.status;
      $('cpLast').textContent = d.last || '';
    }, function () { $('cpStatus').textContent = T.offline; });
  }

  if (STATUS && $('cpStatus')) {
    $('cpRefresh').addEventListener('click', function () { status(true).then(function () { setTimeout(function () { status(); load(); }, 1500); }); });
    status();
    setInterval(function () { if (!document.hidden) status(); }, 3000);
  }

  $('q').addEventListener('input', filter);
  load();
})();
