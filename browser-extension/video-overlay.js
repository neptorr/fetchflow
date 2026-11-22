(() => {
  const directExtensions = new Set(["mp4", "webm", "ogv", "ogg", "mov", "m4v", "mkv", "avi", "flv", "3gp"]);
  const videos = new Map();
  const youtubeRequests = new Map();
  const isYoutube = /(^|\.)youtube\.com$|(^|\.)youtu\.be$|(^|\.)youtube-nocookie\.com$/i.test(location.hostname);
  let videoButtonsEnabled = false;

  chrome.storage.local.get({ videoButtonsEnabled: true }).then(settings => {
    videoButtonsEnabled = settings.videoButtonsEnabled !== false;
    schedulePosition();
    if (videoButtonsEnabled) for (const item of videos.values()) maybePrefetchYoutube(item);
  }).catch(() => {});
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area !== "local" || !changes.videoButtonsEnabled) return;
    videoButtonsEnabled = changes.videoButtonsEnabled.newValue !== false;
    schedulePosition();
    if (videoButtonsEnabled) for (const item of videos.values()) maybePrefetchYoutube(item);
  });

  function absoluteHttp(value) {
    try {
      const url = new URL(value, location.href);
      return url.protocol === "http:" || url.protocol === "https:" ? url.href : "";
    } catch { return ""; }
  }

  function sourceQuality(source, url, video) {
    const explicit = source && (source.getAttribute("data-quality") || source.getAttribute("data-res") || source.getAttribute("label") || source.getAttribute("height"));
    if (explicit) return /^\d{3,4}$/.test(explicit) ? `${explicit}p` : explicit;
    const match = url.match(/(?:^|[^0-9])(2160|1440|1080|900|720|576|540|480|360|240)p(?:[^0-9]|$)|(?:quality|height|res)[=_/-](2160|1440|1080|900|720|576|540|480|360|240)(?:[^0-9]|$)/i);
    if (match) return `${match[1] || match[2]}p`;
    if (video.currentSrc && absoluteHttp(video.currentSrc) === url && video.videoHeight > 0) return `${video.videoHeight}p`;
    return "";
  }

  function videoOptions(video) {
    const candidates = [];
    for (const source of video.querySelectorAll("source[src], source[data-src]")) {
      const url = absoluteHttp(source.getAttribute("src") || source.getAttribute("data-src"));
      if (url) candidates.push({ url, source, type: source.type || "" });
    }
    for (const value of [video.currentSrc, video.getAttribute("src")]) {
      const url = absoluteHttp(value);
      if (url) candidates.push({ url, source: null, type: "" });
    }
    const unique = new Map();
    for (const candidate of candidates) {
      const parsed = new URL(candidate.url);
      const extension = (parsed.pathname.split(".").pop() || "").toLowerCase();
      const directMime = /^video\/(mp4|webm|ogg|quicktime|x-matroska|x-msvideo)$/i.test(candidate.type);
      if (!directExtensions.has(extension) && !directMime) continue;
      if (!unique.has(candidate.url)) {
        const quality = sourceQuality(candidate.source, candidate.url, video);
        let filename = parsed.pathname.split("/").pop() || "Video";
        try { filename = decodeURIComponent(filename); } catch { }
        filename = filename.replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_").slice(0, 140) || "Video";
        const label = quality ? `Download ${quality}` : `Download file · ${filename}`;
        unique.set(candidate.url, { url: candidate.url, name: filename, label, quality });
      }
    }
    return [...unique.values()];
  }

  function setMenuOpen(item, open) {
    item.menuOpen = !!open;
    item.menu.hidden = !item.menuOpen;
    // Set display explicitly because some player page styles can override the
    // browser's default rendering for the `hidden` attribute.
    item.menu.style.setProperty("display", item.menuOpen ? "block" : "none", "important");
  }

  function sendToFetchflow(item, url, name) {
    chrome.runtime.sendMessage({ type: "fetchflow-intercept-click", url, name, explicit: true }).then(result => {
      if (!result || !result.ok) console.warn("Fetchflow could not start this video download.", result && result.error);
    }).catch(error => console.warn("Fetchflow could not be reached.", error));
  }

  function youtubePageUrl() {
    try {
      const url = new URL(location.href);
      let id = url.hostname.toLowerCase() === "youtu.be" ? url.pathname.split("/").filter(Boolean)[0] : url.searchParams.get("v");
      if (!id) {
        const match = url.pathname.match(/^\/(?:embed|shorts|live)\/([^/?#]+)/i);
        if (match) id = match[1];
      }
      return id ? `https://www.youtube.com/watch?v=${encodeURIComponent(id)}` : "";
    } catch { return ""; }
  }

  function fetchYoutubeFormats(item) {
    const url = youtubePageUrl();
    if (!url) return Promise.resolve({ ok: false, error: "Open a YouTube video to load its qualities." });
    if (item.youtubeUrl === url && item.youtubeFormats) return Promise.resolve({ ok: true, title: item.youtubeTitle, formats: item.youtubeFormats });
    if (item.youtubeUrl === url && item.youtubePromise) return item.youtubePromise;
    item.youtubeUrl = url;
    item.youtubeFormats = null;
    if (!youtubeRequests.has(url)) {
      const request = chrome.runtime.sendMessage({ type: "fetchflow-youtube-formats", url }).catch(error => ({ ok: false, error: error.message || "Could not reach Fetchflow." }));
      youtubeRequests.set(url, request);
      request.finally(() => { if (youtubeRequests.get(url) === request) youtubeRequests.delete(url); });
    }
    item.youtubePromise = youtubeRequests.get(url).then(result => {
      if (item.youtubeUrl === url && result && result.ok && Array.isArray(result.formats)) {
        item.youtubeFormats = result.formats;
        item.youtubeTitle = result.title || "YouTube video";
      }
      return result;
    }).finally(() => { item.youtubePromise = null; });
    return item.youtubePromise;
  }

  function maybePrefetchYoutube(item) {
    if (!isYoutube || !videoButtonsEnabled || !(item.video.readyState > 0 || item.video.currentSrc)) return;
    void fetchYoutubeFormats(item);
  }

  function renderYoutubeFormats(item, result) {
    item.menu.replaceChildren();
    if (!result || !result.ok || !Array.isArray(result.formats) || result.formats.length === 0) {
      const error = document.createElement("div");
      error.className = "unavailable";
      error.textContent = result && result.error ? result.error : "No downloadable qualities were found.";
      item.menu.append(error);
      return;
    }
    for (const format of result.formats) addOption(item, format.label || "Download format", () => sendYoutubeFormat(item, format));
  }

  function sendYoutubeFormat(item, format) {
    chrome.runtime.sendMessage({ type: "fetchflow-youtube-download", url: item.youtubeUrl || youtubePageUrl(), formatKey: format.key }).then(result => {
      if (!result || !result.ok) console.warn("Fetchflow could not start the selected YouTube quality.", result && result.error);
    }).catch(error => console.warn("Fetchflow could not be reached.", error));
  }

  function openOptions(item) {
    item.menu.replaceChildren();
    if (isYoutube) {
      const currentVideoUrl = youtubePageUrl();
      if (item.youtubeUrl !== currentVideoUrl) item.youtubeFormats = null;
      setMenuOpen(item, true);
      if (item.youtubeFormats) renderYoutubeFormats(item, { ok: true, formats: item.youtubeFormats, title: item.youtubeTitle });
      else {
        const loading = document.createElement("div");
        loading.className = "unavailable";
        loading.textContent = "Loading available video and audio qualities…";
        item.menu.append(loading);
        fetchYoutubeFormats(item).then(result => { if (item.menuOpen && item.youtubeUrl === currentVideoUrl) renderYoutubeFormats(item, result); });
      }
    } else {
      const options = item.options;
      if (!options.length) {
        const unavailable = document.createElement("div");
        unavailable.className = "unavailable";
        unavailable.textContent = "This player does not expose a direct video file or quality list. Protected or segmented streams may not be downloadable.";
        item.menu.append(unavailable);
      } else {
        for (const option of options) addOption(item, option.label, () => sendToFetchflow(item, option.url, option.name));
      }
    }
    setMenuOpen(item, true);
  }

  function addOption(item, text, onChoose) {
    const option = document.createElement("button");
    option.className = "option";
    option.type = "button";
    option.textContent = text;
    option.addEventListener("click", event => {
      event.stopPropagation();
      setMenuOpen(item, false);
      onChoose();
    });
    item.menu.append(option);
  }

  function attach(video) {
    if (videos.has(video)) return;
    const host = document.createElement("div");
    host.setAttribute("aria-label", "Fetchflow video download controls");
    host.style.cssText = "position:fixed;left:-1000px;top:-1000px;width:200px;height:210px;z-index:2147483647;pointer-events:none;";
    const shadow = host.attachShadow({ mode: "open" });
    shadow.innerHTML = `
      <style>
        *{box-sizing:border-box;font-family:Segoe UI,Arial,sans-serif}
        .button{position:absolute;top:0;right:0;pointer-events:auto;border:1px solid rgba(91,231,208,.48);border-radius:8px;padding:8px 11px;background:rgba(12,24,31,.56);color:#e6fffa;font-size:12px;font-weight:650;box-shadow:0 3px 14px rgba(0,0,0,.32);cursor:pointer;backdrop-filter:blur(3px)}
        .button:hover{background:rgba(23,75,72,.76)}
        .menu{position:absolute;top:40px;right:0;width:240px;max-height:180px;overflow:auto;pointer-events:auto;padding:7px;border:1px solid #344b56;border-radius:9px;background:#101a22;color:#e5eef2;box-shadow:0 8px 28px #000b}
        .option{display:block;width:100%;margin:2px 0;padding:9px;border:0;border-radius:6px;background:transparent;color:#e5eef2;text-align:left;font-size:12px;cursor:pointer}
        .option:hover{background:#203540}
        .unavailable{padding:8px;color:#a9bbc3;font-size:11px;line-height:1.4;overflow-wrap:anywhere}
      </style>
      <button class="button" type="button">↓ Fetchflow</button>
      <div class="menu" hidden></div>`;
    const item = { video, host, menu: shadow.querySelector(".menu"), options: videoOptions(video), menuOpen: false };
    shadow.querySelector(".button").addEventListener("click", event => {
      event.stopPropagation();
      if (item.menuOpen) {
        setMenuOpen(item, false);
        return;
      }
      openOptions(item);
    });
    shadow.addEventListener("click", event => event.stopPropagation());
    if (!document.documentElement) return;
    document.documentElement.append(host);
    videos.set(video, item);
    for (const name of ["loadedmetadata", "loadeddata", "play", "pause", "resize", "emptied", "durationchange", "loadstart"]) video.addEventListener(name, () => { item.options = videoOptions(video); maybePrefetchYoutube(item); schedulePosition(); }, { passive: true });
    updatePosition(item);
    maybePrefetchYoutube(item);
  }

  function updatePosition(item) {
    const video = item.video;
    if (!videoButtonsEnabled || !video.isConnected || document.visibilityState === "hidden") { item.host.style.display = "none"; return; }
    if (!(video.readyState > 0 || video.currentSrc || video.getAttribute("src") || video.getAttribute("data-src") || video.querySelector("source[src], source[data-src]"))) { item.host.style.display = "none"; return; }
    const rect = video.getBoundingClientRect();
    if (rect.width < 150 || rect.height < 90 || rect.bottom < 0 || rect.top > innerHeight || rect.right < 0 || rect.left > innerWidth) { item.host.style.display = "none"; return; }
    const left = Math.max(4, Math.min(innerWidth - 204, rect.right - 204));
    const top = Math.max(4, Math.min(innerHeight - 50, rect.top + 8));
    item.host.style.display = "block";
    item.host.style.left = `${left}px`;
    item.host.style.top = `${top}px`;
  }

  let positionFrame = 0;
  function schedulePosition() {
    if (positionFrame) return;
    positionFrame = requestAnimationFrame(() => {
      positionFrame = 0;
      for (const item of videos.values()) updatePosition(item);
    });
  }

  let scanTimer = 0;
  function scheduleScan() {
    if (scanTimer) return;
    scanTimer = setTimeout(() => {
      scanTimer = 0;
      for (const [video, item] of videos) {
        if (!video.isConnected) { item.host.remove(); videos.delete(video); }
      }
      for (const video of document.querySelectorAll("video")) {
        attach(video);
        const item = videos.get(video);
        if (item) item.options = videoOptions(video);
      }
      schedulePosition();
    }, 250);
  }

  function startObserver() {
    if (!document.documentElement) { setTimeout(startObserver, 0); return; }
    new MutationObserver(scheduleScan).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ["src", "data-src", "type", "data-quality", "data-res", "label", "height"] });
    scheduleScan();
  }

  document.addEventListener("DOMContentLoaded", scheduleScan, { once: true });
  window.addEventListener("load", scheduleScan, { once: true });
  window.addEventListener("scroll", schedulePosition, { passive: true });
  window.addEventListener("resize", schedulePosition, { passive: true });
  document.addEventListener("visibilitychange", schedulePosition);
  startObserver();
})();

