'use strict';
(async () => {
  const main = document.getElementById('main');
  header('Settings', 'Settings');
  const { settings: s, engines } = await gb.send('settings.get');
  const set = (key, value) => gb.send('settings.set', { key, value }).catch(e => snack(e.message));

  function sw(key) {
    const b = el('button', { class: 'switch', role: 'switch', 'aria-checked': String(!!s[key]) });
    b.addEventListener('click', () => { s[key] = !s[key]; b.setAttribute('aria-checked', String(s[key])); set(key, s[key]); });
    return b;
  }
  function row(iconName, title, sub, control) {
    return el('div', { class: 'row' }, icon(iconName), el('div', { class: 'text' },
      el('div', { class: 'title', text: title }), sub ? el('div', { class: 'sub', text: sub }) : null), control);
  }

  // Start and search
  const engine = el('select', { 'aria-label': 'Search engine' }, engines.map(e => el('option', { value: e.id, text: e.name, selected: e.id === s.SearchEngine })));
  engine.addEventListener('change', () => set('SearchEngine', engine.value));
  const home = el('input', { class: 'field', value: s.HomePage, spellcheck: 'false', 'aria-label': 'Home page' });
  home.addEventListener('change', () => set('HomePage', home.value));
  main.append(el('div', { class: 'card' }, el('h2', { text: 'Start and search' }),
    row('Search', 'Search engine', null, engine),
    row('Home', 'Home page', null, home),
    row('Restore', 'Reopen tabs on start', null, sw('RestoreTabs')),
    row('Star', 'Bookmarks bar', null, sw('ShowBookmarksBar'))));

  // Ad blocking
  const ab = await gb.send('adblock.info').catch(() => null);
  const abCard = el('div', { class: 'card' }, el('h2', { text: 'Ad blocking' }),
    row('Shield', 'Ad blocking', ab ? (ab.total.toLocaleString() + ' blocked in total') : null, sw('AdblockEnabled')),
    row('Block', 'Annoyances', 'Cookie notices, popups, app banners', sw('BlockAnnoyances')),
    row('Cookie', 'Trackers', 'Also removes tracking codes from links', sw('BlockTrackers')),
    row('Tune', 'Hide ad leftovers', null, sw('HideAdLeftovers')),
    row('Verified', 'Stealth mode', 'Sites cannot see hidden ads are gone', sw('Stealth')),
    row('Lock', 'Block pages of ad sites', null, sw('BlockAdSitePages')),
    row('Person', 'Do Not Track', null, sw('DoNotTrack')));
  main.append(abCard);
  if (ab) {
    const allowed = el('div', { class: 'card' }, el('h2', { text: 'Sites without blocking' }));
    if (!ab.allowed.length) allowed.append(el('div', { class: 'row' }, el('div', { class: 'sub', text: 'None' })));
    for (const site of ab.allowed) {
      const r = el('div', { class: 'row' }, letter('https://' + site), el('div', { class: 'text' }, el('div', { class: 'title', text: site })),
        iconButton('Delete', 'Remove', async () => { await gb.send('allow.remove', { site }); r.remove(); }));
      allowed.append(r);
    }
    main.append(allowed);
    const lists = el('div', { class: 'card' }, el('h2', { text: 'Filter lists' }),
      row('Shield', ab.ready ? ab.rules.toLocaleString() + ' rules' : (ab.error || 'Loading'),
        ab.ready ? 'Built ' + ab.built + ', loaded in ' + ab.loadMs + ' ms' : null, null));
    for (const l of ab.lists)
      lists.append(el('div', { class: 'row' }, el('div', { class: 'text' }, el('div', { class: 'title', text: l.name }),
        el('div', { class: 'sub', text: (+l.rules).toLocaleString() + ' rules' + (l.version && !l.version.startsWith('%') ? ' - ' + l.version : '') }))));
    main.append(lists);
  }

  // Memory
  const sleep = el('select', { 'aria-label': 'Sleeping tabs' }, [[0, 'Never'], [1, '1 minute'], [5, '5 minutes'], [15, '15 minutes'], [30, '30 minutes'], [60, '1 hour']]
    .map(([v, t]) => el('option', { value: v, text: t, selected: v === s.SleepMinutes })));
  sleep.addEventListener('change', () => set('SleepMinutes', +sleep.value));
  const memRows = el('div');
  const memCard = el('div', { class: 'card' }, el('h2', { text: 'Memory' }),
    row('Bedtime', 'Put background tabs to sleep after', null, sleep),
    row('Memory', 'Low memory for background tabs', null, sw('LowMemoryHiddenTabs')),
    memRows);
  main.append(memCard);
  async function mem() {
    try {
      const m = await gb.send('memory');
      memRows.textContent = '';
      memRows.append(row('Memory', 'In use now: ' + m.totalMb.toFixed(0) + ' MB',
        m.tabs + ' tabs: ' + m.live + ' active, ' + m.sleeping + ' sleeping, ' + m.unloaded + ' not loaded', null));
    } catch (e) { }
  }
  mem(); setInterval(mem, 3000);

  // Privacy
  const kinds = [['cookies', 'Cookies and site data'], ['cache', 'Cached files'], ['history', 'Browsing and download history'], ['passwords', 'Saved passwords'], ['autofill', 'Autofill data']];
  const checks = kinds.map(([k, t]) => { const c = el('input', { type: 'checkbox', value: k }); if (k === 'cache') c.checked = true; return el('label', { class: 'row' }, c, el('span', { text: t })); });
  const clearBtn = el('button', { class: 'btn', onclick: async () => {
    const sel = checks.map(l => l.querySelector('input')).filter(c => c.checked).map(c => c.value);
    if (!sel.length) return;
    clearBtn.disabled = true;
    try { await gb.send('clearData', { kinds: sel }); snack('Cleared'); } catch (e) { snack(e.message); }
    clearBtn.disabled = false;
  } }, icon('Delete'), 'Clear data');
  main.append(el('div', { class: 'card' }, el('h2', { text: 'Clear browsing data' }), checks, el('div', { class: 'row' }, clearBtn)));
})();
