(() => {
  const form = document.getElementById('email-test-form');
  if (!form) return;
  const sendButton = form.querySelector('button[type=submit]');
  const unavailable = sendButton.disabled;
  let generation = 0;
  let controller;
  const clear = () => { generation++; controller?.abort(); sendButton.disabled = unavailable; form.reset(); form.querySelectorAll('input:not([type=hidden]), textarea').forEach(field => { field.value = ''; }); const note = document.getElementById('email-test-result'); note.textContent = ''; note.hidden = true; };
  window.addEventListener('pagehide', clear);
  window.addEventListener('pageshow', event => { if (event.persisted) clear(); });
  form.setAttribute('autocomplete', 'off');
  // The page asks for the part before the @ and a domain; the server still gets, and checks, one address.
  const payload = () => {
    const data = Object.fromEntries(new FormData(form));
    data.from = `${(data.fromLocal ?? '').trim()}@${data.fromDomain ?? ''}`;
    delete data.fromLocal; delete data.fromDomain;
    return data;
  };
  form.addEventListener('submit', async event => {
    event.preventDefault();
    const button = form.querySelector('button[type=submit]');
    if (button.disabled || !form.reportValidity()) return;
    const note = document.getElementById('email-test-result');
    note.hidden = true;
    button.disabled = true;
    const ticket = generation;
    controller = new AbortController();
    try {
      const response = await fetch('/admin/email-test', {
        method: 'POST', signal: controller.signal, credentials: 'same-origin', cache: 'no-store', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': form.querySelector('input[name=__RequestVerificationToken]').value },
        body: JSON.stringify(payload())
      });
      const result = await response.json().catch(() => null);
      if (ticket !== generation) return;
      note.className = response.ok ? '' : 'error';
      note.textContent = response.ok ? form.dataset.success
        : form.getAttribute('data-' + (response.status === 429 ? 'rate' : result?.error)) || form.dataset.failed;
    } catch {
      if (ticket !== generation) return;
      note.className = 'error';
      note.textContent = form.dataset.failed;
    } finally {
      if (ticket === generation) { note.hidden = false; button.disabled = false; }
    }
  });
})();
