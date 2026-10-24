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
        telegramBotToken: val('al-telegram'), telegramChatId: val('al-telegram-chat'), clearTelegram: checked('al-clear-telegram'),
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

  root.querySelector('[data-admin=diag]')?.addEventListener('click', event => run(async () => {
    const button = event.currentTarget, out = document.getElementById('diag-results'), summary = document.getElementById('diag-summary');
    button.disabled = true;
    try {
      const r = await send('POST', '/admin/diagnostics', { sendAlarm: document.getElementById('diag-alarm').checked });
      out.replaceChildren();
      const icons = { ok: '✓', info: 'ⓘ', warning: '⚠', fail: '✕' };
      let list = null, last = null;
      for (const i of r.items) {
        if (i.group !== last) { last = i.group; const h = document.createElement('h3'); h.textContent = i.group; out.append(h); list = document.createElement('ul'); list.className = 'diag'; out.append(list); }
        const li = document.createElement('li'); li.className = 'diag-item ' + i.status;
        const icon = document.createElement('span'); icon.className = 'diag-icon'; icon.setAttribute('aria-hidden', 'true'); icon.textContent = icons[i.status];
        const title = document.createElement('b'); title.textContent = i.title;
        const state = document.createElement('span'); state.className = 'diag-state'; state.textContent = ' · ' + i.label;
        const detail = document.createElement('div'); detail.className = 'muted'; detail.textContent = i.detail;
        const body = document.createElement('div'); body.append(title, state, detail);
        li.append(icon, body); list.append(li);
      }
      summary.textContent = r.summary; summary.hidden = false;
    } finally { button.disabled = false; }
  }));

  root.querySelector('[data-admin=tenant]')?.addEventListener('click', () => run(async () => {
    const r = await send('POST', '/platform/tenants', { name: document.getElementById('adm-tenant-name').value, adminEmail: document.getElementById('adm-email').value, tier: document.getElementById('adm-tier')?.value });
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

  root.querySelectorAll('[data-admin=tier-save]').forEach(button => button.addEventListener('click', () => run(async () => {
    const row = root.querySelector(`[data-tier-row="${button.getAttribute('data-tier')}"]`);
    const f = name => row.querySelector(`[data-f=${name}]`);
    await send('PUT', `/platform/tiers/${button.getAttribute('data-tier')}`, {
      maxDomains: f('domains').value, maxEndpoints: f('endpoints').value, maxUsers: f('users').value,
      reportsEnabled: f('reports').checked, retentionMonths: f('retention').value, scanIntervalMinutes: f('interval').value, manualCooldownMinutes: f('cooldown').value });
    const note = document.getElementById('tier-note');
    note.textContent = root.getAttribute('data-saved'); note.hidden = false;
  })));

  root.querySelector('[data-admin=plan-save]')?.addEventListener('click', () => run(async () => {
    const editor = document.getElementById('plan-editor'), v = id => document.getElementById(id).value;
    await send('PUT', `/platform/tenants/${editor.getAttribute('data-tenant')}/plan`, {
      tier: v('pl-tier'), maxDomains: v('pl-domains'), maxEndpoints: v('pl-endpoints'), maxUsers: v('pl-users'), reports: v('pl-reports'), retentionMonths: v('pl-retention'), scanIntervalMinutes: v('pl-interval'), manualCooldownMinutes: v('pl-cooldown') });
    const note = document.getElementById('pl-note');
    note.textContent = root.getAttribute('data-saved'); note.hidden = false;
    setTimeout(() => location.reload(), 600);
  }));

  root.querySelector('[data-admin=tenant-delete]')?.addEventListener('click', event => run(async () => {
    const button = event.currentTarget, zone = button.closest('[data-confirm-name]');
    if (!confirm(zone.getAttribute('data-confirm-name') + ' — ' + button.textContent.trim() + '?')) return;
    await send('POST', button.getAttribute('data-url'), { confirmName: document.getElementById('del-confirm').value });
    location.assign(button.getAttribute('data-after'));
  }));

  root.querySelectorAll('[data-user-role]').forEach(select => {
    const original = select.value;
    select.addEventListener('change', () => run(async () => {
      try { await send('PUT', `/admin/users/${select.getAttribute('data-user-role')}/role`, { role: select.value }); location.reload(); }
      catch (e) { select.value = original; throw e; }
    }));
  });

  root.querySelectorAll('[data-user-name]').forEach(input => {
    const original = input.value;
    input.addEventListener('change', () => run(async () => {
      try { await send('PUT', `/admin/users/${input.getAttribute('data-user-name')}/name`, { name: input.value }); location.reload(); }
      catch (e) { input.value = original; throw e; }
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
