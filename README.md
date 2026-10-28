# Fetchflow Download Manager

Fetchflow is a native Windows download manager for direct HTTP and HTTPS links and public YouTube videos. It does not need a browser window or a local web server.

## Download

Download the Windows MSI installer from [GitHub Releases](https://github.com/neptorr/fetchflow/releases/latest). Run the MSI to install Fetchflow for your Windows account. The installer adds a Start menu shortcut and an uninstall entry.

## Features

- Check direct links before downloading, including the filename, size, host, and byte-range support reported by the server.
- Paste a public YouTube video URL in **LinkGrabber** to view available video resolutions and audio formats before adding a download to the queue.
- Choose a video or audio format, then add the selected item to the download queue. Audio can be saved as MP3 at the available bitrates.
- Queue multiple downloads and pause, resume, retry, or remove items. Resume partial direct-file downloads when the server supports byte ranges.
- Use multiple connections for direct-file downloads when the server supports safe byte ranges.
- Replace an expired direct link and verify that it points to the same file before continuing a partial download.
- Set simultaneous-download, per-host, and speed limits. Snail mode caps combined transfer speed at 10 or 50 KB/s.
- Keep downloads running in the notification area when the window is closed, and optionally resume the queue after signing in to Windows.
- Review link-check diagnostics and test HTTPS connectivity from the **Logs** tab.
- Save downloads to a chosen folder and avoid overwriting existing files.

YouTube format availability depends on the video. Fetchflow uses yt-dlp to inspect and download selected formats; it retrieves the official yt-dlp helper and verifies its checksum when needed. FFmpeg is included with the MSI for merging video and audio streams and converting audio to MP3.

## Getting started

1. Install Fetchflow from the MSI and open **Fetchflow Download Manager** from the Start menu.
2. Open **LinkGrabber** and paste one or more direct HTTP/HTTPS links or a public YouTube video URL.
3. Select **Check links**. For YouTube, wait for available formats to load and choose the video or audio quality you want.
4. Select the items to add to the queue, then choose **Start queue** when ready.

Settings and the download queue are saved on this device. To resume queued downloads automatically after signing in to Windows, enable **Start with Windows and resume queued downloads at sign-in** in Settings.

## License

Fetchflow is distributed under the [PolyForm Noncommercial License 1.0.0](LICENSE). It allows personal and other noncommercial use; commercial use is not permitted under its terms. Third-party components retain their own licenses; see the notices installed with Fetchflow.

## Download limits

Fetchflow supports direct HTTP/HTTPS file links and public YouTube videos. Links that require a website login, an authenticated browser session, or a site-specific resolver may not be downloadable. Link inspection contacts the URL’s server when you choose **Check links**; transfers begin only when you start the queue.

