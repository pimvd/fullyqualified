// Passkey (WebAuthn) ceremonies for the login, setup, invite and account pages.
(() => {
  const dec = s => {
    s = s.replace(/-/g, '+').replace(/_/g, '/');
    s += '='.repeat((4 - (s.length % 4)) % 4);
    return Uint8Array.from(atob(s), c => c.charCodeAt(0)).buffer;
  };
  const enc = buf => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

  const root = document.querySelector('[data-passkey-root]');
  if (!root) return;
  const msg = code => root.getAttribute('data-msg-' + code) || root.getAttribute('data-msg-failed');
  const show = text => { const e = document.getElementById('pk-error'); e.textContent = text; e.hidden = !text; };
  const value = id => (document.getElementById(id)?.value ?? '').trim();

  async function send(method, url, body) {
    const r = await fetch(url, { method, headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin', body: body === undefined ? undefined : JSON.stringify(body) });
    let data = null;
    try { data = await r.json(); } catch { }
    if (!r.ok) throw { code: data?.error ?? 'failed' };
    return data;
  }

  async function create(optionsUrl, completeUrl, request) {
    const o = await send('POST', optionsUrl, request);
    o.challenge = dec(o.challenge);
    o.user.id = dec(o.user.id);
    (o.excludeCredentials ?? []).forEach(c => c.id = dec(c.id));
    const c = await navigator.credentials.create({ publicKey: o });
    return send('POST', completeUrl, {
      id: c.id, rawId: enc(c.rawId), type: c.type,
      response: { attestationObject: enc(c.response.attestationObject), clientDataJSON: enc(c.response.clientDataJSON), transports: c.response.getTransports?.() ?? [] },
      clientExtensionResults: c.getClientExtensionResults(),
    });
  }

  async function login() {
    const o = await send('POST', '/auth/login/options');
    o.challenge = dec(o.challenge);
    (o.allowCredentials ?? []).forEach(c => c.id = dec(c.id));
    const c = await navigator.credentials.get({ publicKey: o });
    return send('POST', '/auth/login/complete', {
      id: c.id, rawId: enc(c.rawId), type: c.type,
      response: {
        authenticatorData: enc(c.response.authenticatorData), clientDataJSON: enc(c.response.clientDataJSON),
        signature: enc(c.response.signature), userHandle: c.response.userHandle ? enc(c.response.userHandle) : null,
      },
      clientExtensionResults: c.getClientExtensionResults(),
    });
  }

  const target = () => {
    const back = root.getAttribute('data-return');
    return back && back.startsWith('/') && !back.startsWith('//') ? back : '/';
  };

  const actions = {
    login: async () => { await login(); location.assign(target()); },
    register: async () => {
      await create('/auth/register/options', '/auth/register/complete',
        { name: value('pk-name'), inviteToken: root.getAttribute('data-invite'), setupCode: value('pk-code') });
      location.assign('/');
    },
    add: async () => { await create('/auth/passkeys/options', '/auth/passkeys/complete', { label: value('pk-label') }); location.reload(); },
  };

  document.querySelectorAll('[data-passkey]').forEach(button => button.addEventListener('click', async () => {
    show('');
    if (!window.PublicKeyCredential) return show(msg('unsupported'));
    button.disabled = true;
    try { await actions[button.getAttribute('data-passkey')](); }
    catch (e) { show(e?.code ? msg(e.code) : e?.name === 'NotAllowedError' ? msg('cancelled') : e?.name === 'InvalidStateError' ? msg('already_registered') : msg('failed')); }
    finally { button.disabled = false; }
  }));

  document.querySelectorAll('[data-passkey-remove]').forEach(button => button.addEventListener('click', async () => {
    if (!confirm(button.getAttribute('data-confirm'))) return;
    show('');
    try { await send('DELETE', '/auth/passkeys/' + button.getAttribute('data-passkey-remove')); location.reload(); }
    catch (e) { show(msg(e?.code ?? 'failed')); }
  }));
})();
