// Bridge between GBrowser's internal pages and the app (src/MainForm.cs, OnPageMessage / Handle).
'use strict';
const gb = (() => {
  let rid = 0;
  const pending = new Map();
  if (window.chrome && chrome.webview) {
    chrome.webview.addEventListener('message', e => {
      const m = e.data;
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
  return d.toLocaleDateString([], { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
}

function header(title, iconName, extra) {
  document.body.prepend(el('header', { class: 'bar' }, el('h1', {}, icon(iconName), title), extra || null));
}
