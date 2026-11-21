// GBrowser: sizes an extension popup like Chrome (Blink auto-resize, measured in real Chrome 158 on 21-11-2026 with
// tools/local/chrome_extpopup.mjs + extsize/):
// - width = the page's minimum preferred width (min-content of <html>, incl. margins; a width the author gave <html>
//   counts as is), at least 25, at most 800 CSS px;
// - height = the content height at that width, at least 25, at most 600;
// - over 600 high: a vertical scrollbar is added to the width; over 800 wide: a horizontal one to the height;
// - measured again whenever the content changes (it grows and shrinks, like Chrome).
// The host (ExtensionPopup.cs) calls window.__gbps() and gets "w,h" (CSS px) or "" when nothing changed.
(() => {
  if (window.__gbps) return;
  const MAX_W = 800, MAX_H = 600, MIN = 25;
  let root = document.documentElement;   // may not exist yet when this runs at document start
  let dirty = true, last = '', sb = -1, measuring = false;

  // ---- does the author give <html> a fixed width? (then that width is its minimum, like Blink)
  const abs = /^-?[\d.]+(px|em|rem|pt|pc|cm|mm|in|q|ch|ex|lh|rlh)$/i;
  function fixedRootWidth() {
    let fixed = false;
    const check = st => {
      for (const p of ['width', 'min-width']) { const v = st.getPropertyValue(p).trim(); if (v && abs.test(v)) fixed = true; }
    };
    check(root.style);
    const walk = rules => {
      for (const r of rules) {
        try {
          if (r.styleSheet) { walk(r.styleSheet.cssRules); continue; }            // @import
          if (r.cssRules && !r.selectorText) { walk(r.cssRules); continue; }      // @media / @layer / @supports
          if (r.selectorText && r.style && root.matches(r.selectorText)) {
            if (r.conditionText === undefined) check(r.style);
            if (r.cssRules) walk(r.cssRules);                                      // nested rules
          }
        } catch (e) { }
      }
    };
    for (const s of document.styleSheets) { try { walk(s.cssRules); } catch (e) { } }   // cross-origin sheets are skipped
    return fixed;
  }

  const num = v => parseFloat(v) || 0;

  // right edge of a box's min-content contribution (laid out with <html> at width 0: every box at its narrowest)
  function rightOf(el) {
    let inner = 0;
    for (const n of el.childNodes) {
      if (n.nodeType === 3) {
        if (!n.data.trim()) continue;
        const rg = document.createRange(); rg.selectNodeContents(n);
        const q = rg.getBoundingClientRect();
        if (q.width || q.height) inner = Math.max(inner, q.right);
      } else if (n.nodeType === 1) {
        const cs = getComputedStyle(n);
        if (cs.display === 'none' || cs.position === 'fixed' || cs.position === 'absolute') continue;
        if (cs.display === 'contents') { inner = Math.max(inner, rightOf(n)); continue; }
        inner = Math.max(inner, rightOf(n) + num(cs.marginRight));
      }
    }
    const cs = getComputedStyle(el), q = el.getBoundingClientRect();
    return Math.max(q.right, inner ? inner + num(cs.paddingRight) + num(cs.borderRightWidth) : 0);
  }

  // bottom edge of the content at the current width: <html>'s own box (it holds the in-flow content, margins already
  // collapsed) or any box sticking out of it (clipped content of overflow boxes does not count), like Blink's ScrollHeight
  function bottomOf(el) {
    let b = el.getBoundingClientRect().bottom;
    const cs = getComputedStyle(el);
    if (el !== root && (cs.overflowY !== 'visible' || cs.overflowX !== 'visible')) return b;
    for (const n of el.children) {
      const c = getComputedStyle(n);
      if (c.display === 'none' || c.position === 'fixed') continue;
      b = Math.max(b, bottomOf(n));
    }
    return b;
  }

  function scrollbar() {
    if (sb >= 0) return sb;
    const d = document.createElement('div');
    d.style.cssText = 'position:absolute;top:-9999px;left:0;width:100px;height:100px;overflow:scroll;visibility:hidden';
    root.appendChild(d);
    sb = d.offsetWidth - d.clientWidth;
    d.remove();
    return sb;
  }

  const narrow = new CSSStyleSheet();
  narrow.replaceSync('html{width:0px!important;min-width:0px!important;max-width:none!important}');
  const noScroll = new CSSStyleSheet();
  noScroll.replaceSync('html{overflow:hidden!important}');

  function measure() {
    root = document.documentElement;
    if (!root || !document.body) return '';
    measuring = true;
    try {
      const sx = scrollX, sy = scrollY;
      const csr = getComputedStyle(root);
      // right edge of <html>'s content (viewport coordinates) + its right margin; laid out narrow unless its width is fixed
      let w;
      if (fixedRootWidth()) w = rightOf(root);
      else {
        const old = [...document.adoptedStyleSheets];   // a copy: the property is a live array
        document.adoptedStyleSheets = [...old, narrow];
        try { w = rightOf(root); } finally { document.adoptedStyleSheets = old; }
      }
      w = Math.ceil(w + num(csr.marginRight) + scrollX);
      // height at the current width, measured without scrollbars (a scrollbar left from the previous size would narrow
      // the text and make it taller); rounded up: a fraction of a pixel too little would bring a scrollbar back
      let h;
      {
        const old = [...document.adoptedStyleSheets];
        document.adoptedStyleSheets = [...old, noScroll];
        try { h = Math.ceil(bottomOf(root) + scrollY + num(csr.marginBottom)); } finally { document.adoptedStyleSheets = old; }
      }
      if (w > MAX_W) { h += scrollbar(); w = MAX_W; if (h > MAX_H) h = MAX_H; }
      else if (h > MAX_H) { w += scrollbar(); h = MAX_H; if (w > MAX_W) w = MAX_W; }
      w = Math.max(MIN, Math.min(MAX_W, w));
      h = Math.max(MIN, Math.min(MAX_H, h));
      if (scrollX !== sx || scrollY !== sy) scrollTo(sx, sy);
      return w + ',' + h;
    } finally {
      mo.takeRecords();   // our own measuring changes are not content changes
      measuring = false;
    }
  }

  const mark = () => { if (!measuring) dirty = true; };
  const mo = new MutationObserver(mark);
  const ro = new ResizeObserver(mark);
  mo.observe(document, { subtree: true, childList: true, attributes: true, characterData: true });
  let seenRoot = null, seenBody = null;
  function watchBoxes() {   // <html> / <body> appear (or are replaced) while the page loads
    const r = document.documentElement, b = document.body;
    if (r && r !== seenRoot) { ro.observe(r); seenRoot = r; }
    if (b && b !== seenBody) { ro.observe(b); seenBody = b; }
  }
  new MutationObserver(watchBoxes).observe(document, { childList: true, subtree: true });
  watchBoxes();
  document.addEventListener('DOMContentLoaded', () => { watchBoxes(); mark(); });
  addEventListener('load', mark);
  addEventListener('resize', mark);
  document.addEventListener('transitionend', mark, true);
  document.addEventListener('animationend', mark, true);
  document.addEventListener('load', mark, true);   // images
  if (document.fonts) document.fonts.addEventListener('loadingdone', mark);

  Object.defineProperty(window, '__gbps', {
    value: () => {
      if (!dirty) return '';
      dirty = false;
      const s = measure();
      if (s === last) return '';
      last = s;
      return s;
    }
  });
})();
