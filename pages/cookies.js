'use strict';
// Cookies and site data (gbrowser://cookies, gbrowser://cookies?site=example.com): sites at the left, the site's
// cookies at the right with every field, readable values, tracker labels; edit / add / duplicate / delete with Undo;
// block a cookie, protect it, per-site rule (Allow / Block all / Clear when closed); export / import; snapshots;
// stored data sizes; auto-delete, lifetime cap and third-party cookies.
(() => {
  const main = document.getElementById('main');
  let data = null, q = '', filter = 'all', site = new URLSearchParams(location.search).get('site') || '', undo = null;
  const box = searchBox('Search name, value or site', v => { q = v.trim().toLowerCase(); render(); });
  header('Cookies and site data', 'Cookie', box);

  const day = ms => new Date(ms).toLocaleDateString('en-GB', { day: '2-digit', month: '2-digit', year: 'numeric' }).replace(/\//g, '-');
  const dayTime = ms => day(ms) + ' ' + new Date(ms).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  const size = n => n < 1024 ? n + ' B' : (n / 1024).toFixed(1) + ' KB';
  const now = () => Date.now() / 1000;
  const pk = c => (c.partitionKey && (c.partitionKey.topLevelSite || c.partitionKey)) || '';

  async function load() {
    try { data = await gb.send('cookies.list'); } catch (e) { main.textContent = e.message; return; }
    render();
  }

  // ------------------------------------------------------------ readable values
  function readable(v) {
    const out = [];
    let dec = v;
    try { const d = decodeURIComponent(v); if (d !== v) { out.push(['URL-decoded', d]); dec = d; } } catch (e) { }
    const parts = dec.split('.');
    if (parts.length === 3 && /^[\w-]+$/.test(parts[0]) && /^[\w-]+$/.test(parts[1])) {
      try {
        const p = JSON.parse(atob(parts[1].replace(/-/g, '+').replace(/_/g, '/')));
        out.push(['JWT payload', JSON.stringify(p, null, 2)]);
        if (p.exp) out.push(['JWT expires', dayTime(p.exp * 1000)]);
      } catch (e) { }
    }
    if (/^[\[{]/.test(dec)) { try { out.push(['JSON', JSON.stringify(JSON.parse(dec), null, 2)]); } catch (e) { } }
    if (/^[A-Za-z0-9+/]{12,}={0,2}$/.test(dec) && dec.length % 4 === 0) {
      try { const t = atob(dec); if (/^[\x20-\x7e\s]+$/.test(t)) out.push(['Base64', t]); } catch (e) { }
    }
    for (const m of dec.matchAll(/\b(1[5-9]\d{8}|1[5-9]\d{11})\b/g)) {
      const n = +m[1], ms = m[1].length === 13 ? n : n * 1000;
      out.push(['Time ' + m[1], dayTime(ms)]);
      if (out.length > 6) break;
    }
    return out;
  }

  // ------------------------------------------------------------ lists
  function sites() {
    const map = new Map();
    for (const c of data.cookies) {
      const s = map.get(c.site) || { site: c.site, n: 0, size: 0, trackers: 0 };
      s.n++; s.size += c.size || 0; if (c.tracker) s.trackers++;
      map.set(c.site, s);
    }
    return [...map.values()].sort((a, b) => a.site.localeCompare(b.site));
  }
  function match(c) {
    if (q && !(c.name.toLowerCase().includes(q) || String(c.value).toLowerCase().includes(q) || c.domain.toLowerCase().includes(q))) return false;
    if (filter === 'session' && !c.session) return false;
    if (filter === 'trackers' && !c.tracker) return false;
    if (filter === 'partitioned' && !pk(c)) return false;
    if (filter === 'expiring' && (c.session || c.expires - now() > 7 * 86400)) return false;
    return true;
  }
  const ruleOf = s => (data.rules.find(r => r.Site === s) || {}).Mode || '';

  let render = function () {
    main.textContent = '';
    if (!data) return;
    const layout = el('div', { class: 'ck' });
    // left: sites
    const nav = el('div', { class: 'card nav' });
    const filters = el('select', { 'aria-label': 'Show' }, [['all', 'All cookies'], ['trackers', 'Trackers'], ['session', 'Session only'],
      ['expiring', 'Expiring within 7 days'], ['partitioned', 'Partitioned (CHIPS)']].map(([v, t]) => el('option', { value: v, text: t, selected: v === filter })));
    filters.addEventListener('change', () => { filter = filters.value; render(); });
    nav.append(el('div', { class: 'row' }, filters));
    const list = sites().filter(s => data.cookies.some(c => c.site === s.site && match(c)));
    const total = data.cookies.length;
    nav.append(siteRow({ site: '', n: total, size: data.cookies.reduce((a, c) => a + (c.size || 0), 0), trackers: data.cookies.filter(c => c.tracker).length }));
    for (const s of list) nav.append(siteRow(s));
    // right: cookies of the site
    const right = el('div', { class: 'card list' });
    const shown = data.cookies.filter(c => (!site || c.site === site) && match(c)).sort((a, b) => (a.site + a.name).localeCompare(b.site + b.name));
    right.append(headRow(shown));
    if (!shown.length) right.append(el('div', { class: 'empty' }, icon('Cookie'), q || filter !== 'all' ? 'No matches' : 'No cookies'));
    for (const c of shown) right.append(cookieRow(c));
    layout.append(nav, right);
    main.append(layout);
    main.append(optionsCard());
    if (site) siteDataCard(site).then(card => { if (card) main.insertBefore(card, main.children[1] || null); });
  }

  function siteRow(s) {
    const r = el('div', { class: 'row link site' + (s.site === site ? ' on' : '') },
      s.site ? letter('https://' + s.site) : icon('Cookie'),
      el('div', { class: 'text' }, el('div', { class: 'title', text: s.site || 'All sites' }),
        el('div', { class: 'sub', text: s.n + (s.n === 1 ? ' cookie' : ' cookies') + ', ' + size(s.size) + (s.trackers ? ', ' + s.trackers + ' tracking' : '') })));
    const mode = s.site && ruleOf(s.site);
    if (mode) r.append(el('span', { class: 'chip' + (mode === 'block' ? ' warn' : ''), text: mode === 'allow' ? 'Allowed' : mode === 'block' ? 'Blocked' : 'Cleared on close' }));
    r.addEventListener('click', () => { site = s.site; history.replaceState(null, '', site ? '?site=' + encodeURIComponent(site) : location.pathname); render(); });
    return r;
  }

  function headRow(shown) {
    const acts = el('div', { class: 'actions' },
      iconButton('Add', 'Add a cookie', () => editor(null)),
      iconButton('Download', 'Export these cookies', () => exportMenu(shown)),
      iconButton('Upload', 'Import cookies', () => importFlow()),
      iconButton('Delete', 'Delete these cookies', () => del(shown)));
    if (site) {
      const sel = el('select', { 'aria-label': 'Cookies on ' + site }, [['', 'Default'], ['allow', 'Allow (never cleared)'], ['block', 'Block all'], ['clear', 'Clear when closed']]
        .map(([v, t]) => el('option', { value: v, text: t, selected: v === ruleOf(site) })));
      sel.addEventListener('change', async () => { await gb.send('cookies.rule', { site, mode: sel.value }); snack('Saved'); load(); });
      acts.prepend(sel);
    }
    const third = shown.filter(c => site && c.site !== site).length;
    return el('div', { class: 'row head' }, el('div', { class: 'text' },
      el('div', { class: 'title', text: site || 'All sites' }), el('div', { class: 'sub', text: shown.length + ' shown' + (third ? ', ' + third + ' third-party' : '') })), acts);
  }

  function cookieRow(c) {
    const flags = [];
    if (c.httpOnly) flags.push('HttpOnly'); if (c.secure) flags.push('Secure'); if (c.sameSite) flags.push('SameSite=' + c.sameSite);
    if (pk(c)) flags.push('Partitioned'); if (c.priority && c.priority !== 'Medium') flags.push(c.priority + ' priority');
    const exp = c.session ? 'Session' : 'Expires ' + day(c.expires * 1000);
    const chips = el('div', { class: 'chips' });
    if (c.tracker) chips.append(el('span', { class: 'chip warn', text: 'Tracker' }));
    if (c.protected) chips.append(el('span', { class: 'chip', text: 'Protected' }));
    if (c.blocked) chips.append(el('span', { class: 'chip warn', text: 'Blocked' }));
    const r = el('div', { class: 'row link', title: c.what || '' },
      el('div', { class: 'text' }, el('div', { class: 'title', text: c.name + ' = ' + String(c.value).slice(0, 80) }),
        el('div', { class: 'sub', text: c.domain + c.path + '  ·  ' + exp + '  ·  ' + size(c.size || 0) + (flags.length ? '  ·  ' + flags.join(', ') : '') + (c.what ? '  ·  ' + c.what : '') })),
      chips,
      el('div', { class: 'actions' },
        iconButton('Copy', 'Copy value', e => { e.stopPropagation(); gb.send('cookies.export', { list: [c], format: 'header', to: 'clipboard' }).then(() => snack('Copied')); }),
        iconButton('Delete', 'Delete', e => { e.stopPropagation(); del([c]); })));
    r.addEventListener('click', () => editor(c));
    return r;
  }

  async function del(list) {
    if (!list.length) return;
    await gb.send('cookies.delete', { list });
    undo = { type: 'delete', list };
    snackUndo(list.length === 1 ? 'Deleted' : list.length + ' deleted');
    load();
  }
  function snackUndo(text) {
    const s = document.querySelector('.snack') || document.body.appendChild(el('div', { class: 'snack' }));
    s.textContent = '';
    const b = el('button', { class: 'btn text', text: 'Undo' });
    b.addEventListener('click', async () => {
      const u = undo; undo = null; s.classList.remove('show');
      if (!u) return;
      for (const c of u.list) await gb.send('cookies.set', { cookie: c }).catch(() => { });
      if (u.type === 'edit') await gb.send('cookies.delete', { list: [u.after] }).then(() => gb.send('cookies.set', { cookie: u.list[0] })).catch(() => { });
      load();
    });
    s.append(el('span', { text }), b);
    s.classList.add('show');
    clearTimeout(s._t); s._t = setTimeout(() => s.classList.remove('show'), 8000);
  }

  // ------------------------------------------------------------ editor
  function field(label, input) { return el('label', { class: 'fld' }, el('span', { text: label }), input); }
  function editor(c) {
    const isNew = !c;
    c = c || { name: '', value: '', domain: site ? '.' + site : '', path: '/', secure: true, httpOnly: false, sameSite: 'Lax', session: false, expires: now() + 30 * 86400, priority: 'Medium' };
    const old = isNew ? null : Object.assign({}, c);
    const f = {
      name: el('input', { class: 'field', value: c.name }), value: el('textarea', { class: 'field small', spellcheck: 'false' }),
      domain: el('input', { class: 'field', value: c.domain }), path: el('input', { class: 'field', value: c.path }),
      expires: el('input', { class: 'field', type: 'datetime-local' }), session: el('input', { type: 'checkbox' }),
      secure: el('input', { type: 'checkbox' }), httpOnly: el('input', { type: 'checkbox' }),
      sameSite: el('select', {}, [['', 'Not set'], ['Lax', 'Lax'], ['Strict', 'Strict'], ['None', 'None']].map(([v, t]) => el('option', { value: v, text: t, selected: v === (c.sameSite || '') }))),
      priority: el('select', {}, ['Low', 'Medium', 'High'].map(v => el('option', { value: v, text: v, selected: v === (c.priority || 'Medium') }))),
    };
    f.value.value = c.value;
    f.session.checked = !!c.session; f.secure.checked = !!c.secure; f.httpOnly.checked = !!c.httpOnly;
    const d = new Date((c.session ? now() + 30 * 86400 : c.expires) * 1000);
    f.expires.value = new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
    const hint = el('div', { class: 'sub warn' });
    const check = () => {
      const msgs = [];
      if (f.sameSite.value === 'None' && !f.secure.checked) msgs.push('SameSite=None needs Secure');
      if (f.name.value.startsWith('__Host-') && (!f.secure.checked || f.path.value !== '/' || f.domain.value.startsWith('.'))) msgs.push('__Host- needs Secure, path / and a host-only domain (no leading dot)');
      if (f.name.value.startsWith('__Secure-') && !f.secure.checked) msgs.push('__Secure- needs Secure');
      hint.textContent = msgs.join('. ');
    };
    Object.values(f).forEach(x => x.addEventListener('input', check)); Object.values(f).forEach(x => x.addEventListener('change', check)); check();
    const read = el('div', { class: 'readable' });
    const showReadable = () => { read.textContent = ''; for (const [k, v] of readable(f.value.value)) read.append(el('div', { class: 'sub', text: k }), el('pre', { text: v })); };
    f.value.addEventListener('input', showReadable); showReadable();
    const collect = () => ({
      name: f.name.value.trim(), value: f.value.value, domain: f.domain.value.trim(), path: f.path.value.trim() || '/',
      session: f.session.checked, expires: f.session.checked ? -1 : new Date(f.expires.value).getTime() / 1000,
      secure: f.secure.checked, httpOnly: f.httpOnly.checked, sameSite: f.sameSite.value, priority: f.priority.value, partitionKey: c.partitionKey,
    });
    const card = el('div', { class: 'card editor' }, el('h2', { text: isNew ? 'Add a cookie' : 'Edit cookie' }),
      el('div', { class: 'grid' }, field('Name', f.name), field('Domain (leading dot = also subdomains)', f.domain), field('Path', f.path),
        field('Expires', f.expires), field('Session cookie (deleted when GBrowser closes)', f.session), field('Secure', f.secure),
        field('HttpOnly (pages cannot read it)', f.httpOnly), field('SameSite', f.sameSite), field('Priority', f.priority)),
      field('Value', f.value), hint, read);
    if (pk(c)) card.append(el('div', { class: 'sub', text: 'Partitioned for ' + pk(c) }));
    const save = el('button', { class: 'btn', text: 'Save' });
    save.addEventListener('click', async () => {
      const n = collect();
      try { await gb.send('cookies.set', { cookie: n, old }); } catch (e) { snack(e.message); return; }
      if (old) { undo = { type: 'edit', list: [old], after: n }; snackUndo('Saved'); } else snack('Added');
      load();
    });
    const actions = el('div', { class: 'row' }, save);
    if (!isNew) {
      actions.append(
        el('button', { class: 'btn tonal', text: 'Duplicate', onclick: () => { const n = collect(); n.name += '_copy'; editor(Object.assign({}, c, n, { name: n.name })); } }),
        el('button', { class: 'btn tonal', text: c.protected ? 'Unprotect' : 'Protect', onclick: async () => { await gb.send('cookies.protect', { cookie: c, on: !c.protected }); snack(c.protected ? 'No longer protected' : 'Protected: kept as it is now'); load(); } }),
        el('button', { class: 'btn tonal', text: c.blocked ? 'Unblock' : 'Block on ' + c.site, onclick: async () => { await gb.send('cookies.block', { name: c.name, site: c.site, on: !c.blocked }); snack(c.blocked ? 'Unblocked' : 'Blocked on ' + c.site); load(); } }),
        el('button', { class: 'btn tonal', text: 'Block everywhere', onclick: async () => { await gb.send('cookies.block', { name: c.name, site: '*', on: true }); snack('Blocked on every site'); load(); } }),
        el('button', { class: 'btn danger', text: 'Delete', onclick: () => del([c]) }));
    }
    actions.append(el('button', { class: 'btn text', text: 'Close', onclick: () => render() }));
    card.append(actions);
    main.prepend(card);
    card.scrollIntoView({ behavior: 'smooth' });
    f.name.focus();
  }

  // ------------------------------------------------------------ export / import / snapshots / site data / options
  function exportMenu(list) {
    const card = el('div', { class: 'card editor' }, el('h2', { text: 'Export ' + list.length + (list.length === 1 ? ' cookie' : ' cookies') }));
    for (const [fmt, name, sub] of [['json', 'JSON', 'Cookie-Editor format'], ['netscape', 'cookies.txt', 'Netscape format (curl, yt-dlp)'], ['header', 'Cookie header', 'name=value; ...']]) {
      card.append(el('div', { class: 'row' }, icon('Download'), el('div', { class: 'text' }, el('div', { class: 'title', text: name }), el('div', { class: 'sub', text: sub })),
        el('button', { class: 'btn text', text: 'Copy', onclick: async () => { await gb.send('cookies.export', { list, format: fmt, to: 'clipboard' }); snack('Copied'); } }),
        el('button', { class: 'btn tonal', text: 'Save', onclick: async () => { if (await gb.send('cookies.export', { list, format: fmt })) snack('Saved'); } })));
    }
    card.append(el('div', { class: 'row' }, el('button', { class: 'btn text', text: 'Close', onclick: () => render() })));
    main.prepend(card);
  }
  async function importFlow(text) {
    let list;
    try { list = await gb.send('cookies.parse', text == null ? { host: site } : { text, host: site }); } catch (e) { snack(e.message); return; }
    if (!list) return;
    const card = el('div', { class: 'card editor' }, el('h2', { text: 'Import ' + list.length + (list.length === 1 ? ' cookie' : ' cookies') + '?' }));
    for (const c of list.slice(0, 30)) card.append(el('div', { class: 'row' }, el('div', { class: 'text' }, el('div', { class: 'title', text: c.name + ' = ' + String(c.value).slice(0, 60) }), el('div', { class: 'sub', text: c.domain + c.path }))));
    if (list.length > 30) card.append(el('div', { class: 'row' }, el('div', { class: 'sub', text: '... and ' + (list.length - 30) + ' more' })));
    const paste = el('textarea', { class: 'field small', placeholder: 'Or paste cookies here (JSON, cookies.txt or name=value; ...)' });
    card.append(el('div', { class: 'row' },
      el('button', { class: 'btn', text: 'Import', onclick: async () => {
        let ok = 0, bad = 0;
        for (const c of list) { try { await gb.send('cookies.set', { cookie: c }); ok++; } catch (e) { bad++; } }
        snack(ok + ' imported' + (bad ? ', ' + bad + ' refused' : '')); load();
      } }),
      el('button', { class: 'btn text', text: 'Close', onclick: () => render() })), paste,
      el('div', { class: 'row' }, el('button', { class: 'btn tonal', text: 'Read pasted text', onclick: () => importFlow(paste.value) })));
    main.prepend(card);
  }

  async function siteDataCard(s) {
    const card = el('div', { class: 'card' }, el('h2', { text: 'Stored data of ' + s }));
    let any = false;
    for (const origin of ['https://' + s, 'https://www.' + s]) {
      let u;
      try { u = await gb.send('sitedata.usage', { origin }); } catch (e) { continue; }
      const parts = (u.usageBreakdown || []).filter(x => x.usage > 0);
      if (!parts.length && !u.usage) continue;
      any = true;
      card.append(el('div', { class: 'row' }, icon('Memory'), el('div', { class: 'text' }, el('div', { class: 'title', text: origin + ': ' + size(u.usage || 0) }),
        el('div', { class: 'sub', text: parts.map(x => x.storageType + ' ' + size(x.usage)).join(', ') || 'nothing stored' })),
        el('button', { class: 'btn tonal', text: 'Delete', onclick: async () => { await gb.send('sitedata.clear', { origin }); snack('Deleted'); render(); } })));
    }
    // snapshots
    const snaps = (await gb.send('cookies.snapshots')).filter(x => x.site === s);
    const nameIn = el('input', { class: 'field', placeholder: 'Snapshot name (e.g. work account)' });
    nameIn.style.flex = '1';
    card.append(el('div', { class: 'row' }, nameIn, el('button', { class: 'btn tonal', text: 'Save snapshot', onclick: async () => {
      if (!nameIn.value.trim()) return;
      const n = await gb.send('cookies.snapshot', { action: 'save', name: nameIn.value.trim(), site: s }); snack(n + ' cookies saved'); render();
    } })));
    for (const x of snaps)
      card.append(el('div', { class: 'row' }, icon('Restore'), el('div', { class: 'text' }, el('div', { class: 'title', text: x.name }), el('div', { class: 'sub', text: 'Saved ' + dayTime(x.time) })),
        el('button', { class: 'btn tonal', text: 'Restore', onclick: async () => { const n = await gb.send('cookies.snapshot', { action: 'restore', name: x.name, site: s }); snack(n + ' cookies restored'); load(); } }),
        iconButton('Delete', 'Delete snapshot', async () => { await gb.send('cookies.snapshot', { action: 'delete', name: x.name, site: s }); render(); })));
    if (!any && !snaps.length) card.append(el('div', { class: 'row' }, el('div', { class: 'sub', text: 'No other stored data. Snapshots keep a copy of this site\'s cookies to switch accounts.' })));
    return card;
  }

  function optionsCard() {
    const sw = (on, f) => { const b = el('button', { class: 'switch', role: 'switch', 'aria-checked': String(!!on) }); b.addEventListener('click', () => { const v = b.getAttribute('aria-checked') !== 'true'; b.setAttribute('aria-checked', String(v)); f(v); }); return b; };
    const opt = async o => { const r = await gb.send('cookies.options', o); if (r && r.restart) snack('Restart GBrowser to apply (Settings > Restart)'); else snack('Saved'); data = await gb.send('cookies.list'); };
    const cap = el('select', { 'aria-label': 'Longest cookie lifetime' }, [[0, 'No limit'], [1, '1 day'], [7, '7 days'], [30, '30 days'], [90, '90 days'], [365, '1 year']]
      .map(([v, t]) => el('option', { value: v, text: t, selected: v === data.maxDays })));
    cap.addEventListener('change', () => opt({ maxDays: +cap.value }));
    const keep = el('input', { class: 'field', value: (data.keep || []).join(', '), placeholder: 'Sites to keep, e.g. google.com, github.com' });
    keep.style.flex = '1';
    keep.addEventListener('change', () => opt({ keep: keep.value.split(',').map(x => x.trim()).filter(Boolean) }));
    return el('div', { class: 'card' }, el('h2', { text: 'Cookie options' }),
      el('div', { class: 'row' }, icon('Block'), el('div', { class: 'text' }, el('div', { class: 'title', text: 'Block third-party cookies' }),
        el('div', { class: 'sub', text: 'Sites cannot read or set cookies inside other sites (some sign-ins and embeds may stop working)' + (data.thirdParty !== data.thirdPartyActive ? '. Restart GBrowser to apply.' : '') })),
        sw(data.thirdParty, v => opt({ thirdParty: v }))),
      el('div', { class: 'row' }, icon('Delete'), el('div', { class: 'text' }, el('div', { class: 'title', text: 'Delete a site\'s cookies after its last tab closes' }),
        el('div', { class: 'sub', text: '15 seconds after; allowed and kept sites stay signed in' })), sw(data.autoDelete, v => opt({ autoDelete: v }))),
      el('div', { class: 'row' }, keep),
      el('div', { class: 'row' }, icon('History'), el('div', { class: 'text' }, el('div', { class: 'title', text: 'Longest cookie lifetime' }),
        el('div', { class: 'sub', text: 'Long-lived cookies are cut to this (allowed and protected ones are not)' })), cap));
  }

  // ------------------------------------------------------------ live changes of the selected site
  let prev = null;
  const log = [];
  async function poll() {
    if (document.hidden || !site) { prev = null; return; }
    let d;
    try { d = await gb.send('cookies.list'); } catch (e) { return; }
    const cur = new Map(d.cookies.filter(c => c.site === site).map(c => [c.key, c]));
    if (prev) {
      for (const [k, c] of cur) {
        const o = prev.get(k);
        if (!o) log.unshift([Date.now(), 'Added', c.name, c.value]);
        else if (o.value !== c.value) log.unshift([Date.now(), 'Changed', c.name, c.value]);
      }
      for (const [k, o] of prev) if (!cur.has(k)) log.unshift([Date.now(), 'Removed', o.name, '']);
      log.length = Math.min(log.length, 50);
      const card = document.getElementById('livelog');
      if (card) fillLog(card);
    }
    prev = cur;
  }
  function fillLog(card) {
    card.textContent = '';
    card.append(el('h2', { text: 'Live changes on ' + site }));
    if (!log.length) card.append(el('div', { class: 'row' }, el('div', { class: 'sub', text: 'Cookies this site sets, changes or removes while this page is open show here' })));
    for (const [t, what, name, v] of log)
      card.append(el('div', { class: 'row' }, el('div', { class: 'text' }, el('div', { class: 'title', text: what + ': ' + name }),
        el('div', { class: 'sub', text: new Date(t).toLocaleTimeString('en-GB') + (v ? '  ' + String(v).slice(0, 80) : '') }))));
  }
  const render0 = render;
  render = function () {
    render0();
    if (site) { const card = el('div', { class: 'card', id: 'livelog' }); fillLog(card); main.append(card); }
  };
  setInterval(poll, 3000);
  load();
})();
