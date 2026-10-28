const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
const model = { items: [], settings: {}, results: [], selectedResultIndexes: new Set(), view: 'downloads', filter: 'all', search: '', lastBytes: new Map(), speeds: new Map(), sortNewest: true, selectedDownloads: new Set(), refreshTargetId: null, contextDownloadId: null, deleteTargetId: null, openActionMenuId: null };

function escapeHtml(value) { return String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch])); }
function formatBytes(value) {
  if (!Number.isFinite(value) || value < 0) return '—';
  if (value < 1000) return `${value} B`;
  const units = ['KB', 'MB', 'GB', 'TB']; let n = value / 1024, i = 0;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return `${n.toFixed(n >= 100 ? 0 : n >= 10 ? 1 : 2)} ${units[i]}`;
}
function shortName(value = '') { return value.length > 32 ? `${value.slice(0, 17)}…${value.slice(-12)}` : value; }
function statusText(value) { return ({ downloading: 'Downloading', queued: 'Queued', paused: 'Paused', finished: 'Completed', error: 'Failed' })[value] || value; }
function statusClass(value) { return ({ downloading: 'status-active', queued: 'status-queued', finished: 'status-finished', paused: 'status-paused', error: 'status-error' })[value] || 'status-queued'; }
function progress(item) { return item.size > 0 ? Math.min(100, item.bytes / item.size * 100) : item.status === 'finished' ? 100 : 0; }
function prettyPercent(item) { return item.size > 0 ? `${Math.floor(progress(item))}%` : item.status === 'finished' ? '100%' : '—'; }
function formatEta(seconds) { if (!Number.isFinite(seconds) || seconds < 0) return ''; if (seconds < 60) return `${Math.ceil(seconds)}s left`; if (seconds < 3600) return `${Math.ceil(seconds / 60)}m left`; return `${Math.floor(seconds / 3600)}h ${Math.ceil(seconds % 3600 / 60)}m left`; }
function toast(message, error = false) {
  const box = document.createElement('div'); box.className = `toast${error ? ' error' : ''}`; box.textContent = message; $('#toast-region').append(box); setTimeout(() => box.remove(), 3600);
}
async function api(url, options = {}) {
  const response = await fetch(url, { ...options, headers: { ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...(options.headers || {}) } });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || `Request failed (${response.status}).`);
  return data;
}
const post = (url, body = {}) => api(url, { method: 'POST', body: JSON.stringify(body) });

function showView(view) {
  model.view = view;
  $$('.view-panel').forEach(panel => panel.classList.toggle('active', panel.id === `view-${view}`));
  $$('.nav-item[data-view]').forEach(button => button.classList.toggle('active', button.dataset.view === view));
  $('#view-crumb').textContent = ({ downloads: 'Downloads', grabber: 'LinkGrabber', settings: 'Settings' })[view];
  if (view === 'settings') populateSettings();
}
function populateSettings() {
  const form = $('#settings-form');
  for (const key of ['folder', 'concurrency', 'perHost', 'connectionsPerFile', 'speedLimitKiB']) if (form.elements[key]) form.elements[key].value = model.settings[key] ?? '';
}
function renderStats() {
  const active = model.items.filter(item => item.status === 'downloading');
  const queued = model.items.filter(item => item.status === 'queued' || item.status === 'paused');
  const finished = model.items.filter(item => item.status === 'finished');
  const speed = active.reduce((sum, item) => sum + (model.speeds.get(item.id) || 0), 0);
  $('#stat-active').innerHTML = `${active.length} <span>downloads</span>`;
  $('#stat-speed').innerHTML = `${speed < 1024 ? speed.toFixed(0) : (speed / 1024).toFixed(speed < 10240 ? 1 : 0)} <span>${speed < 1024 ? 'B/s' : 'KB/s'}</span>`;
  $('#stat-queued').innerHTML = `${queued.length} <span>files</span>`;
  $('#stat-completed').innerHTML = `${finished.length} <span>files</span>`;
  $('#stat-active-foot').textContent = active.length ? 'Transfers in progress' : 'All caught up';
  $('#stat-remaining').textContent = queued.length ? 'Ready when you are' : 'Waiting to start';
  $('#download-count').textContent = model.items.length;
  $('#filter-all-count').textContent = model.items.length;
  $('#grabber-count').textContent = model.results.length;
  $('#connection-label').textContent = active.length ? `${active.length} transfer${active.length === 1 ? '' : 's'} active` : 'Ready to download';
}
function updateSnailModeButton() {
  const cap = Number(model.settings.snailModeKiB || 0);
  const button = $('#snail-mode-button');
  button.textContent = `🐌 Snail: ${cap ? `${cap} KB/s` : 'Off'}`;
  button.classList.toggle('active', cap > 0);
  button.setAttribute('aria-pressed', String(cap > 0));
}
function actionFor(item) {
  if (item.status === 'downloading' || item.status === 'queued') return '<button class="row-menu-action" data-action="pause">Pause</button>';
  if (item.status === 'paused') return '<button class="row-menu-action" data-action="resume">Resume</button>';
  if (item.status === 'error') return '<button class="row-menu-action" data-action="retry">Retry</button>';
  return '';
}
function visibleDownloads() {
  return [...model.items].filter(item => {
    const byStatus = model.filter === 'all' || item.status === model.filter;
    const q = model.search.toLowerCase();
    return byStatus && (!q || `${item.filename} ${item.host} ${item.url}`.toLowerCase().includes(q));
  }).sort((a,b) => model.sortNewest ? String(b.addedAt).localeCompare(String(a.addedAt)) : String(a.filename).localeCompare(String(b.filename)));
}
function updateDownloadSelection() {
  const ids = new Set(model.items.map(item => item.id));
  for (const id of model.selectedDownloads) if (!ids.has(id)) model.selectedDownloads.delete(id);
  const visible = visibleDownloads();
  const selectedVisible = visible.filter(item => model.selectedDownloads.has(item.id)).length;
  const selectAll = $('#select-all-downloads');
  selectAll.checked = visible.length > 0 && selectedVisible === visible.length;
  selectAll.indeterminate = selectedVisible > 0 && selectedVisible < visible.length;
  $('#download-bulk-actions').hidden = model.selectedDownloads.size === 0;
  $('#selected-download-count').textContent = `${model.selectedDownloads.size} selected`;
  $('#delete-selected-files').disabled = !model.items.some(item => model.selectedDownloads.has(item.id) && item.filePath);
}
function renderDownloads() {
  const items = visibleDownloads();
  if (!items.some(item => item.id === model.openActionMenuId)) model.openActionMenuId = null;
  $('#empty-downloads').style.display = items.length ? 'none' : 'flex';
  $('#download-list').innerHTML = items.map(item => {
    const pct = progress(item), speed = model.speeds.get(item.id) || 0;
    const detail = item.status === 'finished' ? 'Complete' : item.status === 'downloading' ? (speed ? `${formatBytes(speed)}/s${item.size ? ` · ${formatEta((item.size - item.bytes) / speed)}` : ''}` : 'Connecting…') : item.status === 'error' ? escapeHtml(item.error || 'Could not download') : item.status === 'paused' ? 'Paused' : 'Ready to start';
    return `<article class="download-row" data-id="${escapeHtml(item.id)}">
      <div class="download-select-cell"><input type="checkbox" class="download-select" data-id="${escapeHtml(item.id)}" ${model.selectedDownloads.has(item.id) ? 'checked' : ''} aria-label="Select ${escapeHtml(item.filename)}"></div>
      <div class="file-info"><div class="file-icon">↓</div><div class="file-text"><div class="file-name" title="${escapeHtml(item.filename)}">${escapeHtml(item.filename)}</div><div class="file-url" title="${escapeHtml(item.url)}">${escapeHtml(item.filePath || item.url)}</div></div></div>
      <div class="size-cell">${formatBytes(item.size)}</div><div><span class="host-pill" title="${escapeHtml(item.host)}">${escapeHtml(shortName(item.host))}</span></div>
      <div class="status-label"><i class="status-dot ${statusClass(item.status)}"></i><span>${statusText(item.status)}</span></div>
      <div class="progress-cell"><div class="progress-top"><span title="${escapeHtml(item.error || '')}">${detail}</span><span>${prettyPercent(item)}</span></div><div class="progress-track"><div class="progress-bar" style="width:${pct}%"></div></div></div>
      <div class="row-action-menu"><button class="row-action" title="Download actions" aria-label="Download actions">···</button><div class="row-menu" ${model.openActionMenuId === item.id ? '' : 'hidden'}>${actionFor(item)}<button class="row-menu-action" data-action="refresh-link">Refresh link and resume…</button><button class="row-menu-action" data-action="remove">Remove from list</button><button class="row-menu-action danger" data-action="delete">Delete download and file…</button></div></div>
    </article>`;
  }).join('');
  $('#list-summary').textContent = `${model.items.length} download${model.items.length === 1 ? '' : 's'}`;
  $('#clear-finished').style.visibility = model.items.some(item => item.status === 'finished') ? 'visible' : 'hidden';
  updateDownloadSelection();
}
function renderResults() {
  $('#result-count').textContent = model.results.length;
  $('#empty-results').style.display = model.results.length ? 'none' : 'flex';
  $('#result-list').innerHTML = model.results.map((item, index) => `<article class="grabber-result" data-index="${index}">
    <div class="check-cell"><input type="checkbox" class="result-select" ${model.selectedResultIndexes.has(index) && item.ok && !item.added ? 'checked' : ''} ${!item.ok || item.added ? 'disabled' : ''} aria-label="Select link"></div>
    <div><div class="result-filename" title="${escapeHtml(item.filename || item.url)}">${escapeHtml(item.filename || item.url)}</div><div class="result-url" title="${escapeHtml(item.url)}">${escapeHtml(item.error || item.url)}</div></div>
    <div><span class="host-pill">${escapeHtml(item.host || 'Unknown host')}</span></div><div class="size-cell">${item.size ? formatBytes(item.size) : 'Unknown'}</div>
    <div class="resume-chip">${item.acceptsRanges ? 'Supported' : 'Unknown'}</div><div class="result-status ${item.ok ? '' : 'offline'}">${item.added ? 'Queued' : item.ok ? 'Online' : 'Unavailable'}</div>
  </article>`).join('');
  updateSelectedCount();
}
function updateSelectedCount() {
  const selected = model.selectedResultIndexes.size;
  $('#queue-selected').disabled = selected === 0;
  $('#queue-selected').textContent = selected ? `＋ Add ${selected} to queue` : '＋ Add selected to queue';
  const eligible = model.results.map((item, index) => ({ item, index })).filter(({ item }) => item.ok && !item.added);
  const selectedEligible = eligible.filter(({ index }) => model.selectedResultIndexes.has(index)).length;
  const selectAll = $('#select-all-results');
  selectAll.checked = eligible.length > 0 && selectedEligible === eligible.length;
  selectAll.indeterminate = selectedEligible > 0 && selectedEligible < eligible.length;
}
function render() { renderStats(); renderDownloads(); renderResults(); updateSnailModeButton(); }
async function refresh() {
  try {
    const next = await api('/api/state'), now = Date.now();
    for (const item of next.items) {
      const old = model.lastBytes.get(item.id);
      model.speeds.set(item.id, old ? Math.max(0, (item.bytes - old.bytes) / Math.max(.1, (now - old.time) / 1000)) : 0);
      model.lastBytes.set(item.id, { bytes: item.bytes, time: now });
    }
    model.items = next.items; model.settings = next.settings; render();
  } catch { $('#connection-label').textContent = 'Server unavailable'; }
}
function selectedResults() { return [...model.selectedResultIndexes].map(index => model.results[index]).filter(item => item && item.ok && !item.added); }

$$('.nav-item[data-view]').forEach(button => button.addEventListener('click', () => showView(button.dataset.view)));
$$('[data-open-grabber]').forEach(button => button.addEventListener('click', () => showView('grabber')));
$$('.filter-tab').forEach(button => button.addEventListener('click', () => {
  model.filter = button.dataset.filter;
  $$('.filter-tab').forEach(tab => tab.classList.toggle('selected', tab === button)); renderDownloads();
}));
$('#search-input').addEventListener('input', event => { model.search = event.target.value; renderDownloads(); });
$('#sort-button').addEventListener('click', () => { model.sortNewest = !model.sortNewest; renderDownloads(); toast(model.sortNewest ? 'Sorted by recent' : 'Sorted by file name'); });
$('#refresh-button').addEventListener('click', refresh);
$('#snail-mode-button').addEventListener('click', async () => {
  const current = Number(model.settings.snailModeKiB || 0);
  const snailModeKiB = current === 0 ? 10 : current === 10 ? 50 : 0;
  try {
    const data = await api('/api/settings', { method: 'PUT', body: JSON.stringify({ snailModeKiB }) });
    model.settings = data.settings;
    updateSnailModeButton();
    toast(snailModeKiB ? `Snail mode caps combined downloads at ${snailModeKiB} KB/s.` : 'Snail mode off. Your regular speed limit applies.');
  } catch (error) { toast(error.message, true); }
});
$('#start-all-button').addEventListener('click', async () => { try { await post('/api/start'); await refresh(); toast('Queue started.'); } catch (error) { toast(error.message, true); } });
$('#pause-all-button').addEventListener('click', async () => { try { await post('/api/pause-all'); await refresh(); toast('Queue paused.'); } catch (error) { toast(error.message, true); } });
$('#clear-finished').addEventListener('click', async () => { try { await post('/api/clear-finished'); await refresh(); toast('Completed downloads cleared from the list. Files were kept.'); } catch (error) { toast(error.message, true); } });
$('#select-all-downloads').addEventListener('change', event => {
  for (const item of visibleDownloads()) {
    if (event.target.checked) model.selectedDownloads.add(item.id);
    else model.selectedDownloads.delete(item.id);
  }
  renderDownloads();
});
$('#download-list').addEventListener('change', event => {
  if (!event.target.matches('.download-select')) return;
  if (event.target.checked) model.selectedDownloads.add(event.target.dataset.id);
  else model.selectedDownloads.delete(event.target.dataset.id);
  updateDownloadSelection();
});
async function removeSelectedDownloads(deleteFiles) {
  const ids = [...model.selectedDownloads];
  if (!ids.length) return;
  try {
    const result = await api('/api/downloads/remove-selected', { method: 'POST', body: JSON.stringify({ ids, deleteFiles }) });
    model.selectedDownloads.clear();
    await refresh();
    toast(deleteFiles ? `Removed ${result.removed} downloads and deleted ${result.deletedFiles} local file(s).` : `Removed ${result.removed} downloads from the list. Files were kept.`);
  } catch (error) { toast(error.message, true); }
}
$('#remove-selected-downloads').addEventListener('click', () => removeSelectedDownloads(false));
$('#delete-selected-files').addEventListener('click', () => {
  const count = model.selectedDownloads.size;
  $('#delete-files-message').textContent = `${count} selected download${count === 1 ? '' : 's'}. Any downloaded or partial files for these items will be permanently deleted, and their queue entries will be removed. This can’t be undone.`;
  $('#delete-files-dialog').showModal();
});
$('#cancel-delete-files').addEventListener('click', () => { model.deleteTargetId = null; $('#delete-files-dialog').close(); });
$('#confirm-delete-files').addEventListener('click', async event => {
  event.currentTarget.disabled = true;
  if (model.deleteTargetId) {
    const id = model.deleteTargetId;
    try {
      const result = await api('/api/downloads/remove-selected', { method: 'POST', body: JSON.stringify({ ids: [id], deleteFiles: true }) });
      await refresh();
      toast(`Deleted the download and ${result.deletedFiles} local file(s).`);
    } catch (error) { toast(error.message, true); }
    model.deleteTargetId = null;
  } else await removeSelectedDownloads(true);
  $('#delete-files-dialog').close();
  event.currentTarget.disabled = false;
});
$('#links-input').addEventListener('input', event => { const count = event.target.value.split(/\r?\n/).filter(line => line.trim()).length; $('#link-lines').textContent = `${count} link${count === 1 ? '' : 's'} · Maximum 80`; });
$('#clear-links').addEventListener('click', () => { $('#links-input').value = ''; $('#link-lines').textContent = '0 links · Maximum 80'; model.results = []; model.selectedResultIndexes.clear(); renderResults(); });
$('#check-links').addEventListener('click', async () => {
  const urls = [...new Set($('#links-input').value.split(/\r?\n/).map(line => line.trim()).filter(Boolean))];
  if (!urls.length) return toast('Paste one or more direct links first.', true);
  if (urls.length > 80) return toast('Check up to 80 links at a time.', true);
  $('#check-links').disabled = true; $('#check-links').innerHTML = '<span>◌</span> Checking…';
  try { const data = await post('/api/inspect', { urls }); model.results = data.items; model.selectedResultIndexes = new Set(data.items.map((item, index) => item.ok && !item.added ? index : -1).filter(index => index >= 0)); renderResults(); toast(`Checked ${data.items.length} link${data.items.length === 1 ? '' : 's'}.`); }
  catch (error) { toast(error.message, true); }
  finally { $('#check-links').disabled = false; $('#check-links').innerHTML = '<span>⌕</span> Check links'; }
});
$('#select-all-results').addEventListener('change', event => {
  for (const [index, item] of model.results.entries()) {
    if (!item.ok || item.added) continue;
    if (event.target.checked) model.selectedResultIndexes.add(index);
    else model.selectedResultIndexes.delete(index);
  }
  renderResults();
});
$('#result-list').addEventListener('change', event => {
  if (!event.target.matches('.result-select')) return;
  const index = Number(event.target.closest('.grabber-result').dataset.index);
  if (event.target.checked) model.selectedResultIndexes.add(index);
  else model.selectedResultIndexes.delete(index);
  updateSelectedCount();
});
$('#queue-selected').addEventListener('click', async () => {
  const selected = selectedResults();
  try {
    await api('/api/downloads', { method: 'POST', body: JSON.stringify({ items: selected.map(item => ({ url: item.url, filename: item.filename, size: item.size, acceptsRanges: item.acceptsRanges })) }) });
    for (const item of selected) item.added = true;
    model.selectedResultIndexes.clear();
    renderResults(); await refresh(); toast(`${selected.length} link${selected.length === 1 ? '' : 's'} added to your queue.`); showView('downloads');
  } catch (error) { toast(error.message, true); }
});
$('#download-list').addEventListener('click', async event => {
  const row = event.target.closest('.download-row'); if (!row) return;
  const menu = row.querySelector('.row-menu');
  if (event.target.closest('.row-action')) {
    model.openActionMenuId = model.openActionMenuId === row.dataset.id ? null : row.dataset.id;
    $$('.row-menu').forEach(other => { other.hidden = !other.closest('.download-row') || other.closest('.download-row').dataset.id !== model.openActionMenuId; });
    return;
  }
  const action = event.target.closest('[data-action]')?.dataset.action;
  if (!action) { model.openActionMenuId = null; $$('.row-menu').forEach(other => other.hidden = true); return; }
  model.openActionMenuId = null;
  menu.hidden = true;
  runDownloadAction(row.dataset.id, action);
});
$('#download-list').addEventListener('contextmenu', event => {
  const row = event.target.closest('.download-row');
  if (!row) return;
  event.preventDefault();
  model.contextDownloadId = row.dataset.id;
  const menu = $('#download-context-menu');
  const item = model.items.find(entry => entry.id === model.contextDownloadId);
  if (!item) return;
  const canPause = ['downloading', 'queued'].includes(item.status);
  const canResume = ['paused', 'error'].includes(item.status);
  $('#download-context-actions').innerHTML = `
    <button type="button" data-action="pause" ${canPause ? '' : 'disabled'}><span>Ⅱ</span> Pause</button>
    <button type="button" data-action="resume" ${canResume ? '' : 'disabled'}><span>▶</span> Resume</button>
    <button type="button" data-action="refresh-link"><span>↻</span> Refresh link and resume…</button>
    <button type="button" data-action="remove"><span>−</span> Remove from list</button>
    <button type="button" data-action="delete" class="danger"><span>×</span> Delete download and file…</button>`;
  menu.hidden = false;
  const left = Math.min(event.clientX, window.innerWidth - menu.offsetWidth - 8);
  const top = Math.min(event.clientY, window.innerHeight - menu.offsetHeight - 8);
  menu.style.left = `${Math.max(8, left)}px`;
  menu.style.top = `${Math.max(8, top)}px`;
});
async function runDownloadAction(id, action) {
  if (action === 'refresh-link') { openRefreshLinkDialog(id); return; }
  if (action === 'delete') {
    const item = model.items.find(entry => entry.id === id);
    if (!item) return;
    model.deleteTargetId = id;
    $('#delete-files-message').textContent = `“${item.filename}” and its partial file, if present, will be permanently deleted from this device and removed from the queue. This can’t be undone.`;
    $('#delete-files-dialog').showModal();
    return;
  }
  try {
    await post(`/api/downloads/${encodeURIComponent(id)}/${action}`);
    await refresh();
    if (action === 'remove') toast('Removed from the list. Downloaded files were kept.');
    else if (action === 'pause') toast('Download paused.');
    else if (action === 'resume') toast('Download resumed.');
  } catch (error) { toast(error.message, true); }
}
$('#download-context-menu').addEventListener('click', event => {
  const button = event.target.closest('[data-action]');
  if (!button || button.disabled || !model.contextDownloadId) return;
  $('#download-context-menu').hidden = true;
  runDownloadAction(model.contextDownloadId, button.dataset.action);
});
document.addEventListener('pointerdown', event => {
  if (!event.target.closest('#download-context-menu')) $('#download-context-menu').hidden = true;
});
function openRefreshLinkDialog(id) {
  const item = model.items.find(entry => entry.id === id);
  if (!item) return;
  model.refreshTargetId = id;
  const savedBytes = item.bytes || 0;
  $('#refresh-link-details').textContent = `${item.filename} · ${formatBytes(savedBytes)} saved. Paste a fresh direct link for this same file.`;
  $('#refresh-link-message').textContent = 'Fetchflow verifies the server can resume at this byte offset and checks the file identity before continuing.';
  $('#refresh-link-message').classList.remove('error');
  $('#refresh-link-input').value = '';
  $('#refresh-link-dialog').showModal();
  $('#refresh-link-input').focus();
}
$('#cancel-refresh-link').addEventListener('click', () => $('#refresh-link-dialog').close());
$('#confirm-refresh-link').addEventListener('click', async event => {
  const id = model.refreshTargetId;
  const url = $('#refresh-link-input').value.trim();
  if (!id || !url) { $('#refresh-link-message').textContent = 'Paste a fresh direct link first.'; $('#refresh-link-message').classList.add('error'); return; }
  const button = event.currentTarget;
  button.disabled = true;
  button.textContent = 'Checking link…';
  $('#refresh-link-message').textContent = 'Checking whether the server can resume this file…';
  $('#refresh-link-message').classList.remove('error');
  try {
    const result = await api(`/api/downloads/${encodeURIComponent(id)}/refresh-link`, { method: 'POST', body: JSON.stringify({ url }) });
    $('#refresh-link-dialog').close();
    await refresh();
    toast(`Link refreshed. Resuming from ${formatBytes(result.resumedAt)}.`);
  } catch (error) {
    $('#refresh-link-message').textContent = error.message;
    $('#refresh-link-message').classList.add('error');
  } finally {
    button.disabled = false;
    button.textContent = 'Verify and resume';
  }
});
$('#settings-form').addEventListener('submit', async event => {
  event.preventDefault(); const form = new FormData(event.currentTarget);
  const settings = { folder: form.get('folder'), concurrency: Number(form.get('concurrency')), perHost: Number(form.get('perHost')), connectionsPerFile: Number(form.get('connectionsPerFile')), speedLimitKiB: Number(form.get('speedLimitKiB')) };
  try { const data = await api('/api/settings', { method: 'PUT', body: JSON.stringify(settings) }); model.settings = data.settings; populateSettings(); $('#settings-message').textContent = 'Preferences saved on this device.'; toast('Preferences saved.'); }
  catch (error) { $('#settings-message').textContent = error.message; toast(error.message, true); }
});
$$('.settings-nav-item').forEach(link => link.addEventListener('click', () => { $$('.settings-nav-item').forEach(item => item.classList.toggle('selected', item === link)); }));
refresh(); setInterval(refresh, 1000);
