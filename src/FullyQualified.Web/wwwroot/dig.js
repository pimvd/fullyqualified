// The DNS lookup overlay: a live "dig" against the system resolver and public resolvers.
(() => {
  const dlg = document.getElementById('dig');
  if (!dlg) return;
  const $ = id => document.getElementById(id);
  const L = key => dlg.getAttribute('data-l-' + key) || key;
  const nameInput = $('dig-name'), resolverSelect = $('dig-resolver'), out = $('dig-out'), cmd = $('dig-cmd'), form = $('dig-form');
  let type = 'A', controller = null, timer = null, last = null;

  const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };

  const open = (name, run, as) => {
    if (!dlg.open) dlg.showModal();
    if (name) nameInput.value = name;
    if (as) setType(as);
    nameInput.focus(); nameInput.select();
    if (run && nameInput.value.trim()) lookup();
  };
  const close = () => { if (dlg.open) dlg.close(); };

  const setType = t => {
    type = t;
    dlg.querySelectorAll('[data-dig-type]').forEach(b => { const on = b.getAttribute('data-dig-type') === t; b.classList.toggle('on', on); b.setAttribute('aria-pressed', on); });
  };

  const ttl = s => s >= 86400 ? Math.round(s / 86400) + 'd' : s >= 3600 ? Math.round(s / 3600) + 'h' : s >= 60 ? Math.round(s / 60) + 'm' : s + 's';

  const command = (name, t, resolver) => {
    const at = resolver !== 'all' && resolver !== 'system' ? ' @' + resolver : '';
    const ip = /^[0-9.]+$|:/.test(name);
    return ip ? `dig +noall +answer -x ${name}${at}` : `dig +noall +answer ${name} ${t}${at}`;
  };

  const recordTable = (records, caption) => {
    const wrap = el('div', 'dig-table-wrap');
    if (caption) wrap.append(el('div', 'dig-caption', caption));
    const table = el('table', 'dig-table');
    const head = el('tr');
    ['name', 'TTL', 'IN', 'type', 'data'].forEach((h, i) => head.append(el('th', i === 4 ? 'wide' : '', i === 0 ? L('name') : i === 3 ? L('type') : i === 4 ? L('data') : h)));
    const thead = el('thead');
    thead.append(head);
    table.append(thead);
    const body = el('tbody');
    records.forEach(r => {
      const row = el('tr');
      row.append(el('td', 'n', r.name), el('td', 'ttl', ttl(r.ttl)), el('td', 'cls', r.class), el('td', 't t-' + r.type.toLowerCase(), r.type), el('td', 'd', r.data));
      row.firstChild.title = r.name;
      row.children[1].title = r.ttl + 's';
      body.append(row);
    });
    table.append(body); wrap.append(table);
    return wrap;
  };

  const signature = r => r.answers.map(a => a.type + ' ' + a.data).sort().join('\n');

  const render = (data) => {
    out.replaceChildren();
    const results = data.results;
    const sigs = new Set(results.filter(r => !r.error).map(signature));
    const differ = results.length > 1 && sigs.size > 1;
    if (results.length > 1) {
      out.append(el('div', 'dig-verdict ' + (differ ? 'bad' : 'good'), (differ ? '⚠ ' : '✓ ') + L(differ ? 'disagree' : 'agree')));
    }
    const majority = (() => { const c = {}; results.filter(r => !r.error).forEach(r => { const s = signature(r); c[s] = (c[s] || 0) + 1; }); return Object.entries(c).sort((a, b) => b[1] - a[1])[0]?.[0]; })();
    results.forEach((r, i) => {
      const card = el('section', 'dig-card');
      card.style.animationDelay = (i * 60) + 'ms';
      const head = el('header');
      head.append(el('b', '', r.label));
      if (r.server) head.append(el('span', 'dig-server', r.server));
      const badges = el('span', 'dig-badges');
      badges.append(el('span', 'rc rc-' + r.status.toLowerCase(), r.status));
      if (r.authoritative) badges.append(el('span', 'fl', 'aa'));
      if (r.truncated) badges.append(el('span', 'fl', 'tc'));
      if (r.recursionAvailable) badges.append(el('span', 'fl', 'ra'));
      if (!r.error) badges.append(el('span', 'ms', r.ms + ' ms'));
      if (differ && !r.error && signature(r) !== majority) { card.classList.add('differs'); badges.append(el('span', 'rc rc-differs', L('differs'))); }
      head.append(badges);
      card.append(head);
      if (r.error) card.append(el('div', 'dig-error', L('failed-resolver') + ': ' + r.error));
      else if (r.answers.length) card.append(recordTable(r.answers));
      else {
        // No records is a normal answer. The SOA that resolvers add to it only proves the zone exists, so it is not shown.
        const none = r.status === 'NXDOMAIN' ? L('nxdomain').replace('{0}', data.name) : L('nodata').replace('{0}', data.type);
        card.append(el('div', 'dig-empty', none));
      }
      out.append(card);
    });
  };

  const lookup = async () => {
    const name = nameInput.value.trim();
    if (!name) { out.replaceChildren(el('div', 'dig-empty', L('empty'))); return; }
    controller?.abort();
    controller = new AbortController();
    const resolver = resolverSelect.value;
    out.replaceChildren(el('div', 'dig-loading', L('querying')));
    out.firstChild.prepend(el('span', 'spin'));
    try {
      const r = await fetch(`/api/dig?name=${encodeURIComponent(name)}&type=${type}&resolver=${encodeURIComponent(resolver)}`, { credentials: 'same-origin', signal: controller.signal });
      const data = await r.json().catch(() => ({}));
      if (!r.ok) { out.replaceChildren(el('div', 'dig-error', L('err-' + (data.error || 'failed')) )); return; }
      last = data;
      cmd.textContent = command(data.name, data.type, resolver);
      render(data);
    } catch (e) {
      if (e.name !== 'AbortError') out.replaceChildren(el('div', 'dig-error', L('err-failed')));
    }
  };

  const soon = () => { clearTimeout(timer); timer = setTimeout(lookup, 600); };

  document.querySelectorAll('[data-dig-open]').forEach(b => b.addEventListener('click', () => open()));
  document.addEventListener('click', e => {
    const link = e.target.closest?.('[data-dig-name]');
    if (link) { e.preventDefault(); open(link.getAttribute('data-dig-name'), true, link.getAttribute('data-dig-as')); }
  });
  dlg.querySelector('[data-dig-close]').addEventListener('click', close);
  dlg.addEventListener('click', e => { if (e.target === dlg) close(); });   // a click on the backdrop
  form.addEventListener('submit', e => { e.preventDefault(); clearTimeout(timer); lookup(); });
  nameInput.addEventListener('input', soon);
  resolverSelect.addEventListener('change', lookup);
  dlg.querySelectorAll('[data-dig-type]').forEach(b => b.addEventListener('click', () => { setType(b.getAttribute('data-dig-type')); if (nameInput.value.trim()) lookup(); }));
  dlg.querySelector('[data-dig-preset=dmarc]').addEventListener('click', () => {
    const base = nameInput.value.trim().replace(/^_dmarc\./i, '');
    if (!base) { nameInput.focus(); return; }
    nameInput.value = '_dmarc.' + base; setType('TXT'); lookup();
  });
  dlg.querySelector('[data-dig-copy]').addEventListener('click', async e => {
    const button = e.currentTarget, text = cmd.textContent;
    if (!text) return;
    try { await navigator.clipboard.writeText(text); const old = button.textContent; button.textContent = L('copied'); setTimeout(() => button.textContent = old, 1200); } catch { }
  });
  // "/" opens the window from anywhere except while typing.
  document.addEventListener('keydown', e => {
    if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey || dlg.open) return;
    const t = e.target;
    if (t instanceof HTMLElement && (t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName))) return;
    e.preventDefault(); open();
  });
})();
