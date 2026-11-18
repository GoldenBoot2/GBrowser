'use strict';
(() => {
  const main = document.getElementById('main');
  const url = new URLSearchParams(location.search).get('u') || '';
  let host = url;
  try { host = new URL(url).host; } catch (e) { }
  main.append(
    icon('Warning', 'big'),
    el('h1', { text: 'Not secure' }),
    el('p', {}, el('span', { class: 'host', text: host }), ' has no secure (HTTPS) version.'),
    el('p', { text: 'Others on your network can see or change what you send and receive there.' }),
    el('div', { class: 'actions' },
      el('button', { class: 'btn', onclick: () => history.length > 1 ? history.back() : gb.send('open', { url: 'https://gbrowser.local/newtab.html' }) },
        icon('Back'), 'Go back'),
      el('button', { class: 'btn text', onclick: () => gb.send('insecure.allow', { url }) }, 'Continue to site')));
})();
