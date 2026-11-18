'use strict';
// Certificate error (expired, wrong name, revoked, untrusted): the site's identity cannot be checked. No way to
// continue, on purpose; Reload tries again (the site may have fixed its certificate).
(() => {
  const main = document.getElementById('main');
  const q = new URLSearchParams(location.search);
  const url = q.get('u') || '', code = q.get('c') || 'ERR_CERT_INVALID';
  let host = url;
  try { host = new URL(url).host; } catch (e) { }
  main.append(
    icon('Warning', 'big'),
    el('h1', { text: 'Your connection isn’t private' }),
    el('p', {}, 'Someone may be trying to steal your information from ', el('span', { class: 'host', text: host }),
      ' (for example passwords, messages or cards). Its security certificate is not valid.'),
    el('p', { class: 'code', text: 'NET::' + code }),
    el('div', { class: 'actions' },
      el('button', { class: 'btn', onclick: () => history.length > 1 ? history.back() : gb.send('open', { url: 'https://gbrowser.local/newtab.html' }) },
        icon('Back'), 'Go back'),
      el('button', { class: 'btn text', onclick: () => gb.send('open', { url }) }, icon('Refresh'), 'Reload')));
})();
