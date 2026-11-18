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
      updateRow(),
      row('Public', 'Engine: Microsoft Edge WebView2 ' + i.engine),
      row('Folder', 'Data folder', i.dataDir, iconButton('OpenNew', 'Open folder', () => gb.send('openDataDir')))));

  // Updates (GitHub Releases): live state from the app, "Check for updates", "Relaunch to update"
  function updateRow() {
    const sub = el('div', { class: 'sub' });
    const btn = el('button', { class: 'btn tonal' });
    const r = el('div', { class: 'row' }, icon('Verified'),
      el('div', { class: 'text' }, el('div', { class: 'title', text: 'Version ' + i.version }), sub), btn);
    function show(u) {
      const ready = u.state === 'ready';
      sub.textContent = u.state === 'checking' ? 'Checking for updates...'
        : u.state === 'downloading' ? 'Downloading version ' + u.latest + ' (' + u.percent + '%)'
        : ready ? 'Version ' + u.latest + ' is ready'
        : u.state === 'error' ? 'Update check failed: ' + u.error
        : u.state === 'uptodate' ? 'Up to date' + (u.checked ? ' · checked ' + u.checked : '')
        : 'Updates are checked automatically';
      btn.textContent = ready ? 'Relaunch to update' : 'Check for updates';
      btn.disabled = u.state === 'checking' || u.state === 'downloading';
      btn.onclick = ready ? () => gb.send('update.install') : async () => show(await gb.send('update.check'));
    }
    gb.send('update.status').then(show);
    window.addEventListener('gbupdate', e => show(e.detail));
    return r;
  }
})();
