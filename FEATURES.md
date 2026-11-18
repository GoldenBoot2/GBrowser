# GBrowser feature checklist

What a current Chrome / Edge (2026) does, and where GBrowser stands. Kept up to date with every change.
Status: **Done** · **Missing** (to build) · **Engine UI** (works, but shows the engine's Microsoft Edge interface: to replace) · **Not possible** (with reason).
Last full audit: 19-11-2026.

## Tabs and windows
| Feature | Status |
|---|---|
| Tabs: new, close, reorder by drag, middle-click close, Ctrl+Tab / Ctrl+1..9 | Done |
| Reopen closed tab (Ctrl+Shift+T), restore tabs on start | Done |
| Tab search (Ctrl+Shift+A) | Done |
| Sleeping / unloaded background tabs, keep sites active | Done |
| Private tabs (Ctrl+Shift+N) | Done |
| Tab right-click menu (new tab to the right, reload, duplicate, pin, mute, close, close others / to the right) | Done (19-11-2026; "move to new window" comes with windows) |
| Pinned tabs (icon only, kept at the left, restored after restart) | Done (19-11-2026) |
| Mute tab, click the speaker icon to mute / unmute | Done (19-11-2026) |
| Tab groups | Missing |
| New window (Ctrl+N), drag a tab out into its own window, multiple windows | Missing |
| Crashed tab page with Reload (keeps address and title), "Page unresponsive" Wait / Exit page, restart with tabs restored when the engine stops | Done (19-11-2026) |
| Tab hover card (title + site) | Missing |
| Tabs not loaded yet show the site's remembered icon | Done (19-11-2026) |

## Address bar (omnibox)
| Feature | Status |
|---|---|
| Type URL or search, Enter, Alt+Enter new tab | Done (Alt+Enter: to verify) |
| Short URL when not focused, full when focused | Done |
| Suggestions while typing (history, bookmarks, open tabs, search suggestions), inline autocomplete, Shift+Delete removes | Done (19-11-2026; setting to turn search suggestions off) |
| Search engines: choose default | Done; custom engines / keywords: Missing |
| Site info panel (padlock: connection, certificate, permissions, cookies, site settings) | Missing |
| Zoom indicator in the address bar (click to reset) | Missing |
| Bookmark star | Done |
| Copy link / QR code / share page | Missing |

## Page and content
| Feature | Status |
|---|---|
| Zoom (Ctrl +/-/0, wheel), remembered per site | Done (19-11-2026) |
| Default page zoom setting | Missing |
| Find in page (Ctrl+F, F3) | Engine UI |
| Page right-click menu (page, link, image, media, selection, text field) | Done (19-11-2026: GBrowser Material menu, engine commands behind it) |
| Print (Ctrl+P) | Engine UI (print preview) |
| Save page as (Ctrl+S), view source (Ctrl+U) | Missing / to verify |
| PDF viewer | Engine UI (to verify branding) |
| JavaScript alert / confirm / prompt dialogs | Engine UI |
| Login dialog (HTTP basic auth) | Engine UI |
| Client certificate selection | Engine UI |
| "Open this app?" for mailto:, ms-settings:, zoommtg: ... | Engine UI |
| Full-screen video (F11 / page full screen) | Done (page full screen: to verify) |
| Picture-in-picture | Engine (to verify) |
| Error pages (no network, DNS, certificate) | Done (GBrowser pages) |
| Status bubble on link hover | Done |
| Popup blocking | Done |
| Drag a link / file into the window | To verify |

## Downloads
| Feature | Status |
|---|---|
| Download button + progress ring, downloads page | Done |
| Pause / resume / cancel / retry | Done |
| Multi-part downloads, speed limit, open when done | Done |
| Location, ask where to save, file-exists choice | Done |
| SmartScreen: block shown in GBrowser, Download anyway | Done (19-11-2026) |
| Show downloads when a download starts (bubble) | Missing (button only) |
| Warn when closing with downloads running | Missing |
| "Always open files of this type" | Missing |

## Ad blocking (AdGuard / uBlock level control)
| Feature | Status |
|---|---|
| Built-in lists (AdGuard, EasyList, EasyPrivacy, uBlock, HaGeZi, OISD...), groups ads / annoyances / trackers | Done |
| Turn each built-in list on / off | Missing (user, 19-11-2026) |
| Custom filter lists by address, with a trust check like AdGuard's (known / trusted list sources vs unknown; unknown lists cannot use risky rule types such as scriptlets / $redirect) and update interval | Missing (user, 19-11-2026) |
| User rules editor (block / allow / cosmetic rules, syntax check) | Missing (user, 19-11-2026) |
| Element picker: hide an element on a page by pointing at it (creates a user rule) | Missing (user, 19-11-2026) |
| Per-site switch, stealth mode, blocked count | Done |

## Privacy and security
| Feature | Status |
|---|---|
| Ad and tracker blocking, per-site switch | Done |
| HTTPS-First, Secure DNS, SmartScreen, tracking prevention, Do Not Track | Done |
| Encrypted local data, clear data on close, clear browsing data | Done (time ranges: Missing) |
| Logins kept after restart (session cookies) | Done (19-11-2026) |
| Site permission prompts (camera, mic, location, notifications, clipboard, downloads, MIDI, sensors...) in GBrowser's own prompt, remembered choices, Site settings page with reset | Done (19-11-2026) |
| Per-site toggles not tied to a request (JavaScript, images, pop-ups, sound) and a camera / mic "in use" indicator | Missing |
| Web notifications shown by GBrowser (instead of the engine's toast) | To verify |
| Cookies and site data page (see / delete per site) | Missing |
| Password manager (save prompt, list, edit, delete, export) | Engine UI (save prompt) / list Missing |
| Autofill addresses / payment | Engine UI |
| Certificate viewer | Missing |
| Password leak warnings | Not possible yet (needs a breach service; ask first) |

## Data and settings
| Feature | Status |
|---|---|
| Bookmarks: bar, page, edit, delete | Done |
| Bookmark folders, bookmark manager, import / export (HTML) | Missing |
| Bookmarks bar: right-click menu (open, open in new tab / private tab, edit, delete, add page / folder), drag to reorder, drop a link or the address-bar icon onto it | Missing (user, 19-11-2026) |
| Bookmarks page: edit, delete, drag to reorder | Partly (edit / delete exist; reorder Missing) (user, 19-11-2026) |
| Empty bookmarks bar handled like Chrome (hint "For quick access, place your bookmarks here on the bookmarks bar", "Import bookmarks now" link) | Missing (user, 19-11-2026) |
| History page with search, delete | Done |
| Import from Chrome / Edge (bookmarks, history; passwords if possible) | Missing |
| Startup: continue where you left off / new tab / specific pages | Partly (restore on/off) |
| Home button / home page | Done |
| Appearance: theme, colours, light / dark | Done |
| Fonts and font size settings | Missing |
| Languages, spell check | Missing (no spelling suggestions offered in text fields: to investigate) |
| Default browser (register with Windows, open links from other apps) | Missing |
| Profiles (separate people) | Missing |
| Sync | Not planned (no account service) |
| Keyboard shortcuts as in Chrome (full list) | Partly |
| Task manager (Shift+Esc) | Missing |
| Extensions (Chrome Web Store) | Done |
| DevTools docked / separate | Done (19-11-2026) |
| Updates from GitHub, About page | Done |
| Installer, uninstall | Done |
