// Bridge between GBrowser's internal pages and the app (src/MainForm.cs, OnPageMessage / Handle).
'use strict';

// Colour scheme chosen in Settings > Appearance (src/Palette.cs builds it): {light:{--var:#hex}, dark:{...}}.
// Cached so a page opens in the right colours at once; the app sends updates when the colour changes.
const gbTheme = (() => {
  let vars = null;
  const mq = matchMedia('(prefers-color-scheme: dark)');
  function apply() {
    if (!vars) return;
    const v = mq.matches ? vars.dark : vars.light;
    for (const k in v) document.documentElement.style.setProperty(k, v[k]);
  }
  function set(v) {
    vars = v;
    try { localStorage.setItem('gbTheme', JSON.stringify(v)); } catch (e) { }
    apply();
  }
  try { vars = JSON.parse(localStorage.getItem('gbTheme') || 'null'); } catch (e) { }
  apply();
  mq.addEventListener('change', apply);
  return { set };
})();
const gb = (() => {
  let rid = 0;
  const pending = new Map();
  if (window.chrome && chrome.webview) {
    chrome.webview.addEventListener('message', e => {
      const m = e.data;
      if (m && m.type === 'theme') { gbTheme.set(m.vars); return; }
      if (m && m.type === 'update') { window.dispatchEvent(new CustomEvent('gbupdate', { detail: m.state })); return; }
      if (m && m.type === 'downloads') { window.dispatchEvent(new CustomEvent('gbdownloads')); return; }
      if (m && m.type === 'bookmarks') { window.dispatchEvent(new CustomEvent('gbbookmarks')); return; }
      if (!m || !pending.has(m.rid)) return;
      const p = pending.get(m.rid); pending.delete(m.rid);
      if (m.error) p.reject(new Error(m.error)); else p.resolve(m.result);
    });
  }
  function send(cmd, args) {
    return new Promise((resolve, reject) => {
      if (!(window.chrome && chrome.webview)) { reject(new Error('No bridge')); return; }
      const id = ++rid;
      pending.set(id, { resolve, reject });
      chrome.webview.postMessage(JSON.stringify(Object.assign({ rid: id, cmd }, args || {})));
    });
  }
  return { send };
})();
gb.send('theme.vars').then(v => gbTheme.set(v)).catch(() => { });

function icon(name, cls) {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 -960 960 960');
  svg.setAttribute('class', 'icon' + (cls ? ' ' + cls : ''));
  svg.setAttribute('aria-hidden', 'true');
  const p = document.createElementNS(ns, 'path');
  p.setAttribute('d', GB_ICONS[name] || '');
  svg.appendChild(p);
  return svg;
}

function el(tag, attrs, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') e.className = v;
    else if (k === 'text') e.textContent = v;
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else e.setAttribute(k, v === true ? '' : v);
  }
  for (const c of kids.flat()) if (c != null) e.append(c);
  return e;
}

function iconButton(name, title, onclick) {
  return el('button', { class: 'icon-btn', title, 'aria-label': title, onclick }, icon(name, 's20'));
}

// Site icon like Chrome's lists: the site's own icon (remembered from visits, src/Favicons.cs); until one is known,
// Chrome's default globe in lists, or the first letter on a big new-tab tile. Fill with loadSiteIcons() after render.
function siteKey(url) {
  try { const h = new URL(url).hostname.toLowerCase(); return h.startsWith('www.') ? h.slice(4) : h; } catch (e) { return ''; }
}

function siteIcon(url, big) {
  const box = el('div', { class: big ? 'site-icon big' : 'site-icon' }, big ? letter(url) : icon('Public', 's20'));
  const k = siteKey(url);
  if (k) box.dataset.site = k;
  return box;
}

async function loadSiteIcons(root) {
  const boxes = [...(root || document).querySelectorAll('.site-icon[data-site]:not([data-done])')];
  if (!boxes.length) return;
  for (const b of boxes) b.dataset.done = '1';
  const urls = [...new Set(boxes.map(b => 'https://' + b.dataset.site + '/'))];
  let map = {};
  try { map = await gb.send('favicons', { urls }) || {}; } catch (e) { }
  for (const b of boxes) {
    const d = map[b.dataset.site];
    if (!d) continue;
    b.textContent = '';
    b.classList.add('has');
    b.append(el('img', { src: d, alt: '' }));
  }
}

function letter(textOrUrl) {
  let s = textOrUrl || '?';
  try { s = new URL(textOrUrl).hostname.replace(/^www\./, ''); } catch (e) { }
  return el('div', { class: 'letter', text: (s.match(/[a-z0-9]/i) || ['?'])[0] });
}

// search box with a clear button inside (shown while it has text)
function searchBox(placeholder, oninput) {
  const input = el('input', { type: 'search', placeholder, spellcheck: 'false', autocomplete: 'off' });
  const box = el('label', { class: 'search' }, icon('Search', 's20'), input);
  const clear = iconButton('Close', 'Clear', () => { input.value = ''; update(); input.focus(); });
  clear.classList.add('clear');
  box.append(clear);
  function update() { box.classList.toggle('has-text', input.value.length > 0); oninput(input.value); }
  input.addEventListener('input', update);
  input.addEventListener('keydown', e => { if (e.key === 'Escape' && input.value) { input.value = ''; update(); } });
  box.input = input;
  return box;
}

function snack(text) {
  let s = document.querySelector('.snack');
  if (!s) { s = el('div', { class: 'snack', role: 'status' }); document.body.append(s); }
  s.textContent = text;
  s.classList.add('show');
  clearTimeout(s._t);
  s._t = setTimeout(() => s.classList.remove('show'), 2600);
}

function openUrl(url, e) { gb.send('open', { url, newTab: !!(e && (e.ctrlKey || e.button === 1)) }); }

function fmtTime(ms) { return new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }
function fmtDay(ms) {
  const d = new Date(ms), t = new Date();
  const y = new Date(t); y.setDate(t.getDate() - 1);
  if (d.toDateString() === t.toDateString()) return 'Today';
  if (d.toDateString() === y.toDateString()) return 'Yesterday';
  // always day first (Wednesday 18 November 2026), whatever the system locale
  return d.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
}
// "2026-11-16" (ISO from data files) -> "16-11-2026"
function fmtIsoDate(s) { const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s || ''); return m ? m[3] + '-' + m[2] + '-' + m[1] : (s || ''); }

function header(title, iconName, extra) {
  document.body.prepend(el('header', { class: 'bar' }, el('h1', {}, icon(iconName), title), extra || null));
}
