'use strict';
(() => {
  const main = document.getElementById('main');
  let q = '', timer = 0, list = [];
  const box = searchBox('Search downloads', v => { q = v.trim().toLowerCase(); render(); });
  const clearAll = el('button', { class: 'btn text', onclick: async () => { await gb.send('downloads.clear'); load(); } }, icon('Delete'), 'Clear all');
  header('Downloads', 'Download', el('div', { class: 'grow-row' }, box, clearAll));

  const act = (id, action) => gb.send('downloads.do', { id, action }).then(load);

  function size(n) {
    if (!n) return '';
    const u = ['B', 'KB', 'MB', 'GB']; let i = 0;
    while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
    return (i ? n.toFixed(1) : n) + ' ' + u[i];
  }

  function row(d) {
    const done = d.state === 'completed';
    let status;
    if (done) status = d.exists ? size(d.total || d.received) : 'Deleted';
    else if (d.state === 'in_progress') status = size(d.received) + (d.total ? ' of ' + size(d.total) : '');
    else if (d.state === 'paused') status = 'Paused - ' + size(d.received) + (d.total ? ' of ' + size(d.total) : '');
    else status = 'Failed';
    const actions = el('div', { class: 'actions' });
    if (done && d.exists) actions.append(iconButton('Folder', 'Show in folder', e => { e.stopPropagation(); act(d.id, 'folder'); }));
    if (d.state === 'in_progress' && d.live) actions.append(iconButton('Pause', 'Pause', e => { e.stopPropagation(); act(d.id, 'pause'); }));
    if (d.state === 'paused' && d.live) actions.append(iconButton('Play', 'Resume', e => { e.stopPropagation(); act(d.id, 'resume'); }));
    if (!done && !d.live) actions.append(iconButton('Refresh', 'Retry', e => { e.stopPropagation(); act(d.id, 'retry'); }));
    if ((d.state === 'in_progress' || d.state === 'paused') && d.live) actions.append(iconButton('Close', 'Cancel', e => { e.stopPropagation(); act(d.id, 'cancel'); }));
    else actions.append(iconButton('Delete', 'Remove from list', e => { e.stopPropagation(); act(d.id, 'remove'); }));
    const text = el('div', { class: 'text' },
      el('div', { class: 'title', text: d.name || d.url }),
      el('div', { class: 'sub' + (status === 'Failed' ? ' state-err' : ''), text: status + '  ' + d.url }));
    if (d.state === 'in_progress' || d.state === 'paused') {
      const pct = d.total ? Math.min(100, d.received * 100 / d.total) : 0;
      const bar = el('div', { class: 'progress' }, el('div'));
      bar.firstChild.style.width = pct + '%';
      text.append(bar);
    }
    const r = el('div', { class: 'row' + (done && d.exists ? ' link' : '') + (done && !d.exists ? ' gone' : ''), title: d.path || d.url },
      el('div', { class: 'time', text: fmtTime(d.time) }), icon('File', 'file-icon'), text, actions);
    if (done && d.exists) r.addEventListener('click', () => act(d.id, 'open'));
    return r;
  }

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
    if (list.some(d => d.state === 'in_progress')) timer = setTimeout(load, 1000);
  }
  load();
})();
