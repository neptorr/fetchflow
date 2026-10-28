import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { promises as fsp } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { Readable } from 'node:stream';
import { spawn } from 'node:child_process';

const HOST = '127.0.0.1';
const PORT = Number(process.env.PORT || 4173);
const APP_DIR = path.join(process.env.LOCALAPPDATA || os.homedir(), 'Fetchflow');
const DATA_FILE = path.join(APP_DIR, 'state.json');
const WEB_DIR = path.join(import.meta.dirname, 'public');
const MAX_BODY = 1_000_000;
const state = { settings: { folder: path.join(os.homedir(), 'Downloads'), concurrency: 3, perHost: 2, connectionsPerFile: 4, speedLimitKiB: 0, snailModeKiB: 0 }, items: [] };
const running = new Map();
let saveTimer;
let saveQueue = Promise.resolve();
let shuttingDown = false;

await fsp.mkdir(APP_DIR, { recursive: true });
await fsp.mkdir(state.settings.folder, { recursive: true }).catch(() => {});
try {
  const saved = JSON.parse(await fsp.readFile(DATA_FILE, 'utf8'));
  if (saved.settings && typeof saved.settings === 'object') Object.assign(state.settings, saved.settings);
  if (Array.isArray(saved.items)) state.items = saved.items;
  for (const item of state.items) {
    if (item.status === 'downloading') item.status = 'paused';
    item.error = item.status === 'paused' ? 'Paused when Fetchflow closed.' : (item.error || '');
  }
} catch {}

function persistState() {
  const snapshot = JSON.stringify({ settings: state.settings, items: state.items }, null, 2);
  const write = saveQueue.then(async () => {
    const temp = `${DATA_FILE}.tmp`;
    await fsp.writeFile(temp, snapshot);
    await fsp.rename(temp, DATA_FILE);
  });
  saveQueue = write.catch(error => console.error('Could not save Fetchflow state:', error));
  return write;
}

function scheduleSave() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => { persistState().catch(() => {}); }, 180);
}

async function persistNow() {
  clearTimeout(saveTimer);
  await persistState();
}

function safeUrl(value) {
  let url;
  try { url = new URL(String(value).trim()); } catch { throw new Error('Enter a complete URL.'); }
  if (!['http:', 'https:'].includes(url.protocol)) throw new Error('Only HTTP and HTTPS links are supported.');
  if (url.username || url.password) throw new Error('Credentials in URLs are not accepted.');
  return url;
}

function dispositionFilename(headers = {}) {
  const disposition = headers.get?.('content-disposition') || '';
  const extended = disposition.match(/filename\*\s*=\s*UTF-8''([^;]+)/i)?.[1];
  const ordinary = disposition.match(/filename\s*=\s*(?:"([^"]+)"|([^;]+))/i);
  const raw = extended ? extended.replace(/^"|"$/g, '') : ordinary ? (ordinary[1] || ordinary[2]).trim() : '';
  if (!raw) return '';
  try { return decodeURIComponent(raw); } catch { return raw; }
}

function filenameFrom(url, headers = {}) {
  let name = dispositionFilename(headers);
  if (!name) {
    const queryName = url.searchParams.get('filename') || url.searchParams.get('file') || url.searchParams.get('name');
    const pathName = url.pathname.split('/').filter(Boolean).at(-1) || '';
    const candidate = queryName || pathName || 'download';
    try { name = decodeURIComponent(candidate); } catch { name = candidate; }
  }
  name = path.basename(name.replace(/[\\/\0]/g, '_')).replace(/[<>:"|?*\x00-\x1F]/g, '_').trim();
  return name || 'download';
}

function needsFilenameProbe(headers = {}) {
  const name = dispositionFilename(headers).trim();
  return !name || !path.extname(name) || /^(?:download|file|unknown)(?:\.[^.]+)?$/i.test(name);
}

function json(res, status, value) {
  const body = JSON.stringify(value);
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': Buffer.byteLength(body), 'Cache-Control': 'no-store' });
  res.end(body);
}

async function readBody(req) {
  let body = '';
  for await (const chunk of req) {
    body += chunk;
    if (body.length > MAX_BODY) throw new Error('Request is too large.');
  }
  return body ? JSON.parse(body) : {};
}

function publicItem(item) {
  return { ...item, controller: undefined };
}

function snapshot() {
  return { settings: state.settings, items: state.items.map(publicItem), active: running.size };
}

function totalActiveForHost(host) {
  return [...running.values()].filter(run => run.host === host).length;
}

function effectiveSpeedLimitKiB() { return Number(state.settings.snailModeKiB) > 0 ? Number(state.settings.snailModeKiB) : Number(state.settings.speedLimitKiB || 0); }

let pumpBusy = false;
async function pump() {
  if (pumpBusy || shuttingDown) return;
  pumpBusy = true;
  try {
    while (running.size < Number(state.settings.concurrency || 1)) {
      const next = state.items.find(item => item.status === 'queued' && totalActiveForHost(item.host) < Number(state.settings.perHost || 1));
      if (!next) break;
      startItem(next);
    }
  } finally { pumpBusy = false; }
}

async function choosePath(folder, filename) {
  await fsp.mkdir(folder, { recursive: true });
  let candidate = path.join(folder, filename);
  const ext = path.extname(filename);
  const stem = path.basename(filename, ext);
  let n = 1;
  while (true) {
    try { await fsp.access(candidate); candidate = path.join(folder, `${stem} (${n++})${ext}`); }
    catch { return candidate; }
  }
}

function mergedRanges(ranges = []) {
  const sorted = ranges.filter(range => Array.isArray(range) && Number.isSafeInteger(range[0]) && Number.isSafeInteger(range[1]) && range[0] >= 0 && range[1] >= range[0]).map(range => [...range]).sort((a, b) => a[0] - b[0]);
  const merged = [];
  for (const range of sorted) {
    const last = merged.at(-1);
    if (last && range[0] <= last[1] + 1) last[1] = Math.max(last[1], range[1]);
    else merged.push(range);
  }
  return merged;
}

function completedByteCount(ranges) { return mergedRanges(ranges).reduce((sum, [start, end]) => sum + end - start + 1, 0); }

async function transferSegmented(item, controller, partPath) {
  const connections = Math.max(1, Math.min(16, Number(state.settings.connectionsPerFile) || 1));
  if ((connections < 2 && !item.segmentedMode && !(item.completedRanges || []).length) || !item.acceptsRanges || !Number.isSafeInteger(item.size) || item.size <= 0) return false;

  let ranges = mergedRanges(item.completedRanges || []);
  if (!ranges.length && !item.segmentedMode) {
    let existingBytes = 0;
    try { existingBytes = Math.min(item.size, (await fsp.stat(partPath)).size); } catch {}
    if (existingBytes) ranges = [[0, existingBytes - 1]];
  }

  let probe;
  try { probe = await fetch(item.url, { headers: { Range: 'bytes=0-0', 'Accept-Encoding': 'identity' }, signal: controller.signal, redirect: 'follow' }); }
  catch (error) { if (controller.signal.aborted) throw error; return false; }
  const probeRange = probe.headers.get('content-range')?.match(/^bytes\s+0-0\/(\d+)$/i);
  if (probe.status !== 206 || !probeRange || Number(probeRange[1]) !== item.size) {
    probe.body?.cancel().catch(() => {});
    if (ranges.length > 0) throw new Error('This server stopped supporting safe ranged resume. The saved partial file was kept.');
    return false;
  }
  const etag = probe.headers.get('etag') || '';
  const modified = probe.headers.get('last-modified') || '';
  if ((item.etag && etag && item.etag !== etag) || (item.lastModified && modified && item.lastModified !== modified)) {
    probe.body?.cancel().catch(() => {});
    throw new Error('The server content changed since this download started; the partial file was kept.');
  }
  probe.body?.cancel().catch(() => {});
  item.etag = etag || item.etag || '';
  item.lastModified = modified || item.lastModified || '';

  const chunkSize = Math.ceil(item.size / connections);
  const todo = [];
  for (let start = 0; start < item.size; start += chunkSize) {
    const end = Math.min(item.size - 1, start + chunkSize - 1);
    let cursor = start;
    for (const [doneStart, doneEnd] of ranges) {
      if (doneEnd < cursor || doneStart > end) continue;
      if (doneStart > cursor) todo.push([cursor, Math.min(end, doneStart - 1)]);
      cursor = Math.max(cursor, doneEnd + 1);
      if (cursor > end) break;
    }
    if (cursor <= end) todo.push([cursor, end]);
  }
  if (!todo.length) return true;

  await fsp.mkdir(path.dirname(partPath), { recursive: true });
  let handle;
  try { handle = await fsp.open(partPath, 'r+'); } catch (error) { if (error.code !== 'ENOENT') throw error; handle = await fsp.open(partPath, 'w+'); }
  await handle.truncate(item.size);
  item.segmentedMode = true;
  item.completedRanges = ranges;
  item.bytes = completedByteCount(ranges);
  const workerController = new AbortController();
  const abortWorkers = () => workerController.abort();
  controller.signal.addEventListener('abort', abortWorkers, { once: true });
  const inFlight = new Map();
  let nextTask = 0;
  const worker = async workerId => {
    while (nextTask < todo.length) {
      if (workerController.signal.aborted) throw new DOMException('Paused', 'AbortError');
      const [start, end] = todo[nextTask++];
      const headers = { Range: `bytes=${start}-${end}`, 'Accept-Encoding': 'identity' };
      if (item.etag) headers['If-Range'] = item.etag;
      else if (item.lastModified) headers['If-Range'] = item.lastModified;
      const response = await fetch(item.url, { headers, signal: workerController.signal, redirect: 'follow' });
      const actualRange = response.headers.get('content-range')?.match(/^bytes\s+(\d+)-(\d+)\/(\d+)$/i);
      if (response.status !== 206 || !actualRange || Number(actualRange[1]) !== start || Number(actualRange[2]) !== end || Number(actualRange[3]) !== item.size) {
        response.body?.cancel().catch(() => {});
        throw new Error('The server did not return the requested file range; the partial download was kept.');
      }
      const responseEtag = response.headers.get('etag') || '';
      const responseModified = response.headers.get('last-modified') || '';
      if ((item.etag && responseEtag && item.etag !== responseEtag) || (item.lastModified && responseModified && item.lastModified !== responseModified)) {
        response.body?.cancel().catch(() => {});
        throw new Error('The server returned changed content for one of the file ranges.');
      }
      let position = start;
      inFlight.set(workerId, 0);
      let throttleStart = Date.now();
      let throttleBytes = 0;
      if (!response.body) throw new Error('The server returned an empty range.');
      for await (const chunk of Readable.fromWeb(response.body)) {
        if (workerController.signal.aborted) throw new DOMException('Paused', 'AbortError');
        let written = 0;
        while (written < chunk.length) {
          const result = await handle.write(chunk, written, chunk.length - written, position + written);
          if (!result.bytesWritten) throw new Error('Could not write the downloaded range to disk.');
          written += result.bytesWritten;
        }
        position += chunk.length;
        inFlight.set(workerId, position - start);
        item.bytes = completedByteCount(item.completedRanges) + [...inFlight.values()].reduce((sum, n) => sum + n, 0);
        scheduleSave();
        const aggregateLimit = Math.max(0, effectiveSpeedLimitKiB()) * 1024;
        if (aggregateLimit) {
          const activeItems = Math.max(1, running.size);
          const perWorkerLimit = aggregateLimit / (activeItems * Math.max(1, Math.min(connections, todo.length)));
          throttleBytes += chunk.length;
          const expectedMs = throttleBytes / perWorkerLimit * 1000;
          const elapsedMs = Date.now() - throttleStart;
          if (expectedMs > elapsedMs) await new Promise(resolve => setTimeout(resolve, Math.min(expectedMs - elapsedMs, 250)));
          if (Date.now() - throttleStart >= 1000) { throttleStart = Date.now(); throttleBytes = 0; }
        }
      }
      if (position !== end + 1) throw new Error('The server closed a file range before all requested bytes arrived.');
      inFlight.delete(workerId);
      item.completedRanges = mergedRanges([...item.completedRanges, [start, end]]);
      item.bytes = completedByteCount(item.completedRanges);
      scheduleSave();
    }
  };
  try {
    const workers = Array.from({ length: Math.min(connections, todo.length) }, (_, index) => worker(index));
    try { await Promise.all(workers); }
    catch (error) { workerController.abort(); await Promise.allSettled(workers); throw error; }
    await handle.sync();
  } finally {
    controller.signal.removeEventListener('abort', abortWorkers);
    await handle.close();
  }
  item.bytes = completedByteCount(item.completedRanges);
  item.acceptsRanges = true;
  scheduleSave();
  return true;
}

async function transfer(item, controller) {
  const folder = item.folder || state.settings.folder;
  item.folder = folder;
  const finalPath = item.filePath || await choosePath(folder, item.filename);
  const partPath = `${finalPath}.fetchflow-part`;
  item.filePath = finalPath;
  if (await transferSegmented(item, controller, partPath)) {
    await fsp.rename(partPath, finalPath).catch(async () => { await fsp.copyFile(partPath, finalPath); await fsp.unlink(partPath); });
    item.status = 'finished';
    item.bytes = item.size;
    item.completedRanges = [];
    item.segmentedMode = false;
    item.error = '';
    item.completedAt = new Date().toISOString();
    scheduleSave();
    return;
  }
  let offset = 0;
  try { offset = (await fsp.stat(partPath)).size; } catch {}
  if (item.size > 0 && offset >= item.size) {
    await fsp.rename(partPath, finalPath).catch(async () => { await fsp.copyFile(partPath, finalPath); await fsp.unlink(partPath); });
    item.bytes = item.size;
    item.status = 'finished';
    item.error = '';
    scheduleSave();
    return;
  }

  const headers = offset ? { Range: `bytes=${offset}-`, 'Accept-Encoding': 'identity' } : { 'Accept-Encoding': 'identity' };
  let response;
  try { response = await fetch(item.url, { headers, signal: controller.signal, redirect: 'follow' }); }
  catch (error) { if (controller.signal.aborted) throw error; throw new Error(error.message || 'Connection failed.'); }

  if (offset && response.status !== 206) {
    offset = 0;
    item.bytes = 0;
    try { response = await fetch(item.url, { signal: controller.signal, redirect: 'follow' }); }
    catch (error) { if (controller.signal.aborted) throw error; throw new Error(error.message || 'Connection failed.'); }
  }
  if (!response.ok) throw new Error(`Server responded ${response.status} ${response.statusText}.`);

  if (offset && response.status === 206) {
    const rangeStart = response.headers.get('content-range')?.match(/^bytes\s+(\d+)-/)?.[1];
    if (rangeStart == null || Number(rangeStart) !== offset) throw new Error('The server did not resume at the saved byte offset.');
  }

  const range = response.headers.get('content-range')?.match(/\/(\d+)$/);
  const responseSize = Number(response.headers.get('content-length') || 0);
  if (range) item.size = Number(range[1]);
  else if (response.status !== 206 && responseSize) item.size = responseSize;
  item.etag = response.headers.get('etag') || item.etag || '';
  item.lastModified = response.headers.get('last-modified') || item.lastModified || '';
  item.acceptsRanges = response.status === 206 || /bytes/i.test(response.headers.get('accept-ranges') || '');
  if (response.url) {
    try { item.finalHost = new URL(response.url).hostname; } catch {}
  }
  item.bytes = offset;
  const flags = offset && response.status === 206 ? 'a' : 'w';
  const file = fs.createWriteStream(partPath, { flags });
  let streamError;
  file.on('error', error => { streamError = error; });
  let lastThrottle = Date.now();
  let bytesSinceThrottle = 0;
  try {
    if (!response.body) throw new Error('The server returned an empty response.');
    for await (const chunk of Readable.fromWeb(response.body)) {
      if (controller.signal.aborted) throw new DOMException('Paused', 'AbortError');
      if (streamError) throw streamError;
      if (!file.write(chunk)) await new Promise((resolve, reject) => {
        const cleanup = () => { file.off('drain', onDrain); file.off('error', onError); };
        const onDrain = () => { cleanup(); resolve(); };
        const onError = error => { cleanup(); reject(error); };
        file.once('drain', onDrain); file.once('error', onError);
      });
      item.bytes += chunk.length;
      bytesSinceThrottle += chunk.length;
      scheduleSave();
      const limit = Math.max(0, effectiveSpeedLimitKiB()) * 1024 / Math.max(1, running.size);
      if (limit) {
        const elapsed = Math.max(1, Date.now() - lastThrottle);
        const expected = (bytesSinceThrottle / limit) * 1000;
        if (expected > elapsed) await new Promise(resolve => setTimeout(resolve, Math.min(expected - elapsed, 250)));
        if (Date.now() - lastThrottle >= 1000) { lastThrottle = Date.now(); bytesSinceThrottle = 0; }
      }
    }
    await new Promise((resolve, reject) => file.end(error => error ? reject(error) : resolve()));
  } catch (error) {
    const closed = new Promise(resolve => file.once('close', resolve));
    file.destroy();
    await closed;
    throw error;
  }
  await fsp.rename(partPath, finalPath).catch(async () => { await fsp.copyFile(partPath, finalPath); await fsp.unlink(partPath); });
  item.status = 'finished';
  item.error = '';
  item.completedAt = new Date().toISOString();
  scheduleSave();
}

function startItem(item) {
  const controller = new AbortController();
  let host;
  try { host = new URL(item.url).hostname.toLowerCase(); } catch { item.status = 'error'; item.error = 'Invalid URL.'; return; }
  const run = { controller, host };
  running.set(item.id, run);
  item.status = 'downloading';
  item.error = '';
  item.startedAt = Date.now();
  scheduleSave();
  run.done = transfer(item, controller).catch(error => {
    if (controller.signal.aborted) {
      if (item.status === 'downloading') item.status = 'paused';
      item.error = '';
    } else {
      item.status = 'error';
      item.error = error.message || 'Download failed.';
    }
  }).finally(() => {
    if (running.get(item.id) === run) running.delete(item.id);
    scheduleSave();
    pump();
  });
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url || '/', `http://${HOST}:${PORT}`);
    if (req.method !== 'GET' && req.headers.origin !== `http://${HOST}:${PORT}`) return json(res, 403, { error: 'Request origin rejected.' });

    if (req.method === 'POST' && url.pathname === '/api/shutdown') {
      shuttingDown = true;
      const activeRuns = [...running.values()];
      for (const item of state.items) {
        const run = running.get(item.id);
        if (run) { item.status = 'paused'; run.controller.abort(); }
      }
      await Promise.all(activeRuns.map(run => run.done));
      await persistNow();
      json(res, 200, { stopped: true });
      setTimeout(() => server.close(() => process.exit(0)), 50);
      return;
    }
    if (req.method === 'GET' && url.pathname === '/api/state') return json(res, 200, snapshot());
    if (req.method === 'POST' && url.pathname === '/api/inspect') {
      const { urls } = await readBody(req);
      if (!Array.isArray(urls) || urls.length > 80) return json(res, 400, { error: 'Enter up to 80 links at a time.' });
      const checked = await Promise.all(urls.map(async (value) => {
        try {
          const target = safeUrl(value);
          let head = null;
          try { head = await fetch(target, { method: 'HEAD', signal: AbortSignal.timeout(12000), redirect: 'follow' }); } catch {}
          let probe = null;
          if (!head?.ok || [403, 405, 501].includes(head.status) || needsFilenameProbe(head.headers)) {
            try { probe = await fetch(target, { headers: { Range: 'bytes=0-0' }, signal: AbortSignal.timeout(12000), redirect: 'follow' }); } catch {}
          }
          const probeWorks = probe && (probe.ok || probe.status === 206);
          const response = probeWorks || !head?.ok ? (probe || head) : head;
          if (!response) throw new Error('The server could not be reached.');
          const nameHeaders = probeWorks && dispositionFilename(probe.headers) ? probe.headers : (head?.headers || response.headers);
          const resolvedUrl = new URL(response.url || head?.url || target);
          const contentRangeTotal = response.headers.get('content-range')?.match(/\/(\d+)$/)?.[1];
          const headSize = Number(head?.headers.get('content-length') || 0);
          const responseSize = Number(response.headers.get('content-length') || 0);
          const size = Number(contentRangeTotal || (response.status === 206 ? 0 : responseSize) || headSize || 0);
          const acceptsRanges = response.status === 206 || /bytes/i.test(response.headers.get('accept-ranges') || '') || /bytes/i.test(head?.headers.get('accept-ranges') || '');
          probe?.body?.cancel().catch(() => {});
          return { url: value, ok: response.ok || response.status === 206, filename: filenameFrom(resolvedUrl, nameHeaders), size, host: resolvedUrl.hostname, acceptsRanges, status: response.status, contentType: response.headers.get('content-type') || head?.headers.get('content-type') || '' };
        } catch (error) { return { url: value, ok: false, filename: '', size: 0, host: '', acceptsRanges: false, status: 0, contentType: '', error: error.message || 'Could not inspect this link.' }; }
      }));
      return json(res, 200, { items: checked });
    }
    if (req.method === 'POST' && url.pathname === '/api/downloads') {
      const { items } = await readBody(req);
      if (!Array.isArray(items) || !items.length || items.length > 80) return json(res, 400, { error: 'Select between 1 and 80 checked links.' });
      const added = [];
      for (const raw of items) {
        const target = safeUrl(raw.url);
        const item = { id: randomUUID(), url: target.href, filename: filenameFrom(target), host: target.hostname, folder: '', filePath: '', size: Math.max(0, Number(raw.size || 0)), bytes: 0, acceptsRanges: Boolean(raw.acceptsRanges), status: 'queued', error: '', addedAt: new Date().toISOString() };
        if (raw.filename) item.filename = path.basename(String(raw.filename).replace(/[\\/\0]/g, '_')).replace(/[<>:"|?*\x00-\x1F]/g, '_') || item.filename;
        state.items.unshift(item);
        added.push(item);
      }
      scheduleSave();
      return json(res, 201, { items: added });
    }
    if (req.method === 'POST' && url.pathname === '/api/start') {
      for (const item of state.items) if (item.status === 'paused') item.status = 'queued';
      pump(); scheduleSave(); return json(res, 200, snapshot());
    }
    if (req.method === 'POST' && url.pathname === '/api/pause-all') {
      for (const item of state.items) {
        if (item.status === 'queued') item.status = 'paused';
        const run = running.get(item.id);
        if (run) { item.status = 'paused'; run.controller.abort(); }
      }
      scheduleSave(); return json(res, 200, snapshot());
    }
    if (req.method === 'POST' && url.pathname === '/api/clear-finished') {
      state.items = state.items.filter(item => item.status !== 'finished');
      scheduleSave(); return json(res, 200, snapshot());
    }
    if (req.method === 'POST' && url.pathname === '/api/downloads/remove-selected') {
      const { ids, deleteFiles = false } = await readBody(req);
      if (!Array.isArray(ids) || ids.length > 500 || ids.some(id => typeof id !== 'string')) return json(res, 400, { error: 'Select up to 500 downloads.' });
      const selectedIds = new Set(ids);
      const selected = state.items.filter(item => selectedIds.has(item.id));
      for (const item of selected) {
        const run = running.get(item.id);
        if (run) { item.status = 'paused'; run.controller.abort(); await run.done; }
      }

      const pathsToDelete = [];
      if (deleteFiles) {
        for (const item of selected) {
          if (!item.filePath) continue;
          const finalPath = path.resolve(item.filePath);
          const folder = path.resolve(item.folder || path.dirname(finalPath));
          if (!path.isAbsolute(item.filePath) || path.dirname(finalPath).toLowerCase() !== folder.toLowerCase()) return json(res, 400, { error: `Cannot safely delete files for ${item.filename}; its saved destination is invalid.` });
          for (const target of [finalPath, `${finalPath}.fetchflow-part`]) {
            try {
              const file = await fsp.lstat(target);
              if (!file.isFile() || file.isSymbolicLink()) return json(res, 400, { error: `Cannot safely delete ${path.basename(target)}; it is not a regular file.` });
              pathsToDelete.push(target);
            } catch (error) { if (error.code !== 'ENOENT') throw error; }
          }
        }
      }

      let deletedFiles = 0;
      for (const target of pathsToDelete) {
        try { await fsp.unlink(target); deletedFiles++; }
        catch (error) { if (error.code !== 'ENOENT') throw error; }
      }
      state.items = state.items.filter(item => !selectedIds.has(item.id));
      scheduleSave();
      return json(res, 200, { removed: selected.length, deletedFiles, state: snapshot() });
    }
    if (req.method === 'POST' && url.pathname.startsWith('/api/downloads/')) {
      const parts = url.pathname.split('/');
      const id = parts[3];
      const action = parts[4];
      const item = state.items.find(entry => entry.id === id);
      if (!item) return json(res, 404, { error: 'Download not found.' });
      if (action === 'pause') {
        const run = running.get(item.id);
        if (run) { item.status = 'paused'; run.controller.abort(); }
        else if (item.status === 'queued') item.status = 'paused';
      } else if (action === 'resume') {
        const run = running.get(item.id);
        if (run) await run.done;
        if (['paused', 'error'].includes(item.status)) item.status = 'queued';
        pump();
      } else if (action === 'retry') {
        if (item.status === 'error') { item.status = 'queued'; item.error = ''; pump(); }
      } else if (action === 'refresh-link') {
        const { url: refreshedUrl } = await readBody(req);
        const target = safeUrl(refreshedUrl);
        const run = running.get(item.id);
        if (run) { item.status = 'paused'; run.controller.abort(); await run.done; }

        let offset = (item.completedRanges || []).length ? completedByteCount(item.completedRanges) : 0;
        const segmented = Boolean(item.segmentedMode || (item.completedRanges || []).length);
        if (!segmented && item.filePath) {
          try { offset = (await fsp.stat(`${item.filePath}.fetchflow-part`)).size; } catch {}
        }
        if (offset > 0 && item.size > 0 && offset >= item.size) {
          item.url = target.href;
          item.host = target.hostname;
          item.bytes = offset;
          item.status = 'queued';
          item.error = '';
          scheduleSave();
          pump();
          return json(res, 200, { state: snapshot(), resumedAt: offset, verifiedRange: true });
        }

        let probe;
        try {
          const probeOffset = segmented ? 0 : offset;
          probe = await fetch(target, { headers: { Range: `bytes=${probeOffset}-${probeOffset}`, 'Accept-Encoding': 'identity' }, signal: AbortSignal.timeout(15000), redirect: 'follow' });
        } catch (error) { return json(res, 400, { error: `Could not check the refreshed link: ${error.message || 'connection failed'}` }); }
        const contentRange = probe.headers.get('content-range')?.match(/^bytes\s+(\d+)-(\d+)\/(\d+|\*)$/i);
        const rangeValid = probe.status === 206 && contentRange && Number(contentRange[1]) === (segmented ? 0 : offset);
        const total = contentRange?.[3] && contentRange[3] !== '*' ? Number(contentRange[3]) : 0;
        const newEtag = probe.headers.get('etag') || '';
        const newModified = probe.headers.get('last-modified') || '';
        let validationError = '';
        if (offset > 0 && !rangeValid) validationError = segmented ? 'This refreshed link cannot serve byte ranges, so the saved pieces were kept unchanged.' : 'This refreshed link cannot resume at the saved byte offset. It must return a matching HTTP range response.';
        else if (offset === 0 && !probe.ok && probe.status !== 206) validationError = `The refreshed link returned ${probe.status} ${probe.statusText}.`;
        else if (offset > 0 && item.size > 0 && total > 0 && total !== item.size) validationError = 'The refreshed link appears to point to a different-sized file, so the partial download was kept unchanged.';
        else if (offset > 0 && item.etag && newEtag && item.etag !== newEtag) validationError = 'The refreshed link points to changed content (ETag differs), so the partial download was kept unchanged.';
        else if (offset > 0 && !item.etag && item.lastModified && newModified && item.lastModified !== newModified) validationError = 'The refreshed link points to changed content (last-modified time differs), so the partial download was kept unchanged.';
        else if (offset > 0 && !(item.etag && newEtag) && !(item.lastModified && newModified)) validationError = 'The server did not provide a matching file validator, so Fetchflow cannot safely confirm this is the same file.';
        probe.body?.cancel().catch(() => {});
        if (validationError) return json(res, 409, { error: validationError });

        const finalUrl = new URL(probe.url || target);
        item.url = finalUrl.href;
        item.host = finalUrl.hostname;
        if (!item.size && total) item.size = total;
        item.etag = newEtag || item.etag || '';
        item.lastModified = newModified || item.lastModified || '';
        item.acceptsRanges = probe.status === 206 || /bytes/i.test(probe.headers.get('accept-ranges') || '');
        item.bytes = offset;
        item.status = 'queued';
        item.error = '';
        scheduleSave();
        pump();
        return json(res, 200, { state: snapshot(), resumedAt: offset, verifiedRange: rangeValid || offset === 0, segmented });
      } else if (action === 'remove') {
        const run = running.get(item.id);
        if (run) { item.status = 'paused'; run.controller.abort(); }
        state.items = state.items.filter(entry => entry.id !== id);
        scheduleSave();
        return json(res, 200, snapshot());
      } else return json(res, 404, { error: 'Unknown download action.' });
      scheduleSave(); return json(res, 200, snapshot());
    }
    if (req.method === 'PUT' && url.pathname === '/api/settings') {
      const changes = await readBody(req);
      if (changes.folder != null) {
        const folder = String(changes.folder).trim();
        if (!path.isAbsolute(folder)) return json(res, 400, { error: 'Choose an absolute destination folder.' });
        await fsp.mkdir(folder, { recursive: true });
        state.settings.folder = folder;
      }
      for (const key of ['concurrency', 'perHost', 'connectionsPerFile']) if (changes[key] != null) state.settings[key] = Math.max(1, Math.min(16, Math.round(Number(changes[key]) || 1)));
      if (changes.speedLimitKiB != null) state.settings.speedLimitKiB = Math.max(0, Math.min(1_000_000, Math.round(Number(changes.speedLimitKiB) || 0)));
      if (changes.snailModeKiB != null) {
        const snail = Number(changes.snailModeKiB);
        state.settings.snailModeKiB = [0, 10, 50].includes(snail) ? snail : 0;
      }
      await persistNow();
      if (running.size && (changes.concurrency != null || changes.perHost != null)) pump();
      return json(res, 200, snapshot());
    }

    if (req.method === 'GET') {
      const relative = url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.slice(1));
      const filePath = path.resolve(WEB_DIR, relative);
      if (!filePath.startsWith(path.resolve(WEB_DIR) + path.sep)) return json(res, 404, { error: 'Not found.' });
      const type = filePath.endsWith('.html') ? 'text/html; charset=utf-8' : filePath.endsWith('.css') ? 'text/css; charset=utf-8' : filePath.endsWith('.js') ? 'text/javascript; charset=utf-8' : 'application/octet-stream';
      const data = await fsp.readFile(filePath);
      res.writeHead(200, { 'Content-Type': type, 'Content-Length': data.length, 'Cache-Control': 'no-cache', 'Content-Security-Policy': "default-src 'self'; connect-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'" });
      return res.end(data);
    }
    return json(res, 404, { error: 'Not found.' });
  } catch (error) { return json(res, 400, { error: error.message || 'Request failed.' }); }
});

server.listen(PORT, HOST, () => {
  console.log(`Fetchflow is ready at http://${HOST}:${PORT}`);
  console.log(`Queue data: ${DATA_FILE}`);
  if (process.env.FETCHFLOW_NO_OPEN !== '1') {
    if (process.platform === 'win32') spawn('rundll32.exe', ['url.dll,FileProtocolHandler', `http://${HOST}:${PORT}`], { stdio: 'ignore', windowsHide: true, detached: true }).unref();
  }
});

process.on('SIGINT', () => { for (const run of running.values()) run.controller.abort(); server.close(() => process.exit(0)); });
