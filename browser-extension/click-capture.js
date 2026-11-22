(() => {
  const downloadExtensions = new Set([
    "7z", "apk", "appx", "bin", "bz2", "cab", "cbr", "cbz", "deb", "dmg", "doc", "docx",
    "epub", "exe", "gz", "img", "iso", "jar", "msi", "msix", "mp3", "mp4", "mpeg", "mpg",
    "mov", "mkv", "ods", "odt", "ogg", "pdf", "pkg", "ppt", "pptx", "rar", "rpm", "tar",
    "tgz", "torrent", "wav", "webm", "wma", "wmv", "xls", "xlsx", "xz", "zip", "zst"
  ]);

  function findAnchor(event) {
    const path = typeof event.composedPath === "function" ? event.composedPath() : [];
    for (const node of path) {
      if (node && node.nodeType === Node.ELEMENT_NODE && node.tagName === "A") return node;
    }
    const target = event.target && event.target.nodeType === Node.ELEMENT_NODE ? event.target : event.target && event.target.parentElement;
    return target && typeof target.closest === "function" ? target.closest("a[href]") : null;
  }

  function isDownloadLink(anchor) {
    if (anchor.hasAttribute("download")) return true;
    try {
      const url = new URL(anchor.href, location.href);
      if (url.protocol !== "http:" && url.protocol !== "https:") return false;
      const lastPart = decodeURIComponent(url.pathname.split("/").pop() || "");
      const dot = lastPart.lastIndexOf(".");
      return dot > 0 && downloadExtensions.has(lastPart.slice(dot + 1).toLowerCase());
    } catch { return false; }
  }

  async function routeClick(anchor) {
    let result;
    try {
      result = await chrome.runtime.sendMessage({
        type: "fetchflow-intercept-click",
        url: anchor.href,
        name: anchor.getAttribute("download") || ""
      });
    } catch { result = null; }
    if (result && result.ok) return;
    // Preserve the browser's normal action if routing is disabled or Fetchflow is unavailable.
    location.assign(anchor.href);
  }

  document.addEventListener("click", event => {
    if (!event.isTrusted || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    const anchor = findAnchor(event);
    if (!anchor || !anchor.href || !isDownloadLink(anchor)) return;
    const target = (anchor.getAttribute("target") || "").toLowerCase();
    if (target && target !== "_self") return;
    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation();
    void routeClick(anchor);
  }, true);
})();

