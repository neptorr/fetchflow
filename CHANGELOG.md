# Changelog

## 1.3.30

- Added the Chrome download catcher and video quality controls, including preloaded YouTube formats.
- Made the video control menu toggle closed from its button and removed the floating status card.
- Improved YouTube filename sanitizing and started the queue after adding selected LinkGrabber items.
- Made the video button more transparent and hardened MSI packaging against a locked, already-correct FFmpeg payload.

## 1.3.3

- Moved the YouTube format picker next to the filename so it remains visible in LinkGrabber.
- Kept direct file links labeled as direct downloads and made YouTube format inspection explicit in the row.

## 1.3.2

- Restored the standard WiX Minimal UI after the custom MSI dialog flow caused setup errors.

## 1.3.1

- Removed WiX's default placeholder EULA from the MSI setup flow; Fetchflow now shows a simple welcome screen without presenting placeholder legal terms.

## 1.3.0

- Added YouTube format inspection to LinkGrabber with selectable video qualities and available audio-only formats.
- Added MP3 quality choices when FFmpeg is installed; other supported audio formats download without conversion.
- Added resumable YouTube transfers through the official yt-dlp helper, downloaded on first use and verified against the release SHA-256 list.

## 1.2.2

- Added a 45-second response and no-data timeout so stalled transfers retry instead of remaining at zero speed indefinitely.
- Logged when the window is hidden to and restored from the notification area; hiding the window does not cancel downloads.

## 1.2.1

- Held new queued downloads while an interrupted file receives up to three automatic retries.
- Continued to the next queued file when retries are exhausted or the failure cannot be resumed safely.

## 1.2.0

- Added automatic retries for transient network failures and HTTP 408, 425, 429, and 5xx responses, with backoff capped at 60 seconds.
- Kept partial download data between attempts and added pause support for scheduled retries.
- Kept permanent HTTP errors such as expired or forbidden links from retrying indefinitely.
- Prioritized retrying downloads ahead of waiting queue items and added per-file live speed.
- Added successful-install completion screens with a checked-by-default launch option to both Windows installers.

## 1.1.1

- Fixed resumable downloads rejecting valid byte-range responses when the server omits the optional total size.
- Fixed resume handling when a server returns more bytes than the requested range by reading only the requested segment.
- Added precise resume diagnostics for HTTP status, returned byte range, and saved file size.

## 1.1.0

- Added a Logs tab with link-check details, diagnostic export, and an HTTPS internet check.
- Added a range-request fallback for hosts that do not answer HEAD requests promptly.
- Added per-request timeout details and DNS/TCP diagnostics for connectivity failures.
- Added last-good backup recovery for saved settings and the download queue.
- Prevented multiple app instances from opening for the same Windows account.
- Corrected live speed reporting to aggregate bytes over each UI sampling interval across all active connections.

## 1.0.0

- Added the native Windows app and per-user installer.
- Added persistent download queues, resumable transfers, and multiple connections per file when supported by the server.
- Added LinkGrabber inspection, batch selection, queue controls, and per-download actions.
- Added speed limits and Snail mode.
- Added optional resume-at-sign-in and notification-area behavior.

