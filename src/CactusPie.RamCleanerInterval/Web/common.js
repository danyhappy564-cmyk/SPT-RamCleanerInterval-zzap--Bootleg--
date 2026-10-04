// Shared by every page: POST helper (the custom header is what lets the plugin accept the change) and the language button.
window.rcPost = function (url, fields) {
  var body = Object.keys(fields).map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(fields[k]); }).join('&');
  return fetch(url, {
    method: 'POST', cache: 'no-store',
    headers: { 'X-RamCleaner': '1', 'Content-Type': 'application/x-www-form-urlencoded' },
    body: body
  }).then(function (r) { return r.json(); });
};
window.rcEsc = function (t) {
  return String(t == null ? '' : t).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; });
};
(function () {
  var b = document.getElementById('lang');
  if (!b) return;
  b.addEventListener('click', function () {
    b.disabled = true;
    window.rcPost('/api/settings', { key: '0. Mode|Language', value: b.getAttribute('data-to') })
      .then(function () { location.reload(); }, function () { b.disabled = false; });
  });
})();
