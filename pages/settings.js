'use strict';
(async () => {
  // The cards are built off-screen and shown only once they are sorted into sections: built in place, the first
  // cards (Appearance) were shown while the rest loaded, then the page jumped to the chosen section.
  const main = document.createElement('div');
  header('Settings', 'Settings');
  const { settings: s, engines } = await gb.send('settings.get');
  const set = (key, value) => gb.send('settings.set', { key, value }).catch(e => snack(e.message));

  function sw(key) {
    const b = el('button', { class: 'switch', role: 'switch', 'aria-checked': String(!!s[key]) });
    b.addEventListener('click', () => { s[key] = !s[key]; b.setAttribute('aria-checked', String(s[key])); set(key, s[key]); });
    return b;
  }
  const fmtDT = d => d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) + ' ' + d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  // a filter list's version: dates written in many ways by the lists ("202611191107", "19 Nov 2026 08:58 UTC") shown
  // in one day-first style; other versions as "version 2.5.5.59"
  function fmtVersion(v) {
    if (!v || v.startsWith('%')) return '';
    let m = /^(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})$/.exec(v);
    if (m) return fmtDT(new Date(Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5])));
    m = /^(\d{1,2} \w{3} \d{4}) (\d{2}:\d{2}) UTC$/.exec(v);
    if (m) { const d = new Date(v.replace(' UTC', 'Z').replace(/^(\d{1,2}) (\w{3}) (\d{4}) /, '$2 $1 $3 ')); return isNaN(d) ? v : fmtDT(d); }
    return /^\d+(\.\d+)+$/.test(v) ? 'version ' + v : v;
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
  // font size, Chrome's five steps
  const fsSel = el('select', { 'aria-label': 'Font size' }, [[9, 'Very small'], [12, 'Small'], [16, 'Medium (recommended)'], [20, 'Large'], [24, 'Very large']]
    .map(([v, t]) => el('option', { value: String(v), text: t, selected: v === (s.FontSize || 16) })));
  fsSel.addEventListener('change', () => set('FontSize', +fsSel.value));
  const fontSizeRow = row('TextFields', 'Font size', null, fsSel);
  const fontsBox = el('div', { class: 'sub-rows' });
  const fontRestartRow = el('div', { class: 'row' }, icon('Restore'), el('div', { class: 'text' },
    el('div', { class: 'title', text: 'Restart GBrowser to use the new fonts' })),
    el('button', { class: 'btn', onclick: () => gb.send('app.restart') }, icon('Refresh'), 'Restart'));
  async function fontRestart() { const st = await gb.send('fonts.state'); fontRestartRow.style.display = st.restart ? '' : 'none'; }
  fontRestart();
  const fontsRow = el('details', { class: 'fonts' }, el('summary', { class: 'row' }, icon('FontDownload'), el('div', { class: 'text' }, el('div', { class: 'title', text: 'Customize fonts' })), icon('ArrowDrop', 's20 chev')), fontsBox, fontRestartRow);
  // page zoom for sites without their own zoom (Chrome's list)
  const dz = Math.round((s.DefaultZoom || 1) * 100);
  const zoomSel = el('select', { 'aria-label': 'Page zoom' }, [25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500]
    .map(v => el('option', { value: String(v), text: v + '%', selected: v === dz })));
  zoomSel.addEventListener('change', () => set('DefaultZoom', +zoomSel.value / 100));
  main.append(el('div', { class: 'card', id: 'appearance' }, el('h2', { text: 'Appearance' }), row('Contrast', 'Theme', null, themeSel),
    el('div', { class: 'row' }, icon('Palette'), el('div', { class: 'text' }, el('div', { class: 'title', text: 'Colour' }), swatches)),
    row('ZoomIn', 'Page zoom', null, zoomSel), fontSizeRow, fontsRow));
  // fonts, like Chrome's Customize fonts (empty = the default font)
  gb.send('fonts.list').then(list => {
    for (const [key, label, def] of [['FontStandard', 'Standard font', 'Times New Roman'], ['FontSerif', 'Serif font', 'Times New Roman'],
                                      ['FontSans', 'Sans-serif font', 'Arial'], ['FontFixed', 'Fixed-width font', 'Consolas']]) {
      const sel = el('select', { 'aria-label': label }, el('option', { value: '', text: def + ' (default)' }),
        ...list.filter(f => f !== def).map(f => el('option', { value: f, text: f, selected: f === s[key] })));
      sel.style.fontFamily = s[key] || def;
      sel.addEventListener('change', async () => { await set(key, sel.value); sel.style.fontFamily = sel.value || def; fontRestart(); });
      fontsBox.append(el('div', { class: 'row' }, el('div', { class: 'text' }, el('div', { class: 'title', text: label })), sel));
    }
  });

  // Languages (Chrome's Settings > Languages): the order is the Accept-Language header and navigator.languages
  const langAll = await gb.send('languages.all');
  const langName = c => (langAll.find(l => l.code === c) || {}).name || c;
  let langs = s.Languages && s.Languages.length ? s.Languages.slice() : navigator.languages.slice();
  const langList = el('div');
  const langRestart = el('div', { class: 'row' }, icon('Restore'), el('div', { class: 'text' },
    el('div', { class: 'title', text: 'Restart GBrowser to use the new language settings' })),
    el('button', { class: 'btn', onclick: () => gb.send('app.restart') }, icon('Refresh'), 'Restart'));
  async function langState() { const st = await gb.send('languages.state'); langRestart.style.display = st.restart ? '' : 'none'; }
  async function saveLangs(list) { await set('Languages', list); s.Languages = list.length ? list : null; langs = list.length ? list : navigator.languages.slice(); drawLangs(); langState(); }
  const langAdd = el('button', { class: 'btn tonal' }, icon('Add'), 'Add languages');
  langAdd.addEventListener('click', () => {
    // Chrome's "Add languages" dialog: search, tick, Add
    const picked = new Set();
    const search = el('input', { class: 'field', placeholder: 'Search languages', spellcheck: 'false', 'aria-label': 'Search languages' });
    const list = el('div', { class: 'pick-list' });
    const ok = el('button', { class: 'btn', text: 'Add', disabled: true });
    const cancel = el('button', { class: 'btn text', text: 'Cancel' });
    const shade = el('div', { class: 'dialog-shade' }, el('div', { class: 'dialog', role: 'dialog', 'aria-label': 'Add languages' },
      el('h2', { text: 'Add languages' }), search, list, el('div', { class: 'dialog-actions' }, cancel, ok)));
    const draw = () => {
      const q = search.value.trim().toLowerCase();
      list.textContent = '';
      const starts = l => q && (l.name.toLowerCase().startsWith(q) || l.code.toLowerCase().startsWith(q)) ? 0 : 1;
      for (const l of langAll.slice().sort((a, b) => starts(a) - starts(b))) {
        if (langs.includes(l.code)) continue;
        if (q && !(l.name.toLowerCase().includes(q) || l.code.toLowerCase().includes(q))) continue;
        const cb = el('input', { type: 'checkbox', checked: picked.has(l.code) });
        cb.addEventListener('change', () => { if (cb.checked) picked.add(l.code); else picked.delete(l.code); ok.disabled = !picked.size; });
        list.append(el('label', { class: 'row' }, cb, el('div', { class: 'text' }, el('div', { class: 'title', text: l.name }), el('div', { class: 'sub', text: l.code }))));
      }
      if (!list.children.length) list.append(el('div', { class: 'row' }, el('div', { class: 'sub', text: 'No match' })));
    };
    const close = () => shade.remove();
    search.addEventListener('input', draw);
    cancel.addEventListener('click', close);
    shade.addEventListener('click', e => { if (e.target === shade) close(); });
    shade.addEventListener('keydown', e => { if (e.key === 'Escape') close(); });
    ok.addEventListener('click', () => { close(); saveLangs(langs.concat([...picked])); });
    document.body.append(shade);
    draw();
    search.focus();
  });
  const langReset = el('button', { class: 'btn text', text: 'Use Windows languages', onclick: () => saveLangs([]) });
  function drawLangs() {
    langList.textContent = '';
    langs.forEach((c, i) => {
      const up = iconButton('ArrowUp', 'Move up', () => { const l = langs.slice(); [l[i - 1], l[i]] = [l[i], l[i - 1]]; saveLangs(l); });
      const down = iconButton('ArrowDown', 'Move down', () => { const l = langs.slice(); [l[i + 1], l[i]] = [l[i], l[i + 1]]; saveLangs(l); });
      const del = iconButton('Close', 'Remove', () => saveLangs(langs.filter(x => x !== c)));
      if (i === 0) up.disabled = true;
      if (i === langs.length - 1) down.disabled = true;
      if (langs.length === 1) del.disabled = true;
      langList.append(el('div', { class: 'row' }, icon('Language'), el('div', { class: 'text' },
        el('div', { class: 'title', text: langName(c) }), el('div', { class: 'sub', text: c })), el('div', { class: 'actions' }, up, down, del)));
    });
    langReset.hidden = !(s.Languages && s.Languages.length) && langs.join() === navigator.languages.join();
  }
  drawLangs();
  langState();
  main.append(el('div', { class: 'card', id: 'languages' }, el('div', { class: 'row' }, el('div', { class: 'text' },
    el('div', { class: 'title', text: 'Preferred languages' }), el('div', { class: 'sub', text: 'Sites show content in these languages when they can, in this order' })),
    langReset, langAdd), langList, langRestart));

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

  // Ad blocking (Chrome-style: a short main page; the long lists and the rules editor are sub-pages)
  const ab = await gb.send('adblock.info').catch(() => null);
  const abSecCards = [];
  const abMain = row('Shield', 'Ad blocking', 'Ads, pop-ups and harmful sites, on every site', sw('AdblockEnabled'));
  const resetBtn = el('button', { class: 'btn text', text: 'Reset' });
  const countRow = ab ? row('Speed', ab.total.toLocaleString() + ' blocked', 'Ads, trackers and annoyances so far', resetBtn) : null;
  resetBtn.addEventListener('click', async () => { await gb.send('stats.reset'); countRow.querySelector('.title').textContent = '0 blocked'; snack('Count reset'); });
  // the Do Not Track row is moved to Privacy > Third-party cookies below
  const abc = el('div', { class: 'card' }, el('h2', { text: 'Ad blocking' }), abMain, countRow,
    row('Person', 'Do Not Track', null, sw('DoNotTrack')));
  main.append(abc);
  abSecCards.push(abc);
  const blockCard = el('div', { class: 'card' }, el('h2', { text: 'Also block' }),
    row('Block', 'Annoyances', 'Cookie notices, pop-ups, app banners', sw('BlockAnnoyances')),
    row('Cookie', 'Trackers', 'Also removes tracking codes from links', sw('BlockTrackers')),
    row('Lock', 'Ad and tracker sites', 'Pages of these sites do not open', sw('BlockAdSitePages')));
  const hideCard = el('div', { class: 'card' }, el('h2', { text: 'Page cleanup' }),
    row('Tune', 'Hide ad leftovers', 'Removes empty spaces where ads were', sw('HideAdLeftovers')),
    row('Verified', 'Stealth mode', 'Sites cannot see that ads are hidden', sw('Stealth')));
  main.append(blockCard, hideCard);
  abSecCards.push(blockCard, hideCard);

  // a row that opens a sub-page of its section (Chrome's arrow rows)
  function subLink(iconName, title, sub, target) {
    const r = row(iconName, title, sub, icon('ChevronRight'));
    r.classList.add('link');
    r.tabIndex = 0;
    const go = () => { history.pushState({ sub: true }, '', '#' + target); show(target); };
    r.addEventListener('click', go);
    r.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); go(); } });
    return r;
  }
  // a sub-page: back arrow + title, then its cards
  function subPage(id, title, ...kids) {
    const back = iconButton('Back', 'Back', () => { if (history.state && history.state.sub) history.back(); else { history.pushState(null, '', '#' + id.split('-')[0]); show(id.split('-')[0]); } });
    return el('div', { class: 'subpage', id }, el('div', { class: 'subpage-head' }, back, el('h2', { class: 'section-title', text: title })), ...kids);
  }

  if (ab) {
    const listsOn = ab.lists.filter(l => l.on !== false).length;
    const linkCard = el('div', { class: 'card' });
    const ruleCountSub = n => n === 1 ? '1 rule' : n + ' rules';
    const urLink = subLink('Edit', 'Your rules', 'Rules you write yourself', 'adblock-rules');
    linkCard.append(
      subLink('Shield', 'Filter lists', listsOn + ' of ' + ab.lists.length + ' lists on, ' + ab.rules.toLocaleString() + ' rules', 'adblock-lists'),
      urLink);
    main.append(linkCard);
    abSecCards.push(linkCard);

    // sites where blocking is off (added from the shield in the toolbar)
    const allowed = el('div', { class: 'card' }, el('h2', { text: 'Sites without blocking' }));
    const drawAllowed = () => {
      [...allowed.querySelectorAll('.row')].forEach(r => r.remove());
      if (!ab.allowed.length) allowed.append(el('div', { class: 'row' }, icon('Public'), el('div', { class: 'text' }, el('div', { class: 'sub', text: 'No sites added' }))));
      for (const site of ab.allowed) {
        const r = el('div', { class: 'row' }, letter('https://' + site), el('div', { class: 'text' }, el('div', { class: 'title', text: site })),
          iconButton('Delete', 'Remove', async () => { await gb.send('allow.remove', { site }); ab.allowed = ab.allowed.filter(x => x !== site); drawAllowed(); }));
        allowed.append(r);
      }
    };
    drawAllowed();
    main.append(allowed);
    abSecCards.push(allowed);

    // ---- sub-page: Filter lists ----
    const hs = await gb.send('hosts.state');
    const upd = el('button', { class: 'btn tonal' }, icon('Refresh'), 'Update now');
    const hsub = () => hs.state || (hs.updated ? 'Last update ' + fmtDT(new Date(hs.updated)) + (hs.hosts ? ', ' + hs.hosts.toLocaleString() + ' new hosts' : ', built-in lists are newer') : 'Not updated yet');
    const lastRow = row('Restore', 'Host lists', hsub(), upd);
    upd.addEventListener('click', async () => {
      upd.disabled = true;
      try { await gb.send('hosts.update'); Object.assign(hs, await gb.send('hosts.state')); lastRow.querySelector('.sub').textContent = hsub(); snack('Host lists updated'); }
      catch (e) { snack(e.message); }
      upd.disabled = false;
    });
    const builtDate = /^\d{4}-\d{2}-\d{2}$/.test(ab.built || '') ? new Date(ab.built + 'T00:00').toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) : fmtIsoDate(ab.built);
    const updCard = el('div', { class: 'card', id: 'filterlists' }, el('h2', { text: 'Updates' }),
      row('Shield', ab.ready ? ab.rules.toLocaleString() + ' rules in use' : (ab.error || 'Loading'), ab.ready ? 'Built ' + builtDate + ', loaded in ' + ab.loadMs + ' ms' : null, null),
      row('Download', 'Update host lists daily', null, sw('HostListUpdates')),
      lastRow);
    // the built-in lists, one card per group (the groups are the switches on the main page)
    const groupCards = [];
    [['0', 'Ads'], ['1', 'Annoyances'], ['2', 'Trackers']].forEach(([g, name]) => {
      const of = ab.lists.filter(l => (l.group || '0') === g);
      if (!of.length) return;
      const c = el('div', { class: 'card' }, el('h2', { text: name }));
      for (const l of of) {
        const b = el('button', { class: 'switch', role: 'switch', 'aria-checked': String(l.on !== false), 'aria-label': l.name });
        b.addEventListener('click', async () => {
          l.on = !(l.on !== false);
          b.setAttribute('aria-checked', String(l.on));
          try { await gb.send('lists.set', { file: l.file, on: l.on }); snack(l.on ? 'On for pages you open now' : 'Off for pages you open now'); }
          catch (e) { snack(e.message); }
        });
        if (!l.file) b.disabled = true;
        const v = fmtVersion(l.version);
        c.append(row('Shield', l.name, (+l.rules).toLocaleString() + ' rules' + (v ? ', ' + v : ''), b));
      }
      groupCards.push(c);
    });

    // your own lists by address (trust check like AdGuard: unknown sources cannot use trusted-* scriptlets or
    // CSS rules unless you trust them); the address box at the top (the list can be long)
    const cl = el('div', { class: 'card', id: 'customlists' });
    const listRows = el('div', { class: 'rows' });
    const url = el('input', { class: 'field', placeholder: 'List address, e.g. https://example.com/list.txt', 'aria-label': 'List address' });
    url.style.flex = '1';
    const addBtn = el('button', { class: 'btn tonal' }, icon('Add'), 'Add');
    const updAll = el('button', { class: 'btn text', text: 'Update all' });
    async function drawLists() {
      const ls = await gb.send('lists.custom');
      listRows.textContent = '';
      updAll.hidden = !ls.length;
      if (!ls.length) listRows.append(el('div', { class: 'row' }, icon('Shield'), el('div', { class: 'text' }, el('div', { class: 'sub', text: 'No lists added' }))));
      for (const l of ls) {
        const on = el('button', { class: 'switch', role: 'switch', 'aria-checked': String(l.on), 'aria-label': 'Use ' + l.title });
        on.addEventListener('click', async () => { l.on = !l.on; on.setAttribute('aria-checked', String(l.on)); await gb.send('lists.toggle', { url: l.url, on: l.on }); });
        const trust = el('span', { class: 'chip' + (l.trusted ? '' : ' warn'), text: l.known ? 'Known source' : l.trusted ? 'Trusted' : 'Not trusted' });
        const acts = el('div', { class: 'actions' });
        if (!l.known) acts.append(iconButton(l.trusted ? 'ShieldLock' : 'Shield', l.trusted ? 'Stop trusting' : 'Trust this list (allows trusted scriptlets and CSS rules)', async () => {
          if (!l.trusted && !confirm('Trust "' + l.title + '"? A trusted list may run trusted scriptlets and change page styles. Only trust lists from makers you know.')) return;
          await gb.send('lists.trust', { url: l.url, on: !l.trusted }); drawLists();
        }));
        acts.append(iconButton('Delete', 'Remove', async () => { await gb.send('lists.remove', { url: l.url }); drawLists(); }));
        const when = l.updated ? fmtDT(new Date(l.updated)) : 'never';
        listRows.append(el('div', { class: 'row' }, icon('Shield'), el('div', { class: 'text' },
          el('div', { class: 'title', text: l.title }),
          el('div', { class: 'sub', text: l.rules.toLocaleString() + ' rules, updated ' + when + (l.error ? ', ' + l.error : '') }),
          el('div', { class: 'sub', text: l.url })), trust, acts, on));
      }
    }
    addBtn.addEventListener('click', async () => {
      const u = url.value.trim(); if (!u) { url.focus(); return; }
      addBtn.disabled = true;
      try { await gb.send('lists.add', { url: u }); url.value = ''; snack('Added'); } catch (e) { snack(e.message); }
      addBtn.disabled = false;
      setTimeout(drawLists, 300);
    });
    url.addEventListener('keydown', e => { if (e.key === 'Enter') addBtn.click(); });
    updAll.addEventListener('click', async () => { updAll.disabled = true; try { await gb.send('lists.update'); snack('Updated'); } catch (e) { snack(e.message); } updAll.disabled = false; drawLists(); });
    cl.append(el('div', { class: 'card-head' }, el('h2', { text: 'Your lists' }), updAll), el('div', { class: 'row' }, url, addBtn), listRows);
    drawLists();
    const listsPage = subPage('adblock-lists', 'Filter lists', updCard, ...groupCards, cl);

    // ---- sub-page: Your rules ----
    const ur = await gb.send('userrules.get');
    const ta = el('textarea', { class: 'field', spellcheck: 'false', 'aria-label': 'Your rules', placeholder: '||ads.example.com^\nexample.com##.banner\n@@||example.org^' });
    ta.value = ur.text;
    const probs = el('div', { class: 'rows' });
    const count = el('div', { class: 'sub' });
    const showProblems = (list, n) => {
      probs.textContent = '';
      count.textContent = n + (n === 1 ? ' rule in use' : ' rules in use') + (list.length ? ', ' + list.length + ' not used' : '');
      urLink.querySelector('.sub').textContent = ruleCountSub(n);
      for (const p of list.slice(0, 50))
        probs.append(el('div', { class: 'row' }, icon('Warning'), el('div', { class: 'text' },
          el('div', { class: 'title', text: 'Line ' + p.line + ': ' + p.text }), el('div', { class: 'sub', text: p.reason }))));
    };
    showProblems(ur.problems, ur.rules);
    const save = el('button', { class: 'btn' }, icon('Check'), 'Save');
    save.addEventListener('click', async () => {
      try { const r = await gb.send('userrules.set', { text: ta.value }); showProblems(r.problems, r.rules); snack('Saved: applies to pages you open now'); }
      catch (e) { snack(e.message); }
    });
    // the rule kinds as a small table: what it does, how it is written
    const syntax = el('div', { class: 'syntax' }, ...[
      ['Block a site', '||ads.example.com^'],
      ['Allow a site', '@@||example.org^'],
      ['Hide on a site', 'example.com##.banner'],
      ['Hide everywhere', '##.banner'],
      ['Scriptlet', "example.com#%#//scriptlet('name', 'arg')"],
    ].map(([what, how]) => el('div', { class: 'syntax-row' }, el('span', { class: 'sub', text: what }), el('code', { text: how }))));
    const edit = el('div', { class: 'row editor' }, ta);
    const rulesCard = el('div', { class: 'card', id: 'userrules' }, el('h2', { text: 'Rules' }), edit,
      el('div', { class: 'row' }, el('div', { class: 'text' }, count), save), probs);
    const helpCard = el('div', { class: 'card' }, el('h2', { text: 'How to write rules' }), syntax);
    const rulesPage = subPage('adblock-rules', 'Your rules', rulesCard, helpCard);
    abSecCards.push(listsPage, rulesPage);
  }

  // Memory
  const sleep = el('select', { 'aria-label': 'Sleeping tabs' }, [[0, 'Never'], [1, '1 minute'], [5, '5 minutes'], [15, '15 minutes'], [30, '30 minutes'], [60, '1 hour']]
    .map(([v, t]) => el('option', { value: v, text: t, selected: v === s.SleepMinutes })));
  sleep.addEventListener('change', () => set('SleepMinutes', +sleep.value));
  const memRows = el('div', { class: 'rows' });
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
        m.tabs + (m.tabs === 1 ? ' tab: ' : ' tabs: ') + m.live + ' active, ' + m.sleeping + ' sleeping, ' + m.unloaded + ' not loaded', null));
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
  // "Always open files of this type" (chosen in a download's menu), like Chrome's "Open certain file types automatically"
  const autoSub = el('div', { class: 'sub' });
  const autoClear = el('button', { class: 'btn text', onclick: async () => showAutoOpen(await gb.send('downloads.autoOpen', { clear: true })) }, 'Clear');
  const autoRow = el('div', { class: 'row' }, icon('OpenNew'), el('div', { class: 'text' }, el('div', { class: 'title', text: 'Open automatically' }), autoSub), autoClear);
  main.append(el('div', { class: 'card', id: 'downloads' }, el('h2', { text: 'Downloads' }),
    el('div', { class: 'row' }, icon('Folder'), el('div', { class: 'text' }, el('div', { class: 'title', text: 'Location' }), dlPath),
      el('div', { class: 'actions' }, dlReset,
        el('button', { class: 'btn tonal', onclick: async () => showFolder(await gb.send('downloads.pick')) }, 'Change'))),
    row('Download', 'Ask where to save each file', null, sw('AskWhereToSave')),
    row('File', 'If the file already exists', null, conflict),
    row('Speed', 'Split downloads into parts', 'More connections where the server allows it', conn),
    row('Speed', 'Speed limit', 'GBrowser\'s own downloads; files the engine downloads itself are not limited', limit), autoRow));
  gb.send('downloads.folder').then(showFolder);
  async function showAutoOpen(list) {
    autoSub.textContent = list.length ? list.join(', ') : 'None';
    autoClear.hidden = !list.length;
  }
  gb.send('downloads.autoOpen').then(showAutoOpen);

  // Site settings: what sites were allowed / blocked (camera, microphone, location, notifications...)
  const permCard = el('div', { class: 'card', id: 'sites' }, el('h2', { text: 'Site settings' }));
  const permList = el('div');
  permCard.append(permList);
  async function drawPerms() {
    const list = await gb.send('permissions.list').catch(() => []);
    permList.textContent = '';
    if (!list.length) { permList.append(el('div', { class: 'row' }, el('div', { class: 'text' }, el('div', { class: 'sub', text: 'No site has asked yet' })))); return; }
    let exts = [];
    if (list.some(p => p.origin.startsWith('chrome-extension://'))) exts = await gb.send('ext.list').catch(() => []);
    for (const p of list) {
      let site = p.origin; try { site = new URL(p.origin).host; } catch (e) { }
      if (p.origin.startsWith('chrome-extension://')) { const x = (exts || []).find(e => e.id === site); site = x ? x.name + ' (extension)' : site; }
      permList.append(row(p.kind === 'Camera' ? 'Videocam' : p.kind === 'Microphone' ? 'Mic' : p.kind === 'Geolocation' ? 'Location' : p.kind === 'Notifications' ? 'Notifications' : 'Tune',
        site, p.what + ' \u00b7 ' + (p.state === 'Allow' ? 'Allowed' : 'Blocked'),
        iconButton('Delete', 'Reset', async () => { await gb.send('permissions.reset', { kind: p.kind, origin: p.origin }); drawPerms(); })));
    }
  }
  drawPerms();
  main.append(permCard);

  // Hide my identity
  const idi = await gb.send('identity.get');
  const idSub = el('div', { class: 'title', text: 'Identity ' + idi.id });
  const newId = el('button', { class: 'btn tonal', text: 'New identity' });
  newId.addEventListener('click', async () => {
    const out = confirm('New identity: clear what sites stored about this browser (site storage, caches, tracking cookies)? You stay signed in.');
    if (!out) return;
    const id = await gb.send('identity.new', { signOut: false });
    idSub.textContent = 'Identity ' + id; snack('New identity');
  });
  const idRow = row('Person', 'Hide my identity', 'Sites cannot recognise you again or link your visits: tracking IDs change, nothing local is handed out. GBrowser stays a genuine Microsoft Edge.', sw('HideIdentity'));
  main.append(el('div', { class: 'card', id: 'identity' }, el('h2', { text: 'Hide my identity' }), idRow,
    row('Restore', 'New identity each start', null, sw('IdentityEachStart')),
    row('Palette', 'Canvas, WebGL and audio noise', 'Stable per site and identity; some fingerprint test sites can notice it', sw('IdentityNoise')),
    el('div', { class: 'row' }, icon('Fingerprint'), el('div', { class: 'text' }, idSub, el('div', { class: 'sub', text: 'The IP address stays the same (that needs a VPN).' })), newId)));
  main.append(el('div', { class: 'card' }, el('h2', { text: 'Privacy and security' }),
    row('Lock', 'Always use secure connections', 'Opens sites over HTTPS; warns before an insecure site', sw('HttpsFirst')),
    row('ShieldLock', 'Dangerous site and download protection', 'Microsoft Defender SmartScreen', sw('SmartScreen')),
    row('Policy', 'Tracking prevention', 'Microsoft Edge tracking prevention, on top of ad blocking', tp),
    dnsRow,
    row('Delete', 'Clear data when GBrowser closes', 'Cookies, site data, cache and history (passwords kept)', sw('ClearOnExit')),
    (() => { const r = row('Cookie', 'Cookies and site data', 'See, edit, block and export cookies; third-party cookies; auto-delete', icon('OpenNew')); r.classList.add('link'); r.addEventListener('click', () => openUrl(location.origin + '/cookies.html')); return r; })(),
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

  // ---- Chrome's layout: a sidebar of sections in Chrome's order, each section's cards under its title ----
  const cards = [...main.children];
  const cardOf = t => cards.find(c => c.classList.contains('card') && c.querySelector(':scope > h2')?.textContent === t);
  const rowOf = (card, t) => card && [...card.children].find(r => r.querySelector('.title')?.textContent === t);
  const card = (title, id, ...kids) => el('div', { class: 'card', id: id || null }, el('h2', { text: title }), ...kids.filter(Boolean));
  const start = cardOf('Start and search'), priv = cardOf('Privacy and security');
  // Appearance: home button with its page and the bookmarks bar, before the font settings (Chrome's order)
  const appear = cardOf('Appearance');
  const fontAnchor = appear && [...appear.children].find(r => r.querySelector('.title')?.textContent === 'Font size');
  for (const t of ['Show home button', 'Home page', 'Bookmarks bar']) { const r = rowOf(start, t); if (r && appear) appear.insertBefore(r, fontAnchor || null); }
  { const z = rowOf(appear, 'Page zoom'); if (z) appear.append(z); }   // Chrome: page zoom after the fonts
  const searchCard = card('Search engine', 'search', rowOf(start, 'Search engine'), rowOf(start, 'Search suggestions'));
  const startupCard = card('On startup', 'startup', rowOf(start, 'Reopen tabs on start'));
  // Privacy and security: Chrome's cards
  const delCard = cardOf('Clear browsing data');
  if (delCard) delCard.querySelector(':scope > h2').textContent = 'Delete browsing data';
  const cookieCard = card('Third-party cookies and site data', 'cookies', rowOf(priv, 'Cookies and site data'), rowOf(abc, 'Do Not Track'),
    rowOf(priv, 'Clear data when GBrowser closes'));
  const dnsBox = priv && [...priv.children].find(r => r.classList.contains('rows'));
  const secCard = card('Security', 'security', rowOf(priv, 'Always use secure connections'), rowOf(priv, 'Dangerous site and download protection'),
    rowOf(priv, 'Tracking prevention'), dnsBox, rowOf(priv, 'Your data is encrypted'));
  const SECTIONS = [
    ['privacy', 'Privacy and security', 'Lock', [delCard, cookieCard, secCard, cardOf('Site settings'), cardOf('Hide my identity')]],
    ['adblock', 'Ad blocking', 'Shield', abSecCards],
    ['performance', 'Performance', 'Speed', [cardOf('Memory'), cardOf('Always keep these sites active')]],
    ['appearance-sec', 'Appearance', 'Palette', [appear]],
    ['search-sec', 'Search engine', 'Search', [searchCard]],
    ['default-sec', 'Default browser', 'Public', [cardOf('Default browser')]],
    ['startup-sec', 'On startup', 'Restore', [startupCard]],
    ['languages-sec', 'Languages', 'Language', [main.querySelector('#languages')]],
    ['downloads-sec', 'Downloads', 'Download', [cardOf('Downloads')]],
    // shown here, not in a new tab: their pages' scripts draw into these boxes (loaded when the section is first opened)
    ['extensions-sec', 'Extensions', 'Extension', [el('div', { class: 'embed', 'data-script': 'extensions.js' })]],
    ['about-sec', 'About GBrowser', 'Info', [el('div', { class: 'embed', 'data-script': 'about.js' })]],
  ];
  // the old containers are empty now
  for (const c of [start, priv]) if (c && c.querySelectorAll('.row').length === 0) c.remove();
  const nav = el('nav', { class: 'settings-nav', 'aria-label': 'Settings sections' });
  const content = el('div', { class: 'settings-content' });
  const links = {};
  for (const [id, title, iconName, list] of SECTIONS) {
    // a card that is the whole section does not repeat the section's title (like Chrome)
    for (const c of list) { const h = c && c.querySelector(':scope > h2'); if (h && h.textContent === title) h.remove(); }
    const sec = el('section', { id, class: 'settings-section' }, el('h2', { class: 'section-title', text: title }), ...list.filter(Boolean));
    content.append(sec);
    const a = el('a', { href: '#' + id, class: 'nav-item' }, icon(iconName, 's20'), el('span', { text: title }));
    a.addEventListener('click', e => { e.preventDefault(); if (location.hash !== '#' + id) history.pushState(null, '', '#' + id); show(id); });
    links[id] = a;
    nav.append(a);
  }
  const page = document.getElementById('main');
  page.classList.add('settings-layout');
  page.append(nav, content);
  // one section at a time: the one chosen in the sidebar (the address keeps it, so Back / Forward and links work)
  function show(target) {
    let secId = SECTIONS[0][0], card = null, sub = null;
    const t = target && document.getElementById(target);
    if (t) {
      const sec = t.closest('.settings-section');
      if (sec) { secId = sec.id; sub = t.closest('.subpage'); if (t !== sec && t !== sub) card = t; }
    }
    for (const sec of content.children) {
      sec.hidden = sec.id !== secId;
      // a sub-page (e.g. Ad blocking > Filter lists) replaces its section's main page
      sec.classList.toggle('in-sub', !!sub && sec.id === secId);
      for (const p of sec.querySelectorAll(':scope > .subpage')) p.classList.toggle('on', p === sub);
    }
    loadEmbed(document.getElementById(secId));
    for (const k in links) links[k].classList.toggle('on', k === secId);
    if (card) setTimeout(() => card.scrollIntoView({ block: 'start' }), 30);
    else window.scrollTo(0, 0);
  }
  function loadEmbed(sec) {
    const box = sec && sec.querySelector('.embed');
    if (!box || box.dataset.loaded) return;
    box.dataset.loaded = '1';
    window.__gbEmbed = box;
    const sc = document.createElement('script');
    sc.src = box.dataset.script;
    sc.onload = sc.onerror = () => { if (window.__gbEmbed === box) window.__gbEmbed = null; };
    document.body.append(sc);
  }
  window.addEventListener('popstate', () => show(location.hash.slice(1)));
  show(location.hash.slice(1));
  window.__gbReady = performance.now();   // for timing checks
})();
