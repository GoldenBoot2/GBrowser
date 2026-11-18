'use strict';
(async () => {
  document.getElementById('brand').append(el('div', { class: 'logo' }, icon('Language')), 'GBrowser');
  const f = document.getElementById('f');
  const input = el('input', { type: 'text', spellcheck: 'false', autocomplete: 'off', 'aria-label': 'Search or type a URL' });
  const clear = iconButton('Close', 'Clear', () => { input.value = ''; f.classList.remove('has-text'); input.focus(); });
  clear.classList.add('clear'); clear.type = 'button';
  f.append(icon('Search'), input, clear);
  input.addEventListener('input', () => f.classList.toggle('has-text', input.value.length > 0));
  f.addEventListener('submit', e => { e.preventDefault(); if (input.value.trim()) gb.send('go', { text: input.value }); });
  let data = { tiles: [], engine: 'Google' };
  try { data = await gb.send('newtab.data'); } catch (e) { }
  input.placeholder = 'Search ' + data.engine + ' or type a URL';
  const tiles = document.getElementById('tiles');
  for (const t of data.tiles) {
    const a = el('a', { class: 'tile', href: t.url, title: t.title + '\n' + t.url }, letter(t.url), el('span', { text: t.title }));
    tiles.append(a);
  }
})();
