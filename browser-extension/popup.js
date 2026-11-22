const list = document.getElementById("items");
const autoRoute = document.getElementById("auto-route");
const videoButtons = document.getElementById("video-buttons");
const empty = document.getElementById("empty");
const count = document.getElementById("count");
const notice = document.getElementById("notice");

function hostLabel(url) {
  try { return new URL(url).hostname; } catch { return "Browser download"; }
}

async function sendToFetchflow(item) {
  notice.textContent = "Waiting for your choice in Fetchflow…";
  const result = await chrome.runtime.sendMessage({ type: "fetchflow-send-item", id: item.id });
  notice.textContent = result && result.ok ? "Fetchflow accepted the download." : (result && result.error ? result.error : "Could not send this download to Fetchflow.");
  await refresh();
}

function render(items) {
  list.replaceChildren();
  const entries = Array.isArray(items) ? items : [];
  count.textContent = `${entries.length} recent download${entries.length === 1 ? "" : "s"}`;
  empty.hidden = entries.length !== 0;
  for (const item of entries) {
    const card = document.createElement("section");
    card.className = "download";
    const name = document.createElement("div");
    name.className = "name";
    name.textContent = item.name || "Download";
    name.title = item.name || "Download";
    const host = document.createElement("div");
    host.className = "host";
    host.textContent = `${hostLabel(item.url)} · ${item.routeStatus || item.state || "captured"}`;
    const actions = document.createElement("div");
    actions.className = "actions";
    const send = document.createElement("button");
    send.className = "send";
    send.type = "button";
    send.textContent = "Send to Fetchflow";
    send.addEventListener("click", () => { void sendToFetchflow(item); });
    const remove = document.createElement("button");
    remove.className = "remove";
    remove.type = "button";
    remove.textContent = "Remove";
    remove.addEventListener("click", async () => {
      await chrome.runtime.sendMessage({ type: "fetchflow-remove", id: item.id });
      await refresh();
    });
    actions.append(send, remove);
    card.append(name, host, actions);
    list.append(card);
  }
}

async function refresh() {
  const state = await chrome.runtime.sendMessage({ type: "fetchflow-list" });
  autoRoute.checked = state.autoRoute !== false;
  videoButtons.checked = state.videoButtonsEnabled !== false;
  render(state.capturedDownloads);
  if (state.lastError) notice.textContent = state.lastError;
}

autoRoute.addEventListener("change", async () => {
  await chrome.runtime.sendMessage({ type: "fetchflow-settings", autoRoute: autoRoute.checked });
  notice.textContent = autoRoute.checked ? "New Chrome downloads will open a confirmation in Fetchflow." : "Automatic routing is off. Chrome downloads will stay in Chrome unless you send one here.";
});

videoButtons.addEventListener("change", async () => {
  await chrome.runtime.sendMessage({ type: "fetchflow-settings", videoButtonsEnabled: videoButtons.checked });
  notice.textContent = videoButtons.checked ? "Video download buttons are enabled on supported players." : "Video download buttons are hidden on webpages.";
});

document.getElementById("clear").addEventListener("click", async () => {
  await chrome.runtime.sendMessage({ type: "fetchflow-clear" });
  await refresh();
});

void refresh();

