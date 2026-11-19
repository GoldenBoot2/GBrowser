'use strict';
// Task manager (Shift+Esc), like Chrome's: every GBrowser and engine process with what it serves, its memory and CPU;
// End process for page, frame, GPU and utility processes; double-click a tab row to switch to it.
(() => {
  const main = document.getElementById('main');
  let rows = [], selected = null, sortKey = 'mem', sortDesc = true;
  const endBtn = el('button', { class: 'btn', text: 'End process', disabled: true });
  header('Task manager', 'Memory', endBtn);
  endBtn.addEventListener('click', async () => {
    if (selected == null) return;
    try { await gb.send('tasks.end', { pid: selected }); snack('Process ended'); } catch (e) { snack(e.message); }
    selected = null; load();
  });
  const fmtMem = b => b < 0 ? '-' : b >= 1 << 30 ? (b / (1 << 30)).toFixed(1) + ' GB' : (b / (1 << 20)).toFixed(1) + ' MB';
  const COLS = [['task', 'Task'], ['mem', 'Memory'], ['cpu', 'CPU'], ['pid', 'Process ID']];
  const ICON = { app: 'Memory', browser: 'Language', gpu: 'Tune', utility: 'Settings', tab: 'Tab', frame: 'Public', extension: 'Extension', spare: 'Memory', other: 'Memory' };
  async function load() {
    try { rows = await gb.send('tasks.list'); } catch (e) { return; }
    render();
  }
  function render() {
    const list = rows.slice().sort((a, b) => {
      const x = a[sortKey], y = b[sortKey];
      const c = typeof x === 'string' ? x.localeCompare(y) : x - y;
      return sortDesc ? -c : c;
    });
    const total = rows.reduce((s, r) => s + (r.mem > 0 ? r.mem : 0), 0);
    const head = el('tr', null, ...COLS.map(([k, t]) => {
      const th = el('th', { class: (k === 'task' ? '' : 'num') + (k === sortKey ? ' sorted' + (sortDesc ? '' : ' up') : '') },
        el('span', { text: t }), k === sortKey ? icon('ArrowDrop', 's20') : null);
      th.addEventListener('click', () => { if (sortKey === k) sortDesc = !sortDesc; else { sortKey = k; sortDesc = k !== 'task'; } render(); });
      return th;
    }));
    const body = list.map(r => {
      const tr = el('tr', { class: r.pid === selected ? 'sel' : '', tabindex: '0' },
        el('td', null, el('span', { class: 'task' }, icon(ICON[r.kind] || 'Memory', 's20'), el('span', { text: r.task }))),
        el('td', { class: 'num', text: fmtMem(r.mem) }),
        el('td', { class: 'num', text: r.cpu < 0 ? '-' : r.cpu.toFixed(1) }),
        el('td', { class: 'num', text: String(r.pid) }));
      tr.addEventListener('click', () => { selected = r.canEnd ? r.pid : null; endBtn.disabled = selected == null; render(); });
      tr.addEventListener('dblclick', () => { if (r.tab) gb.send('tasks.show', { tab: r.tab }); });
      return tr;
    });
    main.textContent = '';
    main.append(el('div', { class: 'card' }, el('table', { class: 'tasks' }, el('thead', null, head), el('tbody', null, ...body)),
      el('div', { class: 'sub total', text: rows.length + ' processes, ' + fmtMem(total) + ' in total' })));
    endBtn.disabled = selected == null || !rows.some(r => r.pid === selected && r.canEnd);
  }
  load();
  setInterval(() => { if (!document.hidden) load(); }, 2000);
})();
