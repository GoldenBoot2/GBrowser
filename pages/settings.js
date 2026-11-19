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
  // Default browser (Windows lets only the user choose: GBrowser registers itself and opens Default apps)
  const defRow = el('div');
  async function drawDefault() {
    const d = await gb.send('default.info');
    const btn = el('button', { class: 'btn tonal', text: 'Make default' });
    btn.addEventListener('click', async () => { await gb.send('default.make'); snack('Choose GBrowser in Default apps'); });
    defRow.textContent = '';
    defRow.append(d.isDefault ? row('Public', 'GBrowser is your default browser', null, null)
      : row('Public', 'Default browser', 'Make GBrowser the browser Windows opens links with', btn));
  }
  await drawDefault();
  document.addEventListener('visibilitychange', () => { if (!document.hidden) drawDefault(); });
  window.addEventListener('focus', drawDefault);
  main.append(el('div', { class: 'card', id: 'default' }, el('h2', { text: 'Default browser' }), defRow));
  main.append(el('div', { class: 'card' }, el('h2', { text: 'Start and search' }),
    row('Search', 'Search engine', null, engine),
    row('Search', 'Search suggestions', 'What you type in the address bar is sent to the search engine', sw('SearchSuggestions')),
    row('Home', 'Home page', null, home),
    row('Restore', 'Reopen tabs on start', null, sw('RestoreTabs')),
    row('Star', 'Bookmarks bar', null, sw('ShowBookmarksBar')),
    row('Home', 'Show home button', null, sw('ShowHomeButton'))));

  // Ad blocking
  const ab = await gb.send('adblock.info').catch(() => null);
  const abCard = el('div', { class: 'card' }, el('h2', { text: 'Ad blocking' }),
    row('Shield', 'Ad blocking', ab ? (ab.total.toLocaleString() + ' blocked in total') : null, sw('AdblockEnabled')),
    (() => { const b = el('button', { class: 'btn text', text: 'Reset count' }); b.addEventListener('click', async () => { await gb.send('stats.reset'); snack('Count reset'); }); return row('Speed', 'Statistics', ab ? ab.rules.toLocaleString() + ' rules from ' + ab.lists.length + ' lists' + (ab.lists.some(l => l.on === false) ? ' (' + ab.lists.filter(l => l.on !== false).length + ' on)' : '') : null, b); })(),
    row('Block', 'Annoyances', 'Cookie notices, popups, app banners', sw('BlockAnnoyances')),
    row('Cookie', 'Trackers', 'Also removes tracking codes from links', sw('BlockTrackers')),
    row('Tune', 'Hide ad leftovers', null, sw('HideAdLeftovers')),
    row('Verified', 'Stealth mode', 'Sites cannot see hidden ads are gone', sw('Stealth')),
    row('Lock', 'Block pages of ad and tracker sites', null, sw('BlockAdSitePages')),
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
    // each list on / off (applies to pages loaded after the change); grouped like the switches above
    const groupName = ['Ads', 'Annoyances', 'Trackers'];
    for (const g of ['0', '1', '2']) {
      const of = ab.lists.filter(l => (l.group || '0') === g);
      if (!of.length) continue;
      lists.append(el('div', { class: 'row' }, el('div', { class: 'sub', text: groupName[+g] })));
      for (const l of of) {
        const b = el('button', { class: 'switch', role: 'switch', 'aria-checked': String(l.on !== false), 'aria-label': l.name });
        b.addEventListener('click', async () => {
          l.on = !(l.on !== false);
          b.setAttribute('aria-checked', String(l.on));
          try { await gb.send('lists.set', { file: l.file, on: l.on }); snack(l.on ? 'On for pages you open now' : 'Off for pages you open now'); }
          catch (e) { snack(e.message); }
        });
        if (!l.file) b.disabled = true;
        lists.append(el('div', { class: 'row' }, el('div', { class: 'text' }, el('div', { class: 'title', text: l.name }),
          el('div', { class: 'sub', text: (+l.rules).toLocaleString() + ' rules' + (l.version && !l.version.startsWith('%') ? ' - ' + l.version : '') })), b));
      }
    }
    // daily host-list update
    const hs = await gb.send('hosts.state');
    const upd = el('button', { class: 'btn text', text: 'Update now' });
    const hsub = () => hs.state || (hs.updated ? 'Updated ' + new Date(hs.updated).toLocaleString('en-GB') + ', ' + hs.hosts.toLocaleString() + ' hosts' : 'Not updated yet');
    const hrow = row('Download', 'Update host lists daily', hsub(), el('div', { class: 'actions' }, upd, sw('HostListUpdates')));
    upd.addEventListener('click', async () => {
      upd.disabled = true; upd.textContent = 'Updating...';
      try { await gb.send('hosts.update'); Object.assign(hs, await gb.send('hosts.state')); hrow.querySelector('.sub').textContent = hsub(); snack('Host lists updated'); }
      catch (e) { snack(e.message); }
      upd.disabled = false; upd.textContent = 'Update now';
    });
    lists.insertBefore(hrow, lists.children[2] || null);
    main.append(lists);
  }

  // Your rules (trusted) and custom filter lists (trust check like AdGuard: unknown sources cannot use trusted-*
  // scriptlets or CSS rules unless you trust them)
  if (ab) {
    const ur = await gb.send('userrules.get');
    const ta = el('textarea', { class: 'field', spellcheck: 'false', 'aria-label': 'Your rules', placeholder: '||ads.example.com^\nexample.com##.banner\n@@||example.org^' });
    ta.value = ur.text;
    const probs = el('div');
    const showProblems = (list, n) => {
      probs.textContent = '';
      probs.append(el('div', { class: 'row' }, el('div', { class: 'sub', text: n + (n === 1 ? ' rule in use' : ' rules in use') + (list.length ? ', ' + list.length + ' not used:' : '') })));
      for (const p of list.slice(0, 50))
        probs.append(el('div', { class: 'row' }, icon('Warning'), el('div', { class: 'text' },
          el('div', { class: 'title', text: 'Line ' + p.line + ': ' + p.text }), el('div', { class: 'sub', text: p.reason }))));
    };
    showProblems(ur.problems, ur.rules);
    const save = el('button', { class: 'btn tonal', text: 'Save' });
    save.addEventListener('click', async () => {
      try { const r = await gb.send('userrules.set', { text: ta.value }); showProblems(r.problems, r.rules); snack('Saved: applies to pages you open now'); }
      catch (e) { snack(e.message); }
    });
    const help = el('div', { class: 'sub', text: 'Block: ||host^  or an address pattern.  Allow: @@||host^.  Hide: site.com##selector (or ##selector everywhere).  Scriptlets: site.com#%#//scriptlet(\'name\', \'arg\').' });
    const pad = el('div', { class: 'row' }, el('div', { class: 'text' }, ta, help));
    pad.style.display = 'block';
    main.append(el('div', { class: 'card', id: 'userrules' }, el('h2', { text: 'Your rules' }), pad, el('div', { class: 'row' }, save), probs));

    const cl = el('div', { class: 'card', id: 'customlists' }, el('h2', { text: 'Custom filter lists' }));
    const listRows = el('div');
    const url = el('input', { class: 'field', placeholder: 'https://... list address', 'aria-label': 'List address' });
    url.style.flex = '1';
    const addBtn = el('button', { class: 'btn tonal', text: 'Add' });
    const upd = el('button', { class: 'btn text', text: 'Update all' });
    async function drawLists() {
      const ls = await gb.send('lists.custom');
      listRows.textContent = '';
      if (!ls.length) listRows.append(el('div', { class: 'row' }, el('div', { class: 'sub', text: 'None. Add a list by its address.' })));
      for (const l of ls) {
        const on = el('button', { class: 'switch', role: 'switch', 'aria-checked': String(l.on), 'aria-label': 'Use ' + l.title });
        on.addEventListener('click', async () => { l.on = !l.on; on.setAttribute('aria-checked', String(l.on)); await gb.send('lists.toggle', { url: l.url, on: l.on }); });
        const trust = el('span', { class: 'chip' + (l.trusted ? '' : ' warn'), text: l.known ? 'Known source' : l.trusted ? 'Trusted by you' : 'Not trusted' });
        const acts = el('div', { class: 'actions' });
        if (!l.known) acts.append(iconButton(l.trusted ? 'ShieldLock' : 'Shield', l.trusted ? 'Stop trusting' : 'Trust this list (allows trusted scriptlets and CSS rules)', async () => {
          if (!l.trusted && !confirm('Trust "' + l.title + '"? A trusted list may run trusted scriptlets and change page styles. Only trust lists from makers you know.')) return;
          await gb.send('lists.trust', { url: l.url, on: !l.trusted }); drawLists();
        }));
        acts.append(iconButton('Delete', 'Remove', async () => { await gb.send('lists.remove', { url: l.url }); drawLists(); }));
        const when = l.updated ? new Date(l.updated).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) : 'never';
        listRows.append(el('div', { class: 'row' }, icon('Shield'), el('div', { class: 'text' },
          el('div', { class: 'title', text: l.title }),
          el('div', { class: 'sub', text: l.rules.toLocaleString() + ' rules - updated ' + when + (l.error ? ' - ' + l.error : '') + ' - ' + l.url })), trust, acts, on));
      }
    }
    addBtn.addEventListener('click', async () => {
      const u = url.value.trim(); if (!u) return;
      addBtn.disabled = true; addBtn.textContent = 'Adding...';
      try { await gb.send('lists.add', { url: u }); url.value = ''; snack('Added'); } catch (e) { snack(e.message); }
      addBtn.disabled = false; addBtn.textContent = 'Add';
      setTimeout(drawLists, 300);
    });
    upd.addEventListener('click', async () => { upd.disabled = true; try { await gb.send('lists.update'); snack('Updated'); } catch (e) { snack(e.message); } upd.disabled = false; drawLists(); });
    cl.append(listRows, el('div', { class: 'row' }, url, addBtn, upd));
    main.append(cl);
    drawLists();
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
  // Downloads: folder, Save As for every file, multi-part downloads
  const dlPath = el('div', { class: 'sub mono' });
  const dlReset = el('button', { class: 'btn text', onclick: async () => showFolder(await gb.send('downloads.resetFolder')) }, 'Use Downloads');
  function showFolder(f) { dlPath.textContent = f.path; dlReset.hidden = !f.custom; }
  const conn = el('select', { 'aria-label': 'Parts per download' }, [[1, 'Off'], [2, '2'], [4, '4'], [8, '8'], [12, '12'], [16, '16']]
    .map(([v, t]) => el('option', { value: v, text: t, selected: v === (s.DownloadConnections || 8) })));
  conn.addEventListener('change', () => set('DownloadConnections', +conn.value));
  const conflict = el('select', { 'aria-label': 'If the file already exists' }, [['ask', 'Ask'], ['keep', 'Keep both'], ['replace', 'Replace']]
    .map(([v, t]) => el('option', { value: v, text: t, selected: v === (s.DownloadConflict || 'ask') })));
  conflict.addEventListener('change', () => set('DownloadConflict', conflict.value));
  const limit = el('select', { 'aria-label': 'Speed limit' }, [[0, 'No limit'], [256, '256 KB/s'], [512, '512 KB/s'], [1024, '1 MB/s'],
    [2048, '2 MB/s'], [5120, '5 MB/s'], [10240, '10 MB/s']].map(([v, t]) => el('option', { value: v, text: t, selected: v === (s.DownloadSpeedLimit || 0) })));
  limit.addEventListener('change', () => set('DownloadSpeedLimit', +limit.value));
  main.append(el('div', { class: 'card', id: 'downloads' }, el('h2', { text: 'Downloads' }),
    el('div', { class: 'row' }, icon('Folder'), el('div', { class: 'text' }, el('div', { class: 'title', text: 'Location' }), dlPath),
      el('div', { class: 'actions' }, dlReset,
        el('button', { class: 'btn tonal', onclick: async () => showFolder(await gb.send('downloads.pick')) }, 'Change'))),
    row('Download', 'Ask where to save each file', null, sw('AskWhereToSave')),
    row('File', 'If the file already exists', null, conflict),
    row('Speed', 'Split downloads into parts', 'More connections where the server allows it', conn),
    row('Speed', 'Speed limit', 'GBrowser\'s own downloads; files the engine downloads itself are not limited', limit)));
  gb.send('downloads.folder').then(showFolder);

  // Site settings: what sites were allowed / blocked (camera, microphone, location, notifications...)
  const permCard = el('div', { class: 'card', id: 'sites' }, el('h2', { text: 'Site settings' }));
  const permList = el('div');
  permCard.append(permList);
  async function drawPerms() {
    const list = await gb.send('permissions.list').catch(() => []);
    permList.textContent = '';
    if (!list.length) { permList.append(el('div', { class: 'row' }, el('div', { class: 'text' }, el('div', { class: 'sub', text: 'No site has asked yet' })))); return; }
    for (const p of list) {
      let site = p.origin; try { site = new URL(p.origin).host; } catch (e) { }
      permList.append(row(p.kind === 'Camera' ? 'Videocam' : p.kind === 'Microphone' ? 'Mic' : p.kind === 'Geolocation' ? 'Location' : p.kind === 'Notifications' ? 'Notifications' : 'Tune',
        site, p.what + ' \u00b7 ' + (p.state === 'Allow' ? 'Allowed' : 'Blocked'),
        iconButton('Delete', 'Reset', async () => { await gb.send('permissions.reset', { kind: p.kind, origin: p.origin }); drawPerms(); })));
    }
  }
  drawPerms();
  main.append(permCard);

  main.append(el('div', { class: 'card' }, el('h2', { text: 'Privacy and security' }),
    row('Lock', 'Always use secure connections', 'Opens sites over HTTPS; warns before an insecure site', sw('HttpsFirst')),
    row('ShieldLock', 'Dangerous site and download protection', 'Microsoft Defender SmartScreen', sw('SmartScreen')),
    row('Policy', 'Tracking prevention', 'Microsoft Edge tracking prevention, on top of ad blocking', tp),
    dnsRow,
    row('Delete', 'Clear data when GBrowser closes', 'Cookies, site data, cache and history (passwords kept)', sw('ClearOnExit')),
    (() => { const r = row('Cookie', 'Cookies and site data', 'See, edit, block and export cookies; third-party cookies; auto-delete', icon('OpenNew')); r.classList.add('link'); r.addEventListener('click', () => openUrl('https://gbrowser.local/cookies.html')); return r; })(),
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
