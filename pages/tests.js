'use strict';
// Blocking and privacy tests: public test pages, opened in a new tab (like the Android app's Tests page).
(() => {
  const main = document.getElementById('main');
  header('Blocking tests', 'Verified');
  const groups = [
    ['Ad blocking', [
      ['Super Adblock Test', 'https://superadblocktest.com/', 'Hundreds of ad, tracker and analytics hosts'],
      ['Adblock test (turtlecute)', 'https://adblock.turtlecute.org/', 'Ads, analytics, error trackers, social trackers, mix'],
      ['AdBlock Tester', 'https://adblock-tester.com/', 'Banners, scripts, flash, GIF, analytics'],
      ['Can You Block It', 'https://canyoublockit.com/', 'Real ad networks, popups and video ads'],
      ['d3ward toolz', 'https://d3ward.github.io/toolz/adblock', 'Ad, analytics and OEM tracking hosts'],
    ]],
    ['Ad-block detection (sites must not notice the blocker)', [
      ['DetectAdblock', 'https://www.detectadblock.com/', 'Should say ads are allowed'],
      ['FuckAdBlock test', 'https://fuckadblock.sitexw.fr/', 'Should say no ad blocker'],
      ['BlockAdBlock test', 'https://blockadblock.com/', 'Detection script demo'],
    ]],
    ['Privacy and fingerprints', [
      ['Cover Your Tracks (EFF)', 'https://coveryourtracks.eff.org/', 'Tracker blocking and fingerprint uniqueness'],
      ['BrowserLeaks', 'https://browserleaks.com/', 'WebRTC, canvas, WebGL, fonts, client hints'],
      ['CreepJS', 'https://abrahamjuliot.github.io/creepjs/', 'Lies and tampering checks'],
      ['BrowserScan', 'https://www.browserscan.net/', 'Fingerprint consistency'],
      ['Pixelscan', 'https://pixelscan.net/', 'Masking and bot checks'],
      ['Third-party cookies', 'https://www.whatismybrowser.com/detect/are-third-party-cookies-enabled', 'Shows whether they are blocked'],
      ['Secure DNS', 'https://1.1.1.1/help', 'Whether DNS over HTTPS is in use'],
    ]],
  ];
  for (const [title, items] of groups) {
    const card = el('div', { class: 'card' }, el('h2', { text: title }));
    for (const [name, url, sub] of items) {
      const r = el('div', { class: 'row link', title: url }, siteIcon(url), el('div', { class: 'text' }, el('div', { class: 'title', text: name }), el('div', { class: 'sub', text: sub })), icon('OpenNew'));
      r.addEventListener('click', () => openUrl(url, { ctrlKey: true }));
      card.append(r);
    }
    main.append(card);
    loadSiteIcons(card);
  }
})();
