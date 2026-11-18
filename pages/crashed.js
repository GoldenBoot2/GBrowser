'use strict';
// A page whose process crashed or was ended (like Chrome's sad tab). The address bar keeps the page's address;
// Reload loads it again in a new process.
(() => {
  const main = document.getElementById('main');
  const q = new URLSearchParams(location.search);
  const url = q.get('u') || '', reason = q.get('r') || 'Unexpected';
  const codes = { Unexpected: 'RESULT_CODE_HUNG', Unresponsive: 'RESULT_CODE_HUNG', TerminatedByOther: 'RESULT_CODE_KILLED',
    Crashed: 'STATUS_ACCESS_VIOLATION', LaunchFailed: 'RESULT_CODE_LAUNCH_FAILED', OutOfMemory: 'STATUS_NO_MEMORY',
    ProfileDeleted: 'RESULT_CODE_PROFILE_DELETED' };
  main.append(
    icon('Sad', 'big'),
    el('h1', { text: 'This page crashed' }),
    el('p', { text: reason === 'OutOfMemory' ? 'The page ran out of memory.' : 'Something went wrong while showing this page.' }),
    el('p', { class: 'code', text: 'Error code: ' + (codes[reason] || reason) }),
    el('div', { class: 'actions' },
      el('button', { class: 'btn', onclick: () => gb.send('open', { url }) }, icon('Refresh'), 'Reload')));
})();
