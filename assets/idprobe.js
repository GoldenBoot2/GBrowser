// Identity check (pages/identity.js): run by GBrowser in the page of the site being checked (main world, where the
// page script's identity changes apply), only when the user opens the check. Measures what that site can read,
// the way fingerprinting scripts do, and returns one JSON object. Leaves nothing behind (no storage, no globals).
(async () => {
  const out = {};
  const h = s => { let x = 2166136261; s = String(s); for (let i = 0; i < s.length; i++) { x ^= s.charCodeAt(i); x = Math.imul(x, 16777619); } return (x >>> 0).toString(16).padStart(8, '0'); };
  const safe = async (k, f) => { try { out[k] = await f(); } catch (e) { out[k] = 'error: ' + (e && e.name || e); } };
  await safe('userAgent', () => navigator.userAgent);
  await safe('brands', () => (navigator.userAgentData && navigator.userAgentData.brands || []).map(b => b.brand + ' ' + b.version).join(', '));
  await safe('platform', () => navigator.platform + (navigator.userAgentData ? ' / ' + navigator.userAgentData.platform : ''));
  await safe('languages', () => navigator.languages.join(', '));
  await safe('timeZone', () => Intl.DateTimeFormat().resolvedOptions().timeZone + ' (UTC' + (-new Date().getTimezoneOffset() / 60) + ')');
  await safe('screen', () => screen.width + 'x' + screen.height + ', ' + screen.colorDepth + '-bit, ratio ' + devicePixelRatio);
  await safe('cores', () => navigator.hardwareConcurrency + ' cores, ' + (navigator.deviceMemory || '?') + ' GB');
  await safe('gpu', () => {
    const g = document.createElement('canvas').getContext('webgl');
    const d = g && g.getExtension('WEBGL_debug_renderer_info');
    return d ? g.getParameter(d.UNMASKED_RENDERER_WEBGL) : 'none';
  });
  await safe('canvas', () => {
    const c = document.createElement('canvas'); c.width = 240; c.height = 60;
    const x = c.getContext('2d'); x.fillStyle = '#f60'; x.fillRect(100, 1, 62, 20); x.fillStyle = '#069'; x.font = '15px Arial';
    x.fillText('Cwm fjordbank glyphs vext quiz', 2, 15); x.fillStyle = 'rgba(102,204,0,0.7)'; x.font = '18px Times New Roman';
    x.fillText('Cwm fjordbank glyphs vext quiz', 4, 45);
    return h(c.toDataURL());
  });
  await safe('webgl', () => {
    const c = document.createElement('canvas'); c.width = c.height = 64;
    const g = c.getContext('webgl', {preserveDrawingBuffer: true}); if (!g) return 'none';
    const vs = g.createShader(g.VERTEX_SHADER); g.shaderSource(vs, 'attribute vec2 p;varying vec2 v;void main(){v=p;gl_Position=vec4(p,0,1);}'); g.compileShader(vs);
    const fs = g.createShader(g.FRAGMENT_SHADER); g.shaderSource(fs, 'precision mediump float;varying vec2 v;void main(){gl_FragColor=vec4(sin(v.x*7.0)*.5+.5,cos(v.y*5.0)*.5+.5,v.x*v.y,1);}'); g.compileShader(fs);
    const pr = g.createProgram(); g.attachShader(pr, vs); g.attachShader(pr, fs); g.linkProgram(pr); g.useProgram(pr);
    const b = g.createBuffer(); g.bindBuffer(g.ARRAY_BUFFER, b); g.bufferData(g.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, 0, 1]), g.STATIC_DRAW);
    const l = g.getAttribLocation(pr, 'p'); g.enableVertexAttribArray(l); g.vertexAttribPointer(l, 2, g.FLOAT, false, 0, 0); g.drawArrays(g.TRIANGLES, 0, 3);
    const px = new Uint8Array(64 * 64 * 4); g.readPixels(0, 0, 64, 64, g.RGBA, g.UNSIGNED_BYTE, px);
    return h(Array.prototype.join.call(px));
  });
  await safe('audio', async () => {
    const a = new OfflineAudioContext(1, 5000, 44100), o = a.createOscillator(), k = a.createDynamicsCompressor();
    o.type = 'triangle'; o.frequency.value = 10000; o.connect(k); k.connect(a.destination); o.start(0);
    const buf = await a.startRendering(), d = buf.getChannelData(0); let s = 0;
    for (let i = 4500; i < 5000; i++) s += Math.abs(d[i]);
    return s.toFixed(10);
  });
  await safe('webrtc', () => new Promise(res => {
    const pc = new RTCPeerConnection({iceServers: []}), seen = new Set();
    pc.createDataChannel('x');
    pc.onicecandidate = e => { if (e.candidate && e.candidate.candidate) { const a = e.candidate.candidate.split(' ')[4]; if (a) seen.add(/\.local$/.test(a) ? 'hidden (.local name)' : a); } };
    pc.createOffer().then(o => pc.setLocalDescription(o));
    setTimeout(() => { try { pc.close(); } catch (e) {} res(seen.size ? [...seen].join(', ') : 'no local addresses'); }, 1200);
  }));
  await safe('trackerIds', () => {
    const names = /^(_ga|_gid|_fbp|_fbc|_gcl_au|_clck|_uetvid|_hjSessionUser_\d+|MUID|ajs_anonymous_id|_ttp|rxVisitor|_pin_unauth)$/;
    const l = document.cookie.split('; ').filter(p => names.test(p.split('=')[0]));
    return l.length ? l.map(p => p.length > 60 ? p.slice(0, 60) + '...' : p).join('; ') : 'none on this site';
  });
  await safe('storage', () => {
    let n = 0; try { n = localStorage.length; } catch (e) {}
    return n + ' local storage entries';
  });
  return JSON.stringify(out);
})()
