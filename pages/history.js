'use strict';
(() => {
  const main = document.getElementById('main');
  let q = '', timer = 0;
  const box = searchBox('Search history', v => { q = v.trim(); clearTimeout(timer); timer = setTimeout(load, 150); });
  const clearAll = el('button', { class: 'btn text', onclick: async () => {
    if (!confirm('Clear all browsing history?')) return;
    await gb.send('history.clear'); snack('History cleared'); load();
  } }, icon('Delete'), 'Clear all');
  header('History', 'History', el('div', { class: 'grow-row' }, box, clearAll));

  async function load() {
    const items = await gb.send('history.list', { q, limit: 1000 });
    main.textContent = '';
    if (!items.length) { main.append(el('div', { class: 'empty' }, icon('History'), q ? 'No matches' : 'No history')); return; }
    let day = null, card = null;
    for (const h of items) {
      const d = fmtDay(h.Time);
      if (d !== day) { day = d; main.append(el('div', { class: 'day', text: d })); card = el('div', { class: 'card' }); main.append(card); }
      const r = el('div', { class: 'row link', title: h.Url },
        el('div', { class: 'time', text: fmtTime(h.Time) }),
        siteIcon(h.Url),
        el('div', { class: 'text' }, el('div', { class: 'title', text: h.Title || h.Url }), el('div', { class: 'sub', text: h.Url })),
        el('div', { class: 'actions' },
          iconButton('Delete', 'Remove', async e => { e.stopPropagation(); await gb.send('history.remove', { url: h.Url, time: h.Time }); r.remove(); })));
      r.addEventListener('click', e => openUrl(h.Url, e));
      r.addEventListener('auxclick', e => { if (e.button === 1) openUrl(h.Url, e); });
      card.append(r);
    }
    loadSiteIcons(main);
  }
  load();
})();
