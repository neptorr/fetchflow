# Fetchflow Download Catcher

Chrome Manifest V3 extension for the Fetchflow Windows app.

- Routes ordinary Chrome downloads to Fetchflow when automatic routing is enabled.
- Intercepts clicks on recognized file links and links with a `download` attribute before Chrome starts its own download.
- Pauses Chrome’s transfer first so Chrome and Fetchflow do not download the same file at once.
- Starts the file in Fetchflow in the background, then shows a confirmation with the resolved filename and full save path, defaulting to Windows Downloads.
- **Keep downloading** continues in Fetchflow. **Cancel** stops and removes the Fetchflow transfer.
- Resumes Chrome’s original download if Fetchflow cannot accept the handoff.
- Lets you send a captured download to Fetchflow from the extension popup.
- Adds **Send link to Fetchflow** to the right-click menu for links.
- Places a Fetchflow button on HTML video players. It lists direct video sources and detected quality labels when the page exposes them; YouTube qualities load into the button and selection starts the chosen format in Fetchflow.
- Includes a popup setting to show or hide video buttons. Direct source and quality details are collected as the video loads, before the menu is opened.

## Install

1. Install Fetchflow Download Manager MSI version 1.3.30 or later. It registers Chrome’s native messaging connection and installs the extension files.
2. Open `chrome://extensions` in Chrome and enable **Developer mode**.
3. Choose **Load unpacked** and select `%LOCALAPPDATA%\Programs\Fetchflow\ChromeExtension`.
4. Remove any previously loaded Fetchflow Download Catcher copy first, then load the updated folder. Pin the extension if you want quick access to recent downloads.

To route downloads, leave **Automatically route Chrome downloads to Fetchflow** enabled in the extension popup. Recognized file links are handed to Fetchflow before Chrome starts a transfer. Downloads started by page scripts or links without a recognizable file type use Chrome’s pause-and-handoff fallback. Chrome asks for access to sites so the extension can see download-link clicks. To send a link manually, right-click it and choose **Send link to Fetchflow**, or use **Send to Fetchflow** for a recent download. Direct downloads that depend on browser login cookies may still need a direct link that Fetchflow can access itself.

On a video player, choose **↓ Fetchflow** to see its directly exposed file sources. Pages that expose only DRM-protected or segmented media may not provide a downloadable file through the player. On YouTube, the extension fetches the available video and audio formats ahead of selection; choose one to start it in Fetchflow.

Use **Show video download buttons** in the extension popup to enable or hide the player controls. The setting is saved by Chrome.

Captured URLs are stored locally in Chrome extension storage. The extension sends a URL to the desktop app only when you choose a send action. Direct downloads that depend on browser login cookies may still need a direct link that Fetchflow can access itself.

