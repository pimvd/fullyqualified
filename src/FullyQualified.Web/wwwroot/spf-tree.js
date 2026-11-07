// Builds the expandable SPF tree of an asset the first time its <details> is opened. All text comes from data-labels (the resource
// files); everything from the network is written with textContent, never as HTML.
(() => {
  document.querySelectorAll('details.spf-tree').forEach(details => {
    let labels = {};
    try { labels = JSON.parse(details.dataset.labels || '{}'); } catch { /* the page still works, with raw codes */ }
    const t = key => labels[key] ?? key;
    const body = details.querySelector('.spf-tree-body');
    let started = false;

    const el = (tag, className, text) => {
      const node = document.createElement(tag);
      if (className) node.className = className;
      if (text !== undefined) node.textContent = text;
      return node;
    };

    const label = node => {
      const row = el('span', 'spf-row');
      if (node.qualifier) {
        const q = el('span', 'spf-q q-' + ({ '+': 'pass', '-': 'fail', '~': 'softfail', '?': 'neutral' }[node.qualifier] ?? 'pass'), node.qualifier);
        q.title = t('q_' + node.qualifier);
        row.append(q);
      }
      row.append(el('span', 'spf-kind', t('k_' + node.kind)), ' ', el('code', null, node.text));
      if (node.note) row.append(' ', el('span', 'spf-note', '(' + t('n_' + node.note) + ')'));
      return row;
    };

    const build = node => {
      const item = el('li');
      if (node.children.length === 0) { item.append(label(node)); return item; }
      const group = el('details');
      group.open = node.kind === 'domain' || node.kind === 'include' || node.kind === 'redirect';
      const summary = el('summary');
      summary.append(label(node));
      group.append(summary);
      if (node.record) group.append(el('div', 'spf-record muted', node.record));
      const list = el('ul', 'spf-list');
      node.children.forEach(child => list.append(build(child)));
      group.append(list);
      item.append(group);
      return item;
    };

    const show = tree => {
      body.replaceChildren();
      const over = tree.lookups > 10;
      body.append(el('p', over ? 'error' : 'muted', t(over ? 'lookupsOver' : 'lookups').replace('{0}', tree.lookups)));
      if (tree.truncated) body.append(el('p', 'muted', t('truncated')));
      const root = el('ul', 'spf-list');
      root.append(build(tree.root));
      body.append(root);
    };

    details.addEventListener('toggle', async () => {
      if (!details.open || started) return;
      started = true;
      body.replaceChildren(el('p', 'muted', t('loading')));
      try {
        const response = await fetch(details.dataset.url, { credentials: 'same-origin', cache: 'no-store' });
        if (!response.ok) throw new Error(String(response.status));
        show(await response.json());
      } catch {
        started = false;                          // let the next opening try again
        body.replaceChildren(el('p', 'error', t('error')));
      }
    });
  });
})();
