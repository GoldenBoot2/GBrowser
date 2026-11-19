'use strict';
// Bookmark manager, like Chrome's: folders at the left (Bookmarks bar, Other bookmarks and their folders), the
// open folder's pages and folders at the right; search over everything; add page / folder, edit, delete, drag to
// reorder or onto a folder; import from Chrome / Edge / Brave on this PC or an HTML file, export as HTML.
(() => {
  const main = document.getElementById('main');
  let tree = { roots: [], items: [] }, q = '', open = 1, dragId = 0;
  const box = searchBox('Search bookmarks', v => { q = v.trim().toLowerCase(); render(); });
  header('Bookmarks', 'Bookmarks', box);

  const byId = id => tree.items.find(b => b.Id === id);
  const kids = id => tree.items.filter(b => b.Parent === id);
  const nameOf = id => (tree.roots.find(r => r.Id === id) || byId(id) || { Title: '' }).Title;
  const isRoot = id => tree.roots.some(r => r.Id === id);
  function pathOf(id) {
    const parts = [];
    let cur = id;
    while (!isRoot(cur)) { const b = byId(cur); if (!b) break; parts.unshift(b.Title); cur = b.Parent; }
    parts.unshift(nameOf(cur));
    return parts.join(' / ');
  }
  async function load() {
    tree = await gb.send('bookmarks.tree');
    if (!isRoot(open) && !(byId(open) || {}).Folder) open = 1;
    render();
  }
  const run = async (cmd, args, done) => { try { await gb.send(cmd, args); if (done) snack(done); } catch (e) { snack(e.message); } await load(); };

  // ------------------------------------------------------------ left: folders
  function folderRow(id, depth) {
    const f = byId(id);
    if (f && !f.Folder) return [];
    const r = el('div', { class: 'row link fold' + (id === open && !q ? ' on' : ''), title: nameOf(id) },
      icon(id === open && !q ? 'Folder' : 'FolderClosed'), el('div', { class: 'text' }, el('div', { class: 'title', text: nameOf(id) })));
    r.style.paddingLeft = (16 + depth * 18) + 'px';
    r.addEventListener('click', () => { open = id; q = ''; box.querySelector('input').value = ''; box.classList.remove('has-text'); render(); });
    // drop a dragged bookmark onto a folder: it moves in
    r.addEventListener('dragover', e => { if (dragId && dragId !== id) { e.preventDefault(); r.classList.add('drop'); } });
    r.addEventListener('dragleave', () => r.classList.remove('drop'));
    r.addEventListener('drop', e => { e.preventDefault(); r.classList.remove('drop'); if (dragId) run('bookmarks.move', { id: dragId, parent: id, index: -1 }, 'Moved'); });
    const out = [r];
    for (const c of kids(id).filter(b => b.Folder)) out.push(...folderRow(c.Id, depth + 1));
    return out;
  }

  // ------------------------------------------------------------ right: a folder's contents
  function itemRow(b, index, parent) {
    const title = el('div', { class: 'title', text: b.Title || b.Url });
    const n = b.Folder ? kids(b.Id).length : 0;
    const sub = b.Folder ? n + (n === 1 ? ' item' : ' items') : (q ? pathOf(b.Parent) + '  ·  ' : '') + b.Url;
    const acts = el('div', { class: 'actions' });
    if (!b.Folder) acts.append(iconButton('OpenNew', 'Open in new tab', e => { e.stopPropagation(); openUrl(b.Url, { ctrlKey: true }); }));
    acts.append(iconButton('Edit', 'Edit', e => { e.stopPropagation(); edit(r, b); }),
      iconButton('Delete', 'Delete', e => { e.stopPropagation(); run('bookmarks.delete', { id: b.Id }, 'Deleted'); }));
    const r = el('div', { class: 'row link', title: b.Folder ? b.Title : b.Url, draggable: q ? 'false' : 'true' },
      b.Folder ? icon('FolderClosed') : siteIcon(b.Url), el('div', { class: 'text' }, title, el('div', { class: 'sub', text: sub })), acts);
    r.addEventListener('click', e => { if (b.Folder) { open = b.Id; render(); } else openUrl(b.Url, e); });
    r.addEventListener('auxclick', e => { if (e.button === 1 && !b.Folder) openUrl(b.Url, e); });
    if (!q) {
      r.addEventListener('dragstart', e => { dragId = b.Id; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(b.Id)); r.classList.add('dragging'); });
      r.addEventListener('dragend', () => { dragId = 0; r.classList.remove('dragging'); clearMarks(); });
      r.addEventListener('dragover', e => {
        if (!dragId || dragId === b.Id) return;
        e.preventDefault();
        clearMarks();
        const box = r.getBoundingClientRect(), y = e.clientY - box.top;
        // over a folder's middle: into it; else before / after this row
        if (b.Folder && y > box.height * 0.25 && y < box.height * 0.75) r.classList.add('drop');
        else r.classList.add(y < box.height / 2 ? 'before' : 'after');
      });
      r.addEventListener('drop', e => {
        e.preventDefault();
        const into = r.classList.contains('drop'), after = r.classList.contains('after');
        clearMarks();
        if (!dragId) return;
        if (into) { run('bookmarks.move', { id: dragId, parent: b.Id, index: -1 }, 'Moved'); return; }
        const sibs = kids(parent);
        let to = index + (after ? 1 : 0);
        const from = sibs.findIndex(x => x.Id === dragId);
        if (from >= 0 && from < to) to--;
        run('bookmarks.move', { id: dragId, parent, index: to });
      });
    }
    return r;
  }
  function clearMarks() { main.querySelectorAll('.before,.after,.drop').forEach(x => x.classList.remove('before', 'after', 'drop')); }

  function edit(r, b) {
    const t = el('input', { class: 'field', value: b.Title || '', 'aria-label': 'Name' });
    const u = b.Folder ? null : el('input', { class: 'field', value: b.Url, 'aria-label': 'URL' });
    t.style.flex = '1'; if (u) u.style.flex = '2';
    const save = () => run('bookmarks.edit', { id: b.Id, title: t.value, url: u ? u.value : null });
    const form = el('div', { class: 'row' }, t, u,
      el('button', { class: 'btn', onclick: save }, 'Save'),
      el('button', { class: 'btn text', onclick: () => render() }, 'Cancel'));
    [t, u].filter(Boolean).forEach(i => i.addEventListener('keydown', e => { if (e.key === 'Enter') save(); if (e.key === 'Escape') render(); }));
    r.replaceWith(form);
    t.focus(); t.select();
  }

  function addForm(card, folder) {
    const t = el('input', { class: 'field', placeholder: folder ? 'Folder name' : 'Name', 'aria-label': 'Name' });
    const u = folder ? null : el('input', { class: 'field', placeholder: 'URL', 'aria-label': 'URL' });
    t.style.flex = '1'; if (u) u.style.flex = '2';
    const save = () => folder ? run('bookmarks.addFolder', { parent: open, title: t.value }, 'Folder added')
      : run('bookmarks.add', { parent: open, title: t.value, url: u.value }, 'Added');
    const form = el('div', { class: 'row' }, t, u, el('button', { class: 'btn', onclick: save }, 'Save'),
      el('button', { class: 'btn text', onclick: () => render() }, 'Cancel'));
    [t, u].filter(Boolean).forEach(i => i.addEventListener('keydown', e => { if (e.key === 'Enter') save(); if (e.key === 'Escape') render(); }));
    card.insertBefore(form, card.children[1] || null);
    t.focus();
  }

  // ------------------------------------------------------------ import / export
  async function importCard() {
    const card = el('div', { class: 'card', id: 'import' }, el('h2', { text: 'Import and export' }));
    let found = [];
    try { found = await gb.send('bookmarks.browsers'); } catch (e) { }
    for (const f of found) {
      const b = el('button', { class: 'btn tonal', text: 'Import' });
      b.addEventListener('click', async () => {
        try { const n = await gb.send('bookmarks.importBrowser', { i: f.i }); snack(n ? n + ' bookmarks imported' : 'Nothing new'); } catch (e) { snack(e.message); }
        load();
      });
      card.append(el('div', { class: 'row' }, icon('Bookmarks'), el('div', { class: 'text' },
        el('div', { class: 'title', text: f.browser }), el('div', { class: 'sub', text: 'Profile: ' + f.profile })), b));
    }
    if (!found.length) card.append(el('div', { class: 'row' }, icon('Info'), el('div', { class: 'text', text: 'No Chrome, Edge or Brave bookmarks found on this PC' })));
    const fileBtn = el('button', { class: 'btn tonal', text: 'Choose file' });
    fileBtn.addEventListener('click', async () => {
      try { const n = await gb.send('bookmarks.importFile'); if (n >= 0) snack(n ? n + ' bookmarks imported' : 'Nothing new'); } catch (e) { snack(e.message); }
      load();
    });
    const expBtn = el('button', { class: 'btn tonal', text: 'Export' });
    expBtn.addEventListener('click', async () => { try { if (await gb.send('bookmarks.export')) snack('Exported'); } catch (e) { snack(e.message); } });
    card.append(el('div', { class: 'row' }, icon('Upload'), el('div', { class: 'text' }, el('div', { class: 'title', text: 'From an HTML file' }),
      el('div', { class: 'sub', text: 'Bookmarks exported from any browser' })), fileBtn),
      el('div', { class: 'row' }, icon('Download'), el('div', { class: 'text' }, el('div', { class: 'title', text: 'Export bookmarks' }),
        el('div', { class: 'sub', text: 'As an HTML file other browsers can import' })), expBtn));
    return card;
  }

  // ------------------------------------------------------------ page
  let importEl = null;
  async function render() {
    main.textContent = '';
    const layout = el('div', { class: 'bm' });
    const nav = el('div', { class: 'card nav' });
    for (const r of tree.roots) nav.append(...folderRow(r.Id, 0));
    const list = el('div', { class: 'card list' });
    if (q) {
      const hits = tree.items.filter(b => (b.Title || '').toLowerCase().includes(q) || (b.Url || '').toLowerCase().includes(q));
      list.append(el('h2', { text: hits.length + ' found' }));
      hits.forEach((b, i) => list.append(itemRow(b, i, b.Parent)));
      if (!hits.length) list.append(el('div', { class: 'empty' }, icon('Search'), 'No matches'));
    } else {
      const head = el('div', { class: 'row head' }, el('div', { class: 'text' }, el('div', { class: 'title', text: pathOf(open) })),
        el('div', { class: 'actions' },
          iconButton('BookmarkAdd', 'Add page', () => addForm(list, false)),
          iconButton('FolderNew', 'Add folder', () => addForm(list, true)),
          iconButton('Upload', 'Import and export', () => { const i = document.getElementById('import'); if (i) i.scrollIntoView({ behavior: 'smooth' }); })));
      list.append(head);
      const items = kids(open);
      items.forEach((b, i) => list.append(itemRow(b, i, open)));
      if (!items.length) list.append(el('div', { class: 'empty' }, icon('Star'), open === 1 ? 'Pages you put on the bookmarks bar show here' : 'This folder is empty'));
    }
    layout.append(nav, list);
    main.append(layout);
    if (!importEl) importEl = await importCard();
    main.append(importEl);
    loadSiteIcons(list);
    if (location.hash === '#import') { importEl.scrollIntoView(); history.replaceState(null, '', location.pathname); }
  }

  window.addEventListener('gbbookmarks', load);
  load();
})();
