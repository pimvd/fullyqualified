(() => {
  const form = document.getElementById('email-test-form');
  if (!form) return;
  form.addEventListener('submit', async event => {
    event.preventDefault();
    const button = form.querySelector('button[type=submit]');
    if (button.disabled || !form.reportValidity()) return;
    const note = document.getElementById('email-test-result');
    note.hidden = true;
    button.disabled = true;
    try {
      const response = await fetch('/admin/email-test', {
        method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(Object.fromEntries(new FormData(form)))
      });
      const result = await response.json().catch(() => null);
      note.className = response.ok ? '' : 'error';
      note.textContent = response.ok ? form.dataset.success
        : form.getAttribute('data-' + (response.status === 429 ? 'rate' : result?.error)) || form.dataset.failed;
    } catch {
      note.className = 'error';
      note.textContent = form.dataset.failed;
    } finally {
      note.hidden = false;
      button.disabled = false;
    }
  });
})();
