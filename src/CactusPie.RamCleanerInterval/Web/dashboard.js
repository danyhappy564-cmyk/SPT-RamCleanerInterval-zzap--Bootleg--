// Live page: polls /api/live once a second (every 5 s while the tab is hidden) and redraws.
(function () {
  var T = window.RC || {};
  var $ = function (id) { return document.getElementById(id); };
  var esc = window.rcEsc;
  var hover = {};
  ['memChart', 'fpsChart'].forEach(function (id) {
    var box = $(id);
    box.addEventListener('mouseenter', function () { hover[id] = 1; });
    box.addEventListener('mouseleave', function () { hover[id] = 0; });
  });

  function bars(id, list) {
    var card = $(id + 'Card');
    if (!list || !list.length) { card.classList.add('hidden'); return; }
    card.classList.remove('hidden');
    $(id).innerHTML = list.map(function (b) {
      var p = Math.max(1, Math.min(100, b.s * 100));
      return '<div class="bar' + (b.c ? ' ' + b.c : '') + '"><span class="blbl" title="' + esc(b.l) + '">' + esc(b.l) +
        '</span><span class="track"><span class="fill" style="width:' + p.toFixed(1) + '%"></span></span><span class="bval">' + esc(b.v) + '</span></div>';
    }).join('');
  }

  function chart(id, d) {
    var box = $(id);
    if (!d || !d.x || d.x.length < 2) { box.innerHTML = '<p class="sub">' + esc(T.collecting) + '</p>'; return; }
    if (!hover[id]) window.rcChart(box, d);
  }

  function render(d) {
    if (d.busy) return; // another tab's update is still waiting for the game
    if (d.error) { $('sub').textContent = d.error; return; }
    $('sub').textContent = d.sub;
    $('alerts').innerHTML = (d.alerts || []).map(function (a) { return '<div class="alert">' + esc(a) + '</div>'; }).join('');
    $('tiles').innerHTML = d.tiles.map(function (t) {
      return '<div class="tile lv' + t.lv + '"><div class="lbl">' + esc(t.k) + '</div><div class="val">' + esc(t.v) +
        '</div><div class="note">' + esc(t.n) + '</div></div>';
    }).join('');
    chart('memChart', d.mem);
    chart('fpsChart', d.fps);
    bars('hitch', d.hitch);
    bars('mods', d.mods);
    bars('alloc', d.alloc);
    bars('heavy', d.heavy);
    $('diagBtn').textContent = d.diagOn ? T.diagOff : T.diagOn;
    $('status').textContent = d.status;
  }

  function tick() {
    fetch('api/live', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(render, function () { $('sub').textContent = T.offline; })
      .then(function () { setTimeout(tick, document.hidden ? 5000 : 1000); });
  }

  document.querySelectorAll('[data-act]').forEach(function (b) {
    b.addEventListener('click', function () {
      b.disabled = true;
      window.rcPost('api/action', { name: b.getAttribute('data-act') })
        .then(function (r) { $('actmsg').textContent = r.msg || r.error || ''; }, function () { $('actmsg').textContent = T.offline; })
        .then(function () { b.disabled = false; });
    });
  });

  tick();
})();
