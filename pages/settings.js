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

  // Appearance
  const themeSel = el('select', { 'aria-label': 'Theme' }, [['system', 'System'], ['light', 'Light'], ['dark', 'Dark']]
    .map(([v, t]) => el('option', { value: v, text: t, selected: v === (s.ThemeMode || 'system') })));
  themeSel.addEventListener('change', () => set('ThemeMode', themeSel.value));
  // colour: presets, Windows accent colour, or any colour (like Chrome's "Customize Chrome")
  const ti = await gb.send('theme.info');
  const swatches = el('div', { class: 'swatches' });
  function swatch(id, title, hex, iconName) {
    const b = el('button', { class: 'swatch' + (ti.color === id ? ' on' : ''), title, 'aria-label': title, 'aria-pressed': String(ti.color === id) });
    if (hex) b.style.background = hex;
    if (iconName) b.append(icon(iconName, 's20'));
    b.addEventListener('click', () => choose(id));
    return b;
  }
  async function choose(id) {
    await set('ThemeColor', id);
    ti.color = id;
    drawSwatches();
  }
  function drawSwatches() {
    swatches.textContent = '';
    for (const p of ti.presets) swatches.append(swatch(p.id, p.name, p.hex));
    if (ti.accent) swatches.append(swatch('accent', 'Windows accent colour', ti.accent, 'Desktop'));
    const custom = ti.color.startsWith('#') && !ti.presets.some(p => p.id === ti.color);
    const picker = el('input', { type: 'color', value: custom ? ti.color : '#785a00', 'aria-label': 'Custom colour' });
    picker.addEventListener('change', () => choose(picker.value));
    const cb = el('label', { class: 'swatch custom' + (custom ? ' on' : ''), title: 'Custom colour' }, icon('Colorize', 's20'), picker);
    if (custom) cb.style.background = ti.color;
    swatches.append(cb);
  }
  drawSwatches();
  main.append(el('div', { class: 'card', id: 'appearance' }, el('h2', { text: 'Appearance' }), row('Tune', 'Theme', null, themeSel),
    el('div', { class: 'row' }, icon('Palette'), el('div', { class: 'text' }, el('div', { class: 'title', text: 'Colour' }), swatches))));
  if (location.hash === '#appearance') document.getElementById('appearance').scrollIntoView();

  // Start and search
  const engine = el('select', { 'aria-label': 'Search engine' }, engines.map(e => el('option', { value: e.id, text: e.name, selected: e.id === s.SearchEngine })));
  engine.addEventListener('change', () => set('SearchEngine', engine.value));
  const home = el('input', { class: 'field', value: s.HomePage, spellcheck: 'false', 'aria-label': 'Home page' });
  home.addEventListener('change', () => set('HomePage', home.value));
  main.append(el('div', { class: 'card' }, el('h2', { text: 'Start and search' }),
    row('Search', 'Search engine', null, engine),
    row('Home', 'Home page', null, home),
    row('Restore', 'Reopen tabs on start', null, sw('RestoreTabs')),
    row('Star', 'Bookmarks bar', null, sw('ShowBookmarksBar')),
    row('Home', 'Show home button', null, sw('ShowHomeButton'))));

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
        ab.ready ? 'Built ' + fmtIsoDate(ab.built) + ', loaded in ' + ab.loadMs + ' ms' : null, null));
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

  // Sites that are never put to sleep
  const keepCard = el('div', { class: 'card' }, el('h2', { text: 'Always keep these sites active' }));
  const keepList = el('div');
  const keepInput = el('input', { class: 'field grow', placeholder: 'Site, e.g. web.whatsapp.com', spellcheck: 'false', 'aria-label': 'Site' });
  async function addKeep() {
    if (!keepInput.value.trim()) return;
    try { await gb.send('keep.add', { site: keepInput.value }); keepInput.value = ''; drawKeep(); }
    catch (e) { snack(e.message); }
  }
  keepInput.addEventListener('keydown', e => { if (e.key === 'Enter') addKeep(); });
  keepCard.append(el('div', { class: 'row' }, keepInput, el('button', { class: 'btn tonal', onclick: addKeep }, icon('Add'), 'Add')), keepList);
  async function drawKeep() {
    const cur = (await gb.send('settings.get')).settings.KeepActiveSites || [];
    keepList.textContent = '';
    for (const site of cur) {
      const r = el('div', { class: 'row' }, letter('https://' + site), el('div', { class: 'text' }, el('div', { class: 'title', text: site })),
        iconButton('Delete', 'Remove', async () => { await gb.send('keep.remove', { site }); r.remove(); }));
      keepList.append(r);
    }
  }
  drawKeep();
  main.append(keepCard);

  // Secure DNS (DNS over HTTPS): provider list, custom address, restart notice
  const dns = await gb.send('dns.info');
  const isCustom = dns.setting.startsWith('custom:');
  const dnsSel = el('select', { 'aria-label': 'Secure DNS' },
    [['off', 'Automatic']].concat(dns.providers.map(p => [p.id, p.name])).concat([['custom', 'Custom']])
      .map(([v, t]) => el('option', { value: v, text: t, selected: v === (isCustom ? 'custom' : dns.setting) })));
  const dnsCustom = el('input', { class: 'field grow', placeholder: 'https://dns.example/dns-query', spellcheck: 'false',
    'aria-label': 'Custom DNS over HTTPS address', value: isCustom ? dns.setting.slice(7) : '' });
  const restartRow = el('div', { class: 'row' }, icon('Restore'), el('div', { class: 'text' },
    el('div', { class: 'title', text: 'Restart GBrowser to use the new DNS setting' })),
    el('button', { class: 'btn', onclick: () => gb.send('app.restart') }, icon('Refresh'), 'Restart'));
  const customRow = el('div', { class: 'row' }, el('div', { class: 'grow' }), dnsCustom,
    el('button', { class: 'btn tonal', onclick: () => saveDns('custom:' + dnsCustom.value.trim()) }, 'Save'));
  let current = dns.setting;
  function dnsUi() {
    customRow.style.display = dnsSel.value === 'custom' ? '' : 'none';
    restartRow.style.display = current !== dns.active ? '' : 'none';
  }
  async function saveDns(value) {
    try { await gb.send('settings.set', { key: 'SecureDns', value }); current = value; dnsUi(); }
    catch (e) { snack(e.message); }
  }
  dnsSel.addEventListener('change', () => { if (dnsSel.value === 'custom') { dnsUi(); dnsCustom.focus(); } else saveDns(dnsSel.value); });
  dnsCustom.addEventListener('keydown', e => { if (e.key === 'Enter') saveDns('custom:' + dnsCustom.value.trim()); });
  const dnsRow = el('div', { class: 'rows' },
    row('Language', 'Secure DNS', 'Looks up site addresses over an encrypted connection (DNS over HTTPS)', dnsSel),
    customRow, restartRow);
  dnsUi();

  // Privacy and security
  const tp = el('select', { 'aria-label': 'Tracking prevention' }, [['basic', 'Basic'], ['balanced', 'Balanced'], ['strict', 'Strict']]
    .map(([v, t]) => el('option', { value: v, text: t, selected: v === (s.TrackingPrevention || 'balanced') })));
  tp.addEventListener('change', () => set('TrackingPrevention', tp.value));
  main.append(el('div', { class: 'card' }, el('h2', { text: 'Privacy and security' }),
    row('Lock', 'Always use secure connections', 'Opens sites over HTTPS; warns before an insecure site', sw('HttpsFirst')),
    row('ShieldLock', 'Dangerous site and download protection', 'Microsoft Defender SmartScreen', sw('SmartScreen')),
    row('Policy', 'Tracking prevention', 'Microsoft Edge tracking prevention, on top of ad blocking', tp),
    dnsRow,
    row('Delete', 'Clear data when GBrowser closes', 'Cookies, site data, cache and history (passwords kept)', sw('ClearOnExit')),
    row('Verified', 'Your data is encrypted', 'History, bookmarks, tabs and settings: only your Windows account can read them', null)));

  // Clear browsing data
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
