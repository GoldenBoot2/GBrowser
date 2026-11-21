'use strict';
(() => {
  // Chrome's chrome://extensions (measured on Chrome 158, 21-11-2026): list with search + "Pin new extensions to
  // toolbar", and a details view per extension (?id=). Shown on its own page, or inside Settings > Extensions
  // (window.__gbEmbed = the container; the details view then opens inside it).
  const embed = window.__gbEmbed;
  const main = embed || document.getElementById('main');
  const storeUrl = 'https://chromewebstore.google.com/category/extensions';
  const storeBtn = el('button', { class: 'btn tonal', onclick: () => gb.send('open', { url: storeUrl }) },
    icon('OpenNew'), 'Chrome Web Store');
  let list = [], q = '', detailId = embed ? null : new URLSearchParams(location.search).get('id');
  const search = searchBox('Search extensions', v => { q = v.trim().toLowerCase(); if (!detailId) renderList(); });
  if (!embed) header('Extensions', 'Extension', el('div', { class: 'head-tools' }, search, storeBtn));

  const sizeText = n => n < 1024 * 1024 ? '< 1 MB' : (n / 1024 / 1024).toFixed(1) + ' MB';
  const extIcon = x => x.icon ? el('img', { class: 'fav', src: x.icon, alt: '' }) : icon('Extension');

  function switchBtn(on, title, onchange) {
    const sw = el('button', { class: 'switch', role: 'switch', 'aria-checked': String(!!on), 'aria-label': title, title });
    sw.addEventListener('click', async () => {
      const now = sw.getAttribute('aria-checked') !== 'true';
      try { await onchange(now); sw.setAttribute('aria-checked', String(now)); } catch (e) { snack(e.message); }
    });
    return sw;
  }

  function row(iconName, title, sub, ...right) {
    return el('div', { class: 'row' }, typeof iconName === 'string' ? icon(iconName) : iconName,
      el('div', { class: 'text' }, el('div', { class: 'title', text: title }), sub ? el('div', { class: 'sub', text: sub }) : null), ...right);
  }

  async function remove(x) {
    if (!confirm('Remove ' + x.name + '?')) return false;
    await gb.send('ext.remove', { id: x.id });
    snack('Removed');
    return true;
  }

  // ---------- add card (store link / ID, or an unpacked folder)
  function addCard() {
    const input = el('input', { class: 'field grow', placeholder: 'Chrome Web Store link or extension ID', spellcheck: 'false', 'aria-label': 'Store link or ID' });
    const btn = el('button', { class: 'btn' }, icon('Add'), 'Add');
    async function install() {
      if (!input.value.trim()) return;
      btn.disabled = true; btn.lastChild.textContent = 'Adding...';
      try { const name = await gb.send('ext.install', { text: input.value }); snack(name + ' added'); input.value = ''; load(); }
      catch (e) { snack(e.message); }
      btn.disabled = false; btn.lastChild.textContent = 'Add';
    }
    btn.addEventListener('click', install);
    input.addEventListener('keydown', e => { if (e.key === 'Enter') install(); });
    const unpacked = el('button', { class: 'btn text', onclick: async () => {
      try { const n = await gb.send('ext.pick'); if (n) { snack(n + ' loaded'); load(); } } catch (e) { snack(e.message); }
    } }, icon('Folder'), 'Load unpacked');
    return el('div', { class: 'card' }, el('h2', { text: 'Add extension' }), el('div', { class: 'row' }, input, btn, unpacked));
  }

  // ---------- list
  let prefs = { pinNew: true };
  function renderList() {
    main.textContent = '';
    if (embed) main.append(el('div', { class: 'card' }, row('Extension', 'Get extensions', 'Chrome Web Store', storeBtn), el('div', { class: 'row' }, search)));
    main.append(addCard());
    const pinCard = el('div', { class: 'card' },
      row('Pin', 'Pin new extensions to toolbar', null, switchBtn(prefs.pinNew, 'Pin new extensions to toolbar', on => gb.send('ext.pinnew', { on }))));
    main.append(pinCard);
    if (!list.length) { main.append(el('div', { class: 'empty' }, icon('Extension'), 'No extensions')); return; }
    const shown = list.filter(x => !q || (x.name + ' ' + x.description).toLowerCase().includes(q));
    const card = el('div', { class: 'card' }, el('h2', { text: 'Installed' }));
    if (!shown.length) card.append(el('div', { class: 'row' }, el('div', { class: 'sub', text: 'No matches' })));
    for (const x of shown) {
      const sw = switchBtn(x.enabled, x.enabled ? 'Turn off' : 'Turn on', async on => { await gb.send('ext.enable', { id: x.id, on }); x.enabled = on; sw.title = on ? 'Turn off' : 'Turn on'; });
      card.append(el('div', { class: 'row' }, extIcon(x),
        el('div', { class: 'text' },
          el('div', { class: 'title', text: x.name + (x.version ? '  ' + x.version : '') }),
          el('div', { class: 'sub', text: x.description || x.id })),
        el('div', { class: 'actions' },
          el('button', { class: 'btn text', onclick: () => openDetails(x.id) }, 'Details'),
          x.options ? iconButton('Settings', 'Options', () => gb.send('ext.options', { id: x.id })) : null,
          iconButton(x.pinned ? 'PinFill' : 'Pin', x.pinned ? 'Unpin from toolbar' : 'Pin to toolbar', async () => {
            await gb.send('ext.pin', { id: x.id, on: !x.pinned }); x.pinned = !x.pinned; renderList();
          }),
          iconButton('Delete', 'Remove', async () => { if (await remove(x)) load(); })),
        sw));
    }
    main.append(card);
  }

  // ---------- details (Chrome's chrome://extensions/?id=)
  function openDetails(id) {
    detailId = id;
    if (!embed) history.pushState({ id }, '', '?id=' + id);
    render();
  }
  function closeDetails() {
    if (!embed && history.state && history.state.id) { history.back(); return; }
    detailId = null;
    if (!embed) history.replaceState(null, '', location.pathname);
    render();
  }
  addEventListener('popstate', () => { detailId = new URLSearchParams(location.search).get('id'); render(); });

  function shortcutField(value, onset) {
    // like Chrome's "Type a shortcut": Ctrl or Alt + letter / digit / F-key; Backspace / Delete clears
    const box = el('input', { class: 'field shortcut', readonly: 'readonly', value: value || '', placeholder: 'Not set', 'aria-label': 'Shortcut', spellcheck: 'false' });
    let before = value || '';
    box.addEventListener('focus', () => { before = box.value; box.placeholder = 'Type a shortcut'; });
    box.addEventListener('blur', () => { box.placeholder = 'Not set'; if (box.value !== before) box.value = before; });
    box.addEventListener('keydown', async e => {
      e.preventDefault();
      if (e.key === 'Escape') { box.value = before; box.blur(); return; }
      if (e.key === 'Tab') return;
      if (e.key === 'Backspace' || e.key === 'Delete') {
        try { await onset(''); before = box.value = ''; snack('Shortcut removed'); } catch (er) { snack(er.message); }
        return;
      }
      const k = /^Key([A-Z])$/.exec(e.code) ? e.code.slice(3) : /^Digit(\d)$/.exec(e.code) ? e.code.slice(5) : /^F([1-9]|1[0-2])$/.test(e.code) ? e.code : null;
      if (!k) return;
      if (!e.ctrlKey && !e.altKey) { snack('Include Ctrl or Alt'); return; }
      const text = (e.ctrlKey ? 'Ctrl+' : '') + (e.altKey ? 'Alt+' : '') + (e.shiftKey ? 'Shift+' : '') + k;
      try { await onset(text); before = box.value = text; box.blur(); } catch (er) { snack(er.message); }
    });
    return box;
  }

  async function renderDetails() {
    const x = list.find(i => i.id === detailId);
    let d = null;
    try { d = await gb.send('ext.details', { id: detailId }); } catch (e) { }
    main.textContent = '';
    const back = iconButton('Back', 'Back', closeDetails);
    if (!x || !d) {
      main.append(el('div', { class: 'subpage-head' }, back, el('h2', { class: 'section-title', text: 'Extension' })),
        el('div', { class: 'empty' }, icon('Extension'), 'Not installed'));
      return;
    }
    main.append(el('div', { class: 'subpage-head' }, back, el('h2', { class: 'section-title', text: x.name })));
    // Chrome: the name is the page heading; the first row is just On / Off with the switch
    const top = el('div', { class: 'card' },
      row(extIcon(x), x.enabled ? 'On' : 'Off', null,
        switchBtn(x.enabled, x.enabled ? 'Turn off' : 'Turn on', async on => { await gb.send('ext.enable', { id: x.id, on }); x.enabled = on; renderDetails(); })),
      row('Info', 'Description', x.description || 'None'),
      row('Verified', 'Version', d.version || x.version || ''),
      row('Folder', 'Size', sizeText(d.size)));
    main.append(top);

    const perm = el('div', { class: 'card' }, el('h2', { text: 'Permissions' }));
    if (!d.access.length) perm.append(row('Verified', 'No special permissions', null));
    for (const a of d.access) perm.append(row(a.startsWith('Read and change all') ? 'Warning' : 'Policy', a, null));
    main.append(perm);

    const site = el('div', { class: 'card' }, el('h2', { text: 'Site access' }));
    if (!d.hosts.length) site.append(row('Public', 'No site access', null));
    else {
      site.append(row('Public', 'Can read and change your data on these sites', null));
      for (const h of d.hosts) site.append(el('div', { class: 'row sub-row' }, el('div', { class: 'text' }, el('div', { class: 'sub', text: h }))));
    }
    main.append(site);

    const use = el('div', { class: 'card' },
      row('Pin', 'Pin to toolbar', null, switchBtn(d.pinned, 'Pin to toolbar', on => gb.send('ext.pin', { id: x.id, on }))));
    if (x.options) use.append(row('Settings', 'Extension options', null, el('button', { class: 'btn text', onclick: () => gb.send('ext.options', { id: x.id }) }, icon('OpenNew'), 'Open')));
    if (x.popup) use.append(row('Keyboard', 'Activate the extension', 'Keyboard shortcut', shortcutField(d.shortcut, key => gb.send('ext.shortcut', { id: x.id, key }))));
    for (const c of d.commands) use.append(row('Keyboard', c.description, c.key ? 'Set by the extension: ' + c.key : 'No shortcut'));
    main.append(use);

    const src = el('div', { class: 'card' },
      row(d.store ? 'Verified' : 'Folder', 'Source', d.store ? 'Chrome Web Store' : 'Unpacked extension' + (d.path ? ' loaded from ' + d.path : '')),
      d.store ? row('OpenNew', 'View in Chrome Web Store', null, el('button', { class: 'btn text', onclick: () => gb.send('open', { url: d.homepage }) }, icon('OpenNew'), 'Open')) : null,
      row('Delete', 'Remove extension', null, el('button', { class: 'btn danger', onclick: async () => { if (await remove(x)) { detailId = null; if (!embed) history.replaceState(null, '', location.pathname); load(); } } }, 'Remove')));
    main.append(src);
  }

  function render() { if (detailId) renderDetails(); else renderList(); }

  async function load() {
    try { list = await gb.send('ext.list'); } catch (e) { list = []; }
    try { prefs = await gb.send('ext.prefs'); } catch (e) { }
    render();
  }
  load();
})();
