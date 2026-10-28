# Fetchflow

Fetchflow is a Windows download manager for direct HTTP and HTTPS file links. Add links to LinkGrabber, inspect the files, then manage transfers from one queue.

The desktop app is a native Windows application. It does not need Node.js, a browser window, or a local server.

## Download

Get the latest installer from [Releases](https://github.com/neptorr/fetchflow/releases/latest). Run `Fetchflow-Setup.exe` to install for your Windows account. The installer adds a Start menu shortcut and an uninstall entry. You can also download `Fetchflow.exe` from the release for portable use.

## Features

- Inspect direct links before adding them, including the server-provided filename, size, host, and byte-range support.
- Queue multiple downloads, select several at once, and pause, resume, retry, or remove items.
- Resume partial downloads and use multiple connections when the server supports safe byte ranges.
- Replace an expired link and verify that it points to the same file before continuing a partial download.
- Set simultaneous-download, per-host, and speed limits. Snail mode caps the combined transfer speed at 10 or 50 KB/s.
- Keep downloads running when the window is closed; Fetchflow moves to the notification area. Use the tray menu to reopen or exit.
- Optionally start at Windows sign-in and resume the saved queue after a restart.
- View link-check diagnostics and test HTTPS connectivity from the Logs tab.
- Save downloads to a chosen folder and avoid overwriting existing files.

## Getting started

1. Install Fetchflow and open it from the Start menu.
2. Open **LinkGrabber**, paste one direct HTTP or HTTPS URL per line, and choose **Check links**.
3. Select the files you want and add them to the queue.
4. Choose **Start queue** when you’re ready.

To resume the queue automatically after signing in to Windows, enable **Start with Windows and resume queued downloads at sign-in** in Settings. This setting is off by default.

## Build from source

Run `build-windows.ps1` in PowerShell. It builds the native app and a per-user installer with the .NET Framework C# compiler available on Windows. To build an MSI, install WiX Toolset 3.14.1, extract its binaries to `work\tooling\wix314`, then run `packaging\Build-Msi.ps1`. The app stores its queue and preferences in `%LOCALAPPDATA%\Fetchflow\state.json` and keeps a last-good recovery copy at `state.json.bak`; uninstalling preserves this data.

The browser preview is separate from the Windows app. To run it from source, install Node.js 22 or later, run `npm start`, and open the local address printed in the terminal.

## Download limits

Fetchflow handles direct HTTP and HTTPS file URLs. Links that require a website login, a browser session, or a site-specific resolver may not be downloadable. Link inspection contacts the URL’s server when you choose **Check links**; transfers begin only when you start the queue.
