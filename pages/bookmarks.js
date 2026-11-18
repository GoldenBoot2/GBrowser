'use strict';
(() => {
  const main = document.getElementById('main');
  let all = [], q = '';
  const box = searchBox('Search bookmarks', v => { q = v.trim().toLowerCase(); render(); });
  header('Bookmarks', 'Bookmarks', box);

  async function load() { all = await gb.send('bookmarks.list'); render(); }

  function render() {
    main.textContent = '';
    const list = all.filter(b => !q || (b.Title || '').toLowerCase().includes(q) || b.Url.toLowerCase().includes(q));
    if (!list.length) { main.append(el('div', { class: 'empty' }, icon('Star'), q ? 'No matches' : 'No bookmarks')); return; }
    const card = el('div', { class: 'card' });
    for (const b of list) card.append(row(b));
    main.append(card);
    loadSiteIcons(card);
  }

  function row(b) {
    const title = el('div', { class: 'title', text: b.Title || b.Url });
    const r = el('div', { class: 'row link', title: b.Url },
      siteIcon(b.Url),
      el('div', { class: 'text' }, title, el('div', { class: 'sub', text: b.Url })),
      el('div', { class: 'actions' },
        iconButton('OpenNew', 'Open in new tab', e => { e.stopPropagation(); openUrl(b.Url, { ctrlKey: true }); }),
        iconButton('Tune', 'Edit', e => { e.stopPropagation(); edit(r, b); }),
        iconButton('Delete', 'Delete', async e => { e.stopPropagation(); await gb.send('bookmarks.remove', { url: b.Url }); snack('Deleted'); load(); })));
    r.addEventListener('click', e => openUrl(b.Url, e));
    r.addEventListener('auxclick', e => { if (e.button === 1) openUrl(b.Url, e); });
    return r;
  }

  function edit(r, b) {
    const t = el('input', { class: 'field', value: b.Title || '', 'aria-label': 'Name' });
    const u = el('input', { class: 'field', value: b.Url, 'aria-label': 'URL' });
    t.style.flex = '1'; u.style.flex = '2';
    const save = async () => { await gb.send('bookmarks.rename', { url: b.Url, title: t.value, newUrl: u.value }); load(); };
    const form = el('div', { class: 'row' }, t, u,
      el('button', { class: 'btn', onclick: save }, 'Save'),
      el('button', { class: 'btn text', onclick: () => render() }, 'Cancel'));
    [t, u].forEach(i => i.addEventListener('keydown', e => { if (e.key === 'Enter') save(); if (e.key === 'Escape') render(); }));
    r.replaceWith(form);
    t.focus(); t.select();
  }

  load();
})();
