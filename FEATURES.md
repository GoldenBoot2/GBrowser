# GBrowser feature checklist

What a current Chrome / Edge (2026) does, and where GBrowser stands. Kept up to date with every change.
Status: **Done** · **Missing** (to build) · **Engine UI** (works, but shows the engine's Microsoft Edge interface: to replace) · **Not possible** (with reason).
Last full audit: 19-11-2026.

## Pending (build order, 19-11-2026)
1. #7 New window (Ctrl+N, drag a tab out), set as default browser.
2. #8 Import from Chrome / Edge, cookies and site data, passwords page, bookmark folders.
3. Bookmarks (user, 19-11-2026): bar right-click menu (open, new tab, private, edit, delete, add), drag to reorder, drop links on the bar, reorder on the bookmarks page, empty-bar hint like Chrome.
4. Ad blocking (user, 19-11-2026): built-in lists on / off, custom lists with trust check, user rules editor, element picker.
5. Cookie manager, 19 features (user, 19-11-2026): see "Cookie manager" below.
6. Android app features missing here (user, 19-11-2026): see "From the Android app" below.
7. Remaining Missing rows in the tables below.

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
| Site info panel (icon left of the address: connection, permissions with switches + reset, cookies count + delete site data, ad blocking on the site, site settings) | Done (19-11-2026); certificate viewer missing |
| Zoom indicator in the address bar (click to reset) | Missing |
| Bookmark star | Done |
| Copy link / QR code / share page | Missing |

## Page and content
| Feature | Status |
|---|---|
| Zoom (Ctrl +/-/0, wheel), remembered per site | Done (19-11-2026) |
| Default page zoom setting | Missing |
| Find in page (Ctrl+F, F3, Ctrl+G, Shift+F3, Enter / Shift+Enter, Esc; per-tab, match count) | Done, GBrowser find bar (19-11-2026) |
| Page right-click menu (page, link, image, media, selection, text field) | Done (19-11-2026: GBrowser Material menu, engine commands behind it) |
| Print (Ctrl+P) | Engine UI (print preview) |
| Save page as (Ctrl+S), view source (Ctrl+U) | Missing / to verify |
| PDF viewer | Engine UI (to verify branding) |
| JavaScript alert / confirm / prompt dialogs, "Leave site?" | Done, GBrowser dialogs (19-11-2026) |
| Login dialog (HTTP basic auth), password hidden | Done, GBrowser "Sign in" (19-11-2026) |
| Client certificate selection | Engine UI |
| "Open this app?" for mailto:, ms-settings:, zoommtg: ... with "Always allow" (secure sites) | Done (19-11-2026); remembered choices not yet listed in Site settings |
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
| Cookies and site data page (see / delete per site) | Site delete Done in site info (19-11-2026); full page: see Cookie manager |
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

## Cookie manager (planned 19-11-2026, user: "can we edit cookies values, block specific values, remove" + "add more features")
Researched against Chrome DevTools Application > Cookies, Cookie-Editor, Cookie Editor Pro, EditThisCookie, Cookie AutoDelete.
Engine route: CoreWebView2CookieManager + DevTools protocol (Storage.getCookies / Network.setCookie for partition key, priority, size).

| # | Feature | Status |
|---|---|---|
| 1 | Site cookies page from site info; "All cookies" manager in Settings (every site, counts, size) | Planned |
| 2 | Fields: name, value, domain, path, expiry / session, size, HttpOnly, Secure, SameSite, partitioned (CHIPS), priority, first / third party | Planned |
| 3 | Search name / value / domain; filters: third-party, session, trackers, expiring soon | Planned |
| 4 | Readable values: URL-decoded, Base64, JSON, JWT (payload + expiry), timestamps as dd-mm-yyyy | Planned |
| 5 | Tracker label (cookie domain on tracker lists; known names like _ga, _fbp explained) | Planned |
| 6 | Edit every field (date picker for expiry); checks: SameSite=None needs Secure, __Host- / __Secure- rules | Planned |
| 7 | Add a new cookie; duplicate a cookie | Planned |
| 8 | Delete one / selected / all of the site / third-party only; Undo after delete or edit | Planned |
| 9 | Block a cookie (this site / everywhere): page-script layer + Set-Cookie removed from responses | Planned |
| 10 | Per-site cookie rule: Allow / Block all / Clear when closed (Chrome site settings) | Planned |
| 11 | Block third-party cookies setting | Planned (engine support to verify) |
| 12 | Protect (lock) a cookie: site changes are undone, never removed by clean-ups | Planned |
| 13 | Auto-delete (Cookie AutoDelete style): site cookies removed after its last tab closes; keep list + keep-until-restart list | Planned |
| 14 | Lifetime cap: long-lived cookies shortened (e.g. 7 days) except kept sites | Planned |
| 15 | Export / import: JSON (Cookie-Editor format), Netscape cookies.txt, Cookie header; import shows a preview first | Planned |
| 16 | Copy value / copy as Cookie header | Planned |
| 17 | Live log: cookies set / changed / blocked on this page, by server or script | Planned |
| 18 | Snapshots: save a site's cookies under a name, restore later (switch accounts) | Planned |
| 19 | Other site data: local / session storage, IndexedDB, caches: view sizes, delete | Planned |

## From the Android app (WebView Adblocker 2.9.4; user 19-11-2026: "we added some stuffs in the android browser are missing in this pc browser")
| Feature | Status |
|---|---|
| Hide my identity (decided by the user 19-11-2026: stay a genuine Microsoft Edge, but change everything a site uses to recognise and block this browser, like the Android app's Hide my identity + Stealth). Each identity = a different but genuine-looking Edge on Windows: values measured from real Edge / Chrome on this PC, varied only within real ranges, consistent with each other (UA = client hints = platform version; GPU string matches the GPU vendor; cores / memory / screen plausible for a laptop). Covers: canvas, WebGL (vendor / renderer + noise), audio, fonts list, screen and window sizes, hardwareConcurrency, deviceMemory, media device IDs, client hints (model, platformVersion), plugins, battery, WebRTC local addresses, storage IDs. Not changeable without approval: the IP address (no VPN / proxy). | Missing (spec agreed) |
| New identity: new fingerprint seed + clears what sites store to recognise the browser (cookies, local / session storage, IndexedDB, caches, service workers, HSTS / favicon / HTTP caches, ETags); "Also sign out of all sites"; new identity each start; shows the identity ID | Missing |
| Canvas noise (per identity, invisible, passes canvas consistency checks) | Missing |
| Identity: more signals (user 19-11-2026: "none of them have to identify us a previous user or the blocked user"): WebGPU adapter info, fonts by text measuring (measureText / DOMRect), emoji / text rendering, speech voices, keyboard layout (getLayoutMap), media codecs / capabilities, storage quota (navigator.storage.estimate reveals the disk size), performance.memory, network info, screen colour depth / gamut / HDR, touch / pointer, gamepads, Bluetooth / USB / HID availability, Intl / locale details, math precision | Missing |
| Identity: hidden "supercookies" cleared on New identity: HSTS pins, favicon cache, HTTP cache + ETag / Last-Modified, TLS session tickets and open connections (HTTP/2, QUIC), DNS cache, service-worker and push subscriptions, Private State Tokens, Shared Storage, Topics / Protected Audience / Attribution data, window.name, remembered permission grants, WebAuthn hints | Missing |
| Identity: each site sees a different identity (per-site fingerprint seed and storage keys), so two sites cannot link the visits and a site cannot match us across its own domains | Missing |
| Identity: blend into the crowd: values chosen from the most common real Edge-on-Windows set (fewer unique values), never random rare values; values stay fixed for the whole identity (a fingerprint that changes every page load is itself a flag) | Missing |
| Identity: matches the network: time zone, language and Accept-Language consistent with the IP's country (as Android NetGeo); no rare headers that make us unique (DNT / GPC only when the user turns them on) | Missing |
| Identity: block known fingerprinting / re-identification scripts and services (FingerprintJS and similar lists, CNAME-cloaked trackers, bot-detection beacons that only collect device data) | Missing |
| Identity: referrer trimmed to the site's origin across sites; tracking parameters removed from links (exists for trackers: extend to every identity-linking ID such as gclid, fbclid, msclkid, _gl) | Partly (tracker setting) |
| Identity: "Fresh start for a site": new identity and data for one site only (e.g. a site that blocked us), leaving other sites signed in | Missing |
| Identity: UNDETECTABLE (user 19-11-2026: "and should be undetectable"). A site must not be able to tell that anything is masked. Rules: (1) change values inside the engine wherever it allows it (DevTools Emulation overrides: user agent + client hints, hardware concurrency, screen / device metrics, time zone, locale, media features; to verify per method), so pages see native values with no script patches; (2) where a script patch is unavoidable: native-looking functions (toString, name, length, property descriptors and order, prototype chain, Error stack traces), patched before any page script in every realm (main page, iframes incl. about:blank / srcdoc, workers, shared and service workers), same values everywhere; (3) every surface tells the same story: JS values = HTTP headers = CSS media queries = worker values; (4) noise is stable for the identity (same canvas / audio read twice gives the same result, toDataURL matches getImageData, WebGL readPixels matches), never a pattern a detector can spot; (5) no timing differences a site can measure; (6) checked before release against public detectors (CreepJS, BrowserScan, Pixelscan, FingerprintJS / fingerprint.com demo, BrowserLeaks, AmIUnique, the Android test set): no "lies", "tampering" or "inconsistent" flags, and real Edge with masking off gives the same verdicts | Missing (requirement for every identity row) |
| Identity check page: shows what sites can see (each signal, current identity ID), compares with the last identity to prove everything changed, with links to public test sites | Missing |
| Identity: not changeable without the user's approval: the IP address (needs a VPN / proxy) and the TLS / HTTP network fingerprint (it is the genuine Edge one, shared by millions); behaviour (typing / mouse rhythm, visit times) and signing in to an old account still re-identify: shown as notes in the identity check page | Note |

| Masked cookie-consent records (OneTrust / Cookiebot) | Missing |
| IP-lookup masking | Missing (no VPN / proxy without approval) |
| Block tracker pages (strict) | Missing (compare with "Block pages of ad sites") |
| Per-page request log: Blocked / Allowed / Cookies | Missing |
| Ad-block Tests page (blocking + detection tests) | Missing |
| Stats: rules, filter lists, blocked total with Reset | Missing (total shown in shield menu only) |
| Daily host-list update + "Update host lists" button | Missing |
| Element hiding switch | To verify ("Hide ad leftovers" may be the same) |

## Undetectable page scripts (user 19-11-2026: "make all our scripts non detectable by any sites but it should work on those sites without compromising our protections")
| Item | Status |
|---|---|
| Cloudflare managed challenge (visas-de.tlscontact.com) failed with Stealth on: getComputedStyle wrapper ran the hidden-by-us check on every call (507 us vs 1.6 us) | Fixed ba7f977 (passes with all protections on) |
| Errors thrown through wrappers showed our stack frame (e.g. "at get offsetHeight (<anonymous>:2095:17)") | Fixed ba7f977 (nat(): browser-identical stack, also for Error.prepareStackTrace) |
| Symbol(gbab) marker visible in Object.getOwnPropertySymbols(window) | Fixed ba7f977 |
| Reading styles / sizes of an ad we hid costs up to 26 ms (all our style sheets switched off and on): timing trace on bait elements | Open (per-element override or user-origin CSS) |
| Cosmetic hiding adds <style> elements a page can list (document.styleSheets); $csp rules add <meta> elements | Open (needs user-origin CSS, e.g. a built-in extension's insertCSS; engine support to verify) |
| getComputedStyle result for an ad we hid is a Proxy whose methods are not the browser's own functions | Open |
| Privacy-mode wrappers (methodOn, cookie masking) not yet on nat(); fixed values must still run the browser's receiver checks | Open (with Hide my identity) |
| AdGuard scriptlet library wrappers: audit with the same probes | Open |
| Repeatable check: tools/local/stealthprobe.js (on vs off diff), cftest.ps1 (Cloudflare), public detectors | In use |

VPN test (19-11-2026, VeePN free extension, United Kingdom / London): the extension works in GBrowser (pages saw 51.195.242.237, OVH, AS16276), but TLScontact answers Cloudflare Error 1005: the site owner has banned that whole network (ASN) by IP, before any browser check. Not fixable in the browser: needs a VPN / proxy on a residential or non-banned network.
