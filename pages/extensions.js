'use strict';
(() => {
  const main = document.getElementById('main');
  header('Extensions', 'Extension',
    el('button', { class: 'btn tonal', onclick: () => gb.send('open', { url: 'https://chromewebstore.google.com/category/extensions' }) },
      icon('OpenNew'), 'Chrome Web Store'));

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

  async function load() {
    let list = [];
    try { list = await gb.send('ext.list'); } catch (e) { }
    main.textContent = '';
    main.append(addCard());
    if (!list.length) { main.append(el('div', { class: 'empty' }, icon('Extension'), 'No extensions')); return; }
    const card = el('div', { class: 'card' }, el('h2', { text: 'Installed' }));
    for (const x of list) {
      const sw = el('button', { class: 'switch', role: 'switch', 'aria-checked': String(x.enabled), title: x.enabled ? 'Turn off' : 'Turn on' });
      sw.addEventListener('click', async () => {
        const on = sw.getAttribute('aria-checked') !== 'true';
        await gb.send('ext.enable', { id: x.id, on }); sw.setAttribute('aria-checked', String(on));
      });
      const img = x.icon ? el('img', { class: 'fav', src: x.icon, alt: '' }) : icon('Extension');
      card.append(el('div', { class: 'row' }, img,
        el('div', { class: 'text' },
          el('div', { class: 'title', text: x.name + (x.version ? '  ' + x.version : '') }),
          el('div', { class: 'sub', text: x.description || x.id })),
        el('div', { class: 'actions' },
          x.popup ? iconButton('OpenNew', 'Open', () => gb.send('ext.popup', { id: x.id })) : null,
          x.options ? iconButton('Settings', 'Options', () => gb.send('ext.options', { id: x.id })) : null,
          iconButton('Delete', 'Remove', async () => {
            if (!confirm('Remove ' + x.name + '?')) return;
            await gb.send('ext.remove', { id: x.id }); snack('Removed'); load();
          })),
        sw));
    }
    main.append(card);
  }
  load();
})();
