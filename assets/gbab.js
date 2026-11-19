/* GoldenBoot WebView Adblocker: runs at document start in every frame, before the page's scripts.
   1. Privacy mode: device / app identity and fingerprints masked.
   2. Scriptlets (AdGuard / uBO anti-adblock defusers), AdGuard JS rules and extended CSS for this site.
   3. Element hiding: site CSS at once; generic rules only for the classes / ids the page uses. */
(function () {
  'use strict';
  // used by the scriptlet library in place of AdGuard's page-visible Symbol.for(...) markers (Blocking.PageScript)
  var adgSpoof = {}, adgHrefKey = Symbol();
  var LIB0 = lineHere();
  var SL = (function (Object, Error) { return /*SCRIPTLETS*/null; })(slObject(), slError());
  var RAWJS = /*RAWJS*/null;
  var LIB1 = lineHere();
  var EXTCSS = /*EXTCSS*/null;
  var B = window.__gbAdblock;
  if (!B) return;
  // keep the bridge out of the page's sight (ad-block detectors look for odd globals)
  try { delete window.__gbAdblock; } catch (e) {}
  var DBG = '';
  try { DBG = String(B.dbg() || ''); } catch (e) {}
  if (DBG.indexOf('n') >= 0) return;
  var href = location.href;
  if (!/^https?:/i.test(href)) {
    try { href = window.top.location.href; } catch (e) { href = document.referrer || href; }
  }

  // ---- functions we replace look like the browser's own ----
  // ---- calling the browser's own functions from our wrappers ----
  // A wrapper is one more JavaScript frame. When the built-in throws (wrong receiver or argument: checks a
  // site makes on purpose), the error's stack would show our line, e.g. "at get offsetHeight (<anonymous>:
  // 2095:17)", and at the depth limit lose one of the page's frames (measured 19-11-2026: Cloudflare's check
  // failed with Stealth on for exactly this). Such throws have no side effects, so the built-in runs once more
  // with room for our frames, and our frames are taken out, also of the frames a page-installed
  // Error.prepareStackTrace receives: the page gets exactly the error the browser itself throws.
  // A bound apply captured now: no stack frame of its own (measured) and immune to later page changes.
  var bApply = Function.prototype.call.bind(Function.prototype.apply);
  var gOPD = Object.getOwnPropertyDescriptor;
  // nat(): call a built-in for a wrapper of ours; an error it throws loses our frames (scrub(), below: frames of
  // this injected script are told apart by identity, so wrappers inside wrappers come out right too)
  function nat(f, self, args, extra, Err) {
    try { return bApply(f, self, args); } catch (e) { throw scrub(f, self, args, e, Err || Error, true); }
  }
  var natives = new WeakMap();
  var fnToString = Function.prototype.toString;
  function asNative(wrapper, original) {
    natives.set(wrapper, original);
    // same length and name as the browser's own function
    try {
      if (typeof original === 'string') return wrapper;
      if (original && wrapper.length !== original.length)
        Object.defineProperty(wrapper, 'length', {value: original.length, configurable: true});
      if (original && wrapper.name !== original.name)
        Object.defineProperty(wrapper, 'name', {value: original.name, configurable: true});
    } catch (e) {}
    return wrapper;
  }
  if (DBG.indexOf('t') < 0) try {
    // a method (not a plain function): like the built-in it has no prototype, cannot be used with
    // new, and owns only length and name
    var toStr = asNative({toString() {
      var o = natives.get(this);
      while (o && typeof o !== 'string' && natives.has(o)) o = natives.get(o);
      if (typeof o === 'string') return o;
      return nat(fnToString, o || this, [], 1);
    }}.toString, fnToString);
    Object.defineProperty(Function.prototype, 'toString', {value: toStr, writable: true, configurable: true});
  } catch (e) {}

  // ---- same-origin child frames (about:blank, srcdoc, javascript:) never get this script from WebView,
  // so a clean toString / navigator taken from one would show our changes. Every such frame gets the same
  // treatment the moment the page can reach it (inserted into the page, or its window/document read). ----
  var realmHooks = [], patchedRealms = new WeakSet();
  function dropAndroid(w) {
    try {
      if (w.android && Object.prototype.toString.call(w.android) === '[object Android]') delete w.android;
    } catch (e) {}
  }
  dropAndroid(window);
  realmHooks.push(dropAndroid);
  function patchRealm(win) {
    try {
      if (!win || win === window || patchedRealms.has(win)) return;
      var FP = win.Function && win.Function.prototype;
      if (!FP) return;
      patchedRealms.add(win);
      var ct = FP.toString, RE = win.Error;
      var w = {toString() { var o = natives.get(this); while (o && typeof o !== 'string' && natives.has(o)) o = natives.get(o); if (typeof o === 'string') return o;
        return nat(ct, o || this, [], 1, RE); }}.toString;
      try { Object.setPrototypeOf(w, FP); } catch (e) {}
      natives.set(w, ct);
      Object.defineProperty(FP, 'toString', {value: w, writable: true, enumerable: false, configurable: true});
      for (var i = 0; i < realmHooks.length; i++) { try { realmHooks[i](win); } catch (e) {} }
    } catch (e) {}
  }
  function patchFramesIn(node) {
    try {
      if (!node || node.nodeType !== 1) return;
      var t = node.tagName;
      if (t === 'IFRAME' || t === 'FRAME') { patchRealm(cwGet && cwGet.call(node)); return; }
      if (!node.firstElementChild) return;
      var l = node.getElementsByTagName('iframe');
      for (var i = 0; i < l.length; i++) patchRealm(cwGet && cwGet.call(l[i]));
    } catch (e) {}
  }
  var cwGet = null;
  if (DBG.indexOf('f') < 0) try {
    [window.HTMLIFrameElement, window.HTMLFrameElement].forEach(function (F) {
      if (!F) return;
      var P = F.prototype;
      var dw = Object.getOwnPropertyDescriptor(P, 'contentWindow');
      var dd = Object.getOwnPropertyDescriptor(P, 'contentDocument');
      if (dw && dw.get) {
        if (F === window.HTMLIFrameElement) cwGet = dw.get;
        Object.defineProperty(P, 'contentWindow', {configurable: true, enumerable: dw.enumerable,
          get: asNative({get contentWindow() { var v = nat(dw.get, this, [], 1); patchRealm(v); return v; }}.__lookupGetter__('contentWindow'), dw.get)});
      }
      if (dd && dd.get) {
        Object.defineProperty(P, 'contentDocument', {configurable: true, enumerable: dd.enumerable,
          get: asNative({get contentDocument() { var v = nat(dd.get, this, [], 1); try { if (v) patchRealm(v.defaultView); } catch (e) {} return v; }}.__lookupGetter__('contentDocument'), dd.get)});
      }
    });
    // insertion paths: patch right after the frame enters the page (window[0] / frames[0] reach it too)
    var NP = Node.prototype, EP = Element.prototype;
    [[NP, 'appendChild', 0], [NP, 'insertBefore', 0], [NP, 'replaceChild', 0]].forEach(function (m) {
      var f = m[0][m[1]];
      if (typeof f !== 'function') return;
      var o = {};
      o[m[1]] = {appendChild(n) { var r = nat(f, this, arguments, 1); patchFramesIn(n); return r; },
        insertBefore(n) { var r = nat(f, this, arguments, 1); patchFramesIn(n); return r; },
        replaceChild(n) { var r = nat(f, this, arguments, 1); patchFramesIn(n); return r; }}[m[1]];
      Object.defineProperty(m[0], m[1], {value: asNative(o[m[1]], f), writable: true, enumerable: true, configurable: true});
    });
    ['append', 'prepend', 'after', 'before', 'replaceWith', 'insertAdjacentElement', 'insertAdjacentHTML', 'replaceChildren'].forEach(function (n) {
      var f = EP[n];
      if (typeof f !== 'function') return;
      var w = {append() { return run(this, arguments); }, prepend() { return run(this, arguments); },
        after() { return run(this, arguments); }, before() { return run(this, arguments); },
        replaceWith() { return run(this, arguments); }, insertAdjacentElement() { return run(this, arguments); },
        insertAdjacentHTML() { return run(this, arguments); }, replaceChildren() { return run(this, arguments); }}[n];
      function run(self, args) {
        var r = nat(f, self, args, 2);   // wrapper + run
        try {
          for (var i = 0; i < args.length; i++) patchFramesIn(args[i]);
          patchFramesIn(self.parentNode || self);
        } catch (e) {}
        return r;
      }
      Object.defineProperty(EP, n, {value: asNative(w, f), writable: true, enumerable: true, configurable: true});
    });
    ['innerHTML', 'outerHTML'].forEach(function (n) {
      var d = Object.getOwnPropertyDescriptor(EP, n);
      if (!d || !d.set) return;
      var s = {set innerHTML(v) { nat(d.set, this, [v], 1); patchFramesIn(this); },
        set outerHTML(v) { var p = this.parentNode; nat(d.set, this, [v], 1); patchFramesIn(p); }}.__lookupSetter__(n);
      Object.defineProperty(EP, n, {get: d.get, set: asNative(s, d.set), enumerable: d.enumerable, configurable: true});
    });
  } catch (e) {}

  // ---- privacy mode: hide who the user is (device, app, fingerprints) ----
  function getterOn(proto, name, value) {
    try {
      var d = Object.getOwnPropertyDescriptor(proto, name);
      if (!d || !d.get) return;
      var g = d.get;
      var w = asNative({get [name]() { var real = nat(g, this, [], 1); return typeof value === 'function' ? value.call(this, g, real) : value; }}
        .__lookupGetter__(name), g);
      Object.defineProperty(proto, name, {get: w, set: d.set, enumerable: d.enumerable, configurable: true});
    } catch (e) {}
  }
  function methodOn(proto, name, make) {
    try {
      var f = proto && proto[name];
      if (typeof f !== 'function') return;
      var w = asNative(make(f), f);
      Object.defineProperty(proto, name, {value: w, writable: true, enumerable: false, configurable: true});
    } catch (e) {}
  }
  // ---- scriptlets leave no trace ----
  // AdGuard's scriptlets put their own functions and Proxies in place of built-ins. Measured 19-11-2026 with
  // tools/local/slaudit (84 calls, every scriptlet the lists use, each in its own frame, compared with a clean
  // frame): their source text or a nameless "function () { [native code] }" showed through toString, wrappers
  // had a prototype, errors carried their frames ("at Object.apply (<anonymous>...)"), receiver errors changed
  // ("Cannot read properties of undefined" instead of "Illegal invocation"), accessors replaced data methods and
  // own properties appeared where the browser has none. The generic no-topics scriptlet did that to
  // document.browsingTopics on EVERY page. So: everything the scriptlets change on the built-ins is recorded
  // and each changed function gets a shell that is the browser's own function to every check (a Proxy of the
  // original: same name, length, prototype, own keys, constructability; toString from the original), calls the
  // scriptlet's version, and takes our frames out of any error that passes through.
  var oKeys = Reflect.ownKeys, oDef = Object.defineProperty, rConstruct = Reflect.construct;
  var isProtoOf = Function.prototype.call.bind(Object.prototype.isPrototypeOf), ProxyC = Proxy;
  // a frame of this injected script: no file name, not eval code, has a line (built-ins have none)
  function ourFrame(c) {
    try { return !c.isEval() && !c.getFileName() && c.getLineNumber() != null; } catch (e) { return false; }
  }
  // The first line of a stack. Blink adds "Failed to execute 'x' on 'Y': " to the message of the TypeErrors it
  // throws after V8 has recorded their stack, so there the browser's own first line has the message without it
  // (DOMExceptions keep it). Measured 19-11-2026 (getComputedStyle(null), appendChild(1), open() with no
  // arguments, new Element()). blink = thrown by the browser, not by page code.
  var blinkCtx = /^Failed to (?:execute '[^']*' on '[^']*'|construct '[^']*'|read the '[^']*' property from '[^']*'|set the '[^']*' property on '[^']*'|delete the '[^']*' property from '[^']*'): /;
  function errHead(err, blink) {
    var n, m;
    try { n = err.name; n = n === undefined ? 'Error' : String(n); } catch (e) { n = 'Error'; }
    try { m = err.message; m = m === undefined ? '' : String(m); } catch (e) { m = ''; }
    if (blink) {
      try { if (!(typeof DOMException === 'function' && err instanceof DOMException)) m = m.replace(blinkCtx, ''); } catch (e) {}
    }
    return n === '' ? m : m === '' ? n : n + ': ' + m;
  }
  // our frames out of e's stack (formatted now, as V8 would); when they cost the page frames at the depth
  // limit and the throw did not come from page code, the call runs once more with room for them
  function natF(f, self, args, Err) {
    try { return bApply(f, self, args); } catch (e) { throw scrub(f, self, args, e, Err || Error, true); }
  }
  function scrub(f, self, args, e, Err, again) {
    if (e === null || (typeof e !== 'object' && typeof e !== 'function')) return e;
    try {
      var dl = gOPD(Err, 'stackTraceLimit'), dp = gOPD(Err, 'prepareStackTrace');
      if (!dl || !('value' in dl) || !dl.writable || typeof dl.value !== 'number' || !(dl.value > 0)) return e;
      if (dp && !('value' in dp && dp.writable)) return e;
      var lim = dl.value, prep = dp && dp.value, info = {ours: 0, total: 0, page: false};
      var install = function () {
        Err.prepareStackTrace = function (err, frames) {
          var out = [];
          info.total = frames.length;
          for (var i = 0; i < frames.length; i++) {
            if (ourFrame(frames[i])) { info.ours++; continue; }
            if (info.ours === 0 && i === 0 && frames[i].getLineNumber() != null) info.page = true;
            if (out.length < lim) out.push(frames[i]);
          }
          if (typeof prep === 'function') return bApply(prep, Err, [err, out]);
          var s = errHead(err, !info.page);
          for (var j = 0; j < out.length; j++) s += '\n    at ' + out[j];
          return s;
        };
      };
      var restore = function () {
        Err.stackTraceLimit = lim;
        if (dp) Err.prepareStackTrace = prep; else delete Err.prepareStackTrace;
      };
      try { install(); void e.stack; } finally { restore(); }
      if (again && !info.page && info.ours > 0 && info.total >= lim && lim !== Infinity) {
        try {
          Err.stackTraceLimit = lim + 64;
          install();
          var e2 = e;
          try { bApply(f, self, args); } catch (x) { e2 = x; }
          if (e2 !== e && e2 !== null && typeof e2 === 'object') { void e2.stack; e = e2; }
        } finally { restore(); }
      }
    } catch (z) {}
    return e;
  }
  // what a shell reports as its source: the original's (through earlier masks of ours)
  function shellSrc(orig) {
    var o = orig;
    while (natives.has(o)) { o = natives.get(o); if (typeof o === 'string') break; }
    return o;
  }
  var shellCur = new WeakMap();
  // which receivers the browser's own method accepts: window functions take window (or nothing), methods of
  // an interface prototype take its instances. Other receivers go straight to the browser's function, which
  // answers them exactly as it always does (its "Illegal invocation"), never to the scriptlet's code.
  // Array / String / Object / Function methods are generic (any receiver), so they get no check.
  var genericProtos = [Array.prototype, String.prototype, Object.prototype, Function.prototype];
  function receiverCheck(holder) {
    if (holder === window) return function (th) { return th === undefined || th === null || th === window; };
    if (holder === document) return function (th) { return th === document; };
    try {
      if (genericProtos.indexOf(holder) >= 0) return null;
      var C = gOPD(holder, 'constructor');
      if (C && typeof C.value === 'function' && C.value.prototype === holder)
        return function (th) { return th !== null && (typeof th === 'object' || typeof th === 'function') && isProtoOf(holder, th); };
    } catch (e) {}
    return null;
  }
  function shell(orig, cur, holder, brand) {
    var ok = holder ? receiverCheck(holder) : null;
    var px = new ProxyC(orig, {
      apply: function (t, th, a) {
        if (ok && !ok(th)) return natF(orig, th, a);
        if (brand) natF(brand, th, []);
        return natF(cur, th, a);
      },
      construct: function (t, a, nt) { return natF(rConstruct, undefined, [cur, a, nt === px ? cur : nt]); }
    });
    natives.set(px, shellSrc(orig));
    shellCur.set(px, cur);
    return px;
  }
  // a read trap (abort-current-inline-script, abort-on-stack-trace, abort-on-property-read on a built-in
  // method) as a check when the method is called: the property stays the browser's plain method
  function trapCall(get, holder) {
    return function () { var fn = bApply(get, holder, []); return bApply(fn, this, arguments); };
  }
  // a function made by a scriptlet where the browser has none: looks like a built-in accessor / method
  function fakeShell(kind, name, cur) {
    var n = typeof name === 'symbol' ? '[' + (name.description || '') + ']' : String(name), base;
    if (kind === 'get') base = gOPD({get [name]() { return undefined; }}, name).get;
    else if (kind === 'set') base = gOPD({set [name](v) {}}, name).set;
    else base = {[name]() {}}[name];
    var px = new ProxyC(base, {apply: function (t, th, a) { return natF(cur, th, a); }});
    natives.set(px, 'function ' + (kind ? kind + ' ' : '') + n + '() { [native code] }');
    return px;
  }
  // What the scriptlets change, recorded before they run. Reading every property of window and ~70
  // prototypes cost 5-20 ms per page (measured 19-11-2026: YouTube, TOI, BBC), so only these are watched:
  // what the library assigns to directly (found in the library source and the slaudit run), the property
  // chain each call names, and everything it defines (recorded by its Object.defineProperty, below).
  var cloakList = null, cloakSeen = null;
  var CLOAK_WATCH = [
    ['', 'setTimeout setInterval requestAnimationFrame fetch open eval getComputedStyle atob btoa onerror Promise ' +
      'IntersectionObserver MutationObserver RTCPeerConnection webkitRTCPeerConnection addEventListener ' +
      'removeEventListener decodeURIComponent'],
    ['document', 'write writeln createElement addEventListener cookie'],
    ['Document.prototype', 'write writeln createElement browsingTopics'],
    ['EventTarget.prototype', 'addEventListener removeEventListener dispatchEvent'],
    ['Node.prototype', 'appendChild insertBefore textContent'],
    ['Element.prototype', 'attachShadow getBoundingClientRect setAttribute getAttribute hasAttribute innerHTML ' +
      'append prepend remove'],
    ['HTMLElement.prototype', 'onerror onload'],
    ['HTMLScriptElement.prototype', 'src setAttribute'], ['HTMLImageElement.prototype', 'src setAttribute'],
    ['HTMLIFrameElement.prototype', 'src setAttribute'], ['HTMLLinkElement.prototype', 'href setAttribute'],
    ['HTMLCanvasElement.prototype', 'getContext'],
    ['XMLHttpRequest.prototype', 'open send abort getAllResponseHeaders getResponseHeader setRequestHeader'],
    ['Response.prototype', 'json text'], ['JSON', 'parse stringify'], ['Object', 'keys entries values assign'],
    ['DOMTokenList.prototype', 'add remove toggle'], ['TextDecoder.prototype', 'decode'],
    ['WeakMap.prototype', 'set get'], ['Map.prototype', 'set get'], ['Array.prototype', 'push'],
    ['RTCPeerConnection.prototype', 'createDataChannel createOffer setLocalDescription setRemoteDescription close']
  ];
  function cloakNote(o, k) {
    if (!cloakList || o === null || (typeof o !== 'object' && typeof o !== 'function')) return;
    try {
      var ks = cloakSeen.get(o);
      if (!ks) { ks = new Set(); cloakSeen.set(o, ks); }
      if (ks.has(k)) return;
      ks.add(k);
      cloakList.push([o, k, gOPD(o, k)]);
    } catch (e) {}
  }
  function chainTarget(chain) {
    try {
      var parts = String(chain).split('.'), o = window;
      if (parts.length > 1 && parts[0] === 'window') parts.shift();
      for (var i = 0; i < parts.length - 1; i++) { o = o[parts[i]]; if (o === null || o === undefined) return null; }
      return [o, parts[parts.length - 1]];
    } catch (e) { return null; }
  }
  function cloakStart(calls) {
    cloakList = [];
    cloakSeen = new WeakMap();
    for (var i = 0; i < CLOAK_WATCH.length; i++) {
      var root = CLOAK_WATCH[i][0] === '' ? window : (chainTarget(CLOAK_WATCH[i][0] + '.x') || [null])[0];
      if (!root) continue;
      var ks = CLOAK_WATCH[i][1].split(' ');
      for (var j = 0; j < ks.length; j++) cloakNote(root, ks[j]);
    }
    // the property chain a call names ("document.createElement", "Element.prototype.setAttribute", "atob")
    if (calls) for (var c = 0; c < calls.length; c++) {
      var a0 = calls[c][1];
      if (typeof a0 !== 'string' || !/^[\w$]+(\.[\w$]+)*$/.test(a0)) continue;
      var t = chainTarget(a0);
      if (t) cloakNote(t[0], t[1]);
    }
  }
  function sameDesc(a, b) {
    return a.value === b.value && a.get === b.get && a.set === b.set && a.writable === b.writable &&
      a.enumerable === b.enumerable && a.configurable === b.configurable;
  }
  function inherited(o, k) {
    try {
      for (var p = Object.getPrototypeOf(o); p; p = Object.getPrototypeOf(p)) {
        var d = gOPD(p, k);
        if (d) return {holder: p, desc: d};
      }
    } catch (e) {}
    return null;
  }
  function cloakFix() {
    var list = cloakList, later = [];
    cloakList = null;
    if (!list) return;
    for (var i = 0; i < list.length; i++) {
      var o = list[i][0], k = list[i][1], b = list[i][2], d;
      try { d = gOPD(o, k); } catch (e) { continue; }
      if (!d || (b && sameDesc(b, d))) continue;
      try {
        if (b) fixChanged(o, k, b, d);
        else fixAdded(o, k, d, later);
      } catch (e) {}
    }
    // own properties over inherited methods, once every holder has its final shell
    for (var h = 0; h < later.length; h++) { try { fixOwn(later[h]); } catch (e) {} }
  }
  function asData(o, k, b, v) {
    oDef(o, k, {value: v, writable: b.writable, enumerable: b.enumerable, configurable: b.configurable});
  }
  function fixChanged(o, k, b, d) {
    var bf = 'value' in b && typeof b.value === 'function';
    if (bf && 'value' in d) {
      if (typeof d.value === 'function') asData(o, k, b, shell(b.value, d.value, o));
      return;
    }
    if (bf && d.get) {
      // a method turned into an accessor: a guard that always hands out the scriptlet's wrapper, or a trap that
      // checks every read. Either way a plain method again, as the browser has it.
      var v;
      try { v = bApply(d.get, o, []); } catch (e) { v = undefined; }
      if (typeof v === 'function' && v !== b.value) asData(o, k, b, shell(b.value, v, o));
      else asData(o, k, b, shell(b.value, trapCall(d.get, o), o));
      return;
    }
    if (b.get || b.set) {
      if (!('get' in d)) return;
      var g = d.get, st = d.set;
      if (g && g !== b.get) g = b.get ? shell(b.get, g, o) : fakeShell('get', k, g);
      if (st && st !== b.set) st = b.set ? shell(b.set, st, o, b.get) : fakeShell('set', k, st);
      oDef(o, k, {get: g, set: st, enumerable: d.enumerable, configurable: d.configurable});
      return;
    }
    // a value property turned into a trap (set-constant on an existing value)
    if (d.get || d.set)
      oDef(o, k, {get: d.get ? fakeShell('get', k, d.get) : undefined, set: d.set ? fakeShell('set', k, d.set) : undefined,
        enumerable: d.enumerable, configurable: d.configurable});
  }
  function fixAdded(o, k, d, later) {
    var inh = inherited(o, k);
    if (inh && (('value' in inh.desc && typeof inh.desc.value === 'function') || inh.desc.get) && d.configurable &&
        (typeof d.value === 'function' || d.get)) {
      later.push({o: o, k: k, d: d, holder: inh.holder, orig: inh.desc.value});
      return;
    }
    // a new trap (abort-on-property-read, set-constant ...) on a name the browser does not have
    if (d.get || d.set)
      oDef(o, k, {get: d.get ? fakeShell('get', k, d.get) : undefined, set: d.set ? fakeShell('set', k, d.set) : undefined,
        enumerable: d.enumerable, configurable: d.configurable});
  }
  // an own method or accessor over an inherited method (document.write, document.createElement trap,
  // window.addEventListener guard, HTMLScriptElement.prototype.setAttribute): the browser has none there, so it
  // goes; what it did moves into the holder's method for this object (or its instances) only
  function fixOwn(x) {
    var o = x.o, k = x.k, d = x.d, hd = gOPD(x.holder, k);
    if (!hd || !('value' in hd) || typeof hd.value !== 'function') {
      if (d.get || d.set)
        oDef(o, k, {get: d.get ? fakeShell('get', k, d.get) : undefined, set: d.set ? fakeShell('set', k, d.set) : undefined,
          enumerable: d.enumerable, configurable: d.configurable});
      return;
    }
    if (x.orig === undefined) x.orig = shellSrc(hd.value);
    var cur = hd.value, own;
    if (d.get) {
      var v;
      try { v = bApply(d.get, o, []); } catch (e) { v = undefined; }
      // a guard handing out the scriptlet function behind the holder's shell: the holder already does it
      if (shellCur.has(cur) && shellCur.get(cur) === v) { delete o[k]; return; }
      // a trap (hands out the browser's method, checks on each read) or its own wrapper for this object
      own = typeof v === 'function' && v !== x.orig && v !== cur ? v : trapCall(d.get, o);
    } else own = d.value;
    var mine = function (th) { return th === o || (th !== null && (typeof th === 'object' || typeof th === 'function') && isProtoOf(o, th)); };
    var px = new ProxyC(cur, {
      apply: function (t, th, a) { return natF(mine(th) ? own : cur, th, a); }
    });
    natives.set(px, shellSrc(cur));
    delete o[k];
    oDef(x.holder, k, {value: px, writable: hd.writable, enumerable: hd.enumerable, configurable: hd.configurable});
  }
  // the scriptlets lock what they define (configurable: false); the browser's own properties are configurable,
  // and theirs must stay so for the reshaping above. The library sees this Object (see SL below).
  // line of the caller in this script (to know where the scriptlet library sits)
  function lineHere() {
    var o = Error.prepareStackTrace, n = 0;
    try {
      Error.prepareStackTrace = function (e, f) { return f.length > 1 ? f[1].getLineNumber() : 0; };
      n = new Error().stack;
    } catch (e) {} finally { Error.prepareStackTrace = o; if (o === undefined) delete Error.prepareStackTrace; }
    return typeof n === 'number' ? n : 0;
  }
  // a frame of our own wrappers (this script, outside the scriptlet library and the AdGuard JS rules)
  function wrapperFrame(c) {
    if (!ourFrame(c)) return false;
    var n = c.getLineNumber();
    return !(LIB0 && LIB1 && n > LIB0 && n < LIB1);
  }
  // The scriptlets read stacks (abort-on-stack-trace, "inlineScript" / "injectedScript" matching, json-prune
  // with a stack argument ...). Their Error shows them the stack without our wrappers' frames, with room for
  // those frames under the depth limit: what they would see without the shells.
  function slError() {
    var E = Error, px;
    var make = function (a, nt) {
      var dl = gOPD(E, 'stackTraceLimit'), dp = gOPD(E, 'prepareStackTrace');
      var lim = dl && 'value' in dl ? dl.value : 10, prep = dp && dp.value;
      var free = dl && dl.writable && typeof lim === 'number' && lim > 0 && lim !== Infinity && !(dp && !('value' in dp && dp.writable));
      if (!free) return rConstruct(E, a, nt);
      var e;
      E.stackTraceLimit = lim + 64;
      E.prepareStackTrace = function (err, frames) {
        var out = [];
        for (var i = 0; i < frames.length && out.length < lim; i++) if (!wrapperFrame(frames[i])) out.push(frames[i]);
        if (typeof prep === 'function') return bApply(prep, E, [err, out]);
        var st = errHead(err);
        for (var j = 0; j < out.length; j++) st += '\n    at ' + out[j];
        return st;
      };
      try { e = rConstruct(E, a, nt); void e.stack; }
      finally { E.stackTraceLimit = lim; if (dp) E.prepareStackTrace = prep; else delete E.prepareStackTrace; }
      return e;
    };
    px = new Proxy(E, {
      construct: function (t, a, nt) { return make(a, nt === px ? E : nt); },
      apply: function (t, th, a) { return make(a, E); }
    });
    return px;
  }
  // configurable unless the property already exists locked (then defineProperty must fail as it would)
  function openDesc(o, k, d) {
    try {
      if (!d || typeof d !== 'object' || d.configurable === true) return d;
      var ex = gOPD(o, k);
      if (ex && !ex.configurable) return d;
      var c = {};
      for (var x in d) c[x] = d[x];
      c.configurable = true;
      return c;
    } catch (e) { return d; }
  }
  function slObject() {
    var def = function defineProperty(o, k, d) {
      cloakNote(o, k);
      d = openDesc(o, k, d);
      return Object.defineProperty(o, k, d);
    };
    var defs = function defineProperties(o, ps) {
      try {
        var c = {}, ks = oKeys(Object(ps));
        for (var i = 0; i < ks.length; i++) {
          cloakNote(o, ks[i]);
          c[ks[i]] = openDesc(o, ks[i], ps[ks[i]]);
        }
        ps = c;
      } catch (e) {}
      return Object.defineProperties(o, ps);
    };
    return new Proxy(Object, {get: function (t, k) {
      if (k === 'defineProperty') return def;
      if (k === 'defineProperties') return defs;
      return t[k];
    }});
  }

  // battery: a plugged-in, full battery (the commonest reading); the real API object stays, so nothing
  // looks removed (used by privacy mode and instead of the lists' "getBattery = nothing" scriptlet)
  var batteryDone = false;
  function batteryMask() {
    if (batteryDone || !window.BatteryManager) return;
    batteryDone = true;
    var BM = BatteryManager.prototype;
    getterOn(BM, 'charging', true);
    getterOn(BM, 'level', 1);
    getterOn(BM, 'chargingTime', 0);
    getterOn(BM, 'dischargingTime', Infinity);
  }
  // generic privacy scriptlets from the lists that would leave visible marks: run them only where the
  // API really exists (never add a new property), and mask the battery our own way
  function quietCall(name, args) {
    var a0 = String(args[0] || '');
    if (/set-constant/.test(name)) {
      if (/^navigator\.getBattery$/.test(a0)) { batteryMask(); return false; }
      var m = /^navigator\.([A-Za-z_$][\w$]*)$/.exec(a0);
      if (m && !(m[1] in navigator)) return false;
    }
    // Topics API: in this engine document.browsingTopics() already rejects ("deprecated and has been removed",
    // measured 19-11-2026, WebView2 157); the scriptlet would put a resolving stand-in on every site instead
    if (/no-topics/.test(name)) return false;
    if (/no-protected-audience/.test(name) && !('joinAdInterestGroup' in navigator) && !('runAdAuction' in navigator)) return false;
    return true;
  }
  // Web Speech synthesis: Chrome has it, Android WebView does not ("speechSynthesis is not defined" gives
  // the app away). Added with Chrome's shape: SpeechSynthesis / SpeechSynthesisUtterance /
  // SpeechSynthesisVoice interfaces, a speechSynthesis object, voices loading later (empty list at first,
  // as Chrome reports before its voices arrive). Nothing is spoken.
  function speechMask(W) {
    try {
      if (!W || W.speechSynthesis || !W.EventTarget) return;
      var nat = function (n) { return 'function ' + n + '() { [native code] }'; };
      var ET = W.EventTarget;
      var illegal = function () { throw new W.TypeError('Illegal constructor'); };
      // interfaces
      var SS = function SpeechSynthesis() { illegal(); };
      var SV = function SpeechSynthesisVoice() { illegal(); };
      var state = new W.WeakMap();
      var SU = function SpeechSynthesisUtterance() {
        if (!new.target) throw new W.TypeError("Failed to construct 'SpeechSynthesisUtterance': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
        var self = Reflect.construct(ET, [], new.target);
        state.set(self, {text: arguments.length ? String(arguments[0]) : '', lang: '', voice: null,
          volume: 1, rate: 1, pitch: 1});
        return self;
      };
      [[SS, 'SpeechSynthesis'], [SV, 'SpeechSynthesisVoice'], [SU, 'SpeechSynthesisUtterance']].forEach(function (x) {
        natives.set(x[0], nat(x[1]));
        Object.defineProperty(x[0], 'length', {value: x[0] === SU ? 0 : 0, configurable: true});
      });
      SS.prototype = Object.create(ET.prototype);
      SU.prototype = Object.create(ET.prototype);
      Object.setPrototypeOf(SS, ET);
      Object.setPrototypeOf(SU, ET);
      SV.prototype = Object.create(W.Object.prototype);
      [[SS, 'SpeechSynthesis'], [SV, 'SpeechSynthesisVoice'], [SU, 'SpeechSynthesisUtterance']].forEach(function (x) {
        Object.defineProperty(x[0], 'prototype', {writable: false, enumerable: false, configurable: false});
        Object.defineProperty(x[0].prototype, 'constructor', {value: x[0], writable: true, enumerable: false, configurable: true});
        Object.defineProperty(x[0].prototype, W.Symbol.toStringTag, {value: x[1], configurable: true});
      });
      function method(P, name, fn, len) {
        natives.set(fn, nat(name));
        try { Object.defineProperty(fn, 'length', {value: len, configurable: true}); } catch (e) {}
        try { Object.setPrototypeOf(fn, W.Function.prototype); } catch (e) {}
        Object.defineProperty(P, name, {value: fn, writable: true, enumerable: true, configurable: true});
      }
      function accessor(P, name, get, set) {
        natives.set(get, 'function get ' + name + '() { [native code] }');
        try { Object.setPrototypeOf(get, W.Function.prototype); } catch (e) {}
        var d = {get: get, enumerable: true, configurable: true};
        if (set) {
          natives.set(set, 'function set ' + name + '() { [native code] }');
          try { Object.setPrototypeOf(set, W.Function.prototype); } catch (e) {}
          d.set = set;
        }
        Object.defineProperty(P, name, d);
      }
      // a real EventTarget (addEventListener('voiceschanged') etc. work) with SpeechSynthesis's prototype
      var synth = Reflect.construct(ET, [], SS);
      var check = function (o) { if (o !== synth) throw new W.TypeError('Illegal invocation'); };
      var handler = null;
      ['pending', 'speaking', 'paused'].forEach(function (n) {
        accessor(SS.prototype, n, {get [n]() { check(this); return false; }}.__lookupGetter__(n));
      });
      accessor(SS.prototype, 'onvoiceschanged',
        {get onvoiceschanged() { check(this); return handler; }}.__lookupGetter__('onvoiceschanged'),
        {set onvoiceschanged(v) { check(this); handler = typeof v === 'function' ? v : null; }}.__lookupSetter__('onvoiceschanged'));
      method(SS.prototype, 'speak', {speak(u) {
        check(this);
        if (!(u instanceof SU)) throw new W.TypeError("Failed to execute 'speak' on 'SpeechSynthesis': parameter 1 is not of type 'SpeechSynthesisUtterance'.");
      }}.speak, 1);
      ['cancel', 'pause', 'resume'].forEach(function (n) {
        var o = {cancel() { check(this); }, pause() { check(this); }, resume() { check(this); }};
        method(SS.prototype, n, o[n], 0);
      });
      method(SS.prototype, 'getVoices', {getVoices() { check(this); return []; }}.getVoices, 0);
      ['text', 'lang', 'voice', 'volume', 'rate', 'pitch'].forEach(function (n) {
        var g = {get [n]() { var st = state.get(this); if (!st) throw new W.TypeError('Illegal invocation'); return st[n]; }}.__lookupGetter__(n);
        var s2 = {set [n](v) {
          var st = state.get(this); if (!st) throw new W.TypeError('Illegal invocation');
          st[n] = (n === 'text' || n === 'lang') ? String(v) : n === 'voice' ? (v instanceof SV ? v : null) : Number(v);
        }}.__lookupSetter__(n);
        accessor(SU.prototype, n, g, s2);
      });
      ['onstart', 'onend', 'onerror', 'onpause', 'onresume', 'onmark', 'onboundary'].forEach(function (n) {
        var hs = new W.WeakMap();
        accessor(SU.prototype, n,
          {get [n]() { return hs.has(this) ? hs.get(this) : null; }}.__lookupGetter__(n),
          {set [n](v) { hs.set(this, typeof v === 'function' ? v : null); }}.__lookupSetter__(n));
      });
      ['voiceURI', 'name', 'lang', 'localService', 'default'].forEach(function (n) {
        accessor(SV.prototype, n, {get [n]() { throw new W.TypeError('Illegal invocation'); }}.__lookupGetter__(n));
      });
      [['SpeechSynthesis', SS], ['SpeechSynthesisUtterance', SU], ['SpeechSynthesisVoice', SV]].forEach(function (x) {
        Object.defineProperty(W, x[0], {value: x[1], writable: true, enumerable: false, configurable: true});
      });
      var sg = {get speechSynthesis() { return synth; }}.__lookupGetter__('speechSynthesis');
      natives.set(sg, 'function get speechSynthesis() { [native code] }');
      try { Object.setPrototypeOf(sg, W.Function.prototype); } catch (e) {}
      Object.defineProperty(W, 'speechSynthesis', {get: sg, set: undefined, enumerable: true, configurable: true});
    } catch (e) {}
  }
  // ---- what Chrome on Android has and WebView lacks (measured against Chrome 157 on the vivo, 2026-11-16) ----
  function chromeLike(W, cfg) {
    try {
      var nat = function (n) { return 'function ' + n + '() { [native code] }'; };
      var fnP = W.Function.prototype;
      function mk(name, fn, len) {
        natives.set(fn, nat(name));
        try { Object.setPrototypeOf(fn, fnP); } catch (e) {}
        try { Object.defineProperty(fn, 'length', {value: len || 0, configurable: true}); } catch (e) {}
        try { Object.defineProperty(fn, 'name', {value: name, configurable: true}); } catch (e) {}
        return fn;
      }
      function iface(name, ctor, members) {
        var C = mk(name, ctor || function () { throw new W.TypeError('Illegal constructor'); }, 0);
        var P = Object.create(W.Object.prototype);
        Object.defineProperty(P, 'constructor', {value: C, writable: true, enumerable: false, configurable: true});
        Object.defineProperty(P, W.Symbol.toStringTag, {value: name, configurable: true});
        Object.defineProperty(C, 'prototype', {value: P, writable: false, enumerable: false, configurable: false});
        Object.defineProperty(W, name, {value: C, writable: true, enumerable: false, configurable: true});
        return C;
      }
      // window.chrome {loadTimes, csi}
      if (!('chrome' in W)) {
        var t0 = function () { try { return W.performance.timeOrigin; } catch (e) { return Date.now(); } };
        var ch = {};
        ch.loadTimes = mk('loadTimes', function () {
          var o = t0() / 1000, nav = {};
          try { nav = W.performance.getEntriesByType('navigation')[0] || {}; } catch (e) {}
          return {requestTime: o + (nav.requestStart || 0) / 1000, startLoadTime: o, commitLoadTime: o + (nav.responseStart || 0) / 1000,
            finishDocumentLoadTime: nav.domContentLoadedEventEnd ? o + nav.domContentLoadedEventEnd / 1000 : 0,
            finishLoadTime: nav.loadEventEnd ? o + nav.loadEventEnd / 1000 : 0, firstPaintTime: o + (nav.responseEnd || 0) / 1000,
            firstPaintAfterLoadTime: 0, navigationType: 'Other', wasFetchedViaSpdy: true, wasNpnNegotiated: true,
            npnNegotiatedProtocol: 'h2', wasAlternateProtocolAvailable: false, connectionInfo: 'h2'};
        });
        ch.csi = mk('csi', function () {
          var o = t0(), nav = {};
          try { nav = W.performance.getEntriesByType('navigation')[0] || {}; } catch (e) {}
          return {startE: Math.round(o), onloadT: Math.round(o + (nav.loadEventEnd || nav.domContentLoadedEventEnd || 0)),
            pageT: W.performance.now(), tran: 15};
        });
        Object.defineProperty(W, 'chrome', {value: ch, writable: true, enumerable: true, configurable: true});
      }
      // Notification: exists in Chrome (permission "default"); "new Notification" throws on Android
      if (typeof W.Notification === 'undefined') {
        var N = iface('Notification', function Notification() {
          throw new W.TypeError("Failed to construct 'Notification': Illegal constructor. Use ServiceWorkerRegistration.showNotification() instead.");
        });
        var pg = mk('get permission', function () { return 'default'; });
        Object.defineProperty(N, 'permission', {get: pg, enumerable: true, configurable: true});
        var mg = mk('get maxActions', function () { return 2; });
        Object.defineProperty(N, 'maxActions', {get: mg, enumerable: true, configurable: true});
        Object.defineProperty(N, 'requestPermission', {value: mk('requestPermission', function () {
          var cb = arguments[0];
          return new W.Promise(function (res) { W.setTimeout(function () { if (typeof cb === 'function') cb('default'); res('default'); }, 300); });
        }, 0), writable: true, enumerable: true, configurable: true});
        Object.setPrototypeOf(N.prototype, W.EventTarget.prototype);
        Object.setPrototypeOf(N, W.EventTarget);
      }
      var NP2 = W.Navigator && W.Navigator.prototype;
      // permissions: notifications ask like Chrome ("prompt"), not "denied"
      if (W.Permissions) {
        var PP = W.Permissions.prototype, q = PP.query;
        var qw = mk('query', function (d) {
          var p = q.apply(this, arguments);
          if (d && d.name === 'notifications') return p.then(function (st) {
            try { Object.defineProperty(st, 'state', {value: 'prompt', configurable: true}); } catch (e) {}
            return st;
          });
          return p;
        }, 1);
        natives.set(qw, q);
        Object.defineProperty(PP, 'query', {value: qw, writable: true, enumerable: true, configurable: true});
      }
      // Web Share: present in Chrome; here the sheet is "cancelled"
      if (NP2 && !('share' in NP2)) {
        Object.defineProperty(NP2, 'share', {value: mk('share', function () {
          return W.Promise.reject(new W.DOMException('Share canceled', 'AbortError'));
        }, 0), writable: true, enumerable: true, configurable: true});
        Object.defineProperty(NP2, 'canShare', {value: mk('canShare', function (d) {
          return !!d && (typeof d.url === 'string' || typeof d.text === 'string' || typeof d.title === 'string');
        }, 0), writable: true, enumerable: true, configurable: true});
      }
      // Media Session (lock-screen controls in Chrome)
      if (NP2 && !('mediaSession' in NP2)) {
        var MS = iface('MediaSession');
        var meta = null, state = 'none';
        Object.defineProperty(MS.prototype, 'metadata', {get: mk('get metadata', function () { return meta; }),
          set: mk('set metadata', function (v) { meta = v || null; }), enumerable: true, configurable: true});
        Object.defineProperty(MS.prototype, 'playbackState', {get: mk('get playbackState', function () { return state; }),
          set: mk('set playbackState', function (v) { if (v === 'none' || v === 'paused' || v === 'playing') state = v; }), enumerable: true, configurable: true});
        ['setActionHandler', 'setCameraActive', 'setMicrophoneActive', 'setPositionState'].forEach(function (n) {
          Object.defineProperty(MS.prototype, n, {value: mk(n, function () {}, n === 'setPositionState' ? 0 : 1), writable: true, enumerable: true, configurable: true});
        });
        var session = Object.create(MS.prototype);
        Object.defineProperty(NP2, 'mediaSession', {get: mk('get mediaSession', function () { return session; }), enumerable: true, configurable: true});
        var MM = iface('MediaMetadata', function MediaMetadata(init) {
          if (!new.target) throw new W.TypeError("Failed to construct 'MediaMetadata': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
          var o = Object.create(MM.prototype);
          init = init || {};
          o.title = String(init.title || ''); o.artist = String(init.artist || ''); o.album = String(init.album || '');
          o.artwork = Object.freeze((init.artwork || []).slice());
          return o;
        });
      }
      // screen / window sizes as in Chrome: available area starts below the status bar and leaves out both
      // system bars, the window is the screen
      if (cfg && cfg.bars > 0 && W.Screen) {
        var S = W.Screen.prototype;
        getterOn(S, 'availHeight', function (g) { return Math.max(0, W.screen.height - cfg.bars); });
        if (cfg.st > 0) getterOn(S, 'availTop', function (g) { return cfg.st; });
      }
      // ---- the rest of Chrome-for-Android's interfaces (USB, Bluetooth, Serial, WebXR, Presentation, Fenced
      // Frames ...): same names, parents, constructor rules and member kinds as Chrome 157 (generated by
      // tools/make_chrome_ifaces.py from a DevTools read of the user's Chrome). Nothing here talks to hardware:
      // pickers report "cancelled", lists are empty, support checks say no. ----
      var IFACES = /*IFACES*/[["AuthenticatorResponse","",0,"i","clientDataJSON:g",""],["AuthenticatorAssertionResponse","AuthenticatorResponse",0,"i","authenticatorData:g|signature:g|userHandle:g",""],["AuthenticatorAttestationResponse","AuthenticatorResponse",0,"i","attestationObject:g|getAuthenticatorData:m0|getPublicKey:m0|getPublicKeyAlgorithm:m0|getTransports:m0",""],["Bluetooth","EventTarget",0,"i","getAvailability:m0|requestDevice:m0",""],["BluetoothCharacteristicProperties","",0,"i","broadcast:g|read:g|writeWithoutResponse:g|write:g|notify:g|indicate:g|authenticatedSignedWrites:g|reliableWrite:g|writableAuxiliaries:g",""],["BluetoothDevice","EventTarget",0,"i","id:g|name:g|gatt:g|ongattserverdisconnected:gs",""],["BluetoothRemoteGATTCharacteristic","EventTarget",0,"i","service:g|uuid:g|properties:g|value:g|oncharacteristicvaluechanged:gs|getDescriptor:m1|getDescriptors:m0|readValue:m0|startNotifications:m0|stopNotifications:m0|writeValue:m1|writeValueWithResponse:m1|writeValueWithoutResponse:m1",""],["BluetoothRemoteGATTDescriptor","",0,"i","characteristic:g|uuid:g|value:g|readValue:m0|writeValue:m1",""],["BluetoothRemoteGATTServer","",0,"i","device:g|connected:g|connect:m0|disconnect:m0|getPrimaryService:m1|getPrimaryServices:m0",""],["BluetoothRemoteGATTService","",0,"i","device:g|uuid:g|isPrimary:g|getCharacteristic:m1|getCharacteristics:m0",""],["CanvasPaintEvent","Event",1,"a","changedElements:g",""],["ElementGeometryUpdateEvent","Event",1,"a","elements:g",""],["ElementImage","",0,"i","close:m0",""],["DevicePosture","EventTarget",0,"i","type:g|onchange:gs",""],["DigitalCredential","Credential",0,"i","protocol:g|data:g|toJSON:m0","userAgentAllowsProtocol:m1"],["IdentityCredential","Credential",0,"i","token:g|isAutoSelected:g|configURL:g","disconnect:m1"],["IdentityCredentialError","DOMException",0,"o","code:g|error:g|url:g",""],["IdentityProvider","",0,"i","","close:m0|getUserInfo:m1|resolve:m1"],["NavigatorLogin","",0,"i","setStatus:m1",""],["LanguageDetector","",0,"i","expectedInputLanguages:g|inputQuota:g|destroy:m0|detect:m1|measureInputUsage:m1","availability:m0|create:m0"],["OTPCredential","Credential",0,"i","code:g",""],["PaymentAddress","",0,"i","city:g|country:g|dependentLocality:g|organization:g|phone:g|postalCode:g|recipient:g|region:g|sortingCode:g|addressLine:g|toJSON:m0",""],["PaymentRequestUpdateEvent","Event",1,"a","updateWith:m1",""],["PaymentResponse","EventTarget",0,"i","requestId:g|methodName:g|details:g|shippingAddress:g|shippingOption:g|payerName:g|payerEmail:g|payerPhone:g|onpayerdetailchange:gs|complete:m0|retry:m0|toJSON:m0",""],["PaymentManager","",0,"i","userHint:gs|enableDelegations:m1",""],["PaymentMethodChangeEvent","PaymentRequestUpdateEvent",1,"a","methodName:g|methodDetails:g",""],["Presentation","",0,"i","defaultRequest:gs|receiver:g",""],["PresentationAvailability","EventTarget",0,"i","value:g|onchange:gs",""],["PresentationConnection","EventTarget",0,"i","id:g|url:g|state:g|onconnect:gs|onclose:gs|onterminate:gs|binaryType:gs|onmessage:gs|close:m0|send:m1|terminate:m0",""],["PresentationConnectionAvailableEvent","Event",2,"a","connection:g",""],["PresentationConnectionCloseEvent","Event",2,"a","reason:g|message:g",""],["PresentationConnectionList","EventTarget",0,"i","connections:g|onconnectionavailable:gs",""],["PresentationReceiver","",0,"i","connectionList:g",""],["PresentationRequest","EventTarget",1,"a","onconnectionavailable:gs|getAvailability:m0|reconnect:m1|start:m0",""],["Serial","EventTarget",0,"i","onconnect:gs|ondisconnect:gs|getPorts:m0|requestPort:m0",""],["SerialPort","EventTarget",0,"i","onconnect:gs|ondisconnect:gs|readable:g|writable:g|close:m0|forget:m0|getInfo:m0|getSignals:m0|open:m1|setSignals:m0",""],["USB","EventTarget",0,"i","onconnect:gs|ondisconnect:gs|getDevices:m0|requestDevice:m1",""],["USBAlternateInterface","",2,"a","alternateSetting:g|interfaceClass:g|interfaceSubclass:g|interfaceProtocol:g|interfaceName:g|endpoints:g",""],["USBConfiguration","",2,"a","configurationValue:g|configurationName:g|interfaces:g",""],["USBConnectionEvent","Event",2,"a","device:g",""],["USBDevice","",0,"i","usbVersionMajor:g|usbVersionMinor:g|usbVersionSubminor:g|deviceClass:g|deviceSubclass:g|deviceProtocol:g|vendorId:g|productId:g|deviceVersionMajor:g|deviceVersionMinor:g|deviceVersionSubminor:g|manufacturerName:g|productName:g|serialNumber:g|configuration:g|configurations:g|opened:g|claimInterface:m1|clearHalt:m2|close:m0|controlTransferIn:m2|controlTransferOut:m1|forget:m0|isochronousTransferIn:m2|isochronousTransferOut:m3|open:m0|releaseInterface:m1|reset:m0|selectAlternateInterface:m2|selectConfiguration:m1|transferIn:m2|transferOut:m2",""],["USBEndpoint","",3,"a","endpointNumber:g|direction:g|type:g|packetSize:g",""],["USBInTransferResult","",1,"a","data:g|status:g",""],["USBInterface","",2,"a","interfaceNumber:g|alternate:g|alternates:g|claimed:g",""],["USBIsochronousInTransferPacket","",1,"a","status:g|data:g",""],["USBIsochronousInTransferResult","",1,"a","data:g|packets:g",""],["USBIsochronousOutTransferPacket","",1,"a","bytesWritten:g|status:g",""],["USBIsochronousOutTransferResult","",1,"a","packets:g",""],["USBOutTransferResult","",1,"a","bytesWritten:g|status:g",""],["XRAnchor","",0,"i","anchorSpace:g|delete:m0",""],["XRAnchorSet","",0,"i","size:g|entries:m0|forEach:m1|has:m1|keys:m0|values:m0",""],["XRSpace","EventTarget",0,"i","",""],["XRReferenceSpace","XRSpace",0,"i","onreset:gs|getOffsetReferenceSpace:m1",""],["XRBoundedReferenceSpace","XRReferenceSpace",0,"i","boundsGeometry:g",""],["XRDepthInformation","",0,"i","width:g|height:g|normDepthBufferFromNormView:g|rawValueToMeters:g|projectionMatrix:g|transform:g",""],["XRCPUDepthInformation","XRDepthInformation",0,"i","data:g|getDepthInMeters:m2",""],["XRCamera","",0,"i","width:g|height:g",""],["XRFrame","",0,"i","session:g|getPose:m2|getViewerPose:m1|trackedAnchors:g|createAnchor:m2|fillJointRadii:m2|fillPoses:m3|getDepthInformation:m1|getHitTestResults:m1|getHitTestResultsForTransientInput:m1|getJointPose:m2|getLightEstimate:m1|detectedPlanes:g",""],["XRHand","",0,"i","size:g|get:m1|entries:m0|forEach:m1|keys:m0|values:m0",""],["XRHitTestResult","",0,"i","getPose:m1|createAnchor:m0",""],["XRHitTestSource","",0,"i","cancel:m0",""],["XRInputSource","",0,"i","handedness:g|targetRayMode:g|targetRaySpace:g|gripSpace:g|gamepad:g|hand:g|profiles:g",""],["XRInputSourceArray","",0,"i","entries:m0|keys:m0|values:m0|forEach:m1|length:g",""],["XRInputSourceEvent","Event",2,"a","frame:g|inputSource:g",""],["XRInputSourcesChangeEvent","Event",2,"a","session:g|added:g|removed:g",""],["XRPose","",0,"i","transform:g|emulatedPosition:g",""],["XRJointPose","XRPose",0,"i","radius:g",""],["XRJointSpace","XRSpace",0,"i","jointName:g",""],["XRLightEstimate","",0,"i","sphericalHarmonicsCoefficients:g|primaryLightDirection:g|primaryLightIntensity:g",""],["XRLightProbe","EventTarget",0,"i","probeSpace:g|onreflectionchange:gs",""],["XRRay","",0,"o","origin:g|direction:g|matrix:g",""],["XRReferenceSpaceEvent","Event",2,"a","referenceSpace:g|transform:g",""],["XRRenderState","",0,"i","depthNear:g|depthFar:g|inlineVerticalFieldOfView:g|baseLayer:g|layers:g",""],["XRRigidTransform","",0,"o","position:g|orientation:g|matrix:g|inverse:g",""],["XRSession","EventTarget",0,"i","environmentBlendMode:g|interactionMode:g|visibilityState:g|renderState:g|inputSources:g|domOverlayState:g|preferredReflectionFormat:g|onend:gs|onselect:gs|oninputsourceschange:gs|onselectstart:gs|onselectend:gs|onvisibilitychange:gs|onsqueeze:gs|onsqueezestart:gs|onsqueezeend:gs|depthUsage:g|depthDataFormat:g|depthType:g|depthActive:g|cancelAnimationFrame:m1|end:m0|pauseDepthSensing:m0|requestAnimationFrame:m1|requestHitTestSource:m1|requestHitTestSourceForTransientInput:m1|requestLightProbe:m0|requestReferenceSpace:m1|resumeDepthSensing:m0|updateRenderState:m0|enabledFeatures:g|maxRenderLayers:g|onvisibilitymaskchange:gs|initiateRoomCapture:m0",""],["XRSessionEvent","Event",2,"a","session:g",""],["XRSystem","EventTarget",0,"i","ondevicechange:gs|isSessionSupported:m1|requestSession:m1",""],["XRTransientInputHitTestResult","",0,"i","inputSource:g|results:g",""],["XRTransientInputHitTestSource","",0,"i","cancel:m0",""],["XRView","",0,"i","eye:g|recommendedViewportScale:g|isFirstPersonObserver:g|camera:g|requestViewportScale:m1|index:g|projectionMatrix:g|transform:g",""],["XRViewerPose","XRPose",0,"i","views:g",""],["XRViewport","",0,"i","x:g|y:g|width:g|height:g",""],["XRWebGLDepthInformation","XRDepthInformation",0,"i","texture:g",""],["XRWebGLLayer","XRLayer",2,"a","antialias:g|ignoreDepthValues:g|framebufferWidth:g|framebufferHeight:g|framebuffer:g|getViewport:m1","getNativeFramebufferScaleFactor:m1"],["XRCompositionLayer","XRLayer",0,"i","layout:g|blendTextureSourceAlpha:gs|forceMonoPresentation:gs|opacity:gs|mipLevels:g|needsRedraw:g|destroy:m0",""],["XRProjectionLayer","XRCompositionLayer",0,"i","textureWidth:g|textureHeight:g|textureArrayLength:g|ignoreDepthValues:g|fixedFoveation:gs|deltaPose:gs",""],["XRCubeLayer","XRCompositionLayer",0,"i","space:gs|orientation:gs|onredraw:gs",""],["XRCylinderLayer","XRCompositionLayer",0,"i","space:gs|transform:gs|radius:gs|centralAngle:gs|aspectRatio:gs|onredraw:gs",""],["XREquirectLayer","XRCompositionLayer",0,"i","space:gs|transform:gs|radius:gs|centralHorizontalAngle:gs|upperVerticalAngle:gs|lowerVerticalAngle:gs|onredraw:gs",""],["XRLayerEvent","Event",2,"a","layer:g",""],["XRQuadLayer","XRCompositionLayer",0,"i","space:gs|transform:gs|width:gs|height:gs|onredraw:gs",""],["XRSubImage","",0,"i","viewport:g",""],["XRWebGLSubImage","XRSubImage",0,"i","colorTexture:g|depthStencilTexture:g|motionVectorTexture:g|imageIndex:g|colorTextureWidth:g|colorTextureHeight:g|depthStencilTextureWidth:g|depthStencilTextureHeight:g|motionVectorTextureWidth:g|motionVectorTextureHeight:g",""],["XRPlane","",0,"i","planeSpace:g|polygon:g|orientation:g|lastChangedTime:g|semanticLabel:g",""],["XRPlaneSet","",0,"i","size:g|entries:m0|forEach:m1|has:m1|keys:m0|values:m0",""],["XRVisibilityMaskChangeEvent","Event",2,"a","session:g|eye:g|index:g|vertices:g|indices:g",""],["BackgroundFetchManager","",0,"i","fetch:m2|get:m1|getIds:m0",""],["BackgroundFetchRecord","",0,"i","request:g|responseReady:g",""],["BackgroundFetchRegistration","EventTarget",0,"i","id:g|uploadTotal:g|uploaded:g|downloadTotal:g|downloaded:g|result:g|failureReason:g|recordsAvailable:g|onprogress:gs|abort:m0|match:m1|matchAll:m0",""],["BluetoothUUID","",0,"i","","canonicalUUID:m1|getCharacteristic:m1|getDescriptor:m1|getService:m1"],["Fence","",0,"i","getNestedConfigs:m0|reportEvent:m1|setReportEventDataForAutomaticBeacons:m1",""],["FencedFrameConfig","",0,"i","",""],["HTMLFencedFrameElement","HTMLElement",0,"i","width:gs|height:gs|sandbox:gs|config:gs|allow:gs",""],["HTMLCameraElement","HTMLElement",0,"i","error:g|oncancel:gs|onerror:gs|ontrack:gs|track:g|setConstraints:m0",""],["HTMLMicrophoneElement","HTMLElement",0,"i","error:g|oncancel:gs|onerror:gs|ontrack:gs|track:g|setConstraints:m0",""],["HTMLGeolocationElement","HTMLElement",0,"i","onlocation:gs|position:g|error:g|accuracymode:gs|autolocate:gs|watch:gs|isValid:g|invalidReason:g|initialPermissionStatus:g|permissionStatus:g|onpromptaction:gs|onpromptdismiss:gs|onvalidationstatuschange:gs",""],["HTMLUserMediaElement","HTMLElement",0,"i","onstream:gs|stream:g|error:g|oncancel:gs|onerror:gs|setConstraints:m0",""],["LaunchParams","",0,"i","targetURL:g|files:g",""],["LaunchQueue","",0,"i","setConsumer:m1",""],["PeriodicSyncManager","",0,"i","getTags:m0|register:m1|unregister:m1",""],["PushSubscription","",0,"i","endpoint:g|expirationTime:g|options:g|getKey:m1|toJSON:m0|unsubscribe:m0",""],["PushSubscriptionOptions","",0,"i","userVisibleOnly:g|applicationServerKey:g",""],["RemotePlayback","EventTarget",0,"i","state:g|onconnecting:gs|onconnect:gs|ondisconnect:gs|cancelWatchAvailability:m0|prompt:m0|watchAvailability:m1",""],["SpeechSynthesisEvent","Event",2,"a","utterance:g|charIndex:g|charLength:g|elapsedTime:g|name:g",""],["SpeechSynthesisErrorEvent","SpeechSynthesisEvent",2,"a","error:g",""],["TrustedHTMLParserOptions","",0,"i","",""],["Viewport","",0,"i","segments:g",""]]/*IFACES_END*/;
      IFACES.forEach(function (x) {
        try {
          var name = x[0], parentName = x[1], len = x[2], kind = x[3];
          if (typeof W[name] !== 'undefined') return;
          var Parent = parentName ? W[parentName] : null;
          var C = mk(name, function () {
            if (kind === 'i') throw new W.TypeError('Illegal constructor');
            if (!new.target) throw new W.TypeError("Failed to construct '" + name + "': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
            if (arguments.length < len) throw new W.TypeError("Failed to construct '" + name + "': " + len + ' argument' + (len > 1 ? 's' : '') + ' required, but only ' + arguments.length + ' present.');
            if (Parent) { try { return Reflect.construct(Parent, Array.prototype.slice.call(arguments), new.target); } catch (e) {} }
            return Object.create(new.target.prototype);
          }, len);
          var P = Object.create(Parent ? Parent.prototype : W.Object.prototype);
          Object.defineProperty(P, 'constructor', {value: C, writable: true, enumerable: false, configurable: true});
          Object.defineProperty(P, W.Symbol.toStringTag, {value: name, configurable: true});
          Object.defineProperty(C, 'prototype', {value: P, writable: false, enumerable: false, configurable: false});
          if (Parent) Object.setPrototypeOf(C, Parent);
          var members = function (target, list, isStatic) {
            (list ? list.split('|') : []).forEach(function (m) {
              var i = m.indexOf(':'), k = m.substring(0, i), t = m.substring(i + 1);
              if (!k || Object.prototype.hasOwnProperty.call(target, k)) return;
              var check = function (o) { if (!isStatic && !(o instanceof C)) throw new W.TypeError('Illegal invocation'); };
              if (t.charAt(0) === 'm') {
                Object.defineProperty(target, k, {value: mk(k, function () { check(this); }, +t.substring(1) || 0),
                  writable: true, enumerable: true, configurable: true});
              } else if (t.charAt(0) === 'g') {
                var store = new W.WeakMap();
                var d = {get: mk('get ' + k, function () { check(this); return store.has(this) ? store.get(this) : null; }),
                  enumerable: true, configurable: true};
                if (t === 'gs') d.set = mk('set ' + k, function (v) { check(this); store.set(this, v); });
                Object.defineProperty(target, k, d);
              } else if (t.charAt(0) === 'v') {
                var v; try { v = JSON.parse(t.substring(1)); } catch (e) { v = null; }
                Object.defineProperty(target, k, {value: v, writable: false, enumerable: true, configurable: false});
              }
            });
          };
          members(P, x[4], false);
          members(C, x[5], true);
          Object.defineProperty(W, name, {value: C, writable: true, enumerable: false, configurable: true});
        } catch (e) {}
      });
      // navigator objects, with Chrome-like answers
      var inst = function (name) { var C = W[name]; return C && C.prototype ? Object.create(C.prototype) : null; };
      var rej = function (msg, nm) { return W.Promise.reject(new W.DOMException(msg, nm)); };
      var method = function (o, k, fn, len) {
        if (o) Object.defineProperty(o, k, {value: mk(k, fn, len), writable: true, enumerable: true, configurable: true});
      };
      // prototype methods: only on real instances, like Chrome ("Illegal invocation" otherwise)
      var pm = function (P, k, fn, len) {
        method(P, k, function () {
          if (!(this instanceof P.constructor)) throw new W.TypeError('Illegal invocation');
          return fn.apply(this, arguments);
        }, len);
      };
      if (NP2) {
        var navObj = function (prop, iname, setup) {
          if (prop in NP2) return;
          var o = inst(iname);
          if (!o) return;
          if (setup) setup(Object.getPrototypeOf(o), o);
          Object.defineProperty(NP2, prop, {get: mk('get ' + prop, function () { return o; }), enumerable: true, configurable: true});
        };
        navObj('bluetooth', 'Bluetooth', function (P) {
          pm(P, 'getAvailability', function () { return W.Promise.resolve(true); }, 0);
          pm(P, 'requestDevice', function () { return rej('User cancelled the requestDevice() chooser.', 'NotFoundError'); }, 0);
        });
        navObj('usb', 'USB', function (P) {
          pm(P, 'getDevices', function () { return W.Promise.resolve([]); }, 0);
          pm(P, 'requestDevice', function () { return rej('No device selected.', 'NotFoundError'); }, 1);
        });
        navObj('serial', 'Serial', function (P) {
          pm(P, 'getPorts', function () { return W.Promise.resolve([]); }, 0);
          pm(P, 'requestPort', function () { return rej('No port selected by the user.', 'NotFoundError'); }, 0);
        });
        navObj('xr', 'XRSystem', function (P) {
          pm(P, 'isSessionSupported', function () { return W.Promise.resolve(false); }, 1);
          pm(P, 'requestSession', function () { return rej('The specified session configuration is not supported.', 'NotSupportedError'); }, 1);
        });
        navObj('presentation', 'Presentation');
        navObj('login', 'NavigatorLogin', function (P) {
          pm(P, 'setStatus', function () { return W.Promise.resolve(); }, 1);
        });
        navObj('devicePosture', 'DevicePosture', function (P) {
          Object.defineProperty(P, 'type', {get: mk('get type', function () { return 'continuous'; }), enumerable: true, configurable: true});
        });
        if (!('getInstalledRelatedApps' in NP2))
          method(NP2, 'getInstalledRelatedApps', function () { return W.Promise.resolve([]); }, 0);
      }
      // window values Chrome has
      var winVal = function (k, v) {
        if (!(k in W)) Object.defineProperty(W, k, {value: v, writable: true, enumerable: true, configurable: true});
      };
      winVal('fence', null);
      var lq = inst('LaunchQueue');
      if (lq) { pm(Object.getPrototypeOf(lq), 'setConsumer', function () {}, 1); winVal('launchQueue', lq); }
      var vp = inst('Viewport');
      if (vp) {
        Object.defineProperty(Object.getPrototypeOf(vp), 'segments', {get: mk('get segments', function () { return null; }), enumerable: true, configurable: true});
        winVal('viewport', vp);
      }
      if (!('getDigitalGoodsService' in W))
        winVal('getDigitalGoodsService', mk('getDigitalGoodsService', function () {
          return rej('Payment method is not supported.', 'OperationError'); }, 1));
      var fsErr = function (cb) { if (typeof cb === 'function') W.setTimeout(function () {
        cb(new W.DOMException('An attempt was made to break through the security policy of the user agent.', 'SecurityError')); }, 0); };
      if (!('webkitRequestFileSystem' in W))
        winVal('webkitRequestFileSystem', mk('webkitRequestFileSystem', function (t, sz, ok, err) { fsErr(err); }, 3));
      if (!('webkitResolveLocalFileSystemURL' in W))
        winVal('webkitResolveLocalFileSystemURL', mk('webkitResolveLocalFileSystemURL', function (u, ok, err) { fsErr(err); }, 2));
      // Push API, Payment Request, WebAuthn: in Chrome, missing in WebView (detectors check them)
      if (typeof W.PushManager === 'undefined') {
        var PM = iface('PushManager');
        Object.defineProperty(PM, 'supportedContentEncodings', {get: mk('get supportedContentEncodings', function () {
          return Object.freeze(['aes128gcm', 'aesgcm']); }), enumerable: true, configurable: true});
        ['getSubscription', 'permissionState', 'subscribe'].forEach(function (n) {
          Object.defineProperty(PM.prototype, n, {value: mk(n, function () {
            return n === 'subscribe' ? W.Promise.reject(new W.DOMException('Registration failed - permission denied', 'NotAllowedError'))
              : W.Promise.resolve(n === 'permissionState' ? 'prompt' : null);
          }, 0), writable: true, enumerable: true, configurable: true});
        });
      }
      if (typeof W.PaymentRequest === 'undefined') {
        var PR = iface('PaymentRequest', function PaymentRequest(methods, details) {
          if (!new.target) throw new W.TypeError("Failed to construct 'PaymentRequest': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
          if (arguments.length < 2) throw new W.TypeError("Failed to construct 'PaymentRequest': 2 arguments required, but only " + arguments.length + " present.");
          var o = Reflect.construct(W.EventTarget, [], new.target);
          return o;
        });
        Object.setPrototypeOf(PR.prototype, W.EventTarget.prototype);
        Object.setPrototypeOf(PR, W.EventTarget);
        ['abort', 'canMakePayment', 'hasEnrolledInstrument', 'show'].forEach(function (n) {
          Object.defineProperty(PR.prototype, n, {value: mk(n, function () {
            if (n === 'show') return W.Promise.reject(new W.DOMException('The payment method is not supported', 'NotSupportedError'));
            if (n === 'abort') return W.Promise.reject(new W.DOMException('No show() or retry() in progress, so nothing to abort', 'InvalidStateError'));
            return W.Promise.resolve(false);
          }, 0), writable: true, enumerable: true, configurable: true});
        });
        ['id', 'shippingAddress', 'shippingOption', 'shippingType'].forEach(function (n) {
          Object.defineProperty(PR.prototype, n, {get: mk('get ' + n, function () { return n === 'id' ? '' : null; }), enumerable: true, configurable: true});
        });
      }
      if (typeof W.PublicKeyCredential === 'undefined') {
        var PK = iface('PublicKeyCredential');
        var yes = function (n) { return mk(n, function () { return W.Promise.resolve(true); }, 0); };
        [['isUserVerifyingPlatformAuthenticatorAvailable', yes('isUserVerifyingPlatformAuthenticatorAvailable')],
         ['isConditionalMediationAvailable', yes('isConditionalMediationAvailable')],
         ['getClientCapabilities', mk('getClientCapabilities', function () {
           return W.Promise.resolve({conditionalCreate: false, conditionalGet: true, hybridTransport: true, passkeyPlatformAuthenticator: true,
             userVerifyingPlatformAuthenticator: true, relatedOrigins: true, signalAllAcceptedCredentials: true,
             signalCurrentUserDetails: true, signalUnknownCredential: true});
         }, 0)]].forEach(function (x) {
          Object.defineProperty(PK, x[0], {value: x[1], writable: true, enumerable: true, configurable: true});
        });
      }
      if (cfg) {
        getterOn(W, 'outerHeight', function (g) { return W.screen.height; });
        getterOn(W, 'outerWidth', function (g) { return W.screen.width; });
      }
    } catch (e) {}
  }
  function privacy(cfg) {
    var base = (cfg.seed >>> 0) || 1;
    function rng(seed) {
      return function () {
        seed = (seed + 0x6D2B79F5) | 0;
        var t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
      };
    }
    // same picture -> same tiny noise within this session and site; new noise next session
    function noisePixels(data) {
      var r = rng(base), n = data.length;
      for (var i = 0; i < n; i += 4) {
        if (r() < 0.04) {
          var ch = i + ((r() * 3) | 0);
          data[ch] = data[ch] ^ 1;
        }
      }
    }
    var NP = window.Navigator && Navigator.prototype;
    if (NP && DBG.indexOf('m') < 0) {
      getterOn(NP, 'hardwareConcurrency', 8);
      getterOn(NP, 'deviceMemory', 8);
      // Chrome on arm64 phones says "Linux armv81"; WebView says "Linux aarch64"
      getterOn(NP, 'platform', function (g) { var v = g.call(this); return v === 'Linux aarch64' ? 'Linux armv81' : v; });
      // Chrome lists the base language after each regional one ("en-US", "en"); WebView only the regional one
      var langs = null;
      getterOn(NP, 'languages', function (g) {
        if (langs) return langs;
        var l = (g.call(this) || []).slice(), out = [];
        l.forEach(function (x) {
          if (out.indexOf(x) < 0) out.push(x);
          var base = String(x).split('-')[0];
          if (base !== x && l.indexOf(base) < 0 && out.indexOf(base) < 0) out.push(base);
        });
        langs = Object.freeze(out);
        return langs;
      });
    }
    chromeLike(window, cfg);
    realmHooks.push(function (w) { chromeLike(w, cfg); });
    if (window.NetworkInformation && DBG.indexOf('i') < 0) {
      var NI = NetworkInformation.prototype;
      getterOn(NI, 'effectiveType', '4g');
      getterOn(NI, 'rtt', 50);
      getterOn(NI, 'downlink', 10);
      getterOn(NI, 'saveData', false);
      // Wi-Fi vs mobile data: always report mobile data, matching the 4g values above
      getterOn(NI, 'type', 'cellular');
      getterOn(NI, 'downlinkMax', Infinity);
    }
    batteryMask();
    speechMask(window);
    realmHooks.push(speechMask);
    // client hints from JS: Chrome brands, no phone model, generic Android version
    if (window.NavigatorUAData && DBG.indexOf('u') < 0) {
      var UD = NavigatorUAData.prototype;
      var fix = function (list) {
        return (list || []).map(function (b) {
          return /WebView/i.test(b.brand) ? {brand: 'Google Chrome', version: b.version} : b;
        });
      };
      getterOn(UD, 'brands', function (g) { return fix(g.call(this)); });
      methodOn(UD, 'getHighEntropyValues', function (f) {
        return {getHighEntropyValues(hints) {
          return f.apply(this, arguments).then(function (v) {
            if (v.brands) v.brands = fix(v.brands);
            if (v.fullVersionList) v.fullVersionList = fix(v.fullVersionList);
            return v;
          });
        }}.getHighEntropyValues;
      });
    }
    // canvas fingerprints
    var CP = window.HTMLCanvasElement && HTMLCanvasElement.prototype;
    var C2 = window.CanvasRenderingContext2D && CanvasRenderingContext2D.prototype;
    var origGet = C2 && C2.getImageData;
    function noisyCopy(canvas) {
      var w = canvas.width, h = canvas.height;
      if (!w || !h || w * h > 1048576) return null;
      var c = document.createElement('canvas');
      c.width = w; c.height = h;
      var x = c.getContext('2d');
      x.drawImage(canvas, 0, 0);
      var img = origGet.call(x, 0, 0, w, h);
      noisePixels(img.data);
      x.putImageData(img, 0, 0);
      return c;
    }
    if (CP && C2 && cfg.cn && DBG.indexOf('v') < 0) {
      methodOn(CP, 'toDataURL', function (f) {
        return {toDataURL() {
          try { var c = noisyCopy(this); if (c) return f.apply(c, arguments); } catch (e) {}
          return f.apply(this, arguments);
        }}.toDataURL;
      });
      methodOn(CP, 'toBlob', function (f) {
        return {toBlob() {
          try { var c = noisyCopy(this); if (c) return f.apply(c, arguments); } catch (e) {}
          return f.apply(this, arguments);
        }}.toBlob;
      });
      methodOn(C2, 'getImageData', function (f) {
        return {getImageData() {
          var img = f.apply(this, arguments);
          try { if (img.data.length <= 4194304) noisePixels(img.data); } catch (e) {}
          return img;
        }}.getImageData;
      });
    }
    // WebGL: the real GPU name stays (a fake one contradicts the GPU's real limits and gets flagged);
    // only the rendered pixels read back get the per-session noise
    [window.WebGLRenderingContext, window.WebGL2RenderingContext].forEach(function (G) {
      if (!G || !cfg.cn || DBG.indexOf('g') >= 0) return;
      methodOn(G.prototype, 'readPixels', function (f) {
        return {readPixels() {
          var r = f.apply(this, arguments);
          try { var px = arguments[6]; if (px && px.length <= 4194304) noisePixels(px); } catch (e) {}
          return r;
        }}.readPixels;
      });
    });
    // audio fingerprints: inaudible noise, only on audio the browser rendered (OfflineAudioContext),
    // never on buffers a page fills itself (a known-values read-back would show the change)
    if (window.AudioBuffer && DBG.indexOf('a') < 0) {
      var done = new WeakSet(), rendered = new WeakSet();
      [window.OfflineAudioContext, window.webkitOfflineAudioContext].forEach(function (OAC) {
        if (!OAC) return;
        methodOn(OAC.prototype, 'startRendering', function (f) {
          return {startRendering() {
            var p = f.apply(this, arguments);
            return p && p.then ? p.then(function (b) { try { rendered.add(b); } catch (e) {} return b; }) : p;
          }}.startRendering;
        });
        try {
          var od = Object.getOwnPropertyDescriptor(OAC.prototype, 'oncomplete')
            || Object.getOwnPropertyDescriptor(Object.getPrototypeOf(OAC.prototype), 'oncomplete');
          if (od && od.set) Object.defineProperty(OAC.prototype, 'oncomplete', {
            get: od.get, configurable: true, enumerable: od.enumerable,
            set: asNative({set oncomplete(h) {
              od.set.call(this, typeof h === 'function'
                ? function (e) { try { if (e && e.renderedBuffer) rendered.add(e.renderedBuffer); } catch (x) {} return h.apply(this, arguments); }
                : h);
            }}.__lookupSetter__('oncomplete'), od.set)
          });
        } catch (e) {}
      });
      // copyFromChannel reads the same (noised) samples, so both read-backs always agree
      methodOn(AudioBuffer.prototype, 'copyFromChannel', function (f) {
        return {copyFromChannel(dest, ch) {
          try { if (rendered.has(this)) this.getChannelData(ch); } catch (e) {}
          return f.apply(this, arguments);
        }}.copyFromChannel;
      });
      methodOn(AudioBuffer.prototype, 'getChannelData', function (f) {
        return {getChannelData() {
          var d = f.apply(this, arguments);
          try {
            if (rendered.has(this) && !done.has(d)) {
              done.add(d);
              var r = rng(base ^ 0x5bd1e995);
              // relative noise on sounding samples only: silence stays exactly 0, values keep their scale
              for (var i = 0; i < d.length; i += 97) {
                var v = d[i], n = r();
                if (v !== 0 && v === v) d[i] = v * (1 + (n - 0.5) * 2e-7);
              }
            }
          } catch (e) {}
          return d;
        }}.getChannelData;
      });
    }
    if (window.AnalyserNode && DBG.indexOf('a') < 0) {
      methodOn(AnalyserNode.prototype, 'getFloatFrequencyData', function (f) {
        return {getFloatFrequencyData(a) {
          var r0 = f.apply(this, arguments);
          try { var r = rng(base ^ 0x27d4eb2d); for (var i = 0; i < a.length; i += 7) a[i] += (r() - 0.5) * 1e-4; } catch (e) {}
          return r0;
        }}.getFloatFrequencyData;
      });
    }
    // WebRTC: never hand out local network addresses
    if (window.RTCPeerConnection && DBG.indexOf('w') < 0) {
      var RP = RTCPeerConnection.prototype;
      var hostIp = /\btyp host\b/;
      var badCand = function (c) {
        var s = c && (c.candidate || c);
        return typeof s === 'string' && hostIp.test(s) && !/\.local\b/.test(s);
      };
      var cleanSdp = function (sdp) {
        return typeof sdp === 'string'
          ? sdp.split('\r\n').filter(function (l) { return !(l.indexOf('a=candidate:') === 0 && badCand(l)); }).join('\r\n')
          : sdp;
      };
      var wrapListener = function (l) {
        if (typeof l !== 'function') return l;
        return function (ev) { if (ev && ev.candidate && badCand(ev.candidate)) return; return l.apply(this, arguments); };
      };
      var map = new WeakMap();
      methodOn(RP, 'addEventListener', function (f) {
        return {addEventListener(type, l) {
          var a = Array.prototype.slice.call(arguments);
          if (type === 'icecandidate' && typeof l === 'function') {
            if (!map.has(l)) map.set(l, wrapListener(l));
            a[1] = map.get(l);
          }
          return f.apply(this, a);
        }}.addEventListener;
      });
      methodOn(RP, 'removeEventListener', function (f) {
        return {removeEventListener(type, l) {
          var a = Array.prototype.slice.call(arguments);
          if (type === 'icecandidate' && map.has(l)) a[1] = map.get(l);
          return f.apply(this, a);
        }}.removeEventListener;
      });
      try {
        var od = Object.getOwnPropertyDescriptor(RP, 'onicecandidate');
        if (od && od.set) Object.defineProperty(RP, 'onicecandidate', {
          get: od.get, set: asNative({set onicecandidate(v) { od.set.call(this, wrapListener(v)); }}
            .__lookupSetter__('onicecandidate'), od.set), configurable: true, enumerable: od.enumerable});
      } catch (e) {}
      ['localDescription', 'currentLocalDescription', 'pendingLocalDescription'].forEach(function (n) {
        getterOn(RP, n, function (g) {
          var d = g.call(this);
          if (!d || !d.sdp) return d;
          try { return new RTCSessionDescription({type: d.type, sdp: cleanSdp(d.sdp)}); } catch (e) { return d; }
        });
      });
    }
    // ---- tracker IDs: real-looking values that change every app session ----
    // Dynatrace, Google, Meta, Microsoft, Hotjar, TikTok, Segment ... visitor / session IDs
    var TRACK = /^(rxVisitor|rxvt|dtCookie|dtPC|dtSa|dtLatC|_ga|_ga_[A-Za-z0-9]+|_gid|_gcl_au|_gcl_aw|_gcl_dc|_fbp|_fbc|_clck|_clsk|_uetvid|_uetsid|_hjSessionUser_\d+|_hjSession_\d+|_ttp|_tt_enable_cookie|ajs_anonymous_id|ajs_user_id|_pin_unauth|_scid|_sctr|li_fat_id|_rdt_uuid|__hstc|hubspotutk|_hjid|mp_[a-f0-9]+_mixpanel|amplitude_id_\w+|_vwo_uuid\w*|_kuid_|IDE|test_cookie)$/;
    var masks = Object.create(null);
    function hashStr(s) { var h = 2166136261; for (var i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); } return h >>> 0; }
    var DIG = '0123456789', UP = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', LO = 'abcdefghijklmnopqrstuvwxyz';
    // same shape (digits stay digits, letters stay letters, separators kept), new content; epoch times
    // inside become this session's start time so the value still makes sense to the site
    function mask(name, value) {
      if (!value) return value;
      var shape = value.replace(/\d/g, '0').replace(/[A-Z]/g, 'A').replace(/[a-z]/g, 'a');
      var key = name + '\u0000' + shape;
      if (masks[key] !== undefined) return masks[key];
      var r = rng(base ^ hashStr(key)), out = '', i = 0;
      var HEX = '0123456789abcdef';
      // a whole UUID stays a valid UUID (version 4, variant 8-b, same letter case)
      if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) {
        var u = '';
        for (var q = 0; q < 36; q++) {
          if (q === 8 || q === 13 || q === 18 || q === 23) { u += '-'; continue; }
          if (q === 14) { u += '4'; continue; }
          if (q === 19) { u += HEX.charAt(8 + ((r() * 4) | 0)); continue; }
          u += HEX.charAt((r() * 16) | 0);
        }
        u = value === value.toUpperCase() ? u.toUpperCase() : u;
        masks[key] = u;
        return u;
      }
      while (i < value.length) {
        // runs of letters / digits; short ones (versions like "GA1.1", server codes like "-21$") stay
        var m = /^[A-Za-z0-9]+/.exec(value.substring(i));
        if (!m) {
          out += value.charAt(i);
          i++;
          continue;
        }
        var run = m[0];
        i += run.length;
        // short runs (versions "GA1", codes "-21") and constant padding ("000000...") stay as they are
        if (run.length <= 3 || /^(.)\1*$/.test(run)) {
          out += run;
          continue;
        }
        if (/^\d+$/.test(run) && run.length === 13 && /^1[6-9]/.test(run)) { out += String(cfg.start); continue; }
        if (/^\d+$/.test(run) && run.length === 10 && /^1[6-9]/.test(run)) { out += String(Math.floor(cfg.start / 1000)); continue; }
        // a leading epoch time glued to an ID (Dynatrace rxVisitor "1794817156227A5D5...")
        // hex runs stay valid hex in the same letter case
        if (/^[0-9a-f]+$/.test(run) && /[a-f]/.test(run)) {
          for (var x = 0; x < run.length; x++) out += HEX.charAt((r() * 16) | 0);
          continue;
        }
        if (/^[0-9A-F]+$/.test(run) && /[A-F]/.test(run)) {
          for (var y = 0; y < run.length; y++) out += HEX.charAt((r() * 16) | 0).toUpperCase();
          continue;
        }
        var lead = /^1[6-9]\d{11}(?=[A-Z])/.exec(run);
        var k = 0;
        if (lead) { out += String(cfg.start); k = 13; }
        for (; k < run.length; k++) {
          var ch = run.charAt(k);
          out += DIG.indexOf(ch) >= 0 ? DIG.charAt((r() * 10) | 0)
               : UP.indexOf(ch) >= 0 ? UP.charAt((r() * 26) | 0)
               : LO.charAt((r() * 26) | 0);
        }
      }
      masks[key] = out;
      return out;
    }
    function maskCookieString(str) {
      if (!str) return str;
      return str.split('; ').map(function (p) {
        var eq = p.indexOf('=');
        if (eq <= 0) return p;
        var n = p.substring(0, eq);
        return TRACK.test(n) ? n + '=' + mask(n, p.substring(eq + 1)) : p;
      }).join('; ');
    }
    try {
      var cd = Object.getOwnPropertyDescriptor(Document.prototype, 'cookie');
      if (cd && cd.get && cd.set) {
        var realGet = cd.get, realSet = cd.set;
        // rewrite stored tracker cookies (also ones the server set) with their masked values
        try {
          var dom = cfg.site ? '; domain=' + cfg.site : '';
          realGet.call(document).split('; ').forEach(function (p) {
            var eq = p.indexOf('=');
            if (eq <= 0) return;
            var n = p.substring(0, eq), v = p.substring(eq + 1);
            if (!TRACK.test(n)) return;
            var mv = mask(n, v);
            if (mv === v) return;
            realSet.call(document, n + '=' + mv + '; path=/' + dom);
            realSet.call(document, n + '=' + mv + '; path=/');
          });
        } catch (e) {}
        Object.defineProperty(Document.prototype, 'cookie', {
          configurable: true, enumerable: cd.enumerable,
          get: asNative({get cookie() { return maskCookieString(realGet.call(this)); }}.__lookupGetter__('cookie'), realGet),
          set: asNative({set cookie(v) {
            try {
              var s = String(v), semi = s.indexOf(';'), pair = semi < 0 ? s : s.substring(0, semi), eq = pair.indexOf('=');
              if (eq > 0) {
                var n = pair.substring(0, eq).trim();
                if (TRACK.test(n)) v = n + '=' + mask(n, pair.substring(eq + 1)) + (semi < 0 ? '' : s.substring(semi));
              }
            } catch (e) {}
            return realSet.call(this, v);
          }}.__lookupSetter__('cookie'), realSet)
        });
      }
    } catch (e) {}
    // the same IDs kept in localStorage / sessionStorage
    if (window.Storage) {
      methodOn(Storage.prototype, 'getItem', function (f) {
        return {getItem(k) {
          var v = f.apply(this, arguments);
          return v != null && TRACK.test(String(k)) ? mask(String(k), String(v)) : v;
        }}.getItem;
      });
      methodOn(Storage.prototype, 'setItem', function (f) {
        return {setItem(k, v) {
          if (TRACK.test(String(k))) return f.call(this, k, mask(String(k), String(v)));
          return f.apply(this, arguments);
        }}.setItem;
      });
    }
    // ---- time zone of the connection (VPN / proxy location), so the clock never contradicts the IP ----
    if (cfg.tz) (function (TZ) {
      var OD = Date, DP = Date.prototype, ODTF = Intl.DateTimeFormat;
      var parts;
      try {
        parts = new ODTF('en-US', {timeZone: TZ, hourCycle: 'h23', year: 'numeric', month: '2-digit',
          day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit'});
      } catch (e) { return; } // unknown zone name
      var nameFmt = new ODTF('en-US', {timeZone: TZ, timeZoneName: 'long'});
      var gt = DP.getTime;
      var cache = {t: NaN, o: 0};
      // minutes east of UTC in TZ at time t (follows that zone's daylight saving)
      function off(t) {
        if (t !== t) return NaN;
        var k = Math.floor(t / 60000);
        if (cache.t === k) return cache.o;
        var p = {};
        parts.formatToParts(new OD(t)).forEach(function (x) { p[x.type] = x.value; });
        var asUtc = OD.UTC(+p.year, +p.month - 1, +p.day, +p.hour % 24, +p.minute, +p.second);
        var o = Math.round((asUtc - (t - ((t % 1000) + 1000) % 1000)) / 60000);
        cache = {t: k, o: o};
        return o;
      }
      function wall(d) { var t = gt.call(d); return new OD(t + off(t) * 60000); }
      [['getFullYear', 'getUTCFullYear'], ['getMonth', 'getUTCMonth'], ['getDate', 'getUTCDate'],
       ['getDay', 'getUTCDay'], ['getHours', 'getUTCHours'], ['getMinutes', 'getUTCMinutes'],
       ['getSeconds', 'getUTCSeconds']].forEach(function (m) {
        methodOn(DP, m[0], function (f) {
          var o = {}; o[m[0]] = function () { var t = gt.call(this); return t !== t ? NaN : OD.prototype[m[1]].call(wall(this)); };
          return o[m[0]];
        });
      });
      methodOn(DP, 'getYear', function (f) {
        return {getYear() { var t = gt.call(this); return t !== t ? NaN : OD.prototype.getUTCFullYear.call(wall(this)) - 1900; }}.getYear;
      });
      methodOn(DP, 'getTimezoneOffset', function (f) {
        return {getTimezoneOffset() { return -off(gt.call(this)); }}.getTimezoneOffset;
      });
      function pad(n) { return (n < 10 ? '0' : '') + n; }
      var DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
      var MONS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
      function gmt(t) {
        var o = off(t), s = o < 0 ? '-' : '+'; o = Math.abs(o);
        var name = '';
        try { nameFmt.formatToParts(new OD(t)).forEach(function (x) { if (x.type === 'timeZoneName') name = x.value; }); } catch (e) {}
        return 'GMT' + s + pad(Math.floor(o / 60)) + pad(o % 60) + (name ? ' (' + name + ')' : '');
      }
      function dateStr(w) { return DAYS[w.getUTCDay()] + ' ' + MONS[w.getUTCMonth()] + ' ' + pad(w.getUTCDate()) + ' ' + w.getUTCFullYear(); }
      function timeStr(w, t) { return pad(w.getUTCHours()) + ':' + pad(w.getUTCMinutes()) + ':' + pad(w.getUTCSeconds()) + ' ' + gmt(t); }
      methodOn(DP, 'toString', function (f) {
        return {toString() { var t = gt.call(this); if (t !== t) return 'Invalid Date'; var w = wall(this); return dateStr(w) + ' ' + timeStr(w, t); }}.toString;
      });
      methodOn(DP, 'toDateString', function (f) {
        return {toDateString() { var t = gt.call(this); return t !== t ? 'Invalid Date' : dateStr(wall(this)); }}.toDateString;
      });
      methodOn(DP, 'toTimeString', function (f) {
        return {toTimeString() { var t = gt.call(this); return t !== t ? 'Invalid Date' : timeStr(wall(this), t); }}.toTimeString;
      });
      ['toLocaleString', 'toLocaleDateString', 'toLocaleTimeString'].forEach(function (n) {
        methodOn(DP, n, function (f) {
          var o = {}; o[n] = function (loc, opt) {
            var p = Object.assign({}, opt || {}); if (!p.timeZone) p.timeZone = TZ;
            return f.call(this, loc, p);
          };
          return o[n];
        });
      });
      // Intl: a formatter made without a timeZone uses the connection's zone
      try {
        var DTF = asNative(function DateTimeFormat() {
          var p = Object.assign({}, arguments[1] || {}); if (!p.timeZone) p.timeZone = TZ;
          return new ODTF(arguments[0], p);
        }, ODTF);
        Object.defineProperty(DTF, 'prototype', {value: ODTF.prototype, writable: false, enumerable: false, configurable: false});
        try { Object.defineProperty(ODTF.prototype, 'constructor', {value: DTF, writable: true, enumerable: false, configurable: true}); } catch (e) {}
        DTF.supportedLocalesOf = ODTF.supportedLocalesOf;
        Object.defineProperty(Intl, 'DateTimeFormat', {value: DTF, writable: true, configurable: true, enumerable: false});
      } catch (e) {}
    })(cfg.tz);
    // hyperlink auditing (<a ping>) reports clicks to trackers
    document.addEventListener('click', function (e) {
      var a = e.target && e.target.closest && e.target.closest('a[ping]');
      if (a) a.removeAttribute('ping');
    }, true);
  }
  // ---- $cookie / $csp filter rules for this page (applied here instead of on the network) ----
  var rrs = '';
  try { if (DBG.indexOf('r') < 0) rrs = B.rr(href); } catch (e) {}
  if (rrs) {
    try {
      var rr = JSON.parse(rrs);
      if (rr.p && rr.p.length) {
        var addCsp = function () {
          var root = document.head || document.documentElement;
          if (!root) return false;
          rr.p.forEach(function (v) {
            var m = document.createElement('meta');
            m.httpEquiv = 'Content-Security-Policy';
            m.content = v;
            root.insertBefore(m, root.firstChild);
          });
          return true;
        };
        if (!addCsp()) document.addEventListener('readystatechange', addCsp, {once: true});
      }
      if (rr.c && rr.c.length) {
        var specs = rr.c.map(function (v) {
          v = String(v).split(';')[0];
          if (v.length > 2 && v.charAt(0) === '/' && v.charAt(v.length - 1) === '/') {
            try { return new RegExp(v.slice(1, -1)); } catch (e) { return null; }
          }
          return v;
        }).filter(function (v) { return v !== null; });
        var hit = function (n) {
          for (var i = 0; i < specs.length; i++) {
            var s = specs[i];
            if (s === '' || (typeof s === 'string' ? s === n : s.test(n))) return true;
          }
          return false;
        };
        var cd2 = Object.getOwnPropertyDescriptor(Document.prototype, 'cookie');
        if (cd2 && cd2.get && cd2.set) {
          var g2 = cd2.get, s2 = cd2.set;
          var nativeOf = (typeof asNative === 'function') ? asNative : function (w) { return w; };
          Object.defineProperty(Document.prototype, 'cookie', {
            configurable: true, enumerable: cd2.enumerable,
            get: nativeOf({get cookie() {
              var all = g2.call(this);
              if (!all) return all;
              return all.split('; ').filter(function (p) {
                var eq = p.indexOf('=');
                return !hit(eq < 0 ? p : p.substring(0, eq));
              }).join('; ');
            }}.__lookupGetter__('cookie'), g2),
            set: nativeOf({set cookie(v) {
              var s = String(v), eq = s.indexOf('=');
              var n = (eq < 0 ? s.split(';')[0] : s.substring(0, eq)).trim();
              if (hit(n)) return;
              return s2.call(this, v);
            }}.__lookupSetter__('cookie'), s2)
          });
        }
      }
    } catch (e) {}
  }
  // Do Not Track (its own switch, also without privacy mode): navigator.doNotTrack as Chrome shows it
  var dnt = false;
  try { dnt = !!B.dnt(); } catch (e) {}
  if (dnt && window.Navigator) getterOn(Navigator.prototype, 'doNotTrack', '1');
  var pcfg = '';
  try { pcfg = B.p(href); } catch (e) {}
  if (pcfg) { try { privacy(JSON.parse(pcfg)); } catch (e) {} }

  var calls = null;
  try { calls = B.js(href); } catch (e) {}
  var extra = '', ex = null;
  try { extra = B.extra(href); } catch (e) {}
  if (extra && DBG.indexOf('x') < 0) { try { ex = JSON.parse(extra); } catch (e) { ex = null; } }
  try { calls = calls && SL && DBG.indexOf('s') < 0 ? JSON.parse(calls) : null; } catch (e) { calls = null; }
  var rawIds = ex && RAWJS && ex.r && ex.r.length ? ex.r : null;
  // only the calls that will run (the generic ones mostly do not apply here)
  if (calls) {
    var runs = [];
    for (var q = 0; q < calls.length; q++) {
      if (typeof SL[calls[q][0]] === 'function' && quietCall(calls[q][0], calls[q].slice(1))) runs.push(calls[q]);
    }
    calls = runs.length ? runs : null;
  }
  if (calls || rawIds) {
    var cloakOn = DBG.indexOf('k') < 0, before = null;
    var cloakT0 = DBG.indexOf('z') >= 0 ? performance.now() : 0;
    if (cloakOn) try { cloakStart(calls); before = true; } catch (e) {}
    var cloakT1 = DBG.indexOf('z') >= 0 ? performance.now() : 0;
    if (calls) for (var c = 0; c < calls.length; c++) {
      var f = SL[calls[c][0]], args = calls[c].slice(1);
      try {
        f({name: calls[c][0], args: args, engine: 'corelibs', version: '2.6.0', verbose: false,
           ruleText: ''}, args);
      } catch (e) {}
    }
    // AdGuard JS rules for this site
    if (rawIds) for (var r = 0; r < rawIds.length; r++) {
      var fn = RAWJS[rawIds[r]];
      if (typeof fn === 'function') try { fn(); } catch (e) {}
    }
    var cloakT2 = DBG.indexOf('z') >= 0 ? performance.now() : 0;
    if (before) try { cloakFix(); } catch (e) {}
    if (DBG.indexOf('z') >= 0) window.__gbcT = [cloakT1 - cloakT0, cloakT2 - cloakT1, performance.now() - cloakT2, calls ? calls.length : 0, rawIds ? rawIds.length : 0].join(' ');
    if (DBG.indexOf('z') >= 0) window.__gbc = {natives: natives, srcOf: shellSrc, fts: fnToString, natF: natF, scrub: scrub, ourFrame: ourFrame, fi: function () { var o = Error.prepareStackTrace; Error.prepareStackTrace = function (e, f) { return f.map(function (c) { return [c.getFileName(), c.isEval(), c.getLineNumber(), String(c.getEvalOrigin()), c.getScriptNameOrSourceURL(), c.isNative(), c.isToplevel(), String(c)]; }); }; var s = new Error().stack; Error.prepareStackTrace = o; return JSON.stringify(s); }};
  }
  // extended CSS (:has-text, :upward, :remove ...) for this site
  if (ex) {
    try {
      if (EXTCSS && ex.e.length) {
        var applyExt = function () { try { new EXTCSS({styleSheet: ex.e.join('\n')}).apply(); } catch (e) {} };
        if (document.documentElement) applyExt();
        else document.addEventListener('DOMContentLoaded', applyExt);
      }
    } catch (e) {}
  }
  if (DBG.indexOf('c') >= 0) return;
  var init;
  try { init = B.init(href); } catch (e) { return; }
  if (!init) return;
  var stealth = init.charAt(0) === 'S' && DBG.indexOf('h') < 0;
  init = init.substring(1);
  var keyed = init.charAt(0) === '1';
  var styles = [];
  var sels = [], combined = null, inlineHidden = [];
  // the top document's hiding CSS goes to GBrowser's invisible sheet (Blocking.QueueCss); frames, or when that
  // is not available, use style elements as before
  var DOC = Math.random().toString(36).slice(2) + Date.now().toString(36);
  var IS_TOP = window === window.top, HERE = location.href, useSheet = false, sentCss = [];
  var later2 = window.setTimeout;
  try { useSheet = /^https?:/i.test(HERE) && DBG.indexOf('y') < 0 && B.css(DOC, HERE, IS_TOP, '') === true; } catch (e) {}
  // GBrowser could not place the sheet (e.g. a frame it cannot find): back to style elements with everything so far
  function sheetFailed() {
    if (!useSheet) return;
    var ok = true;
    try { ok = B.cssok(DOC) === true; } catch (e) { ok = false; }
    if (ok) return;
    useSheet = false;
    for (var i = 0; i < sentCss.length; i++) {
      var s = document.createElement('style');
      s.textContent = sentCss[i];
      styles.push(s);
      attach(s);
    }
    sentCss = [];
  }
  if (useSheet && later2) { bApply(later2, window, [sheetFailed, 1500]); bApply(later2, window, [sheetFailed, 6000]); }
  if (DBG.indexOf('z') >= 0) window.__gbt = {ok: function () { try { return B.cssok(DOC); } catch (e) { return 'threw ' + e; } },
    state: function () { return 'useSheet ' + useSheet + ' sent ' + sentCss.length + ' styles ' + styles.length + ' doc ' + DOC; }};
  var CSSS = window.CSSStyleSheet, replaceSync = CSSS && CSSS.prototype.replaceSync,
    adopt = gOPD(Document.prototype, 'adoptedStyleSheets'), arrSlice = Array.prototype.slice;
  function addStyle(css) {
    if (!css) return;
    var sent = false;
    if (useSheet) { try { sent = B.css(DOC, HERE, IS_TOP, css) === true; } catch (e) {} }
    if (sent) sentCss[sentCss.length] = css;
    if (!sent) {
      var s = document.createElement('style');
      s.textContent = css;
      styles.push(s);
      attach(s);
    }
    var parts = css.split('{display:none!important}');
    for (var i = 0; i < parts.length; i++) if (parts[i] && parts[i].indexOf('}') < 0) sels.push(parts[i]);
    combined = null;
    memoGen++;
  }
  function attach(s) {
    var p = document.head || document.documentElement;
    if (p && !s.isConnected) p.appendChild(s);
  }

  // ---- stealth: an element hidden by us measures exactly as it would without the blocker ----
  // Everything the check below calls is the browser's own function captured now: a page that later puts
  // spies on closest / matches / contains / Date.now / WeakMap cannot see our script at work.
  var inStealth = false, memo = new WeakMap(), memoGen = 0;
  var EPs = Element.prototype, cap = {
    closest: EPs.closest, matches: EPs.matches, qs: EPs.querySelector, gbtn: EPs.getElementsByTagName,
    contains: Node.prototype.contains, now: Date.now, mget: WeakMap.prototype.get, mset: WeakMap.prototype.set,
    len: gOPD(HTMLCollection.prototype, 'length').get,
    gpv: CSSStyleDeclaration.prototype.getPropertyValue
  };
  function hiddenByUs(el) {
    if (inStealth || !el || el.nodeType !== 1) return false;
    var now = bApply(cap.now, Date, []), m = bApply(cap.mget, memo, [el]);
    if (m && m.g === memoGen && now - m.t < 250) return m.v;
    var v = hiddenByUsNow(el);
    bApply(cap.mset, memo, [el, {g: memoGen, t: now, v: v}]);
    return v;
  }
  var NOSHOW = {HEAD: 1, SCRIPT: 1, STYLE: 1, META: 1, LINK: 1, TITLE: 1, TEMPLATE: 1, NOSCRIPT: 1, BASE: 1,
    PARAM: 1, SOURCE: 1, TRACK: 1, AREA: 1, DATALIST: 1};
  var memoSelf = new WeakMap();
  function hiddenSelf(el) {
    var now = bApply(cap.now, Date, []), m = bApply(cap.mget, memoSelf, [el]);
    if (m && m.g === memoGen && now - m.t < 250) return m.v;
    var v = false;
    for (var i = 0; i < inlineHidden.length; i++) if (inlineHidden[i] === el) { v = true; break; }
    if (!v && sels.length) {
      if (combined === null) buildCombined();
      if (combined) try { v = !!bApply(cap.matches, el, [combined]); } catch (e) {}
    }
    bApply(cap.mset, memoSelf, [el, {g: memoGen, t: now, v: v}]);
    return v;
  }
  function hiddenByUsNow(el) {
    // small elements (bait wrappers) also count when something we hid sits inside them
    var small = bApply(cap.len, bApply(cap.gbtn, el, ['*']), []) <= 50;
    for (var i = 0; i < inlineHidden.length; i++) {
      var h = inlineHidden[i];
      if (h === el || bApply(cap.contains, h, [el]) || (small && bApply(cap.contains, el, [h]))) return true;
    }
    if (!sels.length) return false;
    if (combined === null) buildCombined();
    if (!combined) return false;
    try { return !!bApply(cap.closest, el, [combined]) || (small && !!bApply(cap.qs, el, [combined])); } catch (e) { return false; }
  }
  var okSels = [];
  function buildCombined() {
    var ok = [], root = document.documentElement;
    for (var j = 0; j < sels.length; j++) {
      try { bApply(cap.matches, root, [sels[j]]); ok[ok.length] = sels[j]; } catch (e) {}
    }
    okSels = ok;
    combined = ok.join(',');
  }
  // run fn with our hiding switched off (no paint happens in between, so nothing flickers)
  var overMemo = new WeakMap(), qsa = EPs.querySelectorAll, nlLen = gOPD(NodeList.prototype, 'length').get;
  function overrideSheet(el) {
    if (combined === null) buildCombined();
    if (!combined || !CSSS || !replaceSync || !el || el.nodeType !== 1) return null;
    var m = bApply(cap.mget, overMemo, [el]);
    if (m && m.g === memoGen) return m.s;
    var sheet = null;
    try {
      var nodes = [], hit = bApply(cap.closest, el, [combined]);
      if (hit) nodes[nodes.length] = hit;
      if (bApply(cap.len, bApply(cap.gbtn, el, ['*']), []) <= 50) {
        var inner = bApply(qsa, el, [combined]), n = bApply(nlLen, inner, []);
        for (var i = 0; i < n; i++) nodes[nodes.length] = inner[i];
      }
      var use = [];
      for (var j = 0; j < okSels.length && nodes.length; j++)
        for (var k = 0; k < nodes.length; k++)
          if (bApply(cap.matches, nodes[k], [okSels[j]])) { use[use.length] = okSels[j]; break; }
      if (use.length) {
        sheet = new CSSS();
        // same layer as the hiding rules, far higher specificity: the page's own display value comes back
        bApply(replaceSync, sheet, ['@layer gbh{:is(' + use.join(',') + '):not(#gb#gb#gb#gb#gb){display:revert-layer!important}}']);
      }
    } catch (e) { sheet = null; }
    bApply(cap.mset, overMemo, [el, {g: memoGen, s: sheet}]);
    return sheet;
  }
  function unhidden(fn, el) {
    inStealth = true;
    var off = [], saved = [], before = null;
    for (var i = 0; i < styles.length; i++) if (!styles[i].disabled) { styles[i].disabled = true; off.push(styles[i]); }
    if (useSheet && adopt && adopt.get && adopt.set) {
      var o = overrideSheet(el);
      if (o) try {
        before = bApply(arrSlice, bApply(adopt.get, document, []), []);
        var withO = bApply(arrSlice, before, []);
        withO[withO.length] = o;
        bApply(adopt.set, document, [withO]);
      } catch (e) { before = null; }
    }
    for (var k = 0; k < inlineHidden.length; k++) {
      var st = inlineHidden[k].style;
      saved.push([st.getPropertyValue('display'), st.getPropertyPriority('display')]);
      st.removeProperty('display');
    }
    try { return fn(); } finally {
      if (before) try { bApply(adopt.set, document, [before]); } catch (e) {}
      for (var j = 0; j < off.length; j++) off[j].disabled = false;
      for (var m = 0; m < inlineHidden.length; m++)
        inlineHidden[m].style.setProperty('display', saved[m][0] || 'none', saved[m][1] || 'important');
      inStealth = false;
    }
  }
  function isEmpty(v) {
    if (v === 0 || v === null || v === false) return true;
    if (v && typeof v.length === 'number' && !('width' in v)) return v.length === 0; // DOMRectList
    if (v && typeof v.width === 'number') return v.width === 0 && v.height === 0;      // DOMRect
    return false;
  }
  // Sizes of an element we hid, read with our hiding lifted. Detectors read several values back to back
  // (offsetHeight, offsetWidth, clientHeight, the rect...): one lift measures them all, and they are reused until
  // the current script run ends (a microtask clears them) or the element's own style / class changes, so a
  // burst of reads costs one layout instead of one per read.
  var GEO = {}, geoNames = [], geo = null, styleSnap = null;
  var getAttr = EPs.getAttribute, rectGet = EPs.getBoundingClientRect, DOMR = window.DOMRect;
  var rx = gOPD(DOMRectReadOnly.prototype, 'x').get, ry = gOPD(DOMRectReadOnly.prototype, 'y').get,
    rw = gOPD(DOMRectReadOnly.prototype, 'width').get, rh = gOPD(DOMRectReadOnly.prototype, 'height').get;
  var later = typeof queueMicrotask === 'function' ? queueMicrotask : null;
  function geoSig(el) { return bApply(getAttr, el, ['style']) + '|' + bApply(getAttr, el, ['class']); }
  function geoFor(el) {
    var sig = geoSig(el);
    if (geo && geo.el === el && geo.g === memoGen && geo.sig === sig) return geo;
    var v = {};
    unhidden(function () {
      for (var i = 0; i < geoNames.length; i++) v[geoNames[i]] = bApply(GEO[geoNames[i]], el, []);
      var r = bApply(rectGet, el, []);
      v.rect = [bApply(rx, r, []), bApply(ry, r, []), bApply(rw, r, []), bApply(rh, r, [])];
      if (styleSnap) v.style = styleSnap(el);   // the computed style in the same lift (one style update)
    }, el);
    geo = {el: el, g: memoGen, sig: sig, v: v};
    if (later) bApply(later, window, [function () { geo = null; }]);
    else geo = null;
    return {el: el, g: memoGen, sig: sig, v: v};
  }
  function stealthGetter(proto, name) {
    var d = Object.getOwnPropertyDescriptor(proto, name);
    if (!d || !d.get) return;
    var g = d.get;
    GEO[name] = g; geoNames[geoNames.length] = name;
    var w = asNative({get [name]() {
      var v = nat(g, this, [], 1);
      if (!isEmpty(v) || !hiddenByUs(this)) return v;
      return geoFor(this).v[name];
    }}.__lookupGetter__(name), g);
    try { Object.defineProperty(proto, name, {get: w, set: d.set, enumerable: d.enumerable, configurable: true}); } catch (e) {}
  }
  function stealthMethod(proto, name) {
    var f = proto[name];
    if (typeof f !== 'function') return;
    var w = asNative({[name]() {
      var v = nat(f, this, arguments, 1);
      if (!isEmpty(v) || !hiddenByUs(this)) return v;
      // the rect from the shared measurement, as a new DOMRect each call (like the browser's own)
      if (name === 'getBoundingClientRect' && DOMR) { var r = geoFor(this).v.rect; return new DOMR(r[0], r[1], r[2], r[3]); }
      var self = this, args = arguments;
      return unhidden(function () { return f.apply(self, args); }, self);
    }}[name], f);
    try { Object.defineProperty(proto, name, {value: w, writable: true, enumerable: false, configurable: true}); } catch (e) {}
  }
  if (stealth) try {
    if (DBG.indexOf('1') < 0) ['offsetHeight', 'offsetWidth', 'offsetParent', 'offsetTop', 'offsetLeft'].forEach(function (n) {
      stealthGetter(HTMLElement.prototype, n);
    });
    if (DBG.indexOf('2') < 0) ['clientHeight', 'clientWidth', 'scrollHeight', 'scrollWidth'].forEach(function (n) {
      stealthGetter(Element.prototype, n);
    });
    if (DBG.indexOf('3') < 0) ['getBoundingClientRect', 'getClientRects', 'checkVisibility'].forEach(function (n) {
      stealthMethod(Element.prototype, n);
    });
    // Computed style of an element we hid: a stand-in that answers with the values the element has with our
    // hiding lifted. Values come from one snapshot per script run (a microtask ends it); methods and other
    // members are the browser's own objects (cs.getPropertyValue === CSSStyleDeclaration.prototype.
    // getPropertyValue, like the real one), and the prototype methods answer for the stand-in; writes give the
    // browser's own read-only error.
    var CSD = CSSStyleDeclaration.prototype, styleProxies = new WeakMap(), PX = window.Proxy;
    var gpv0 = CSD.getPropertyValue, gpp0 = CSD.getPropertyPriority, item0 = CSD.item,
      lenGet = gOPD(CSD, 'length') && gOPD(CSD, 'length').get;
    styleSnap = function (el) {
      var t = nat(gcs, window, [el], 1), snap = Object.create(null);
      for (var k in t) { var v = t[k]; if (typeof v !== 'function') snap[k] = v; }
      var n = bApply(lenGet, t, []);
      for (var q = 0; q < n; q++) {
        var pn = bApply(item0, t, [q]);
        snap['\u0001' + pn] = bApply(gpv0, t, [pn]);
        snap['\u0002' + pn] = bApply(gpp0, t, [pn]);
      }
      return snap;
    };
    function snapOf(pr) { return geoFor(pr.el).v.style; }
    function assign(o, k, v) { o[k] = v; }
    function proxyFor(cs, el) {
      if (!PX) return cs;
      var pr = {t: cs, el: el, snap: null, g: -1, sig: ''};
      var px = new PX(cs, {
        get: function (t, p) {
          if (typeof p === 'string') { var sn = snapOf(pr); if (p in sn) return sn[p]; }
          return t[p];   // methods and the rest: the browser's own objects
        },
        set: function (t, p, v) {
          var d = null, o = t;
          while (o && !(d = gOPD(o, p))) o = Object.getPrototypeOf(o);
          if (d && d.set) nat(d.set, t, [v], 1);
          else nat(assign, undefined, [t, p, v], 2);   // the helper's own frame is ours too
          return true;
        }
      });
      bApply(cap.mset, styleProxies, [px, pr]);
      return px;
    }
    // the browser's own methods, answering for the stand-in (anything else goes straight to the browser)
    [['getPropertyValue', function (pr, a) { return snapOf(pr)['\u0001' + String(a[0])] || ''; }],
     ['getPropertyPriority', function (pr, a) { return snapOf(pr)['\u0002' + String(a[0])] || ''; }],
     ['item', null], ['setProperty', null], ['removeProperty', null]].forEach(function (m) {
      var n = m[0], f = CSD[n], answer = m[1];
      if (typeof f !== 'function') return;
      var o = {};
      o[n] = {getPropertyValue() { return call(this, arguments); }, getPropertyPriority() { return call(this, arguments); },
        item() { return call(this, arguments); }, setProperty() { return call(this, arguments); },
        removeProperty() { return call(this, arguments); }}[n];
      function call(self, args) {
        var pr = bApply(cap.mget, styleProxies, [self]);
        if (!pr) return nat(f, self, args, 2);
        if (answer && args.length >= 1) return answer(pr, args);
        return nat(f, pr.t, args, 2);   // the real object answers (or throws the browser's own error)
      }
      try { Object.defineProperty(CSD, n, {value: asNative(o[n], f), writable: true, enumerable: true, configurable: true}); } catch (e) {}
    });
    var gcs = window.getComputedStyle;
    var gcsW = asNative({getComputedStyle(el, pseudo) {
      var cs = nat(gcs, this, arguments, 1);
      if (inStealth || !el || el.nodeType !== 1 || (!sels.length && !inlineHidden.length)) return cs;
      // only an element that computes display:none can show our hiding: everything else gets the browser's own
      // object at once (measured 19-11-2026: checking every call cost 507 us instead of 1.6 us, and
      // Cloudflare's check, which calls getComputedStyle a lot, never finished)
      // its own display can only be "none" because of us when our rule matches the element itself (an
      // ancestor we hid does not change the element's own display); tags the browser never shows are skipped
      if (bApply(cap.gpv, cs, ['display']) !== 'none' || NOSHOW[el.tagName] === 1 || !hiddenSelf(el)) return cs;
      return proxyFor(cs, el);
    }}.getComputedStyle, gcs);
    if (DBG.indexOf('4') < 0) Object.defineProperty(window, 'getComputedStyle', {value: gcsW, writable: true, configurable: true, enumerable: true});
  } catch (e) {}

  addStyle(init.substring(1));

  function hide(el) {
    if (useSheet) {
      var a = el.getAttribute('src') ? 'src' : el.getAttribute('data') ? 'data' : null;
      if (a && window.CSS && CSS.escape) {
        addStyle(el.tagName.toLowerCase() + '[' + a + '="' + CSS.escape(el.getAttribute(a)) + '"]{display:none!important}');
        return;
      }
    }
    if (inlineHidden.indexOf(el) < 0) {
      inlineHidden = inlineHidden.filter(function (x) { return x.isConnected; });
      inlineHidden.push(el);
      memoGen++;
    }
    el.style.setProperty('display', 'none', 'important');
  }
  function srcOf(el) { return el.currentSrc || el.src || el.data || ''; }

  // collapse elements whose request was blocked (no broken-image boxes / empty ad frames)
  document.addEventListener('error', function (e) {
    var t = e.target, tag = t && t.tagName;
    if (tag === 'IMG' || tag === 'IFRAME' || tag === 'EMBED' || tag === 'OBJECT' ||
        tag === 'VIDEO' || tag === 'AUDIO' || tag === 'INPUT') {
      var u = srcOf(t);
      if (u && /^https?:/i.test(u) && B.blocked(href, u, tag)) hide(t);
    }
  }, true);

  var seen = Object.create(null), pending = [], frames = [];
  function addEl(el) {
    var id = el.id, k, i;
    if (id && typeof id === 'string') {
      k = '#' + id;
      if (!seen[k]) { seen[k] = 1; pending.push(k); }
    }
    var cl = el.classList;
    if (cl) for (i = 0; i < cl.length; i++) {
      k = '.' + cl[i];
      if (!seen[k]) { seen[k] = 1; pending.push(k); }
    }
    var tag = el.tagName;
    if (tag === 'IFRAME' || tag === 'EMBED' || tag === 'OBJECT') frames.push(el);
  }
  function scan(node) {
    if (node.nodeType !== 1) return;
    addEl(node);
    if (node.firstElementChild) {
      var all = node.querySelectorAll(keyed ? '[id],[class],iframe,embed,object' : 'iframe,embed,object');
      for (var i = 0; i < all.length; i++) addEl(all[i]);
    }
  }
  function flush() {
    if (keyed && pending.length) {
      var keys = pending.join(' ');
      pending = [];
      var css = '';
      try { css = B.keys(href, keys); } catch (e) {}
      addStyle(css);
    } else pending = [];
    if (frames.length) {
      var f = frames;
      frames = [];
      for (var i = 0; i < f.length; i++) {
        var u = f[i].src || f[i].data || '';
        if (u && /^https?:/i.test(u) && f[i].__gbSrc !== u) {
          f[i].__gbSrc = u;
          try { if (B.blocked(href, u, f[i].tagName)) hide(f[i]); } catch (e) {}
        }
      }
    }
    for (var j = 0; j < styles.length; j++) if (!styles[j].isConnected) attach(styles[j]);
  }
  if (document.documentElement) scan(document.documentElement);
  flush();
  new MutationObserver(function (muts) {
    for (var i = 0; i < muts.length; i++) {
      var m = muts[i];
      if (m.type === 'attributes') {
        if (m.target.nodeType === 1) addEl(m.target);
      } else {
        var a = m.addedNodes;
        for (var j = 0; j < a.length; j++) scan(a[j]);
      }
    }
    flush();
  }).observe(document, {childList: true, subtree: true, attributes: true,
                        attributeFilter: ['class', 'id', 'src']});
})();
