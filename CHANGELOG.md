# Changelog

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
