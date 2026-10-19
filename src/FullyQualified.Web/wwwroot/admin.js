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

  root.querySelector('[data-admin=invite]')?.addEventListener('click', () => run(async () => {
    const panel = document.getElementById('adm-invite');
    const r = await send('POST', '/admin/invites', { email: document.getElementById('adm-email').value, role: document.getElementById('adm-role').value, tenantId: panel?.getAttribute('data-tenant') });
    const link = document.getElementById('adm-link'), note = document.getElementById('adm-note');
    link.value = r.url; link.hidden = false; link.select();
    note.textContent = r.emailed ? root.getAttribute('data-emailed') : root.getAttribute('data-copy');
    note.hidden = false;
    document.getElementById('adm-email').value = '';
  }));

  const form = document.getElementById('alerts-form');
  if (form) {
    const note = document.getElementById('al-note');
    const say = text => { note.textContent = text; note.hidden = !text; };
    const checked = id => !!document.getElementById(id)?.checked;
    const val = id => document.getElementById(id)?.value ?? '';
    root.querySelector('[data-admin=alerts-save]').addEventListener('click', () => run(async () => {
      say('');
      await send('PUT', '/admin/alerts/', {
        enabled: checked('al-enabled'), slackWebhook: val('al-slack'), webhookUrl: val('al-webhook'), emailTo: val('al-email'),
        minStatus: val('al-min'), notifyRecovery: checked('al-recovery'), notifyDrift: checked('al-drift'),
        clearSlack: checked('al-clear-slack'), clearWebhook: checked('al-clear-webhook'),
      });
      say(form.getAttribute('data-saved'));
      setTimeout(() => location.reload(), 600);
    }));
    root.querySelector('[data-admin=alerts-test]').addEventListener('click', () => run(async () => {
      say('');
      const results = await send('POST', '/admin/alerts/test');
      say(results.length === 0 ? form.getAttribute('data-test-none')
        : results.map(r => `${r.channel}: ${r.success ? form.getAttribute('data-test-ok') : form.getAttribute('data-test-failed') + (r.error ? ' (' + r.error + ')' : '')}`).join(' · '));
    }));
  }

  root.querySelector('[data-admin=tenant]')?.addEventListener('click', () => run(async () => {
    const r = await send('POST', '/platform/tenants', { name: document.getElementById('adm-tenant-name').value, adminEmail: document.getElementById('adm-email').value });
    const link = document.getElementById('adm-link'), note = document.getElementById('adm-note');
    link.value = r.url; link.hidden = false; link.select();
    note.textContent = r.emailed ? root.getAttribute('data-emailed') : root.getAttribute('data-copy');
    note.hidden = false;
  }));

  root.querySelectorAll('[data-user-reset]').forEach(button => button.addEventListener('click', () => run(async () => {
    const r = await send('POST', `/admin/users/${button.getAttribute('data-user-reset')}/reset-passkey`);
    let link = document.getElementById('adm-link'), note = document.getElementById('adm-note');
    if (!link) {
      link = Object.assign(document.createElement('input'), { id: 'adm-link', readOnly: true });
      link.style.width = '100%';
      note = Object.assign(document.createElement('p'), { id: 'adm-note', className: 'muted' });
      document.getElementById('adm-error').before(link, note);
    }
    link.hidden = false; link.value = r.url; link.select();
    note.textContent = r.emailed ? root.getAttribute('data-emailed') : root.getAttribute('data-recovery');
    note.hidden = false;
    link.scrollIntoView({ block: 'center' });
  })));

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
