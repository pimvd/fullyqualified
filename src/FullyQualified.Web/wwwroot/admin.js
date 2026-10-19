// Invitations and people management on the Users page.
(() => {
  const root = document.querySelector('[data-admin-root]');
  if (!root) return;
  const msg = code => root.getAttribute('data-msg-' + code) || root.getAttribute('data-msg-failed');
  const show = text => { const e = document.getElementById('adm-error'); e.textContent = text; e.hidden = !text; };

  async function send(method, url, body) {
    const r = await fetch(url, { method, headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin', body: body === undefined ? undefined : JSON.stringify(body) });
    let data = null;
    try { data = await r.json(); } catch { }
    if (!r.ok) throw { code: data?.error ?? 'failed' };
    return data;
  }

  const run = async action => { show(''); try { await action(); } catch (e) { show(msg(e?.code ?? 'failed')); } };

  root.querySelector('[data-admin=invite]').addEventListener('click', () => run(async () => {
    const r = await send('POST', '/admin/invites', { email: document.getElementById('adm-email').value, role: document.getElementById('adm-role').value });
    const link = document.getElementById('adm-link'), note = document.getElementById('adm-note');
    link.value = r.url; link.hidden = false; link.select();
    note.textContent = r.emailed ? root.getAttribute('data-emailed') : root.getAttribute('data-copy');
    note.hidden = false;
    document.getElementById('adm-email').value = '';
  }));

  root.querySelectorAll('[data-user-role]').forEach(select => {
    const original = select.value;
    select.addEventListener('change', () => run(async () => {
      try { await send('PUT', `/admin/users/${select.getAttribute('data-user-role')}/role`, { role: select.value }); location.reload(); }
      catch (e) { select.value = original; throw e; }
    }));
  });

  root.querySelectorAll('[data-user-remove]').forEach(button => button.addEventListener('click', () => run(async () => {
    if (!confirm(root.getAttribute('data-confirm-user'))) return;
    await send('DELETE', `/admin/users/${button.getAttribute('data-user-remove')}`); location.reload();
  })));

  root.querySelectorAll('[data-invite-revoke]').forEach(button => button.addEventListener('click', () => run(async () => {
    if (!confirm(root.getAttribute('data-confirm-invite'))) return;
    await send('DELETE', `/admin/invites/${button.getAttribute('data-invite-revoke')}`); location.reload();
  })));
})();
