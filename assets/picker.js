/* GBrowser element picker. Runs in an isolated world (DevTools Page.createIsolatedWorld): the page's scripts cannot
   see or change it. Resolves with the CSS selector chosen (or null) and removes everything it added. */
(() => new Promise(resolve => {
  const doc = document, root = doc.documentElement;
  const host = doc.createElement('div');
  const sh = host.attachShadow({ mode: 'closed' });
  const dark = matchMedia('(prefers-color-scheme: dark)').matches;
  sh.innerHTML = `<style>
    :host { all: initial; }
    .box { position: fixed; pointer-events: none; z-index: 2147483646; border: 2px solid #f0bf48; background: rgba(240,191,72,.18);
      border-radius: 4px; transition: all .06s; display: none; }
    .other { position: fixed; pointer-events: none; z-index: 2147483645; border: 1px dashed #f0bf48; display: none; }
    .panel { position: fixed; left: 50%; bottom: 24px; transform: translateX(-50%); z-index: 2147483647; width: min(640px, 92vw);
      background: ${dark ? '#2b2620' : '#fff8f0'}; color: ${dark ? '#ede1d4' : '#1f1b16'}; border-radius: 16px; padding: 14px 16px;
      box-shadow: 0 8px 28px rgba(0,0,0,.35); font: 14px/1.4 'Segoe UI', system-ui, sans-serif; }
    .title { font-weight: 600; margin-bottom: 8px; }
    .sel { width: 100%; box-sizing: border-box; height: 38px; border-radius: 8px; border: 1px solid ${dark ? '#998f80' : '#7f7667'};
      background: transparent; color: inherit; padding: 0 10px; font: 13px Consolas, monospace; }
    .row { display: flex; align-items: center; gap: 8px; margin-top: 10px; }
    .count { flex: 1; opacity: .8; font-size: 13px; }
    button { height: 36px; padding: 0 16px; border-radius: 18px; border: 0; font: 600 13px 'Segoe UI', sans-serif; cursor: pointer; }
    .tonal { background: ${dark ? '#5b4300' : '#ffdfa0'}; color: ${dark ? '#ffdfa0' : '#261a00'}; }
    .filled { background: ${dark ? '#f0bf48' : '#785a00'}; color: ${dark ? '#402d00' : '#fff'}; }
    .text { background: transparent; color: ${dark ? '#f0bf48' : '#785a00'}; }
    .hint { font-size: 12.5px; opacity: .75; margin-top: 6px; }
  </style>
  <div class="box"></div>
  <div class="panel" role="dialog" aria-label="Hide an element">
    <div class="title">Hide an element</div>
    <input class="sel" spellcheck="false" aria-label="Selector" placeholder="Point at the element to hide and click it">
    <div class="hint">Click an element on the page. Use Wider / Narrower to pick its container. Esc cancels.</div>
    <div class="row"><span class="count"></span>
      <button class="text" data-a="wider">Wider</button><button class="text" data-a="narrower">Narrower</button>
      <button class="text" data-a="cancel">Cancel</button><button class="filled" data-a="create">Hide</button></div>
  </div>`;
  const box = sh.querySelector('.box'), panel = sh.querySelector('.panel'), input = sh.querySelector('.sel'), count = sh.querySelector('.count');
  root.appendChild(host);
  let picked = null, chain = [], level = 0, preview = null;

  const randomish = s => /\d{4,}|[0-9a-f]{8,}|^[a-z]{1,2}\d|__|--[a-z0-9]{5,}$/i.test(s) || s.length > 40;
  function part(el) {
    const tag = el.tagName.toLowerCase();
    if (el.id && !randomish(el.id) && doc.querySelectorAll('#' + CSS.escape(el.id)).length === 1) return { s: '#' + CSS.escape(el.id), unique: true };
    const cls = [...el.classList].filter(c => !randomish(c)).slice(0, 3);
    let s = tag + cls.map(c => '.' + CSS.escape(c)).join('');
    const p = el.parentElement;
    if (p && [...p.children].filter(x => x.matches(s)).length > 1) {
      const same = [...p.children].filter(x => x.tagName === el.tagName);
      s += ':nth-of-type(' + (same.indexOf(el) + 1) + ')';
    }
    return { s, unique: false };
  }
  function selectorFor(el) {
    const parts = [];
    for (let e = el; e && e !== root && e !== doc.body; e = e.parentElement) {
      const p = part(e);
      parts.unshift(p.s);
      const s = parts.join(' > ');
      if (p.unique || doc.querySelectorAll(s).length === 1) return s;
      if (parts.length >= 4) return s;
    }
    return parts.join(' > ') || el.tagName.toLowerCase();
  }
  function frame(el) {
    if (!el) { box.style.display = 'none'; return; }
    const r = el.getBoundingClientRect();
    Object.assign(box.style, { display: 'block', left: r.left + 'px', top: r.top + 'px', width: r.width + 'px', height: r.height + 'px' });
  }
  function show() {
    const el = chain[level];
    if (!el) return;
    const s = selectorFor(el);
    input.value = s;
    update();
    frame(el);
  }
  function update() {
    let n = 0;
    try { n = doc.querySelectorAll(input.value).length; } catch (e) { count.textContent = 'Not a valid selector'; return; }
    count.textContent = n === 1 ? '1 element' : n + ' elements';
  }
  const under = (x, y) => { const prev = host.style.display; host.style.display = 'none'; const el = doc.elementFromPoint(x, y); host.style.display = prev; return el; };
  const inPanel = e => e.composedPath().includes(panel);
  function onMove(e) {
    if (inPanel(e) || picked) return;
    const el = under(e.clientX, e.clientY);
    if (el && el !== root && el !== doc.body) frame(el);
  }
  function onClick(e) {
    if (inPanel(e)) return;
    e.preventDefault(); e.stopImmediatePropagation();
    const el = under(e.clientX, e.clientY);
    if (!el || el === root || el === doc.body) return;
    picked = el; chain = []; level = 0;
    for (let x = el; x && x !== doc.body && x !== root; x = x.parentElement) chain.push(x);
    show();
  }
  const block = e => { if (!inPanel(e)) { e.preventDefault(); e.stopImmediatePropagation(); } };
  function finish(v) {
    removeEventListener('mousemove', onMove, true); removeEventListener('click', onClick, true);
    ['mousedown', 'mouseup', 'pointerdown', 'pointerup', 'auxclick', 'contextmenu'].forEach(t => removeEventListener(t, block, true));
    removeEventListener('keydown', onKey, true);
    host.remove();
    resolve(v);
  }
  function onKey(e) { if (e.key === 'Escape') { e.preventDefault(); finish(null); } }
  addEventListener('mousemove', onMove, true); addEventListener('click', onClick, true);
  ['mousedown', 'mouseup', 'pointerdown', 'pointerup', 'auxclick', 'contextmenu'].forEach(t => addEventListener(t, block, true));
  addEventListener('keydown', onKey, true);
  input.addEventListener('input', () => { update(); try { frame(doc.querySelector(input.value)); } catch (e) { } });
  input.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); sh.querySelector('[data-a=create]').click(); } });
  sh.querySelectorAll('button').forEach(b => b.addEventListener('click', e => {
    e.preventDefault(); e.stopPropagation();
    const a = b.dataset.a;
    if (a === 'cancel') return finish(null);
    if (a === 'wider' && level < chain.length - 1) { level++; show(); }
    if (a === 'narrower' && level > 0) { level--; show(); }
    if (a === 'create') {
      const v = input.value.trim();
      try { if (!v || !doc.querySelector(v)) { count.textContent = 'Nothing matches'; return; } } catch (err) { count.textContent = 'Not a valid selector'; return; }
      finish(v);
    }
  }));
  // started from the page's right-click menu (or a test): the element at that point is picked at once. These live
  // in the isolated world, which only GBrowser can reach.
  if (window.__gbPickAt) {
    const p = window.__gbPickAt, auto = window.__gbPickAuto;
    delete window.__gbPickAt; delete window.__gbPickAuto;
    onClick({ clientX: p[0], clientY: p[1], preventDefault() {}, stopImmediatePropagation() {}, composedPath: () => [] });
    if (auto) finish(input.value || null);
  }
}))()
