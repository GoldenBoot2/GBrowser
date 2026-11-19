'use strict';
// Request log of one tab (shield menu > Request log): what the page loaded and what GBrowser blocked or replaced,
// newest first, live; filters, search, the page's cookies.
(() => {
  const main = document.getElementById('main');
  const tabId = +new URLSearchParams(location.search).get('tab');
  let q = '', show = 'all', data = null, paused = false;
  const box = searchBox('Search addresses', v => { q = v.trim().toLowerCase(); render(); });
  header('Request log', 'Find', box);

  async function load() {
    if (paused) return;
    try { data = await gb.send('log.get', { tab: tabId }); } catch (e) { main.textContent = ''; main.append(el('div', { class: 'empty' }, icon('Info'), e.message)); return; }
    render();
  }
  function render() {
    if (!data) return;
    main.textContent = '';
    const blocked = data.entries.filter(x => x.r !== 'allowed');
    const sel = el('select', { 'aria-label': 'Show' }, [['all', 'All requests'], ['blocked', 'Blocked and replaced'], ['allowed', 'Allowed']]
      .map(([v, t]) => el('option', { value: v, text: t, selected: v === show })));
    sel.addEventListener('change', () => { show = sel.value; render(); });
    const pause = el('button', { class: 'btn text', text: paused ? 'Resume' : 'Pause' });
    pause.addEventListener('click', () => { paused = !paused; render(); if (!paused) load(); });
    let site = '';
    try { site = new URL(data.page).hostname.replace(/^www\./, ''); } catch (e) { }
    const head = el('div', { class: 'card' }, el('div', { class: 'row' }, el('div', { class: 'text' },
      el('div', { class: 'title', text: data.title || data.page }),
      el('div', { class: 'sub', text: data.entries.length + ' requests, ' + blocked.length + ' blocked or replaced' })), sel, pause,
      site ? el('button', { class: 'btn tonal', text: 'Cookies', onclick: () => openUrl('https://gbrowser.local/cookies.html?site=' + encodeURIComponent(site), { ctrlKey: true }) }) : null));
    main.append(head);
    const list = el('div', { class: 'card' });
    const rows = data.entries.slice().reverse().filter(x => (show === 'all' || (show === 'allowed' ? x.r === 'allowed' : x.r !== 'allowed')) && (!q || x.u.toLowerCase().includes(q)));
    for (const x of rows.slice(0, 500)) {
      const bad = x.r !== 'allowed';
      let host = x.u, path = '';
      try { const u = new URL(x.u); host = u.hostname; path = u.pathname + u.search; } catch (e) { }
      list.append(el('div', { class: 'row', title: x.u },
        el('span', { class: 'chip' + (bad ? ' warn' : ''), text: x.r }),
        el('div', { class: 'text' }, el('div', { class: 'title', text: host }), el('div', { class: 'sub', text: x.y + '  ' + path.slice(0, 160) })),
        el('div', { class: 'time', text: new Date(x.t).toLocaleTimeString('en-GB') })));
    }
    if (!rows.length) list.append(el('div', { class: 'empty' }, icon('Find'), 'Nothing yet'));
    main.append(list);
  }
  load();
  setInterval(load, 2000);
})();
