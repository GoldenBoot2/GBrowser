# Draws assets/gbrowser.ico: blue circle (Chrome's #0B57D0 -> #1B6EF3, top to bottom) with the white Material Symbols
# Rounded "language" glyph, sizes 16-256 (PNG frames inside the ICO). Stdlib only, anti-aliased, nonzero fill.
# Run: python -I tools/make_icon.py   (after tools/fetch_icons.py, which writes src/IconData.cs)
import os, re, struct, zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
TOP, BOTTOM = (0x0B, 0x57, 0xD0), (0x1B, 0x6E, 0xF3)
GLYPH = 0.62   # glyph box as a share of the icon
SS = 4         # samples per pixel in each direction

def glyph_path():
    src = open(os.path.join(ROOT, 'src', 'IconData.cs'), encoding='utf-8').read()
    return re.search(r'public const string Language = "([^"]+)"', src).group(1)

def flatten(d):
    toks = re.findall(r'[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?', d)
    polys, cur = [], []
    x = y = sx = sy = 0.0
    lq = None; cmd = None; i = 0
    def num():
        nonlocal i
        v = float(toks[i]); i += 1; return v
    def quad(x0, y0, cx, cy, x1, y1):
        for k in range(1, 13):
            t = k / 12
            cur.append(((1-t)**2*x0 + 2*(1-t)*t*cx + t*t*x1, (1-t)**2*y0 + 2*(1-t)*t*cy + t*t*y1))
    while i < len(toks):
        if toks[i].isalpha():
            cmd = toks[i]; i += 1
        c, rel = cmd.upper(), cmd.islower()
        if c == 'Z':
            if cur: polys.append(cur)
            cur = []; x, y = sx, sy; lq = None
            continue
        if c == 'M':
            nx, ny = num(), num()
            if rel: nx += x; ny += y
            if cur: polys.append(cur)
            cur = [(nx, ny)]; x, y, sx, sy = nx, ny, nx, ny
            cmd = 'l' if rel else 'L'; lq = None
        elif c == 'L':
            nx, ny = num(), num()
            if rel: nx += x; ny += y
            cur.append((nx, ny)); x, y = nx, ny; lq = None
        elif c == 'H':
            nx = num() + (x if rel else 0); cur.append((nx, y)); x = nx; lq = None
        elif c == 'V':
            ny = num() + (y if rel else 0); cur.append((x, ny)); y = ny; lq = None
        elif c == 'Q':
            cx, cy, nx, ny = num(), num(), num(), num()
            if rel: cx += x; cy += y; nx += x; ny += y
            quad(x, y, cx, cy, nx, ny); lq = (cx, cy); x, y = nx, ny
        elif c == 'T':
            nx, ny = num(), num()
            if rel: nx += x; ny += y
            cx, cy = (2*x - lq[0], 2*y - lq[1]) if lq else (x, y)
            quad(x, y, cx, cy, nx, ny); lq = (cx, cy); x, y = nx, ny
        else:
            raise SystemExit('unsupported path command ' + cmd)
    if cur: polys.append(cur)
    return polys

def coverage(polys, size, scale, off):
    """Glyph coverage 0..1 per pixel (nonzero winding, SS x SS samples)."""
    edges = []
    for p in polys:
        pts = [((px * scale + off), ((py + 960) * scale + off)) for px, py in p]
        for (x0, y0), (x1, y1) in zip(pts, pts[1:] + pts[:1]):
            if y0 != y1: edges.append((x0, y0, x1, y1))
    cov = [[0.0] * size for _ in range(size)]
    for row in range(size * SS):
        yy = (row + 0.5) / SS
        xs = []
        for x0, y0, x1, y1 in edges:
            if (y0 <= yy < y1) or (y1 <= yy < y0):
                xs.append((x0 + (yy - y0) * (x1 - x0) / (y1 - y0), 1 if y1 > y0 else -1))
        xs.sort()
        for col in range(size * SS):
            xx = (col + 0.5) / SS
            w = sum(d for xv, d in xs if xv < xx)
            if w != 0: cov[row // SS][col // SS] += 1.0 / (SS * SS)
    return cov

def circle(size):
    c = (size - 1) / 2.0; r = size / 2.0
    out = [[0.0] * size for _ in range(size)]
    for y in range(size):
        for x in range(size):
            n = 0
            for sy in range(SS):
                for sx in range(SS):
                    dx = x + (sx + 0.5) / SS - 0.5 - c; dy = y + (sy + 0.5) / SS - 0.5 - c
                    if dx*dx + dy*dy <= r*r: n += 1
            out[y][x] = n / (SS * SS)
    return out

def png(size, rgba):
    raw = b''.join(b'\x00' + bytes(rgba[y*size*4:(y+1)*size*4]) for y in range(size))
    chunk = lambda t, d: struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))

def render(size, polys):
    g = size * GLYPH; scale = g / 960.0; off = (size - g) / 2.0
    cov = coverage(polys, size, scale, off); circ = circle(size)
    px = []
    for y in range(size):
        t = y / max(1, size - 1)
        bg = [round(TOP[k] + (BOTTOM[k] - TOP[k]) * t) for k in range(3)]
        for x in range(size):
            a = circ[y][x]; gl = min(1.0, cov[y][x])
            rgb = [round(bg[k] * (1 - gl) + 255 * gl) for k in range(3)]
            px += rgb + [round(255 * a)]
    return png(size, px)

def main():
    polys = flatten(glyph_path())
    frames = [render(s, polys) for s in SIZES]
    head = struct.pack('<HHH', 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    dirs = b''; data = b''
    for s, f in zip(SIZES, frames):
        dirs += struct.pack('<BBBBHHII', s % 256, s % 256, 0, 0, 1, 32, len(f), offset + len(data))
        data += f
    os.makedirs(os.path.join(ROOT, 'assets'), exist_ok=True)
    with open(os.path.join(ROOT, 'assets', 'gbrowser.ico'), 'wb') as fh:
        fh.write(head + dirs + data)
    with open(os.path.join(ROOT, 'assets', 'gbrowser-256.png'), 'wb') as fh:
        fh.write(frames[-1])
    print('wrote assets/gbrowser.ico with', len(frames), 'sizes')

if __name__ == '__main__':
    main()
