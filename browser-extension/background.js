const MAX_CAPTURED = 50;
const MENU_ID = "fetchflow-send-link";
const NATIVE_HOST = "com.fetchflow.download_manager";
const YOUTUBE_FORMAT_TTL = 6 * 60 * 60 * 1000;
const youtubeFormatRequests = new Map();

chrome.runtime.onInstalled.addListener(async () => {
  const saved = await chrome.storage.local.get({ autoRoute: true, videoButtonsEnabled: true, capturedDownloads: [] });
  await chrome.storage.local.set({
    autoRoute: typeof saved.autoRoute === "boolean" ? saved.autoRoute : true,
    videoButtonsEnabled: typeof saved.videoButtonsEnabled === "boolean" ? saved.videoButtonsEnabled : true,
    capturedDownloads: Array.isArray(saved.capturedDownloads) ? saved.capturedDownloads : []
  });
  await chrome.contextMenus.removeAll();
  chrome.contextMenus.create({ id: MENU_ID, title: "Send link to Fetchflow", contexts: ["link"] });
});

function isHttpUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch { return false; }
}

function canonicalYoutubeUrl(value) {
  try {
    const url = new URL(value);
    const host = url.hostname.toLowerCase();
    if (!/(^|\.)youtube\.com$|(^|\.)youtu\.be$|(^|\.)youtube-nocookie\.com$/.test(host)) return "";
    let id = host === "youtu.be" ? url.pathname.split("/").filter(Boolean)[0] : url.searchParams.get("v");
    if (!id) {
      const match = url.pathname.match(/^\/(?:embed|shorts|live)\/([^/?#]+)/i);
      if (match) id = match[1];
    }
    return id ? `https://www.youtube.com/watch?v=${encodeURIComponent(id)}` : "";
  } catch { return ""; }
}

function cleanName(value) {
  return (value || "Download").split(/[\\/]/).pop().slice(0, 180) || "Download";
}

function suggestedName(value) {
  if (typeof value !== "string" || !value.trim()) return "";
  return value.split(/[\\/]/).pop().slice(0, 180);
}

async function saveCaptured(download, routeStatus) {
  if (!download || download.incognito || !isHttpUrl(download.url)) return;
  const data = await chrome.storage.local.get({ capturedDownloads: [] });
  const list = Array.isArray(data.capturedDownloads) ? data.capturedDownloads : [];
  const captured = {
    id: String(download.id),
    url: download.finalUrl && isHttpUrl(download.finalUrl) ? download.finalUrl : download.url,
    name: cleanName(download.filename),
    capturedAt: Date.now(),
    state: download.state || "in_progress",
    routeStatus: routeStatus || "captured"
  };
  const next = [captured, ...list.filter(item => item.id !== captured.id)].slice(0, MAX_CAPTURED);
  await chrome.storage.local.set({ capturedDownloads: next });
}

async function setRouteStatus(id, routeStatus) {
  const data = await chrome.storage.local.get({ capturedDownloads: [] });
  const list = Array.isArray(data.capturedDownloads) ? data.capturedDownloads : [];
  const index = list.findIndex(item => item.id === String(id));
  if (index >= 0) {
    list[index] = { ...list[index], routeStatus };
    await chrome.storage.local.set({ capturedDownloads: list });
  }
}

async function callNative(message) {
  try {
    const reply = await chrome.runtime.sendNativeMessage(NATIVE_HOST, message);
    if (!reply || reply.ok !== true) throw new Error(reply && reply.error ? reply.error : "Fetchflow did not accept the download.");
    return reply;
  } catch (error) {
    const reason = error && error.message ? error.message : "Could not connect to Fetchflow.";
    return { ok: false, error: reason };
  }
}

async function sendToNative(message) { return await callNative(message); }

async function getYoutubeFormats(value) {
  const url = canonicalYoutubeUrl(value);
  if (!url) return { ok: false, error: "Could not identify the YouTube video." };
  if (youtubeFormatRequests.has(url)) return await youtubeFormatRequests.get(url);
  const request = (async () => {
    const saved = await chrome.storage.local.get({ youtubeFormatCache: {} });
    const cache = saved.youtubeFormatCache && typeof saved.youtubeFormatCache === "object" ? saved.youtubeFormatCache : {};
    const cached = cache[url];
    if (cached && cached.cachedAt && Date.now() - cached.cachedAt < YOUTUBE_FORMAT_TTL && Array.isArray(cached.formats)) return { ok: true, title: cached.title, formats: cached.formats };
    const result = await callNative({ kind: "video-formats", url });
    if (!result.ok || !Array.isArray(result.formats)) return result;
    cache[url] = { cachedAt: Date.now(), title: result.title || "YouTube video", formats: result.formats };
    const kept = Object.fromEntries(Object.entries(cache).sort((a, b) => (b[1].cachedAt || 0) - (a[1].cachedAt || 0)).slice(0, 20));
    await chrome.storage.local.set({ youtubeFormatCache: kept });
    return { ok: true, title: cache[url].title, formats: result.formats };
  })();
  youtubeFormatRequests.set(url, request);
  try { return await request; }
  finally { youtubeFormatRequests.delete(url); }
}

async function routeChromeDownload(download) {
  if (!download || download.incognito || !isHttpUrl(download.url)) return;
  await saveCaptured(download, "waiting");
  const settings = await chrome.storage.local.get({ autoRoute: true });
  if (!settings.autoRoute) return;
  try {
    // Stop Chrome's transfer before Fetchflow takes over, so both apps don't download the same file.
    await chrome.downloads.pause(download.id);
  } catch (error) {
    await setRouteStatus(download.id, "Chrome could not pause this download; it remains in Chrome");
    return;
  }
  const currentItems = await chrome.downloads.search({ id: download.id });
  const current = currentItems && currentItems[0] ? currentItems[0] : download;
  const url = current.finalUrl && isHttpUrl(current.finalUrl) ? current.finalUrl : (download.finalUrl && isHttpUrl(download.finalUrl) ? download.finalUrl : download.url);
  const result = await sendToNative({ kind: "download", url, name: suggestedName(current.filename) || suggestedName(download.filename) });
  if (!result.ok) {
    await setRouteStatus(download.id, "failed: " + result.error);
    try { await chrome.downloads.resume(download.id); } catch { }
    return; // Keep Chrome's original download running if Fetchflow did not accept it.
  }
  try {
    await chrome.downloads.cancel(download.id);
    await setRouteStatus(download.id, "sent to Fetchflow");
  } catch (error) {
    await setRouteStatus(download.id, "sent to Fetchflow; Chrome copy may still be running");
  }
}

chrome.downloads.onCreated.addListener(download => { void routeChromeDownload(download); });

chrome.downloads.onChanged.addListener(async delta => {
  if (!delta.finalUrl && !delta.filename && !delta.state) return;
  const data = await chrome.storage.local.get({ capturedDownloads: [] });
  const list = Array.isArray(data.capturedDownloads) ? data.capturedDownloads : [];
  const index = list.findIndex(item => item.id === String(delta.id));
  if (index < 0) return;
  const item = { ...list[index] };
  if (delta.finalUrl && delta.finalUrl.current && isHttpUrl(delta.finalUrl.current)) item.url = delta.finalUrl.current;
  if (delta.filename && delta.filename.current) item.name = cleanName(delta.filename.current);
  if (delta.state && delta.state.current) item.state = delta.state.current;
  list[index] = item;
  await chrome.storage.local.set({ capturedDownloads: list });
});

chrome.contextMenus.onClicked.addListener(async info => {
  if (info.menuItemId !== MENU_ID || !isHttpUrl(info.linkUrl)) return;
  const result = await sendToNative({ kind: "link", url: info.linkUrl });
  if (!result.ok) await chrome.storage.local.set({ lastError: result.error });
});

chrome.runtime.onMessage.addListener((message, _sender, respond) => {
  if (!message || typeof message !== "object") return;
  if (message.type === "fetchflow-intercept-click") {
    (async () => {
      const settings = await chrome.storage.local.get({ autoRoute: true });
      if (!settings.autoRoute && message.explicit !== true) return { ok: false, error: "Automatic routing is turned off." };
      if (!isHttpUrl(message.url)) return { ok: false, error: "This download link is not an HTTP or HTTPS URL." };
      const result = await sendToNative({ kind: "download", url: message.url, name: suggestedName(message.name) });
      if (!result.ok) await chrome.storage.local.set({ lastError: result.error });
      return result;
    })().then(respond).catch(error => respond({ ok: false, error: error.message || "Could not send this link to Fetchflow." }));
    return true;
  }
  if (message.type === "fetchflow-video-page") {
    if (!isHttpUrl(message.url)) { respond({ ok: false, error: "This video page URL is not supported." }); return; }
    sendToNative({ kind: "link", url: message.url, autoCheck: true }).then(respond);
    return true;
  }
  if (message.type === "fetchflow-youtube-formats") {
    getYoutubeFormats(message.url).then(respond).catch(error => respond({ ok: false, error: error.message || "Could not fetch YouTube quality details." }));
    return true;
  }
  if (message.type === "fetchflow-youtube-download") {
    (async () => {
      const url = canonicalYoutubeUrl(message.url);
      if (!url) return { ok: false, error: "Could not identify the YouTube video." };
      let details = await getYoutubeFormats(url);
      if (!details.ok) return details;
      const format = details.formats.find(item => item.key === message.formatKey);
      if (!format) return { ok: false, error: "That quality is no longer available. Reopen the video menu to refresh its qualities." };
      return await callNative({ kind: "youtube-download", url, name: details.title, selector: format.selector, mode: format.mode, extension: format.extension, audioQuality: format.audioQuality || "" });
    })().then(respond).catch(error => respond({ ok: false, error: error.message || "Could not start this YouTube format in Fetchflow." }));
    return true;
  }
  if (message.type === "fetchflow-list") {
    chrome.storage.local.get({ autoRoute: true, videoButtonsEnabled: true, capturedDownloads: [], lastError: "" }).then(respond);
    return true;
  }
  if (message.type === "fetchflow-settings") {
    const settings = {};
    if (typeof message.autoRoute === "boolean") settings.autoRoute = message.autoRoute;
    if (typeof message.videoButtonsEnabled === "boolean") settings.videoButtonsEnabled = message.videoButtonsEnabled;
    if (!Object.keys(settings).length) { respond({ ok: false }); return; }
    chrome.storage.local.set(settings).then(() => respond({ ok: true }));
    return true;
  }
  if (message.type === "fetchflow-send-item" && typeof message.id === "string") {
    chrome.downloads.search({ id: Number(message.id) }).then(async items => {
      const item = items && items[0];
      if (!item || !isHttpUrl(item.url)) { respond({ ok: false, error: "This Chrome download no longer has a usable URL." }); return; }
      const url = item.finalUrl && isHttpUrl(item.finalUrl) ? item.finalUrl : item.url;
      const result = await sendToNative({ kind: "download", url, name: suggestedName(item.filename) });
      if (result.ok) {
        try { await chrome.downloads.cancel(item.id); } catch { }
        await setRouteStatus(item.id, "sent to Fetchflow");
      }
      respond(result);
    }).catch(error => respond({ ok: false, error: error.message || "Could not find that Chrome download." }));
    return true;
  }
  if (message.type === "fetchflow-remove" && typeof message.id === "string") {
    chrome.storage.local.get({ capturedDownloads: [] }).then(async data => {
      const list = (Array.isArray(data.capturedDownloads) ? data.capturedDownloads : []).filter(item => item.id !== message.id);
      await chrome.storage.local.set({ capturedDownloads: list });
      respond({ ok: true });
    });
    return true;
  }
  if (message.type === "fetchflow-clear") {
    chrome.storage.local.set({ capturedDownloads: [] }).then(() => respond({ ok: true }));
    return true;
  }
});

