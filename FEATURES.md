# GBrowser feature checklist

What a current Chrome / Edge (2026) does, and where GBrowser stands. Kept up to date with every change.
Status: **Done** · **Missing** (to build) · **Engine UI** (works, but shows the engine's Microsoft Edge interface: to replace) · **Not possible** (with reason).
Last full audit: 19-11-2026.

## Pending (build order, 19-11-2026)
0. Undetectable page scripts, open part (user, 19-11-2026; in progress): AdGuard scriptlet audit (done 19-11-2026); same-origin navigated frames (site rules done 19-11-2026); ExtendedCss inline styles; $csp <meta> (with the cookie manager's response-header editing); Stealth read cost on heavy pages (25-28 ms); privacy wrappers (with Hide my identity). See "Undetectable page scripts" below.
1. #7 New window (Ctrl+N, drag a tab out), set as default browser (done 19-11-2026; drag with the real mouse to be tried by the user).
2. #8 Import from Chrome / Edge (bookmarks done 19-11-2026), cookies and site data (with the cookie manager, 5), passwords page, bookmark folders (done 19-11-2026).
3. Bookmarks (user, 19-11-2026; done 19-11-2026): bar right-click menu (open, new tab, private, edit, delete, add), drag to reorder, drop links on the bar, reorder on the bookmarks page, empty-bar hint like Chrome.
4. Ad blocking (user, 19-11-2026): done 19-11-2026 (lists on / off, custom lists with trust check, your rules, element picker).
5. Cookie manager, 19 features (user, 19-11-2026; done 19-11-2026): see "Cookie manager" below.
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
| Tab right-click menu (new tab to the right, reload, duplicate, pin, mute, move to new window / to another window, close, close others / to the right) | Done (19-11-2026) |
| Pinned tabs (icon only, kept at the left, restored after restart) | Done (19-11-2026) |
| Mute tab, click the speaker icon to mute / unmute | Done (19-11-2026) |
| Tab groups | Missing |
| New window (Ctrl+N), close window (Ctrl+Shift+W), Exit, multiple windows in one process (one engine) | Done 19-11-2026: new windows open offset from the last one; Exit keeps every window's tabs, closing windows one by one keeps the last one's (like Chrome); restart brings all windows back |
| Move a tab between windows (it keeps its page and state), drag a tab out of the tab row into its own window (follows the mouse), drop it on another window's tab row | Done 19-11-2026 (move / merge tested with the page state kept; the mouse drag itself not tried by me: needs the real mouse) |
| One running GBrowser: a second start hands its addresses (links, files) to the running one as tabs; no address = a new window | Done 19-11-2026 |
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
| Bookmark star / Ctrl+D: "Bookmark added" with name, address, folder, Remove (Chrome's bubble) | Done 19-11-2026 |
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
| Turn each built-in list on / off | Done 19-11-2026: every compiled rule keeps the lists it comes from (bit mask; one rule in several lists is still one rule), Settings > Filter lists has a switch per list. Regression: with all lists on, 207,612 network answers and all cosmetic / scriptlet / JS / link-cleaning / response answers identical to the old engine; with only some groups on, the new engine also blocks the 2,753 corpus requests that a rule shared by two groups covers (the old build dropped such rules from the second group). Memory and load time unchanged (heap 11.1 vs 10.5 MB, 179 vs 188 ms) |
| Custom filter lists by address, with a trust check like AdGuard's (known / trusted list sources vs unknown; unknown lists cannot use risky rule types such as scriptlets / $redirect) and update interval | Done 19-11-2026: Settings > Custom filter lists (add by address, on / off, remove, update all); lists from known makers (AdGuard, EasyList, uBlock Origin, HaGeZi, OISD, ...) are trusted, others are not until you trust them: no trusted-* scriptlets, no CSS rules (tested: 2 of 4 rules taken untrusted, 4 trusted); update interval from the list's "! Expires" (else a day), checked hourly; plain host rules stored as hashes (HaGeZi Light, 91,291 rules: +0.7 MB) |
| User rules editor (block / allow / cosmetic rules, syntax check) | Done 19-11-2026: Settings > Your rules: ||host^ / patterns / /regex/ / @@ exceptions with $third-party, $domain=, $important, types, $redirect; ##, #@#, extended CSS, #$# CSS, scriptlets (AdGuard and uBO syntax); lines that cannot be used are listed with the reason. Your rules and custom lists decide before the built-in lists (an allow rule allows, a block rule blocks) |
| Element picker: hide an element on a page by pointing at it (creates a user rule) | Done 19-11-2026: shield menu > Hide an element on this page, or right-click > Hide this element...; runs in an isolated world the page cannot reach, highlights the element, Wider / Narrower, editable selector with match count; the rule goes to Your rules and the element hides at once |
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
| Cookies and site data page (see / delete per site) | Done 19-11-2026 (gbrowser://cookies, see Cookie manager) |
| Password manager (save prompt, list, edit, delete, export) | Engine UI (save prompt) / list Missing |
| Autofill addresses / payment | Engine UI |
| Certificate viewer | Missing |
| Password leak warnings | Not possible yet (needs a breach service; ask first) |

## Data and settings
| Feature | Status |
|---|---|
| Bookmarks: bar, page, edit, delete | Done |
| Bookmark folders, bookmark manager, import / export (HTML) | Done 19-11-2026: Bookmarks bar / Other bookmarks with folders (old bookmarks moved to the bar), manager page with folder tree, search, add page / folder, edit, delete, move, drag to reorder or into a folder; HTML import (any browser's export, tested) and export (Netscape format, tested) |
| Bookmarks bar: right-click menu (open, open in new tab / private tab, edit, delete, add page / folder), drag to reorder, drop a link or the address-bar icon onto it | Done 19-11-2026: folders open as menus (Open all), overflow menu, Other bookmarks at the right, right-click menu, drag to reorder / onto a folder, dropped links become bookmarks (tested through the bar's own drop code; real mouse drags to be tried by the user) |
| Bookmarks page: edit, delete, drag to reorder | Done 19-11-2026 (page drag uses the same move command that was tested; the mouse drag itself not tried by me) |
| Empty bookmarks bar handled like Chrome (hint "For quick access, place your bookmarks here on the bookmarks bar", "Import bookmarks now" link) | Done 19-11-2026 |
| History page with search, delete | Done |
| Import from Chrome / Edge (bookmarks, history; passwords if possible) | Bookmarks Done 19-11-2026 (Chrome, Edge, Brave profiles on this PC; the reader was tested with a sample file, not with your own profiles). History: Missing (Chrome keeps it in an SQLite database; needs a reader). Passwords: not possible (Chrome 127+ encrypts them with app-bound keys only Chrome can read) |
| Startup: continue where you left off / new tab / specific pages | Partly (restore on/off) |
| Home button / home page | Done |
| Appearance: theme, colours, light / dark | Done |
| Fonts and font size settings | Missing |
| Languages, spell check | Missing (no spelling suggestions offered in text fields: to investigate) |
| Default browser (register with Windows, open links from other apps) | Done 19-11-2026: registers like Chrome for the current user (StartMenuInternet, Capabilities, RegisteredApplications, ProgIds for http/https and .htm/.html/.svg/.webp/.pdf; installer registers, uninstall removes), Settings > Default browser > Make default opens Windows' Default apps on GBrowser's page (Windows lets only the user choose); links and files from other apps open as tabs in the running window |
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
| 1 | Site cookies page from site info; "All cookies" manager in Settings (every site, counts, size) | Done 19-11-2026: gbrowser://cookies (Settings > Privacy, site info > Cookies and site data opens the site) |
| 2 | Fields: name, value, domain, path, expiry / session, size, HttpOnly, Secure, SameSite, partitioned (CHIPS), priority, first / third party | Done (all fields from the engine's DevTools cookie store, incl. partition and priority) |
| 3 | Search name / value / domain; filters: third-party, session, trackers, expiring soon | Done (search; filters: trackers, session, expiring within 7 days, partitioned; third-party count per site) |
| 4 | Readable values: URL-decoded, Base64, JSON, JWT (payload + expiry), timestamps as dd-mm-yyyy | Done (URL-decoded, Base64, JSON, JWT payload + expiry, 10/13-digit times as dd-mm-yyyy) |
| 5 | Tracker label (cookie domain on tracker lists; known names like _ga, _fbp explained) | Done (cookie domain on GBrowser's ad / tracker lists, or a known tracking name; ~25 well-known names explained) |
| 6 | Edit every field (date picker for expiry); checks: SameSite=None needs Secure, __Host- / __Secure- rules | Done (date and time picker; SameSite=None needs Secure, __Host- / __Secure- checked on the page and in GBrowser) |
| 7 | Add a new cookie; duplicate a cookie | Done |
| 8 | Delete one / selected / all of the site / third-party only; Undo after delete or edit | Done (Undo after delete and edit; delete the shown cookies of a site / filter) |
| 9 | Block a cookie (this site / everywhere): page-script layer + Set-Cookie removed from responses | Done: page scripts can neither set nor read a blocked cookie (tested), server-set ones are removed right after the response (tested) |
| 10 | Per-site cookie rule: Allow / Block all / Clear when closed (Chrome site settings) | Done (Allow / Block all / Clear when closed, tested) |
| 11 | Block third-party cookies setting | Done: the engine's own setting (profile.cookie_controls_mode, applied at start; tested on whatismybrowser.com: Yes off / No on) |
| 12 | Protect (lock) a cookie: site changes are undone, never removed by clean-ups | Done (restored when changed or removed, tested) |
| 13 | Auto-delete (Cookie AutoDelete style): site cookies removed after its last tab closes; keep list + keep-until-restart list | Done (15 s after a site's last tab closes or leaves the site; keep list; tested with Clear when closed) |
| 14 | Lifetime cap: long-lived cookies shortened (e.g. 7 days) except kept sites | Done (1 day ... 1 year; allowed and protected cookies exempt; tested) |
| 15 | Export / import: JSON (Cookie-Editor format), Netscape cookies.txt, Cookie header; import shows a preview first | Done (JSON Cookie-Editor format, Netscape cookies.txt, Cookie header; import shows a preview; parsers tested) |
| 16 | Copy value / copy as Cookie header | Done |
| 17 | Live log: cookies set / changed / blocked on this page, by server or script | Done in part: added / changed / removed cookies of the open site while the page is open (from the cookie store every 3 s); whether a server or a script set it is not shown |
| 18 | Snapshots: save a site's cookies under a name, restore later (switch accounts) | Done (save / restore / delete per site, encrypted file; tested) |
| 19 | Other site data: local / session storage, IndexedDB, caches: view sizes, delete | Done (sizes per storage type from the engine, delete per origin) |

## From the Android app (WebView Adblocker 2.9.4; user 19-11-2026: "we added some stuffs in the android browser are missing in this pc browser")
| Feature | Status |
|---|---|
| Hide my identity (decided by the user 19-11-2026: stay a genuine Microsoft Edge, but change everything a site uses to recognise and block this browser, like the Android app's Hide my identity + Stealth). Each identity = a different but genuine-looking Edge on Windows: values measured from real Edge / Chrome on this PC, varied only within real ranges, consistent with each other (UA = client hints = platform version; GPU string matches the GPU vendor; cores / memory / screen plausible for a laptop). Covers: canvas, WebGL (vendor / renderer + noise), audio, fonts list, screen and window sizes, hardwareConcurrency, deviceMemory, media device IDs, client hints (model, platformVersion), plugins, battery, WebRTC local addresses, storage IDs. Not changeable without approval: the IP address (no VPN / proxy). | Partly, 19-11-2026: Settings > Hide my identity (genuine Edge kept, client hints fixed at engine level; per-identity, per-site tracker-ID rewriting, no local WebRTC addresses, optional stable per-site canvas / WebGL / audio noise; tested: per-site canvas values differ and stay stable, identity off = real values). Still Missing: screen / cores / fonts / media-device variations within real ranges, workers and frames for the noise, detector runs |
| New identity: new fingerprint seed + clears what sites store to recognise the browser (cookies, local / session storage, IndexedDB, caches, service workers, HSTS / favicon / HTTP caches, ETags); "Also sign out of all sites"; new identity each start; shows the identity ID | Done 19-11-2026 (shield menu / Settings: new seed, site storage + caches + tracker cookies cleared, Also sign out = all cookies, each start option, identity ID shown; tested: new values, storage cleared). HSTS / TLS / DNS caches not cleared (no WebView2 API) |
| Canvas noise (per identity, invisible, passes canvas consistency checks) | Done 19-11-2026 as an option (canvas, WebGL readPixels, audio; stable per site and identity; wrappers on the frame-scrubbing call). Detectors that compare against known device hashes can still notice noise: off by default |
| Identity: more signals (user 19-11-2026: "none of them have to identify us a previous user or the blocked user"): WebGPU adapter info, fonts by text measuring (measureText / DOMRect), emoji / text rendering, speech voices, keyboard layout (getLayoutMap), media codecs / capabilities, storage quota (navigator.storage.estimate reveals the disk size), performance.memory, network info, screen colour depth / gamut / HDR, touch / pointer, gamepads, Bluetooth / USB / HID availability, Intl / locale details, math precision | Missing |
| Identity: hidden "supercookies" cleared on New identity: HSTS pins, favicon cache, HTTP cache + ETag / Last-Modified, TLS session tickets and open connections (HTTP/2, QUIC), DNS cache, service-worker and push subscriptions, Private State Tokens, Shared Storage, Topics / Protected Audience / Attribution data, window.name, remembered permission grants, WebAuthn hints | Missing |
| Identity: each site sees a different identity (per-site fingerprint seed and storage keys), so two sites cannot link the visits and a site cannot match us across its own domains | Done for noise and tracker IDs (seed per site and identity, tested) |
| Identity: blend into the crowd: values chosen from the most common real Edge-on-Windows set (fewer unique values), never random rare values; values stay fixed for the whole identity (a fingerprint that changes every page load is itself a flag) | Missing |
| Identity: matches the network: time zone, language and Accept-Language consistent with the IP's country (as Android NetGeo); no rare headers that make us unique (DNT / GPC only when the user turns them on) | Missing |
| Identity: block known fingerprinting / re-identification scripts and services (FingerprintJS and similar lists, CNAME-cloaked trackers, bot-detection beacons that only collect device data) | Missing |
| Identity: referrer trimmed to the site's origin across sites; tracking parameters removed from links (exists for trackers: extend to every identity-linking ID such as gclid, fbclid, msclkid, _gl) | Partly (tracker setting) |
| Identity: "Fresh start for a site": new identity and data for one site only (e.g. a site that blocked us), leaving other sites signed in | Done 19-11-2026 (site info > Fresh start for this site: the site's cookies and data cleared and its own new identity values; tested: only that site's values changed) |
| Identity: UNDETECTABLE (user 19-11-2026: "and should be undetectable"). A site must not be able to tell that anything is masked. Rules: (1) change values inside the engine wherever it allows it (DevTools Emulation overrides: user agent + client hints, hardware concurrency, screen / device metrics, time zone, locale, media features; to verify per method), so pages see native values with no script patches; (2) where a script patch is unavoidable: native-looking functions (toString, name, length, property descriptors and order, prototype chain, Error stack traces), patched before any page script in every realm (main page, iframes incl. about:blank / srcdoc, workers, shared and service workers), same values everywhere; (3) every surface tells the same story: JS values = HTTP headers = CSS media queries = worker values; (4) noise is stable for the identity (same canvas / audio read twice gives the same result, toDataURL matches getImageData, WebGL readPixels matches), never a pattern a detector can spot; (5) no timing differences a site can measure; (6) checked before release against public detectors (CreepJS, BrowserScan, Pixelscan, FingerprintJS / fingerprint.com demo, BrowserLeaks, AmIUnique, the Android test set): no "lies", "tampering" or "inconsistent" flags, and real Edge with masking off gives the same verdicts | Missing (requirement for every identity row) |
| Identity check page: shows what sites can see (each signal, current identity ID), compares with the last identity to prove everything changed, with links to public test sites | Missing |
| Identity: not changeable without the user's approval: the IP address (needs a VPN / proxy) and the TLS / HTTP network fingerprint (it is the genuine Edge one, shared by millions); behaviour (typing / mouse rhythm, visit times) and signing in to an old account still re-identify: shown as notes in the identity check page | Note |

| Masked cookie-consent records (OneTrust / Cookiebot) | Done 19-11-2026 (Consent.cs, from Android: a blocked OneTrust / Cookiebot script gets its "necessary only" cookie written in the tool's own format, values matched to OneTrust's real cookie on cnn.com: version, encoding, fields; consent ID per install and identity, replaced after New identity; the site's own consent cookie never touched; Cookiebot region from Windows' Home location. Tested on adobe.com, cookiebot.com) |
| IP-lookup masking | Missing (no VPN / proxy without approval) |
| Block tracker pages (strict) | Done 19-11-2026: "Block pages of ad and tracker sites" uses every list in use, tracker lists included (tested: google-analytics.com shows the block page) |
| Per-page request log: Blocked / Allowed / Cookies | Done 19-11-2026: shield menu > Request log (last 500 requests of the tab: allowed / blocked / replaced, type, live, search, filter, Cookies button) |
| Ad-block Tests page (blocking + detection tests) | Done 19-11-2026: shield menu > Blocking tests (ad-block, detection and privacy test pages) |
| Stats: rules, filter lists, blocked total with Reset | Done 19-11-2026 (Settings > Ad blocking > Statistics) |
| Daily host-list update + "Update host lists" button | Done 19-11-2026: 9 host-style built-in lists downloaded daily (Settings > Filter lists > Update host lists daily / Update now); their hosts join the compiled lists under the same list bit (574,000 hosts, about 7 MB); lists' exceptions still apply |
| Element hiding switch | Done: "Hide ad leftovers" (shield menu and Settings) switches all element hiding off / on |

## Undetectable page scripts (user 19-11-2026: "make all our scripts non detectable by any sites but it should work on those sites without compromising our protections")
| Item | Status |
|---|---|
| Cloudflare managed challenge (visas-de.tlscontact.com) failed with Stealth on: getComputedStyle wrapper ran the hidden-by-us check on every call (507 us vs 1.6 us) | Fixed ba7f977 (passes with all protections on) |
| Errors thrown through wrappers showed our stack frame (e.g. "at get offsetHeight (<anonymous>:2095:17)") | Fixed ba7f977 (nat(): browser-identical stack, also for Error.prepareStackTrace) |
| Symbol(gbab) marker visible in Object.getOwnPropertySymbols(window) | Fixed ba7f977 |
| Reading styles / sizes of an ad we hid: one lift per script run measures sizes and computed style together (values exact) | Improved 19-11-2026: a whole read burst costs one lift, 25-28 ms on BBC (the browser restyles the page when any sheet is added or removed: 10-20 ms each, measured; 0.7 ms on a light page). Open: below that needs a way to lift our rules without a sheet change |
| Cosmetic hiding adds <style> elements a page can list | Fixed 19-11-2026 for every http(s) document incl. frames: DevTools stylesheet (CSS.createStyleSheet, @layer gbh), in neither DOM nor CSSOM; blocked frames / images hidden by an address rule. Open: ExtendedCss writes inline styles (rules that need text / structure matching); $csp <meta> elements (move to response-header editing with the cookie manager) |
| getComputedStyle result for an ad we hid | Fixed 19-11-2026: its methods are the browser's own objects (identity, name, length, source text, errors identical, measured), values from the lift, writes give the browser's own read-only error. Left: a page calling a CSS property getter from the prototype directly on that object gets "Illegal invocation" |
| Bridge calls after page start all failed (HideWebView deleted chrome.webview, which WebView2's bridge reads at every call): hiding for elements added later and collapsing blocked frames never happened | Fixed 19-11-2026 (chrome.webview put back only during each bridge call) |
| Frames: own invisible sheet (same-process frames by frame id, other-process frames through their DevTools session) | Done 19-11-2026 (YouTube embed: no hiding rules visible in the frame) |
| Privacy-mode wrappers (methodOn, cookie masking) not yet on nat(); fixed values must still run the browser's receiver checks | Open (with Hide my identity) |
| WebView2 announced itself: "Microsoft Edge WebView2" in the Sec-CH-UA header and navigator.userAgentData of every page (real Edge 157 on this PC: Not-A.Brand 99, Microsoft Edge 157, Chromium 157, measured with headless Edge) | Fixed 19-11-2026 at engine level (DevTools user-agent override with the engine's own measured values minus the WebView2 brand, Chromium's own order; page, frames and workers through auto-attach): header, page, worker and fetch identical to real Edge (measured); Cloudflare test passes. Not covered: service workers' own requests (no DevTools access from WebView2) |
| AdGuard scriptlet library wrappers (measured 19-11-2026 with tools/local/slaudit: 84 calls covering every scriptlet the lists use, each in its own frame vs a clean frame; 63 left traces: source text with "[AdGuard]" strings, nameless "function () { [native code] }", wrapper frames in errors, wrong receiver errors, accessors in place of methods, own properties the browser has none of; the generic no-topics did it to document.browsingTopics on EVERY page) | Fixed 19-11-2026: shells that are the browser's own function to every check (Proxy of the original, its toString, receiver checks give the browser's own error), read traps become call-time checks on plain methods, guards on window/document removed, scriptlet definitions stay configurable, our frames taken out of errors by identity (also for gbab's older wrappers) with the browser's own first line (Blink TypeErrors). Left by design: traps on a site's own detector variables (look native), noeval / nowebrtc on the sites that ask for them, stand-in libraries (googletag, ga, FuckAdBlock...). Same blocking results with and without (13 functional checks). Cost < 1 ms per page |
| AdGuard library: window.__debug(details) called each time a scriptlet acts (a page defining that function is told about every block); Symbol.for("adg-spoof-click-isTrusted") on EventTarget.prototype; Symbol.for("adguard.href-sanitizer.writes"); "adguard:..." events | Fixed 19-11-2026 (Blocking.QuietLib) |
| The scriptlets' own stack checks (abort-on-stack-trace, inlineScript / injectedScript) | Their Error leaves out our wrapper frames and makes room for them under the depth limit: they see what they would without the shells |
| no-topics on every site | Skipped 19-11-2026: in this engine document.browsingTopics() already rejects ("deprecated and has been removed"); the scriptlet replaced that with a resolving stand-in |
| Same-origin frames that a script creates and then navigates (e.g. iframe src="/widget" on the same site) | Open, found 19-11-2026: Chromium reuses the frame's window when it leaves its first about:blank, and neither WebView2's document-created script nor DevTools' addScriptToEvaluateOnNewDocument runs again (measured), so that document gets no element hiding; its bridge is gone too. Window-level protections (scriptlets) carry over; chrome.webview does not come back even when the host object is added again (measured). Fixed in part 19-11-2026: GBrowser notices such a frame (no page-script call for its address) and gives it its site's hiding rules in the invisible sheet (tested: TOI frame src=/india, 67,100 chars). Not there: hiding by the classes the page uses (needs the page script) |
| Repeatable check: tools/local/stealthprobe.js (on vs off diff), cftest.ps1 (Cloudflare), public detectors | In use |

VPN test (19-11-2026, VeePN free extension, United Kingdom / London): the extension works in GBrowser (pages saw 51.195.242.237, OVH, AS16276), but TLScontact answers Cloudflare Error 1005: the site owner has banned that whole network (ASN) by IP, before any browser check. Not fixable in the browser: needs a VPN / proxy on a residential or non-banned network.
