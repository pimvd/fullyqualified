// Diagnostic inputs and observations live only in this document and the current request.
(() => {
  'use strict';
  const root = document.getElementById('toolbox');
  if (!root) return;
  const byId = id => document.getElementById(id);
  const form = byId('toolbox-form');
  const tool = byId('toolbox-tool');
  const status = byId('toolbox-status');
  const output = byId('toolbox-results');
  const findings = byId('toolbox-findings');
  const runButton = byId('toolbox-run');
  const cancelButton = byId('toolbox-cancel');
  const progress = byId('toolbox-progress');
  const groups = [...form.querySelectorAll('[data-tools]')];
  const L = key => root.getAttribute('data-l-' + key) || '';
  const ports = { smtp: 25, imap: 143, pop3: 110, smtps: 465, imaps: 993, pop3s: 995 };
  const numericFields = new Set(['Port', 'Percentage']);
  const allowedFields = new Set(['Host', 'Port', 'ServerName', 'Mode', 'Domain', 'Selector', 'Text',
    'RecordType', 'SenderIp', 'EnvelopeFrom', 'Helo', 'Policy', 'SubdomainPolicy',
    'AlignmentSpf', 'AlignmentDkim', 'ReportUri', 'Percentage']);
  let controller = null;
  let generation = 0;
  let busy = false;

  const element = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  };
  const announce = (key, error = false) => {
    status.textContent = L(key);
    status.classList.toggle('error', error);
  };
  const updateGroups = () => {
    groups.forEach(group => {
      const visible = group.getAttribute('data-tools').split(' ').includes(tool.value);
      group.hidden = !visible;
      group.disabled = !visible || busy;
    });
  };
  const setBusy = value => {
    busy = value;
    form.setAttribute('aria-busy', String(value));
    runButton.disabled = value;
    cancelButton.hidden = !value;
    progress.hidden = !value;
    updateGroups();
  };
  const clearResults = () => {
    findings.replaceChildren();
    output.hidden = true;
  };
  const invalidate = () => {
    generation++;
    controller?.abort();
    controller = null;
    setBusy(false);
  };
  const clearSensitive = (message = 'cleared') => {
    invalidate();
    clearResults();
    [...form.elements].forEach(control => {
      if (control.type === 'hidden' || !('value' in control)) return;
      if (control.tagName === 'SELECT') control.selectedIndex = 0;
      else if (control.tagName === 'INPUT' && control.type === 'number') control.value = control.defaultValue;
      else if (control.tagName === 'INPUT' || control.tagName === 'TEXTAREA') control.value = '';
    });
    byId('toolbox-port').value = '443';
    updateGroups();
    announce(message);
  };
  const failValidation = (key, control) => {
    announce(key, true);
    control?.focus();
    return false;
  };
  const privateKey = text => /-----BEGIN[^\r\n-]*PRIVATE KEY-----/i.test(text);
  const validate = () => {
    if (!form.reportValidity()) return failValidation('invalid');
    const text = [...form.elements].find(control => control.name === 'Text' && !control.closest('fieldset[disabled]'));
    if (text && new TextEncoder().encode(text.value).byteLength > 64 * 1024) return failValidation('textlimit', text);
    if (tool.value === 'certificate') {
      const fileControl = byId('toolbox-file');
      const file = fileControl.files[0];
      if (file && file.size > 256 * 1024) return failValidation('filelimit', fileControl);
      if (Boolean(text.value.trim()) === Boolean(file)) return failValidation('certificaterequired', text);
      if (privateKey(text.value)) return failValidation('privatekey', text);
    }
    return true;
  };
  const readFile = (file, signal) => new Promise((resolve, reject) => {
    const reader = new FileReader();
    const abort = () => reader.abort();
    reader.onload = () => resolve(new Uint8Array(reader.result));
    reader.onerror = () => reject(new Error('file'));
    reader.onabort = () => reject(new DOMException('', 'AbortError'));
    reader.onloadend = () => signal.removeEventListener('abort', abort);
    signal.addEventListener('abort', abort, { once: true });
    if (signal.aborted) { reject(new DOMException('', 'AbortError')); return; }
    reader.readAsArrayBuffer(file);
  });
  const render = result => {
    clearResults();
    const sections = Array.isArray(result.sections) ? result.sections : [];
    sections.forEach(section => {
      const card = element('section', 'card toolbox-finding');
      card.append(element('h3', '', section.title));
      const list = element('dl', 'toolbox-observations');
      (Array.isArray(section.rows) ? section.rows : []).forEach(row => {
        const state = ['info', 'ok', 'warning', 'error', 'unknown'].includes(row.status) ? row.status : 'unknown';
        const label = element('dt', '', row.label);
        const value = element('dd');
        value.append(element('span', 'toolbox-badge toolbox-' + state, L('status' + state)),
          element('span', 'toolbox-value', row.value));
        if (row.explanation) value.append(element('p', 'toolbox-explanation', row.explanation));
        list.append(label, value);
      });
      card.append(list);
      findings.append(card);
    });
    if (!sections.length) findings.append(element('p', 'muted', L('noresults')));
    output.hidden = false;
    announce(result.complete === false ? 'partial' : 'done');
  };

  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (busy || !validate()) return;
    clearResults();
    const request = { Tool: tool.value };
    // Disabled fieldsets are excluded, so another tool's defaults cannot change this run.
    for (const [name, value] of new FormData(form)) {
      if (!allowedFields.has(name) || typeof value !== 'string' || value === '') continue;
      request[name] = numericFields.has(name) ? Number(value) : name === 'Text' ? value : value.trim();
    }
    if (request.Tool === 'dns') {
      request.Host = request.Domain;
      delete request.Domain;
    }
    const file = tool.value === 'certificate' ? byId('toolbox-file').files[0] : null;
    const requestId = ++generation;
    const abort = new AbortController();
    controller = abort;
    setBusy(true);
    announce('progress');
    try {
      if (file) {
        const bytes = await readFile(file, abort.signal);
        if (requestId !== generation) return;
        let binary = '';
        for (let offset = 0; offset < bytes.length; offset += 8192) binary += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
        if (privateKey(binary)) { announce('privatekey', true); return; }
        request.FileBase64 = btoa(binary);
      }
      const response = await fetch('/api/toolbox', {
        method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: abort.signal,
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': form.querySelector('input[name="__RequestVerificationToken"]').value },
        body: JSON.stringify(request)
      });
      const data = await response.json().catch(() => null);
      if (requestId !== generation) return;
      if (!response.ok) {
        const key = response.status === 401 ? 'signin' : response.status === 403 ? 'forbidden' : response.status === 429 ? 'rate' : 'failed';
        announce(key, true);
        if (![401, 403, 429].includes(response.status) && typeof data?.error === 'string') status.textContent = data.error;
        return;
      }
      if (!data || !Array.isArray(data.sections)) { announce('failed', true); return; }
      render(data);
    } catch (error) {
      if (requestId === generation && error.name !== 'AbortError') announce('failed', true);
    } finally {
      if (requestId === generation) {
        controller = null;
        setBusy(false);
      }
    }
  });
  form.addEventListener('reset', event => { event.preventDefault(); clearSensitive(); tool.focus(); });
  cancelButton.addEventListener('click', () => { invalidate(); clearResults(); announce('cancelled'); runButton.focus(); });
  tool.addEventListener('change', () => {
    invalidate();
    clearResults();
    if (tool.value === 'tls') byId('toolbox-port').value = '443';
    if (tool.value === 'starttls') byId('toolbox-port').value = String(ports[byId('toolbox-mode').value]);
    announce('ready');
  });
  byId('toolbox-mode').addEventListener('change', event => { byId('toolbox-port').value = String(ports[event.target.value]); });
  // Clear before departure and after bfcache restoration; generations suppress late completions.
  window.addEventListener('pagehide', () => clearSensitive('ready'));
  window.addEventListener('beforeunload', () => clearSensitive('ready'));
  window.addEventListener('pageshow', () => clearSensitive('ready'));
  document.addEventListener('click', event => {
    const anchor = event.target.closest?.('a[href]');
    if (anchor && anchor.getAttribute('href') && !anchor.getAttribute('href').startsWith('#') && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey && (!anchor.target || anchor.target === '_self')) clearSensitive('ready');
    if (event.target.closest?.('[data-refresh]')) clearSensitive('ready');
  }, true);
  clearSensitive('ready');
})();
