'use strict';
(async () => {
  const main = document.getElementById('main');
  header('About', 'Info');
  const i = await gb.send('info');
  const row = (iconName, title, sub, extra) => el('div', { class: 'row' }, icon(iconName),
    el('div', { class: 'text' }, el('div', { class: 'title', text: title }), sub ? el('div', { class: 'sub mono', text: sub }) : null), extra || null);
  main.append(
    el('div', { class: 'card' }, el('div', { class: 'app-card' },
      el('div', { class: 'logo' }, icon('Language')),
      el('div', {}, el('div', { class: 'name', text: 'GBrowser' }), el('div', { class: 'ver', text: 'Version ' + i.version })))),
    el('div', { class: 'card' },
      row('Person', 'Developer: GoldenBoot'),
      row('Verified', 'Version ' + i.version),
      row('Public', 'Engine: Microsoft Edge WebView2 ' + i.engine),
      row('Folder', 'Data folder', i.dataDir, iconButton('OpenNew', 'Open folder', () => gb.send('openDataDir')))));
})();
