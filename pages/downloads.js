'use strict';
(() => {
  const main = document.getElementById('main');
  let q = '', timer = 0, list = [];
  const box = searchBox('Search downloads', v => { q = v.trim().toLowerCase(); render(); });
  const clearAll = el('button', { class: 'btn text', onclick: async () => { await gb.send('downloads.clear'); load(); } }, icon('Delete'), 'Clear all');
  const openFolder = iconButton('Folder', 'Open downloads folder', () => gb.send('downloads.openFolder'));
  header('Downloads', 'Download', el('div', { class: 'grow-row' }, box, openFolder, clearAll));

  const act = (id, action) => gb.send('downloads.do', { id, action }).then(load);

  function size(n) {
    if (!n) return '';
    const u = ['B', 'KB', 'MB', 'GB']; let i = 0;
    while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
    return (i ? n.toFixed(1) : Math.round(n)) + ' ' + u[i];
  }

  // "24 s", "3 min 12 s", "1 h 5 min"
  function dur(ms) {
    const s = Math.max(1, Math.round(ms / 1000));
    if (s < 60) return s + ' s';
    if (s < 3600) { const m = Math.floor(s / 60), r = s % 60; return m + ' min' + (r ? ' ' + r + ' s' : ''); }
    const h = Math.floor(s / 3600), m = Math.round((s % 3600) / 60);
    return h + ' h' + (m ? ' ' + m + ' min' : '');
  }

  function eta(sec) {
    if (!isFinite(sec) || sec <= 0) return '';
    if (sec < 60) return Math.ceil(sec) + ' s left';
    if (sec < 3600) return Math.ceil(sec / 60) + ' min left';
    return (sec / 3600).toFixed(1) + ' h left';
  }

  function row(d) {
    const done = d.state === 'completed';
    let status;
    if (done) status = d.exists ? size(d.total || d.received) + (d.durationMs > 0 ? '  ·  Downloaded in ' + dur(d.durationMs) : '') : 'Deleted';
    else if (d.state === 'in_progress') status = size(d.received) + (d.total ? ' of ' + size(d.total) : '') +
      (d.speed > 0 ? '  ·  ' + size(d.speed) + '/s' + (d.total ? '  ·  ' + eta((d.total - d.received) / d.speed) : '') : '') +
      (d.connections > 1 ? '  ·  ' + d.connections + ' connections' : '') + (d.limited ? '  ·  limited' : '') +
      (d.openWhenDone ? '  ·  opens when done' : '') + (d.replaces ? '  ·  replaces ' + d.replaces : '');
    else if (d.state === 'paused') status = 'Paused - ' + size(d.received) + (d.total ? ' of ' + size(d.total) : '');
    else if (d.state === 'blocked') status = 'Blocked: Microsoft Defender SmartScreen says this file may be harmful';
    else status = /cancel/i.test(d.reason || '') ? 'Cancelled' : 'Failed';
    const actions = el('div', { class: 'actions' });
    if (d.state === 'blocked') actions.append(el('button', { class: 'btn text', onclick: e => { e.stopPropagation(); act(d.id, 'anyway'); } }, 'Download anyway'));
    actions.append(iconButton('Link', 'Download link', e => { e.stopPropagation(); linkMenu(e.currentTarget, d); }));
    if (done && d.exists) actions.append(iconButton('Folder', 'Show in folder', e => { e.stopPropagation(); act(d.id, 'folder'); }));
    if (d.state === 'in_progress' && d.live) actions.append(iconButton('Pause', 'Pause', e => { e.stopPropagation(); act(d.id, 'pause'); }));
    if (d.state === 'paused' && d.live) actions.append(iconButton('Play', 'Resume', e => { e.stopPropagation(); act(d.id, 'resume'); }));
    if (!done && !d.live && d.state !== 'blocked') actions.append(iconButton('Refresh', 'Retry', e => { e.stopPropagation(); act(d.id, 'retry'); }));
    if ((d.state === 'in_progress' || d.state === 'paused') && d.live) actions.append(iconButton('Close', 'Cancel', e => { e.stopPropagation(); act(d.id, 'cancel'); }));
    else actions.append(iconButton('Delete', 'Remove from list', e => { e.stopPropagation(); act(d.id, 'remove'); }));
    const text = el('div', { class: 'text' },
      el('div', { class: 'title', text: d.name || d.url }),
      el('div', { class: 'sub' + (status === 'Failed' || d.state === 'blocked' ? ' state-err' : ''), text: status }));
    if (status === 'Failed' && d.reason) text.lastChild.title = d.reason;
    if (d.state === 'in_progress' || d.state === 'paused') {
      if (d.segments && d.segments.length) {
        // multi-part: each part fills its own stretch of the bar (like IDM)
        const bar = el('div', { class: 'progress parts' + (d.state === 'paused' ? ' paused' : '') });
        for (const [a, b, p] of d.segments) {
          const seg = el('div', { class: 'seg' });
          seg.style.left = (a * 100) + '%';
          seg.style.width = (Math.max(0, p - a) * 100) + '%';
          bar.append(seg);
          if (b < 1) { const cut = el('div', { class: 'cut' }); cut.style.left = (b * 100) + '%'; bar.append(cut); }
        }
        text.append(bar);
      } else {
        const pct = d.total ? Math.min(100, d.received * 100 / d.total) : 0;
        const bar = el('div', { class: 'progress' + (d.state === 'paused' ? ' paused' : '') }, el('div'));
        bar.firstChild.style.width = pct + '%';
        text.append(bar);
      }
    }
    const r = el('div', { class: 'row' + (done && d.exists ? ' link' : '') + (done && !d.exists ? ' gone' : ''), title: d.path || d.url },
      el('div', { class: 'time', text: fmtTime(d.time) }), icon('File', 'file-icon'), text, actions);
    if (done && d.exists) r.addEventListener('click', () => act(d.id, 'open'));
    return r;
  }

  // small Material menu under the link icon
  function linkMenu(anchor, d) {
    const url = d.url;
    closeMenu();
    const item = (iconName, text, fn, checked) => el('button', { class: 'menu-item', role: checked === undefined ? 'menuitem' : 'menuitemcheckbox',
      'aria-checked': checked === undefined ? null : String(checked), onclick: e => { e.stopPropagation(); closeMenu(); fn(); } },
      icon(iconName, 's20'), el('span', { class: 'grow', text }), checked ? icon('Check', 's20 on') : null);
    const running = d.state === 'in_progress' || d.state === 'paused';
    const m = el('div', { class: 'menu', role: 'menu' },
      running ? item('OpenNew', 'Open when done', () => act(d.id, 'openwhendone'), !!d.openWhenDone) : null,
      running ? el('div', { class: 'menu-sep' }) : null,
      item('Public', 'Open link in new tab', () => openUrl(url, { ctrlKey: true })),
      item('Copy', 'Copy link', async () => {
        try { await navigator.clipboard.writeText(url); snack('Link copied'); } catch (err) { snack('Copy failed'); }
      }));
    document.body.append(m);
    const r = anchor.getBoundingClientRect();
    const w = m.offsetWidth, h = m.offsetHeight;
    m.style.left = Math.max(8, Math.min(r.right - w, innerWidth - w - 8)) + 'px';
    m.style.top = (r.bottom + 4 + h > innerHeight ? r.top - h - 4 : r.bottom + 4) + 'px';
    m.querySelector('button').focus();
  }
  function closeMenu() { document.querySelectorAll('.menu').forEach(x => x.remove()); }
  document.addEventListener('click', closeMenu);
  document.addEventListener('keydown', e => { if (e.key === 'Escape') closeMenu(); });
  window.addEventListener('blur', closeMenu);
  window.addEventListener('scroll', closeMenu, true);

  function render() {
    main.textContent = '';
    const shown = list.filter(d => !q || (d.name || '').toLowerCase().includes(q) || d.url.toLowerCase().includes(q));
    if (!shown.length) { main.append(el('div', { class: 'empty' }, icon('Download'), q ? 'No matches' : 'No downloads')); return; }
    let day = null, card = null;
    for (const d of shown) {
      const dd = fmtDay(d.time);
      if (dd !== day) { day = dd; main.append(el('div', { class: 'day', text: dd })); card = el('div', { class: 'card' }); main.append(card); }
      card.append(row(d));
    }
  }

  async function load() {
    list = await gb.send('downloads.list');
    render();
    clearTimeout(timer);
    if (list.some(d => d.state === 'in_progress')) timer = setTimeout(load, 700);
  }
  // the app tells the page when a download starts, changes state or finishes
  let pending = 0;
  window.addEventListener('gbdownloads', () => { if (!pending) pending = setTimeout(() => { pending = 0; load(); }, 150); });
  load();
})();
