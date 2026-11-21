'use strict';
// Bookmark manager like Chrome's chrome://bookmarks (measured on Chrome 158, 21-11-2026, 120 bookmarks):
// - header: search over everything + an Organize menu (Sort by name, Add new bookmark, Add new folder, Import
//   bookmarks, Export bookmarks): import / export never sit below a long list;
// - left: folder tree (collapsible, scrolls on its own); right: the open folder, only the visible rows are drawn,
//   so thousands of bookmarks stay fast;
// - click selects, Ctrl / Shift + click select several, double-click or Enter opens; a "N selected" bar with
//   Delete and a menu; every row has a More actions menu (also on right-click); keyboard: arrows, Home / End,
//   Ctrl+A, Delete, F2, Ctrl+C / X / V, Ctrl+Z, Escape; drag one or several onto a folder or between rows;
//   deleting can be undone.
(() => {
  const main = document.getElementById('main');
  const ROW = 40;                         // row height (CSS px), like Chrome's list
  let tree = { roots: [], items: [] }, q = '', open = 1;
  let byId = new Map(), kidsOf = new Map();
  let shown = [];                         // items in the list now (folder contents or search hits)
  const sel = new Set();                  // selected ids
  let anchor = -1, focusIdx = -1;         // Shift+click / Shift+arrow anchor, keyboard focus (indexes in shown)
  let clip = null;                        // { mode: 'cut' | 'copy', ids }
  let dragIds = null;
  const collapsed = new Set(JSON.parse(localStorage.getItem('gbBmCollapsed') || '[]'));

  const box = searchBox('Search bookmarks', v => { q = v.trim().toLowerCase(); sel.clear(); anchor = focusIdx = -1; render(); window.scrollTo(0, 0); });
  const orgBtn = iconButton('More', 'Organize', e => { e.stopPropagation(); organizeMenu(orgBtn); });
  header('Bookmarks', 'Bookmarks', el('div', { class: 'head-tools' }, box, orgBtn));
  // the sticky list bar and folder tree sit right under the sticky header
  const headH = () => document.documentElement.style.setProperty('--head-h', document.querySelector('header.bar').offsetHeight + 'px');
  headH();
  addEventListener('resize', headH);

  // ------------------------------------------------------------ data
  const isRoot = id => tree.roots.some(r => r.Id === id);
  const nameOf = id => (tree.roots.find(r => r.Id === id) || byId.get(id) || { Title: '' }).Title;
  const kids = id => kidsOf.get(id) || [];
  function pathOf(id) {
    const parts = [];
    let cur = id;
    while (!isRoot(cur)) { const b = byId.get(cur); if (!b) break; parts.unshift(b.Title); cur = b.Parent; }
    parts.unshift(nameOf(cur));
    return parts.join(' / ');
  }
  /** Page addresses under the given items (folders: everything inside), in order. */
  function urlsOf(ids) {
    const out = [];
    const walk = b => { if (!b) return; if (b.Folder) kids(b.Id).forEach(walk); else if (b.Url) out.push(b.Url); };
    ids.forEach(i => walk(byId.get(i)));
    return out;
  }
  async function load() {
    tree = await gb.send('bookmarks.tree');
    byId = new Map(); kidsOf = new Map();
    for (const b of tree.items) {
      byId.set(b.Id, b);
      if (!kidsOf.has(b.Parent)) kidsOf.set(b.Parent, []);
      kidsOf.get(b.Parent).push(b);
    }
    if (!isRoot(open) && !(byId.get(open) || {}).Folder) open = 1;
    for (const id of [...sel]) if (!byId.has(id)) sel.delete(id);
    render();
  }
  const run = async (cmd, args, done) => {
    let r;
    try { r = await gb.send(cmd, args); if (done) snack(typeof done === 'function' ? done(r) : done); } catch (e) { snack(e.message); }
    await load();
    return r;
  };

  // ------------------------------------------------------------ small Material menu (page-level)
  let menuEl = null;
  function closeMenu() { if (menuEl) { menuEl.remove(); menuEl = null; } }
  /** items: { text, icon, click, disabled } or '-' */
  function showMenu(anchorEl, items, at) {
    closeMenu();
    const m = el('div', { class: 'menu', role: 'menu' });
    for (const it of items) {
      if (it === '-') { m.append(el('div', { class: 'menu-sep' })); continue; }
      const b = el('button', { class: 'menu-item', role: 'menuitem' }, it.icon ? icon(it.icon) : el('span', { class: 'menu-gap' }), el('span', { text: it.text }));
      if (it.disabled) b.disabled = true;
      b.addEventListener('click', e => { e.stopPropagation(); closeMenu(); it.click(); });
      m.append(b);
    }
    document.body.append(m);
    const r = at || anchorEl.getBoundingClientRect();
    const mw = m.offsetWidth, mh = m.offsetHeight;
    let x = at ? r.x : r.right - mw, y = at ? r.y : r.bottom + 4;
    if (x + mw > innerWidth - 8) x = innerWidth - 8 - mw;
    if (y + mh > innerHeight - 8) y = Math.max(8, (at ? r.y : r.top - 4) - mh);
    m.style.left = Math.max(8, x) + 'px';
    m.style.top = y + 'px';
    menuEl = m;
    const first = m.querySelector('.menu-item:not(:disabled)');
    if (first) first.focus();
    m.addEventListener('keydown', e => {
      const list = [...m.querySelectorAll('.menu-item:not(:disabled)')];
      const i = list.indexOf(document.activeElement);
      if (e.key === 'ArrowDown') { e.preventDefault(); list[(i + 1) % list.length].focus(); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); list[(i - 1 + list.length) % list.length].focus(); }
      else if (e.key === 'Escape') { e.preventDefault(); closeMenu(); }
      e.stopPropagation();
    });
  }
  document.addEventListener('mousedown', e => { if (menuEl && !menuEl.contains(e.target)) closeMenu(); });
  addEventListener('blur', closeMenu);
  addEventListener('resize', closeMenu);

  // ------------------------------------------------------------ dialogs
  /** fields: [{ label, value }] -> the values, or null when cancelled */
  function formDialog(title, fields, okText) {
    return new Promise(resolve => {
      const inputs = fields.map(f => el('input', { class: 'field', value: f.value || '', 'aria-label': f.label, placeholder: f.label, spellcheck: 'false' }));
      const done = v => { shade.remove(); resolve(v); };
      const ok = el('button', { class: 'btn', text: okText || 'Save', onclick: () => done(inputs.map(i => i.value)) });
      const shade = el('div', { class: 'dialog-shade' }, el('div', { class: 'dialog', role: 'dialog', 'aria-label': title },
        el('h2', { text: title }),
        ...fields.map((f, i) => el('label', { class: 'dlg-field' }, el('span', { text: f.label }), inputs[i])),
        el('div', { class: 'dialog-actions' }, el('button', { class: 'btn text', text: 'Cancel', onclick: () => done(null) }), ok)));
      shade.addEventListener('mousedown', e => { if (e.target === shade) done(null); });
      shade.addEventListener('keydown', e => {
        e.stopPropagation();
        if (e.key === 'Escape') done(null);
        if (e.key === 'Enter') done(inputs.map(i => i.value));
      });
      document.body.append(shade);
      inputs[0].focus(); inputs[0].select();
    });
  }

  async function importDialog() {
    let found = [];
    try { found = await gb.send('bookmarks.browsers'); } catch (e) { }
    const shade = el('div', { class: 'dialog-shade' });
    const close = () => shade.remove();
    const list = el('div', { class: 'pick-list' });
    const busy = async (btn, fn) => {
      btn.disabled = true;
      try { const n = await fn(); if (n >= 0) { snack(n ? n + ' bookmarks imported to Other bookmarks' : 'Nothing new'); close(); await load(); } }
      catch (e) { snack(e.message); }
      btn.disabled = false;
    };
    for (const f of found) {
      const b = el('button', { class: 'btn tonal', text: 'Import' });
      // the count shows before Import is pressed (an empty profile cannot be imported)
      if (f.count === 0) b.disabled = true;
      b.addEventListener('click', () => busy(b, () => gb.send('bookmarks.importBrowser', { i: f.i })));
      const what = f.count > 0 ? (f.count === 1 ? '1 bookmark' : f.count.toLocaleString() + ' bookmarks') : f.count === 0 ? 'No bookmarks' : 'Cannot read';
      list.append(el('div', { class: 'row' }, icon('Bookmarks'), el('div', { class: 'text' },
        el('div', { class: 'title', text: f.browser }), el('div', { class: 'sub', text: 'Profile: ' + f.profile + '  ·  ' + what })), b));
    }
    const fileBtn = el('button', { class: 'btn tonal', text: 'Choose file' });
    fileBtn.addEventListener('click', () => busy(fileBtn, () => gb.send('bookmarks.importFile')));
    list.append(el('div', { class: 'row' }, icon('Upload'), el('div', { class: 'text' },
      el('div', { class: 'title', text: 'HTML file' }), el('div', { class: 'sub', text: 'Exported from any browser' })), fileBtn));
    if (!found.length) list.prepend(el('div', { class: 'row' }, icon('Info'), el('div', { class: 'text' }, el('div', { class: 'sub', text: 'No Chrome, Edge or Brave bookmarks on this PC' }))));
    shade.append(el('div', { class: 'dialog', role: 'dialog', 'aria-label': 'Import bookmarks' },
      el('h2', { text: 'Import bookmarks' }), list,
      el('div', { class: 'dialog-actions' }, el('button', { class: 'btn text', text: 'Close', onclick: close }))));
    shade.addEventListener('mousedown', e => { if (e.target === shade) close(); });
    shade.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Escape') close(); });
    document.body.append(shade);
    (list.querySelector('button') || shade).focus();
  }

  // ------------------------------------------------------------ actions
  const selectedIds = () => shown.filter(b => sel.has(b.Id)).map(b => b.Id);
  const inSearch = () => q.length > 0;

  async function addPage() {
    const v = await formDialog('Add bookmark', [{ label: 'Name' }, { label: 'URL' }], 'Save');
    if (v) await run('bookmarks.add', { parent: open, title: v[0], url: v[1], index: pasteIndex() }, 'Added');
  }
  async function addFolder() {
    const v = await formDialog('Add folder', [{ label: 'Name', value: 'New folder' }], 'Save');
    if (v) await run('bookmarks.addFolder', { parent: open, title: v[0], index: pasteIndex() }, 'Folder added');
  }
  async function editItem(b) {
    const v = b.Folder ? await formDialog('Rename', [{ label: 'Name', value: b.Title }])
      : await formDialog('Edit bookmark', [{ label: 'Name', value: b.Title }, { label: 'URL', value: b.Url }]);
    if (v) await run('bookmarks.edit', { id: b.Id, title: v[0], url: b.Folder ? null : v[1] });
  }
  async function del(ids) {
    if (!ids.length) return;
    const n = await run('bookmarks.deleteMany', { ids });
    sel.clear();
    undoSnack(n === 1 ? 'Deleted' : n + ' deleted');
  }
  function undoSnack(text) {
    let s = document.querySelector('.snack');
    if (!s) { s = el('div', { class: 'snack', role: 'status' }); document.body.append(s); }
    s.textContent = '';
    s.append(el('span', { text }), el('button', { class: 'snack-btn', text: 'Undo', onclick: undo }));
    s.classList.add('show');
    clearTimeout(s._t);
    s._t = setTimeout(() => s.classList.remove('show'), 8000);
  }
  async function undo() {
    const ok = await run('bookmarks.undo', {});
    const s = document.querySelector('.snack');
    if (s) s.classList.remove('show');
    if (ok) snack('Restored');
  }
  function copyIds(ids, mode) {
    if (!ids.length) return;
    clip = { mode, ids };
    const urls = urlsOf(ids);
    if (urls.length) navigator.clipboard.writeText(urls.join('\n')).catch(() => { });
    render();
    snack(mode === 'cut' ? (ids.length === 1 ? 'Cut' : ids.length + ' cut') : (ids.length === 1 ? 'Copied' : ids.length + ' copied'));
  }
  /** Where new / pasted items go in the open folder: after the last selected item, else at the end. */
  function pasteIndex() {
    const list = kids(open);
    const last = list.map(b => b.Id).reduce((a, id, i) => sel.has(id) ? i : a, -1);
    return last >= 0 ? last + 1 : -1;
  }
  async function paste() {
    if (!clip || inSearch()) return;
    const ids = clip.ids.filter(i => byId.has(i));
    if (clip.mode === 'cut') { await run('bookmarks.moveMany', { ids, parent: open, index: pasteIndex() }, 'Moved'); clip = null; }
    else await run('bookmarks.copyMany', { ids, parent: open, index: pasteIndex() }, n => n === 1 ? 'Pasted' : n + ' pasted');
  }
  const openIn = (urls, mode, name) => { if (urls.length) gb.send('bookmarks.open', { urls, mode, name }); };
  function openItem(b, e) {
    if (b.Folder) { goFolder(b.Id); return; }
    if (e && e.shiftKey) openIn([b.Url], 'window');
    else openUrl(b.Url, e);
  }
  function showInFolder(b) {
    q = ''; box.input.value = ''; box.classList.remove('has-text');
    open = b.Parent;
    expandTo(open);
    sel.clear(); sel.add(b.Id);
    render();
    focusIdx = anchor = shown.findIndex(x => x.Id === b.Id);
    scrollToIdx(focusIdx);
    render();
  }

  // the menus (Chrome's wording)
  function organizeMenu(anchorEl) {
    showMenu(anchorEl, [
      { text: 'Sort by name', icon: 'Sort', disabled: inSearch() || kids(open).length < 2, click: () => run('bookmarks.sort', { parent: open }, 'Sorted') },
      { text: 'Add new bookmark', icon: 'BookmarkAdd', disabled: inSearch(), click: addPage },
      { text: 'Add new folder', icon: 'FolderNew', disabled: inSearch(), click: addFolder },
      '-',
      { text: 'Import bookmarks', icon: 'Upload', click: importDialog },
      { text: 'Export bookmarks', icon: 'Download', click: async () => { try { if (await gb.send('bookmarks.export')) snack('Exported'); } catch (e) { snack(e.message); } } },
    ]);
  }
  function itemMenu(anchorEl, at) {
    const ids = selectedIds();
    if (!ids.length) return;
    const one = ids.length === 1 ? byId.get(ids[0]) : null;
    const urls = urlsOf(ids), n = urls.length;
    const items = [];
    if (one) items.push({ text: one.Folder ? 'Rename' : 'Edit', icon: 'Edit', click: () => editItem(one) });
    if (one && !one.Folder) items.push({ text: 'Copy link', icon: 'Link', click: () => { navigator.clipboard.writeText(one.Url).then(() => snack('Link copied')).catch(() => { }); } });
    if (one && inSearch()) items.push({ text: 'Show in folder', icon: 'Folder', click: () => showInFolder(one) });
    items.push({ text: 'Delete', icon: 'Delete', click: () => del(ids) }, '-',
      { text: 'Cut', icon: 'Cut', click: () => copyIds(ids, 'cut') },
      { text: 'Copy', icon: 'Copy', click: () => copyIds(ids, 'copy') },
      { text: 'Paste', icon: 'Paste', disabled: !clip || inSearch(), click: paste }, '-');
    if (one && !one.Folder) items.push(
      { text: 'Open in new tab', icon: 'OpenNew', click: () => openIn(urls, 'tab') },
      { text: 'Open in new window', icon: 'NewWindow', click: () => openIn(urls, 'window') },
      { text: 'Open in private window', icon: 'Private', click: () => openIn(urls, 'private') });
    else items.push(
      { text: 'Open all (' + n + ')', icon: 'OpenNew', disabled: !n, click: () => openIn(urls, 'tab') },
      { text: 'Open all (' + n + ') in new window', icon: 'NewWindow', disabled: !n, click: () => openIn(urls, 'window') },
      { text: 'Open all (' + n + ') in private window', icon: 'Private', disabled: !n, click: () => openIn(urls, 'private') },
      { text: 'Open all (' + n + ') in new tab group', icon: 'Tab', disabled: !n, click: () => openIn(urls, 'group', one ? one.Title : '') });
    showMenu(anchorEl, items, at);
  }

  // ------------------------------------------------------------ selection
  function selectOnly(i) { sel.clear(); if (shown[i]) sel.add(shown[i].Id); anchor = focusIdx = i; }
  function selectRange(i) {
    if (anchor < 0) anchor = i;
    sel.clear();
    for (let k = Math.min(anchor, i); k <= Math.max(anchor, i); k++) sel.add(shown[k].Id);
    focusIdx = i;
  }
  function clickRow(i, e) {
    if (e.shiftKey) selectRange(i);
    else if (e.ctrlKey) { const id = shown[i].Id; if (sel.has(id)) sel.delete(id); else sel.add(id); anchor = focusIdx = i; }
    else selectOnly(i);
    paintRows();
  }

  // ------------------------------------------------------------ left: folder tree
  function goFolder(id) {
    open = id; q = ''; box.input.value = ''; box.classList.remove('has-text');
    sel.clear(); anchor = focusIdx = -1;
    expandTo(id);
    render();
    window.scrollTo(0, 0);
  }
  function expandTo(id) {
    let cur = byId.get(id);
    while (cur) { collapsed.delete(cur.Parent); cur = byId.get(cur.Parent); }
    saveCollapsed();
  }
  const saveCollapsed = () => localStorage.setItem('gbBmCollapsed', JSON.stringify([...collapsed]));
  function folderRows(id, depth, out) {
    const sub = kids(id).filter(b => b.Folder);
    const isOpen = !collapsed.has(id);
    const on = id === open && !q;
    const arrow = sub.length ? iconButton(isOpen ? 'ExpandMore' : 'ChevronRight', isOpen ? 'Collapse' : 'Expand', e => {
      e.stopPropagation();
      if (isOpen) collapsed.add(id); else collapsed.delete(id);
      saveCollapsed(); renderNav();
    }) : el('span', { class: 'arrow-gap' });
    arrow.classList.add('arrow');
    const r = el('div', { class: 'row link fold' + (on ? ' on' : ''), title: nameOf(id), tabindex: '-1' },
      arrow, icon(on ? 'Folder' : 'FolderClosed'), el('div', { class: 'text' }, el('div', { class: 'title', text: nameOf(id) })));
    r.style.paddingLeft = (6 + depth * 16) + 'px';
    r.addEventListener('click', () => goFolder(id));
    r.addEventListener('dragover', e => { if (dragIds && !dragIds.includes(id)) { e.preventDefault(); r.classList.add('drop'); } });
    r.addEventListener('dragleave', () => r.classList.remove('drop'));
    r.addEventListener('drop', e => { e.preventDefault(); r.classList.remove('drop'); if (dragIds) run('bookmarks.moveMany', { ids: dragIds, parent: id, index: -1 }, 'Moved'); });
    out.push(r);
    if (isOpen) for (const c of sub) folderRows(c.Id, depth + 1, out);
  }

  // ------------------------------------------------------------ right: the list (only visible rows are drawn)
  const nav = el('nav', { class: 'card nav', 'aria-label': 'Folders' });
  const listCard = el('div', { class: 'card list' });
  const bar = el('div', { class: 'list-bar' });
  const vlist = el('div', { class: 'vlist', role: 'listbox', tabindex: '0', 'aria-multiselectable': 'true', 'aria-label': 'Bookmarks' });
  const rowsBox = el('div', { class: 'rows-box' });
  vlist.append(rowsBox);
  listCard.append(bar, vlist);
  const layout = el('div', { class: 'bm' }, nav, listCard);

  function renderNav() {
    nav.textContent = '';
    const out = [];
    for (const r of tree.roots) folderRows(r.Id, 0, out);
    nav.append(...out);
  }

  function renderBar() {
    bar.textContent = '';
    const ids = selectedIds();
    bar.classList.toggle('selecting', ids.length > 0);
    if (ids.length) {
      const more = iconButton('More', 'More actions for selected items', e => { e.stopPropagation(); itemMenu(more); });
      bar.append(iconButton('Close', 'Cancel', () => { sel.clear(); paintRows(); }),
        el('div', { class: 'bar-title', text: ids.length + ' selected' }),
        el('button', { class: 'btn text', text: 'Delete', onclick: () => del(ids) }), more);
    } else {
      bar.append(el('div', { class: 'bar-title', text: inSearch() ? 'Search results for ‘' + box.input.value.trim() + '’' : pathOf(open) }),
        el('div', { class: 'bar-count', text: shown.length === 1 ? '1 item' : shown.length + ' items' }));
    }
  }

  const iconCache = new Map();   // site -> data URL (or '' when it has none), so scrolling never asks twice
  async function fillIcons(root) {
    const boxes = [...root.querySelectorAll('.site-icon[data-site]:not([data-done])')];
    const need = [];
    for (const b of boxes) {
      b.dataset.done = '1';
      const k = b.dataset.site;
      if (iconCache.has(k)) { const d = iconCache.get(k); if (d) setIcon(b, d); }
      else need.push(k);
    }
    const sites = [...new Set(need)];
    if (!sites.length) return;
    sites.forEach(k => iconCache.set(k, ''));
    let map = {};
    try { map = await gb.send('favicons', { urls: sites.map(k => 'https://' + k + '/') }) || {}; } catch (e) { }
    for (const k of sites) if (map[k]) iconCache.set(k, map[k]);
    for (const b of root.querySelectorAll('.site-icon[data-site]')) { const d = iconCache.get(b.dataset.site); if (d && !b.classList.contains('has')) setIcon(b, d); }
  }
  function setIcon(b, d) { b.textContent = ''; b.classList.add('has'); b.append(el('img', { src: d, alt: '' })); }

  function rowEl(b, i) {
    const more = iconButton('More', 'More actions for ' + (b.Title || b.Url), e => {
      e.stopPropagation();
      if (!sel.has(b.Id)) { selectOnly(i); paintRows(); }
      itemMenu(more);
    });
    more.classList.add('row-more');
    const r = el('div', { class: 'row item', role: 'option', title: b.Folder ? b.Title : (b.Title ? b.Title + '\n' + b.Url : b.Url), draggable: 'true' },
      b.Folder ? icon('FolderClosed') : siteIcon(b.Url),
      el('div', { class: 'text' }, el('span', { class: 'title', text: b.Title || b.Url }),
        b.Folder ? null : el('span', { class: 'url', text: inSearch() ? pathOf(b.Parent) + '  ·  ' + b.Url : b.Url })),
      more);
    r.dataset.i = i;
    r.addEventListener('mousedown', e => { if (e.button === 0 && !e.target.closest('.row-more')) { vlist.focus({ preventScroll: true }); if (!(sel.has(b.Id) && !e.ctrlKey && !e.shiftKey)) clickRow(i, e); } });
    r.addEventListener('click', e => { if (e.target.closest('.row-more')) return; if (sel.has(b.Id) && !e.ctrlKey && !e.shiftKey && sel.size > 1) { selectOnly(i); paintRows(); } });
    r.addEventListener('dblclick', e => { if (!e.target.closest('.row-more')) openItem(b, e); });
    r.addEventListener('auxclick', e => { if (e.button === 1) { e.preventDefault(); if (b.Folder) openIn(urlsOf([b.Id]), 'tab'); else openUrl(b.Url, e); } });
    r.addEventListener('contextmenu', e => {
      e.preventDefault();
      if (!sel.has(b.Id)) { selectOnly(i); paintRows(); }
      itemMenu(r, { x: e.clientX, y: e.clientY, right: e.clientX, bottom: e.clientY, top: e.clientY });
    });
    // drag one or all selected rows (onto a folder in the tree or the list, or between rows)
    r.addEventListener('dragstart', e => {
      if (!sel.has(b.Id)) { selectOnly(i); paintRows(); }
      dragIds = selectedIds();
      e.dataTransfer.effectAllowed = 'copyMove';
      const urls = urlsOf(dragIds);
      if (urls.length) { e.dataTransfer.setData('text/uri-list', urls.join('\r\n')); e.dataTransfer.setData('text/plain', urls.join('\n')); }
      else e.dataTransfer.setData('text/plain', b.Title || '');
      r.classList.add('dragging');
    });
    r.addEventListener('dragend', () => { dragIds = null; r.classList.remove('dragging'); clearMarks(); });
    r.addEventListener('dragover', e => {
      if (!dragIds || dragIds.includes(b.Id) || inSearch()) return;
      e.preventDefault();
      clearMarks();
      const rc = r.getBoundingClientRect(), y = e.clientY - rc.top;
      if (b.Folder && y > rc.height * 0.25 && y < rc.height * 0.75) r.classList.add('drop');
      else r.classList.add(y < rc.height / 2 ? 'before' : 'after');
    });
    r.addEventListener('drop', e => {
      e.preventDefault();
      const into = r.classList.contains('drop'), after = r.classList.contains('after');
      clearMarks();
      if (!dragIds) return;
      if (into) run('bookmarks.moveMany', { ids: dragIds, parent: b.Id, index: -1 }, 'Moved');
      else run('bookmarks.moveMany', { ids: dragIds, parent: open, index: i + (after ? 1 : 0) });
    });
    return r;
  }
  function clearMarks() { document.querySelectorAll('.before,.after,.drop').forEach(x => x.classList.remove('before', 'after', 'drop')); }

  let drawn = [-1, -1];
  function drawRows(force) {
    const n = shown.length;
    vlist.style.height = Math.max(n * ROW, n ? 0 : 160) + 'px';
    if (!n) {
      rowsBox.textContent = '';
      rowsBox.append(el('div', { class: 'empty' }, icon(inSearch() ? 'Search' : 'Star'),
        inSearch() ? 'No matches' : open === 1 ? 'Pages you put on the bookmarks bar show here' : 'This folder is empty'));
      drawn = [-1, -1];
      return;
    }
    const top = vlist.getBoundingClientRect().top;
    const first = Math.max(0, Math.floor(-top / ROW) - 10);
    const last = Math.min(n, Math.ceil((innerHeight - top) / ROW) + 10);
    if (!force && first === drawn[0] && last === drawn[1]) return;
    drawn = [first, last];
    rowsBox.textContent = '';
    rowsBox.style.transform = 'translateY(' + first * ROW + 'px)';
    for (let i = first; i < last; i++) rowsBox.append(rowEl(shown[i], i));
    paintRows(true);
    fillIcons(rowsBox);
  }
  /** Selection / focus / cut marks on the drawn rows (and the bar), without rebuilding them. */
  function paintRows(noBar) {
    for (const r of rowsBox.children) {
      const i = +r.dataset.i, b = shown[i];
      if (!b) continue;
      r.classList.toggle('sel', sel.has(b.Id));
      r.classList.toggle('focus', i === focusIdx && document.activeElement === vlist);
      r.classList.toggle('cut', !!(clip && clip.mode === 'cut' && clip.ids.includes(b.Id)));
      r.setAttribute('aria-selected', String(sel.has(b.Id)));
    }
    if (!noBar) renderBar();
  }
  addEventListener('scroll', () => { closeMenu(); drawRows(false); }, { passive: true });
  addEventListener('resize', () => drawRows(false));
  vlist.addEventListener('focus', () => paintRows(true));
  vlist.addEventListener('blur', () => paintRows(true));

  function scrollToIdx(i) {
    if (i < 0) return;
    const top = vlist.getBoundingClientRect().top + scrollY + i * ROW;
    const barH = document.querySelector('header.bar').offsetHeight + bar.offsetHeight;
    if (top < scrollY + barH) window.scrollTo(0, top - barH);
    else if (top + ROW > scrollY + innerHeight) window.scrollTo(0, top + ROW - innerHeight);
  }

  // keyboard on the list (Chrome's bookmark manager keys)
  vlist.addEventListener('keydown', e => {
    const n = shown.length;
    const k = e.key, ctrl = e.ctrlKey || e.metaKey;
    const move = to => {
      to = Math.max(0, Math.min(n - 1, to));
      if (e.shiftKey) selectRange(to); else if (ctrl) focusIdx = to; else selectOnly(to);
      scrollToIdx(to); drawRows(false); paintRows();
    };
    if (k === 'ArrowDown' && n) { e.preventDefault(); move(focusIdx < 0 ? 0 : focusIdx + 1); }
    else if (k === 'ArrowUp' && n) { e.preventDefault(); move(focusIdx < 0 ? 0 : focusIdx - 1); }
    else if (k === 'Home' && n) { e.preventDefault(); move(0); }
    else if (k === 'End' && n) { e.preventDefault(); move(n - 1); }
    else if (k === 'PageDown' && n) { e.preventDefault(); move((focusIdx < 0 ? 0 : focusIdx) + Math.floor(innerHeight / ROW) - 2); }
    else if (k === 'PageUp' && n) { e.preventDefault(); move((focusIdx < 0 ? 0 : focusIdx) - Math.floor(innerHeight / ROW) + 2); }
    else if (k === ' ' && ctrl && focusIdx >= 0) { e.preventDefault(); const id = shown[focusIdx].Id; if (sel.has(id)) sel.delete(id); else sel.add(id); paintRows(); }
    else if (k === 'a' && ctrl) { e.preventDefault(); shown.forEach(b => sel.add(b.Id)); paintRows(); }
    else if (k === 'Escape') { if (sel.size) { e.preventDefault(); sel.clear(); paintRows(); } }
    else if (k === 'Delete') { e.preventDefault(); del(selectedIds()); }
    else if (k === 'F2' && sel.size === 1) { e.preventDefault(); editItem(byId.get(selectedIds()[0])); }
    else if (k === 'Enter' && focusIdx >= 0) {
      e.preventDefault();
      const ids = selectedIds();
      if (ids.length > 1) openIn(urlsOf(ids), e.shiftKey ? 'window' : 'tab');
      else openItem(shown[focusIdx], e);
    }
    else if (ctrl && k === 'c') { e.preventDefault(); copyIds(selectedIds(), 'copy'); }
    else if (ctrl && k === 'x') { e.preventDefault(); copyIds(selectedIds(), 'cut'); }
    else if (ctrl && k === 'v') { e.preventDefault(); paste(); }
    else if (ctrl && k === 'z') { e.preventDefault(); undo(); }
    else if (k === 'ContextMenu' || (k === 'F10' && e.shiftKey)) {
      e.preventDefault();
      if (focusIdx >= 0 && !sel.has(shown[focusIdx].Id)) selectOnly(focusIdx);
      const r = rowsBox.querySelector('[data-i="' + focusIdx + '"]');
      if (r) itemMenu(r.querySelector('.row-more'));
    }
  });
  // Ctrl+Z / Ctrl+F also when the list is not focused (not while typing)
  document.addEventListener('keydown', e => {
    if (e.target.closest && e.target.closest('input, textarea, .dialog-shade, .menu')) return;
    if ((e.ctrlKey || e.metaKey) && e.key === 'z' && document.activeElement !== vlist) { e.preventDefault(); undo(); }
    if ((e.ctrlKey || e.metaKey) && e.key === 'f') { e.preventDefault(); box.input.focus(); box.input.select(); }
  });

  // ------------------------------------------------------------ page
  function render() {
    if (inSearch()) {
      shown = tree.items.filter(b => (b.Title || '').toLowerCase().includes(q) || (b.Url || '').toLowerCase().includes(q));
    } else shown = kids(open);
    if (focusIdx >= shown.length) focusIdx = shown.length - 1;
    if (!layout.isConnected) main.append(layout);
    renderNav();
    renderBar();
    drawRows(true);
  }

  window.addEventListener('gbbookmarks', load);
  load().then(() => { if (location.hash === '#import') { history.replaceState(null, '', location.pathname); importDialog(); } });
})();
