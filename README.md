# GBrowser

A light web browser for Windows 10 and 11, built on Microsoft Edge WebView2.

Developer: **GoldenBoot**

## Features

- Tabs: drag to reorder, middle-click to close, reopen closed tabs, tabs restored on start (loaded only when opened)
- Sleeping tabs: background tabs use less memory and are suspended after a set time
- Built-in ad and tracker blocking: over a million rules from AdGuard, EasyList, EasyPrivacy, uBlock, HaGeZi, OISD and more
  - blocked count on every tab and in the toolbar
  - per-site switch, annoyances, trackers (also tracking codes in links), element hiding, stealth mode, block page for ad sites
  - scriptlets, extended CSS and redirect stubs, the same engine as the WebView Adblocker Android app
- Chrome extensions: add from the Chrome Web Store (link or ID, or the "Add to GBrowser" button on a store page) or load unpacked
- Bookmarks (and bookmarks bar), history with search, downloads, find, print, zoom, full screen, developer tools
- Settings: search engine, home page, memory, ad blocking, Do Not Track, clear browsing data

## Build

Requirements: .NET SDK 8 or newer (builds a .NET Framework 4.8 app, which is part of Windows), Python 3, Node.js.

```
python tools/build_filters.py
dotnet build -c Release -o out/GBrowser
```

`tools/build_filters.py` downloads the filter lists and compiles them into `filters/`. GitHub Actions builds every push
(`.github/workflows/build.yml`) and uploads the browser as a zip.

The WebView2 Runtime comes with Windows 11 and current Windows 10.
