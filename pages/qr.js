'use strict';
// QR code for a page (main menu > Create QR code), like Chrome's: editable address, live code, Download as PNG.
(() => {
  const main = document.getElementById('main');
  header('QR code', 'QrCode');
  const start = new URLSearchParams(location.search).get('u') || '';
  const field = el('input', { class: 'field', value: start, spellcheck: 'false', 'aria-label': 'Address' });
  const canvas = el('canvas', { class: 'qr', 'aria-label': 'QR code' });
  const note = el('div', { class: 'sub' });
  const dl = el('button', { class: 'btn filled', text: 'Download' });
  const SCALE = 8, QUIET = 4;
  function draw() {
    const text = field.value.trim();
    const x = canvas.getContext('2d');
    if (!text) { canvas.width = canvas.height = 1; dl.disabled = true; note.textContent = ''; return; }
    let q;
    try { q = qrEncode(text); } catch (e) { canvas.width = canvas.height = 1; dl.disabled = true; note.textContent = 'Too long'; return; }
    const n = (q.size + QUIET * 2) * SCALE;
    canvas.width = canvas.height = n;
    x.fillStyle = '#fff'; x.fillRect(0, 0, n, n); x.fillStyle = '#000';
    for (let yy = 0; yy < q.size; yy++) for (let xx = 0; xx < q.size; xx++)
      if (q.dark(xx, yy)) x.fillRect((xx + QUIET) * SCALE, (yy + QUIET) * SCALE, SCALE, SCALE);
    dl.disabled = false; note.textContent = '';
  }
  field.addEventListener('input', draw);
  dl.addEventListener('click', () => {
    let name = 'qrcode';
    try { name = 'qrcode_' + new URL(field.value.trim()).hostname.replace(/^www\./, ''); } catch (e) { }
    const a = el('a', { href: canvas.toDataURL('image/png'), download: name + '.png' });
    document.body.append(a); a.click(); a.remove();
  });
  main.append(el('div', { class: 'card qrcard' }, field, canvas, note, el('div', { class: 'actions' }, dl)));
  draw();
  field.focus();
})();
