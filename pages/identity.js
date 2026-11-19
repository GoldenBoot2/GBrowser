'use strict';
// Identity check (shield menu > Identity check): what the site in a tab can read about this browser, measured in
// that site's own page, and which values changed since the previous identity (kept here per site and identity).
(() => {
  const main = document.getElementById('main');
  const tabId = +new URLSearchParams(location.search).get('tab');
  // [key, label, what it is expected to do with Hide my identity on]
  const SIGNALS = [
    ['userAgent', 'User agent', 'genuine'], ['brands', 'Client hints', 'genuine'], ['platform', 'Platform', 'genuine'],
    ['languages', 'Languages', 'genuine'], ['timeZone', 'Time zone', 'genuine'], ['screen', 'Screen', 'genuine'],
    ['cores', 'Processor and memory', 'genuine'], ['gpu', 'Graphics', 'genuine'],
    ['canvas', 'Canvas fingerprint', 'noise'], ['webgl', 'WebGL fingerprint', 'noise'], ['audio', 'Audio fingerprint', 'noise'],
    ['webrtc', 'Local network addresses', 'hidden'], ['trackerIds', 'Tracking IDs in cookies', 'changes'], ['storage', 'Site storage', 'changes'],
  ];
  header('Identity check', 'Verified');

  async function run() {
    main.textContent = '';
    main.append(el('div', { class: 'empty' }, icon('Verified'), 'Checking'));
    let r;
    try { r = await gb.send('identity.probe', { tab: tabId }); } catch (e) { main.textContent = ''; main.append(el('div', { class: 'empty' }, icon('Info'), e.message)); return; }
    const prev = r.on ? r.prev : null;   // the previous identity's result on this site (kept by GBrowser)
    main.textContent = '';
    const again = el('button', { class: 'btn text', text: 'Check again', onclick: run });
    const newId = el('button', { class: 'btn tonal', text: 'New identity', onclick: async () => {
      if (!confirm('New identity: clear what sites stored about this browser (site storage, caches, tracking cookies)? You stay signed in.')) return;
      await gb.send('identity.new', { signOut: false }); snack('New identity. Reload the site, then check again');
    } });
    main.append(el('div', { class: 'card' }, el('div', { class: 'row' }, el('div', { class: 'text' },
      el('div', { class: 'title', text: r.title || r.site }),
      el('div', { class: 'sub', text: r.on ? 'Hide my identity on, identity ' + r.id + (r.noise ? ', fingerprint noise on' : '') : 'Hide my identity off: the site sees your real values' })),
      again, r.on ? newId : el('button', { class: 'btn tonal', text: 'Settings', onclick: () => openUrl(location.origin + '/settings.html#identity') }))));
    const card = el('div', { class: 'card' }, el('h2', { text: 'What ' + r.site + ' can read' }));
    for (const [k, label, kind] of SIGNALS) {
      const v = r.values[k];
      if (v === undefined) continue;
      let chip = null;
      if (r.on && prev && prev.values[k] !== undefined) {
        const changed = String(prev.values[k]) !== String(v);
        chip = el('span', { class: 'chip', text: changed ? 'Changed' : kind === 'genuine' ? 'Genuine' : 'Same' });
      } else if (r.on) chip = el('span', { class: 'chip', text: kind === 'genuine' ? 'Genuine' : kind === 'hidden' ? 'Hidden' : kind === 'noise' && !r.noise ? 'Real' : 'This identity' });
      card.append(el('div', { class: 'row' }, el('div', { class: 'text' }, el('div', { class: 'title', text: label }),
        el('div', { class: 'sub', text: String(v) })), chip));
    }
    main.append(card);
    main.append(el('div', { class: 'card' }, el('div', { class: 'row' }, icon('Info'), el('div', { class: 'text' },
      el('div', { class: 'sub', text: 'Genuine: real Microsoft Edge values (changing them would stand out). Your IP address is not changed (that needs a VPN).' + (prev ? ' Compared with identity ' + prev.id + '.' : r.on ? ' After a new identity, check this site again to compare.' : '') })))));
  }
  run();
})();
