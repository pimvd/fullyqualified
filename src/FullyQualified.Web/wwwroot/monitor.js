// Keeps the monitor page current: every few seconds it fetches the page again and swaps the content, without a full reload.
(() => {
  const box = document.getElementById('monitor');
  if (!box) return;
  const every = Number(box.getAttribute('data-refresh') || 10) * 1000;
  async function refresh() {
    if (document.hidden) return;
    try {
      const response = await fetch(location.href, { credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'text/html' } });
      if (!response.ok) return;
      const fresh = new DOMParser().parseFromString(await response.text(), 'text/html').getElementById('monitor');
      if (fresh) document.getElementById('monitor').innerHTML = fresh.innerHTML;   // a sign-in page has no monitor, and is ignored
    } catch { /* the server may be restarting: try again next time */ }
  }
  setInterval(refresh, every);
})();
