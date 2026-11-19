r"""Compiles ad-block filter lists into the compact assets the app loads at start
(app/src/main/assets/filters/). Python 3 stdlib only.

Usage:  python -I tools/build_filters.py [--offline]
  Downloads every list in LISTS into tools/lists/ (unless --offline), then writes:
    hosts.bin                sorted FNV-1a 64 hashes, meta (types, flags), source-list mask of host rules
    hostexc.bin              hashes of "@@||host^" exception hosts
    net.txt                  other network rules: lists flags types token regex domains denyallow
                             ("lists" everywhere = bit mask of the LISTS entries a rule comes from: bit i =
                             LISTS[i]; the app turns lists on / off by that mask)
    page.txt                 page-level exception rules ($document, $elemhide, $generichide ...)
    cosm.txt                 cosmetic rules (generic keyed / unkeyed, per-domain, exceptions, CSS)
    psl.txt                  public suffix list (rules only) for first/third-party checks
    info.txt                 list names, rule counts and build date

Groups: 0 = ads, trackers, malware (always on with blocking), 1 = annoyances (cookie notices,
social widgets; own switch in the app). tools/extra_rules.txt holds our own rules (group 0).

Not compiled (counted in the report): regex network rules, $redirect-rule, $removeparam, $csp,
$replace, $header, $cookie, $popup and other modifiers that need a full browser extension,
$$ HTML filtering, #%# scripts/scriptlets and cosmetic rules with extended pseudo-classes other
than :has() (WebView's Chromium supports :has natively).
"""
import json, os, re, struct, sys, time, urllib.request
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LIST_DIR = os.environ.get('GB_LIST_DIR') or os.path.join(ROOT, 'tools', 'lists')
# GBrowser: same compiler as the WebView Adblocker Android app; output goes to GBrowser/filters
# (built fresh on every GitHub Actions run, git-ignored).
OUT = os.environ.get('GB_FILTER_OUT') or os.path.join(ROOT, 'filters')
AG = 'https://filters.adtidy.org/extension/chromium/filters/%d.txt'
HZ = 'https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/%s.txt'
UBO = 'https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/%s.txt'
HZN = 'https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/native.%s.txt'
LISTS = [  # (file, group, url, title)
    ('adguard_2.txt', 0, AG % 2, 'AdGuard Base'),
    ('adguard_11.txt', 0, AG % 11, 'AdGuard Mobile Ads'),
    ('adguard_3.txt', 2, AG % 3, 'AdGuard Tracking Protection'),
    ('adguard_17.txt', 2, AG % 17, 'AdGuard URL Tracking'),
    ('adguard_15.txt', 0, AG % 15, 'AdGuard DNS filter'),
    ('easyprivacy.txt', 2, 'https://easylist.to/easylist/easyprivacy.txt', 'EasyPrivacy'),
    ('hagezi_proplus.txt', 0, HZ % 'pro.plus', 'HaGeZi Pro++'),
    ('peterlowe.txt', 0, 'https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus'
                         '&showintro=0&mimetype=plaintext', "Peter Lowe's list"),
    ('easylist.txt', 0, 'https://easylist.to/easylist/easylist.txt', 'EasyList'),
    ('ubo_filters.txt', 0, UBO % 'filters', 'uBlock filters'),
    ('ubo_privacy.txt', 2, UBO % 'privacy', 'uBlock filters - Privacy'),
    ('ubo_badware.txt', 0, UBO % 'badware', 'uBlock filters - Badware risks'),
    ('oisd_big.txt', 0, 'https://big.oisd.nl/', 'OISD Big'),
    ('stevenblack.txt', 0, 'https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts',
     'StevenBlack Unified hosts'),
    ('adaway.txt', 0, 'https://adaway.org/hosts.txt', 'AdAway'),
    ('danpollock.txt', 0, 'https://someonewhocares.org/hosts/zero/hosts', "Dan Pollock's hosts"),
    ('urlhaus.txt', 0, 'https://malware-filter.gitlab.io/malware-filter/urlhaus-filter-agh.txt',
     'URLhaus malicious URLs'),
    ('frogeye.txt', 2, 'https://hostfiles.frogeye.fr/firstparty-trackers.txt',
     'Frogeye first-party trackers'),
    ('extra', 0, None, 'GoldenBoot extra rules'),
    ('hagezi_native_amazon.txt', 2, HZN % 'amazon', 'HaGeZi native amazon'),
    ('hagezi_native_apple.txt', 2, HZN % 'apple', 'HaGeZi native apple'),
    ('hagezi_native_huawei.txt', 2, HZN % 'huawei', 'HaGeZi native huawei'),
    ('hagezi_native_lgwebos.txt', 2, HZN % 'lgwebos', 'HaGeZi native lgwebos'),
    ('hagezi_native_oppo_realme.txt', 2, HZN % 'oppo-realme', 'HaGeZi native oppo-realme'),
    ('hagezi_native_roku.txt', 2, HZN % 'roku', 'HaGeZi native roku'),
    ('hagezi_native_samsung.txt', 2, HZN % 'samsung', 'HaGeZi native samsung'),
    ('hagezi_native_tiktok_extended.txt', 2, HZN % 'tiktok.extended', 'HaGeZi native tiktok.extended'),
    ('hagezi_native_vivo.txt', 2, HZN % 'vivo', 'HaGeZi native vivo'),
    ('hagezi_native_winoffice.txt', 2, HZN % 'winoffice', 'HaGeZi native winoffice'),
    ('hagezi_native_xiaomi.txt', 2, HZN % 'xiaomi', 'HaGeZi native xiaomi'),
    ('adguard_14.txt', 1, AG % 14, 'AdGuard Annoyances'),
    ('adguard_4.txt', 1, AG % 4, 'AdGuard Social Media'),
]
DNS_LISTS = {'adguard_15.txt', 'oisd_big.txt', 'urlhaus.txt'}
DOMAIN_LISTS = {'frogeye.txt'}          # one bare domain per line
NOT_HOSTS = {'localhost.localdomain', '0.0.0.0'}
HOSTS_FILES = {'stevenblack.txt', 'adaway.txt', 'danpollock.txt'}
PSL_URL ='https://publicsuffix.org/list/public_suffix_list.dat'

# ---------------------------------------------------------------- constants (match FilterEngine.java)
F_EXC, F_IMPORTANT, F_THIRD, F_FIRST, F_MATCHCASE, F_REGEX = 1, 2, 4, 8, 16, 32
T = {'script': 1, 'image': 2, 'stylesheet': 4, 'css': 4, 'xmlhttprequest': 8, 'xhr': 8,
     'media': 16, 'font': 32, 'subdocument': 64, 'frame': 64, 'websocket': 128, 'other': 256,
     'object': 512, 'object-subrequest': 512, 'ping': 1024, 'beacon': 1024, 'document': 2048,
     'doc': 2048, 'popup': 4096}
T_DEFAULT = 2047          # every type except document
T_ALL = 4095
PAGE = {'document': 1, 'doc': 1, 'urlblock': 1, 'elemhide': 2, 'ehide': 2, 'generichide': 4,
        'ghide': 4, 'specifichide': 8, 'shide': 8, 'jsinject': 16}
PAGE_IGNORED = {'content', 'extension', 'stealth', 'genericblock'}
RESP_MODS = {'csp', 'removeheader', 'header', 'cookie', 'replace', 'permissions'}
SKIP_MODS = {'hls', 'jsonprune', 'xmlprune', 'referrerpolicy', 'urltransform',
             'method', 'app', 'network', 'inline-script', 'inline-font',
             'to', 'webrtc', 'sitekey', 'rewrite', 'genericblock', 'webtransport'}
BLOCK_MODS = {'redirect', 'empty', 'mp4', 'all', 'important', 'match-case', 'third-party', '3p',
              'first-party', '1p', 'strict-third-party', 'strict3p', 'strict-first-party',
              'strict1p', 'domain', 'from', 'denyallow', 'badfilter'}
HOST_RULE = re.compile(r'^\|\|([a-z0-9_][a-z0-9_.-]*\.[a-z0-9-]+)\^\|?$')
FILE_EXT = {'js', 'css', 'php', 'html', 'htm', 'gif', 'png', 'jpg', 'jpeg', 'swf', 'json', 'xml',
            'txt', 'aspx', 'asp', 'cgi', 'jsp', 'mp4', 'svg', 'ico', 'webp', 'pl', 'do', 'ashx',
            'cfm', 'mjs', 'woff', 'woff2', 'ttf', 'avif', 'webm', 'm3u8', 'vtt', 'map', 'axd'}
BARE_HOST = re.compile(r'^([a-z0-9_][a-z0-9_-]*(?:\.[a-z0-9_-]+)*\.[a-z][a-z0-9-]*[a-z0-9])\^?$')
HOSTS_LINE =re.compile(r'^(?:0\.0\.0\.0|127\.0\.0\.1|::1?)\s+([a-z0-9_][a-z0-9_.-]*\.[a-z0-9-]+)\s*$')
COSMETIC = re.compile(r'^([^#$\s/|^"\'=]*?)(#@\$\?#|#\$\?#|#@\$#|#\$#|#@%#|#%#|#@\?#|#\?#|#@#|##|\$@\$|\$\$)(.+)$')
MODS_OK = re.compile(r'^~?[a-z0-9_-]+(=[^,]*)?(,~?[a-z0-9_-]+(=[^,]*)?)*$', re.I)
EXT_PSEUDO = (':-abp-has(', ':has-text(', ':contains(', ':-abp-contains(', ':matches-css(',
              ':matches-css-before(', ':matches-css-after(', ':-abp-properties(', ':matches-attr(',
              ':matches-property(', ':xpath(', ':nth-ancestor(', ':upward(', ':remove(',
              ':remove-attr(', ':remove-class(', ':if-not(', ':if(', ':empty-trimmed',
              ':min-text-length(', ':others(', '[-ext-', ':style(', ':matches-media(',
              ':watch-attr(', ':shadow(', ':matches-path(')
BAD_TOKENS = {'http', 'https', 'www', 'com', 'net', 'org', 'js', 'html', 'php', 'cdn', 'static',
              'api', 'img', 'images', 'assets', 'css', 'json', 'png', 'jpg', 'gif'}

stats = defaultdict(int)


def fnv64(s):
    h = 0xcbf29ce484222325
    for b in s.encode('utf-8'):
        h ^= b
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h - (1 << 64) if h >= (1 << 63) else h   # Java long


def jhash(s):
    """Java String.hashCode-style int hash of an ASCII token (h = 31*h + c)."""
    h = 0
    for c in s:
        h = (31 * h + ord(c)) & 0xFFFFFFFF
    return h - (1 << 32) if h >= (1 << 31) else h


def fetch(name, url):
    os.makedirs(LIST_DIR, exist_ok=True)   # git-ignored: missing on a fresh checkout (CI)
    path = os.path.join(LIST_DIR, name)
    if url and '--offline' not in sys.argv:
        req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0 (filter build)'})
        for attempt in range(3):
            try:
                data = urllib.request.urlopen(req, timeout=120).read()
                assert len(data) > 1000, 'list too small: %s' % name
                open(path, 'wb').write(data)
                break
            except Exception as e:  # keep the last good copy
                print('  download failed (%s): %s' % (name, e))
                time.sleep(2)
    return open(path, encoding='utf-8', errors='replace').read()


# ---------------------------------------------------------------- network rules
OPT_NAME = re.compile(r'^~?[a-z0-9_-]+(=.*)?$', re.I)


def split_opts(opts):
    """Options split on commas that are not escaped (backslash-comma stays inside a value)."""
    out, cur, i = [], [], 0
    while i < len(opts):
        c = opts[i]
        if c == '\\' and i + 1 < len(opts) and opts[i + 1] == ',':
            cur.append(',')
            i += 2
            continue
        if c == ',':
            out.append(''.join(cur))
            cur = []
        else:
            cur.append(c)
        i += 1
    out.append(''.join(cur))
    return out


def split_options(rule):
    if rule.startswith('/') and rule.endswith('/'):
        return rule, ''
    i = len(rule)
    while True:
        i = rule.rfind('$', 0, i)
        if i < 0:
            return rule, ''
        opts = rule[i + 1:]
        parts = split_opts(opts)
        if opts and all(OPT_NAME.match(o) for o in parts) and (i == 0 or rule[i - 1] != '$'):
            return rule[:i], opts


def to_regex(p, match_case):
    host_anchor = p.startswith('||')
    start = end = ''
    if host_anchor:
        p = p[2:]
    elif p.startswith('|'):
        p, start = p[1:], '^'
    if p.endswith('|') and not p.endswith('\\|'):
        p, end = p[:-1], '$'
    p = re.sub(r'\*+', '*', p).strip('*') if not (host_anchor or start or end) else re.sub(r'\*+', '*', p)
    out = []
    for i, ch in enumerate(p):
        if ch == '*':
            out.append('.*')
        elif ch == '^':
            out.append('(?:[^a-z0-9_.%-]|$)' if not match_case else '(?:[^A-Za-z0-9_.%-]|$)')
        elif ch.isalnum() or ch == '_':
            out.append(ch if match_case else ch.lower())
        else:
            out.append('\\' + ch if ch in '\\.+?()[]{}|$^/-' else ch)
    body = ''.join(out)
    if host_anchor:
        body = '^[a-z][a-z0-9+.-]*://(?:[^/?#]*\\.)?' + body
    return start + body + end


TOKEN = re.compile(r'[a-z0-9%]+')


def candidate_tokens(p):
    """Alphanumeric runs of the pattern that are whole tokens of every matching URL."""
    host_anchor = p.startswith('||')
    left_anchor = host_anchor or p.startswith('|')
    q = p[2:] if host_anchor else (p[1:] if p.startswith('|') else p)
    right_anchor = q.endswith('|')
    if right_anchor:
        q = q[:-1]
    q = q.lower()
    toks = []
    for m in TOKEN.finditer(q):
        a, b = m.start(), m.end()
        lok = (a == 0 and left_anchor) or (a > 0 and q[a - 1] != '*')
        rok = (b == len(q) and right_anchor) or (b < len(q) and q[b] != '*')
        if lok and rok and len(m.group()) >= 2 and not (m.group().isdigit() and len(m.group()) < 3):
            toks.append(m.group())
    return toks


def parse_domains(s, sep):
    pos, neg = [], []
    for d in s.split(sep):
        d = d.strip().lower()
        if not d:
            continue
        if d.startswith('~'):
            neg.append(d[1:])
        else:
            pos.append(d)
    return pos, neg


class Net:
    __slots__ = ('group', 'flags', 'types', 'pattern', 'regex', 'domains', 'denyallow', 'page', 'raw',
                 'redirect', 'removeparam', 'resp')


def parse_network(line, group):
    exc = line.startswith('@@')
    body = line[2:] if exc else line
    pat, opts = split_options(body)
    is_regex = pat.startswith('/') and pat.endswith('/') and len(pat) > 2
    r = Net()
    r.group, r.flags, r.types, r.domains, r.denyallow, r.page = group, F_EXC if exc else 0, 0, '', '', 0
    r.redirect = ''
    r.removeparam = None
    r.resp = None
    neg_types = 0
    redirect = False
    for o in (split_opts(opts) if opts else []):
        name, _, val = o.partition('=')
        name = name.strip().lower()
        neg = name.startswith('~')
        if neg:
            name = name[1:]
        if name in SKIP_MODS:
            stats['skip $' + name] += 1
            return None
        if name in T and name not in ('document', 'doc'):
            if neg:
                neg_types |= T[name]
            else:
                r.types |= T[name]
        elif name in ('document', 'doc') and not exc:
            r.types |= T['document']
        elif exc and name in PAGE:
            r.page |= PAGE[name]
        elif exc and name in PAGE_IGNORED:
            if not r.page:
                r.page |= 0x100  # remember: page-level rule we ignore
        elif name in ('third-party', '3p', 'strict-third-party', 'strict3p'):
            r.flags |= F_FIRST if neg else F_THIRD
        elif name in ('first-party', '1p', 'strict-first-party', 'strict1p'):
            r.flags |= F_THIRD if neg else F_FIRST
        elif name == 'important':
            r.flags |= F_IMPORTANT
        elif name == 'match-case':
            r.flags |= F_MATCHCASE
        elif name in ('domain', 'from'):
            r.domains = val.lower()
        elif name == 'denyallow':
            r.denyallow = val.lower()
        elif name == 'all':
            r.types |= T_ALL
        elif name in ('redirect', 'redirect-rule', 'empty', 'mp4'):
            redirect = True
            res = {'empty': 'nooptext', 'mp4': 'noopmp4-1s'}.get(name, val.strip())
            res = REDIRECT_ALIAS.get(res, '')
            if name == 'redirect-rule':
                if not res:
                    stats['skip $redirect-rule (unknown resource)'] += 1
                    return None
                r.redirect = '?' + res   # only when another rule blocks
            else:
                r.redirect = res         # '' = unknown resource: plain block
        elif name == 'urlskip':
            r.removeparam = '!urlskip ' + val    # handled with the link-cleaning rules
        elif name == 'popunder':
            r.types |= T['popup']
        elif name in ('reason', 'from-cache'):
            pass                                 # notes only
        elif name in RESP_MODS:
            r.resp = (name, val)
        elif name in ('removeparam', 'queryprune'):
            r.removeparam = '~' + val if neg else val
        elif name == 'badfilter':
            return ('badfilter', line.replace('$badfilter,', '$').replace(',badfilter', '').replace('$badfilter', ''))
        else:
            stats['skip unknown $' + name] += 1
            return None
    if r.page:
        if r.page == 0x100:
            stats['skip page rule (ignored kind)'] += 1
            return None
        r.page &= 0xFF
    if neg_types and not r.types:
        r.types = T_DEFAULT & ~neg_types
    elif neg_types:
        r.types &= ~neg_types
    if not r.types:
        r.types = T_DEFAULT
    if redirect and exc:
        stats['skip @@$redirect'] += 1
        return None
    if pat in ('', '*', '|', '||') and not r.domains and r.removeparam is None and r.resp is None:
        stats['skip match-all without $domain'] += 1
        return None
    r.pattern = pat
    if r.resp is not None:
        return ('resp', r, is_regex)
    if r.removeparam is not None:
        return ('removeparam', r, is_regex)
    if is_regex:
        r.regex = pat[1:-1]
        r.flags |= F_REGEX
        stats['regex rules'] += 1
    else:
        r.regex = to_regex(pat, r.flags & F_MATCHCASE) if pat not in ('', '*') else ''
    return r


# ---------------------------------------------------------------- cosmetic
def css_ok(sel):
    if any(c in sel for c in '{}\n\r') or '/*' in sel:
        return False
    low = sel.lower()
    return not any(x in low for x in EXT_PSEUDO)


def top_level_split(sel, ch=','):
    parts, depth, cur, quote = [], 0, [], None
    for c in sel:
        if quote:
            cur.append(c)
            if c == quote:
                quote = None
            continue
        if c in '"\'':
            quote = c
        elif c in '([':
            depth += 1
        elif c in ')]':
            depth -= 1
        if c == ch and depth == 0:
            parts.append(''.join(cur).strip())
            cur = []
        else:
            cur.append(c)
    parts.append(''.join(cur).strip())
    return [p for p in parts if p]


KEY = re.compile(r'([.#])(-?[_a-zA-Z][_a-zA-Z0-9-]*)')


def selector_key(sel):
    """First top-level .class or #id of a selector: an element with it must exist for the
    selector to match, so the app adds the rule only when the page has that class / id."""
    depth, quote, i = 0, None, 0
    while i < len(sel):
        c = sel[i]
        if quote:
            if c == quote:
                quote = None
        elif c in '"\'':
            quote = c
        elif c in '([':
            depth += 1
        elif c in ')]':
            depth -= 1
        elif depth == 0 and c in '.#':
            m = KEY.match(sel, i)
            if m and (i + len(m.group(0)) == len(sel) or sel[i + len(m.group(0))] not in '\\('):
                return m.group(1) + m.group(2)
        i += 1
    return ''


def regex_literal(rx):
    """Longest plain-text run every match of a regex must contain ('' when unsure)."""
    depth, i, runs, cur = 0, 0, [], []
    while i < len(rx):
        c = rx[i]
        if c == '\\' and i + 1 < len(rx):
            n = rx[i + 1]
            if not n.isalnum():
                if depth == 0:
                    cur.append(n)
                i += 2
                nxt = rx[i] if i < len(rx) else ''
                if nxt and nxt in '?*{' and cur:
                    cur.pop()
                continue
            runs.append(''.join(cur))
            cur = []
            i += 2
            continue
        if c == '|' and depth == 0:
            return ''          # top-level alternation: no common text
        if c in '([':
            # skip groups / classes entirely
            runs.append(''.join(cur))
            cur = []
            close = ')' if c == '(' else ']'
            lvl, i = 1, i + 1
            while i < len(rx) and lvl:
                if rx[i] == '\\':
                    i += 2
                    continue
                if rx[i] == c and c == '(':
                    lvl += 1
                elif rx[i] == close:
                    lvl -= 1
                i += 1
            if i < len(rx) and rx[i] in '?*{':
                i += 1
            continue
        if c.isalnum() or c in '-_/=&%:;,@~':
            cur.append(c)
            i += 1
            nxt = rx[i] if i < len(rx) else ''
            if nxt and nxt in '?*{':
                cur.pop()
            continue
        runs.append(''.join(cur))
        cur = []
        i += 1
    runs.append(''.join(cur))
    best = max(runs, key=len) if runs else ''
    return best.lower() if len(best) >= 3 and '\t' not in best else ''


def literal(r):
    """Longest plain-text piece of the pattern: the app checks url.contains(piece) before the regex."""
    if r.flags & F_REGEX:
        return regex_literal(r.regex)
    if r.flags & F_MATCHCASE or not r.regex:
        return ''
    p = r.pattern
    p = p[2:] if p.startswith('||') else p.lstrip('|')
    pieces = re.split(r'[*^|]', p.lower())
    best = max(pieces, key=len) if pieces else ''
    return best if len(best) >= 3 and '\t' not in best else ''


# ---------------------------------------------------------------- scriptlets + redirects
# AdGuard Scriptlets (npm @adguard/scriptlets, GPL-3.0): corelibs scriptlet functions and
# redirect resources. Downloaded once into tools/sl_pkg (version pinned below).
SL_VERSION = '2.6.0'
SL_URL = 'https://registry.npmjs.org/@adguard/scriptlets/-/scriptlets-%s.tgz' % SL_VERSION
SL_DIR = os.path.join(ROOT, 'tools', 'sl_pkg', 'package', 'dist')


def load_sl_package():
    if not os.path.isfile(os.path.join(SL_DIR, 'scriptlets.corelibs.json')):
        import io, tarfile
        data = urllib.request.urlopen(urllib.request.Request(SL_URL, headers={'User-Agent': 'build'}),
                                      timeout=120).read()
        with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as t:
            for m in t.getmembers():
                if m.name.startswith('package/dist/') and '..' not in m.name and (m.isfile() or m.isdir()):
                    t.extract(m, os.path.join(ROOT, 'tools', 'sl_pkg'))
    lib = json.load(open(os.path.join(SL_DIR, 'scriptlets.corelibs.json'), encoding='utf-8'))
    red = json.load(open(os.path.join(SL_DIR, 'redirects.json'), encoding='utf-8'))
    return lib, red


SL_LIB, SL_REDIRECTS = load_sl_package()
SL_ALIAS = {}       # any scriptlet name / uBO alias -> canonical name
SL_CODE = {}        # canonical name -> function source
for _s in SL_LIB['scriptlets']:
    for _n in _s['names']:
        SL_ALIAS.setdefault(_n, _s['names'][0])
    SL_CODE[_s['names'][0]] = _s['scriptlet']
SL_USED = set()
REDIRECT_ALIAS = {}
for _r in SL_REDIRECTS:
    for _n in [_r['title']] + list(_r.get('aliases') or []):
        REDIRECT_ALIAS[_n] = _r['title']


def parse_ag_scriptlet(body):
    m = re.match(r'^//scriptlet\((.*)\)\s*;?\s*$', body)
    if not m:
        return None
    inner, args, i = m.group(1), [], 0
    while i < len(inner):
        c = inner[i]
        if c in ' ,':
            i += 1
            continue
        if c not in '\'"':
            return None
        j, buf = i + 1, []
        while j < len(inner) and inner[j] != c:
            if inner[j] == '\\' and j + 1 < len(inner) and inner[j + 1] == c:
                buf.append(c)
                j += 2
                continue
            buf.append(inner[j])
            j += 1
        if j >= len(inner):
            return None
        args.append(''.join(buf))
        i = j + 1
    return args or None


def parse_ubo_scriptlet(body):
    inner = body[4:-1]
    args, cur, i = [], [], 0
    while i < len(inner):
        c = inner[i]
        if c == '\\' and i + 1 < len(inner) and inner[i + 1] == ',':
            cur.append(',')
            i += 2
            continue
        if c == ',':
            args.append(''.join(cur).strip())
            cur = []
        else:
            cur.append(c)
        i += 1
    args.append(''.join(cur).strip())
    args = [a[1:-1] if len(a) >= 2 and a[0] == a[-1] and a[0] in '\'"`' else a for a in args]
    if not args or not args[0]:
        return None
    name = args[0]
    for cand in (name, name + '.js', 'ubo-' + name, 'ubo-' + name + '.js'):
        if cand in SL_ALIAS:
            return [cand] + args[1:]
    return [name] + args[1:]


def write_scriptlet_lib():
    """filters/scriptlets.js: an object literal {name: function(source, args){...}} with only the
    scriptlets the rules use; Blocker puts it into the document-start script."""
    parts = ['%s:%s' % (json.dumps(n), SL_CODE[n]) for n in sorted(SL_USED)]
    with open(os.path.join(OUT, 'scriptlets.js'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('{' + ',\n'.join(parts) + '}')
    stats['scriptlets in library'] = len(parts)


def write_raw_js(js_ids):
    """filters/rawjs.js: {id: function(){...}} with the AdGuard #%# JavaScript rules that parse."""
    import subprocess, tempfile
    items = sorted(js_ids.items(), key=lambda x: x[1])
    ok = set()
    try:
        tmp = os.path.join(tempfile.gettempdir(), 'gb_rawjs_check.json')
        json.dump([c for c, _ in items], open(tmp, 'w', encoding='utf-8'))
        chk = "const a=JSON.parse(require('fs').readFileSync(process.argv[1],'utf8'));" \
              "const ok=[];a.forEach((c,i)=>{try{new Function(c);ok.push(i)}catch(e){}});" \
              "process.stdout.write(JSON.stringify(ok))"
        res = subprocess.run(['node', '-e', chk, tmp], capture_output=True, timeout=120)
        ok = set(json.loads(res.stdout.decode() or '[]'))
        os.remove(tmp)
    except Exception as e:
        print('  rawjs check skipped (node missing?):', e)
    parts = ['%d:function(){%s\n}' % (i, c) for c, i in items if i in ok]
    stats['js rules usable'] = len(parts)
    with open(os.path.join(OUT, 'rawjs.js'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('{' + ',\n'.join(parts) + '}')


def write_redirects():
    """filters/redirects.txt: title<TAB>content type<TAB>base64 body."""
    import base64
    with open(os.path.join(OUT, 'redirects.txt'), 'w', encoding='utf-8', newline='\n') as f:
        for r in SL_REDIRECTS:
            ct = r.get('contentType', 'text/plain')
            body = r.get('content', '')
            if ';base64' in ct:
                ct = ct.replace(';base64', '')
                b64 = ''.join(body.split())
            else:
                b64 = base64.b64encode(body.encode('utf-8')).decode('ascii')
            f.write('%s\t%s\t%s\n' % (r['title'], ct, b64))


def meta(flags, types):
    """Host table entry: bits 0-11 types, 12-16 flags (the source lists are in their own array)."""
    return (types & 0xFFF) | ((flags & 0x1F) << 12)


def addm(d, key, bit):
    d[key] = d.get(key, 0) | bit


def groups_of(mask):
    """The groups (0 ads, 1 annoyances, 2 trackers) of the lists in mask, lowest first."""
    return sorted({LISTS[i][1] for i in range(len(LISTS)) if mask >> i & 1})


def idna(rule):
    if rule.isascii():
        return rule
    pre = ''.join(c for c in rule[:2] if c in '!*.')
    try:
        return pre + rule[len(pre):].encode('idna').decode('ascii')
    except Exception:
        return ''


def main():
    os.makedirs(OUT, exist_ok=True)
    assert len(LISTS) <= 32, 'list masks are 32 bits'
    host_block = {}                      # host -> lists
    host_exc = {}
    nets = []
    badfilters = set()
    generic = {}                         # selector -> lists (keyed later)
    params = []                          # removeparam rules
    resp = []                            # response-rewriting rules
    ext_css = defaultdict(dict)          # domain or '*' -> {extended CSS rule: lists}
    raw_js = defaultdict(dict)           # domain -> {AdGuard #%# JavaScript: lists}
    raw_js_exc = defaultdict(set)
    generic_exc = set()                  # #@#sel (no domains): drop the generic selector
    specific = defaultdict(dict)         # domain -> {selector: lists}
    specific_exc = defaultdict(set)      # domain -> selectors (also for generic ones)
    css_inject = defaultdict(dict)       # domain -> {'selector { style }': lists}
    scriptlets = defaultdict(dict)       # domain or '*' -> {JSON call: lists}
    sl_exc = defaultdict(set)            # domain or '*' -> JSON calls switched off there
    info = []
    extra_path = os.path.join(ROOT, 'tools', 'extra_rules.txt')
    for li, (name, group, url, title) in enumerate(LISTS):
        bit = 1 << li
        text = open(extra_path, encoding='utf-8').read() if name == 'extra' else fetch(name, url)
        n0 = len(nets)
        own_hosts = 0   # host rules of this list (also those another list has too)
        version = ''
        m = re.search(r'^! (?:Version|Last modified|TimeUpdated):\s*(.+)$', text, re.M)
        if m:
            version = m.group(1).strip()
        for line in text.splitlines():
            line = line.strip()
            if not line or line[0] in '![' or line.startswith('# ') or line == '#':
                continue
            if name in HOSTS_FILES or name in DOMAIN_LISTS:
                # hosts-file / plain-domain lists: "0.0.0.0 host  # note" or "host"
                line = line.split('#', 1)[0].strip().lower()
                if not line:
                    continue
                if name in DOMAIN_LISTS:
                    line = '0.0.0.0 ' + line
                m = HOSTS_LINE.match(line)
                if m and m.group(1) not in NOT_HOSTS:
                    addm(host_block, m.group(1), bit)
                    own_hosts += 1
                else:
                    stats['skip hosts line'] += 1
                continue
            m = COSMETIC.match(line)
            if m:
                doms, sep, sel = m.groups()
                if '[$' in doms or doms.startswith('[$'):
                    stats['skip cosmetic [$..]'] += 1
                    continue
                # scriptlets: AdGuard "#%#//scriptlet('name', 'arg')", uBO "##+js(name, arg)"
                call = None
                if sep in ('#%#', '#@%#') and sel.startswith('//scriptlet('):
                    call = parse_ag_scriptlet(sel)
                elif sep in ('##', '#@#') and sel.startswith('+js(') and sel.endswith(')'):
                    call = parse_ubo_scriptlet(sel)
                if call is not None or sel.startswith('//scriptlet(') or sel.startswith('+js('):
                    if not call or call[0] not in SL_ALIAS:
                        stats['skip scriptlet (unknown/invalid)'] += 1
                        continue
                    call = [SL_ALIAS[call[0]]] + call[1:]
                    pos, neg = parse_domains(doms, ',')
                    text = json.dumps(call, separators=(',', ':'), ensure_ascii=False)
                    if '\n' in text or '\x1f' in text or '\t' in text:
                        stats['skip scriptlet (unknown/invalid)'] += 1
                        continue
                    if '@' in sep:
                        for d in pos or ['*']:
                            sl_exc[d].add(text)
                    else:
                        for d in pos or ['*']:
                            addm(scriptlets[d], text, bit)
                        for d in neg:
                            sl_exc[d].add(text)
                        SL_USED.add(call[0])
                    stats['scriptlet rules'] += 1
                    continue
                if sel.startswith('^') or sel.startswith('+'):
                    stats['skip uBO html filter'] += 1
                    continue
                if sep in ('#%#', '#@%#'):
                    pos, neg = parse_domains(doms, ',')
                    if not pos or '\n' in sel:
                        stats['skip #%# generic js'] += 1
                        continue
                    for d in pos:
                        if '@' in sep:
                            raw_js_exc[d].add(sel)
                        else:
                            addm(raw_js[d], sel, bit)
                    stats['js rules'] += 1
                    continue
                if sep in ('$$', '$@$'):
                    stats['skip ' + sep] += 1
                    continue
                exc = '@' in sep
                is_css = '$' in sep
                if '?' in sep or sel.lower().find(':-abp-has(') >= 0:
                    sel = re.sub(r':-abp-has\(', ':has(', sel, flags=re.I)
                if not css_ok(sel if not is_css else sel.split('{')[0]):
                    # extended CSS (:has-text, :upward, :remove ...): run by AdGuard ExtendedCss in the page
                    if exc or any(c in sel for c in '\n\r') or '/*' in sel:
                        stats['skip cosmetic (extended css exception)'] += 1
                        continue
                    pos, neg = parse_domains(doms, ',')
                    if is_css:
                        rule = sel.strip()
                    else:
                        rule = sel.strip() + ' { display: none !important; }'
                    if '\x1f' in rule:
                        continue
                    for d in pos or ['*']:
                        addm(ext_css[d], rule, bit)
                    stats['extended css rules'] += 1
                    continue
                pos, neg = parse_domains(doms, ',')
                if is_css:
                    mm = re.match(r'^(.+?)\s*\{([^{}]*)\}\s*$', sel)
                    if exc or not mm or 'remove:' in mm.group(2).replace(' ', '') or not pos:
                        stats['skip #$# css'] += 1
                        continue
                    rule = '%s{%s}' % (mm.group(1).strip(), mm.group(2).strip())
                    for d in pos:
                        addm(css_inject[d], rule, bit)
                    stats['css injection'] += 1
                    continue
                if exc:
                    if pos:
                        for d in pos:
                            specific_exc[d].add(sel)
                    else:
                        generic_exc.add(sel)
                    stats['cosmetic exception'] += 1
                    continue
                if pos:
                    for d in pos:
                        addm(specific[d], sel, bit)
                    stats['cosmetic specific'] += 1
                else:
                    addm(generic, sel, bit)
                    for d in neg:
                        specific_exc[d].add(sel)
                    stats['cosmetic generic'] += 1
                continue
            if '##' in line or '#?#' in line or '#@#' in line:
                stats['skip odd cosmetic'] += 1
                continue
            m = HOSTS_LINE.match(line.lower())
            if m:
                addm(host_block, m.group(1), bit)
                own_hosts += 1
                continue
            m = HOST_RULE.match(line)
            if m:
                addm(host_block, m.group(1), bit)
                own_hosts += 1
                continue
            if line.startswith('@@'):
                m = HOST_RULE.match(line[2:])
                if m:
                    # The DNS filter's "@@||host^|" entries keep hosts reachable for DNS users
                    # (app links, CMPs); merged here they would cancel every other list's block.
                    if name not in DNS_LISTS:
                        addm(host_exc, m.group(1), bit)
                    else:
                        stats['skip DNS-list host exception'] += 1
                    continue
            r = parse_network(line, bit)
            if r is None:
                continue
            if isinstance(r, tuple):
                if r[0] == 'resp':
                    rr, is_rx = r[1], r[2]
                    pat, (kind, val) = rr.pattern, rr.resp
                    rx = '' if pat in ('', '*', '|', '||') else (pat[1:-1] if is_rx else to_regex(pat, False))
                    if '\t' in val or '\n' in val:
                        continue
                    resp.append('%d\t%d\t%d\t%s\t%s\t%s\t%s' % (bit, rr.flags & (F_EXC | F_IMPORTANT | F_THIRD | F_FIRST),
                                rr.types, rx, rr.domains, kind, val))
                    stats['response rules'] += 1
                    continue
                if r[0] == 'removeparam':
                    rr, is_rx = r[1], r[2]
                    pat = rr.pattern
                    rx = '' if pat in ('', '*', '|', '||') else (pat[1:-1] if is_rx else to_regex(pat, False))
                    params.append('%d\t%d\t%s\t%s\t%s' % (bit, rr.flags & F_EXC, rx, rr.domains, rr.removeparam))
                    stats['removeparam rules'] += 1
                    continue
                badfilters.add(r[1])
                continue
            r.raw = line
            nets.append(r)
        info.append('%s\t%s\t%d\t%d\t%s' % (title, version, len(nets) - n0 + own_hosts, group, name))
        print('%-28s group %d  %s' % (title, group, version))

    # badfilter
    if badfilters:
        before = len(nets)
        nets = [r for r in nets if r.raw not in badfilters]
        for b in badfilters:
            m = HOST_RULE.match(b)
            if m:
                host_block.pop(m.group(1), None)
        stats['removed by $badfilter'] += before - len(nets)

    # page-level rules apart; host-shaped rules ("||host^$opts" without $domain) into the host
    # table; tokens for the rest
    page_rules = [r for r in nets if r.page]
    nets = [r for r in nets if not r.page]
    host_table = {}      # (hash, meta) -> lists
    rest = []
    for r in nets:
        # bare "host.tld$image" rules (hundreds of thousands in Tracking Protection) are host rules too
        m = HOST_RULE.match(r.pattern)
        if not m:
            m = BARE_HOST.match(r.pattern)
            if m and m.group(1).rsplit('.', 1)[1] in FILE_EXT:
                m = None
        if (m and not r.domains and not r.denyallow and not (r.flags & (F_MATCHCASE | F_REGEX))
                and not r.redirect and not (r.types & T['popup'])):
            addm(host_table, (fnv64(m.group(1)), meta(r.flags, r.types)), r.group)
        else:
            rest.append(r)
    nets = rest
    for h, mask in host_block.items():
        addm(host_table, (fnv64(h), meta(0, T_ALL)), mask)
    for h, mask in host_exc.items():
        addm(host_table, (fnv64(h), meta(F_EXC, T_ALL)), mask)
    ht = sorted(host_table.items())
    # count, hashes (sorted), metas, list masks (unsigned)
    with open(os.path.join(OUT, 'hosts.bin'), 'wb') as f:
        f.write(struct.pack('>i', len(ht)))
        f.write(struct.pack('>%dq' % len(ht), *[k[0] for k, _ in ht]))
        f.write(struct.pack('>%di' % len(ht), *[k[1] for k, _ in ht]))
        f.write(struct.pack('>%dI' % len(ht), *[v for _, v in ht]))
    stats['host table entries'] = len(ht)
    for fn in ('hosts0.bin', 'hosts1.bin', 'hostexc.bin'):
        if os.path.exists(os.path.join(OUT, fn)):
            os.remove(os.path.join(OUT, fn))
    freq = defaultdict(int)
    toks_of = []
    for r in nets:
        t = [] if r.flags & F_REGEX else candidate_tokens(r.pattern)
        toks_of.append(t)
        for x in set(t):
            freq[x] += 1
    seen = {}
    rows = []
    untok = 0
    for r, toks in zip(nets, toks_of):
        good = [t for t in toks if t not in BAD_TOKENS] or toks
        tok = min(good, key=lambda t: (freq[t], -len(t))) if good else ''
        if not tok:
            untok += 1
        key = (r.flags, r.types, r.regex, r.domains, r.denyallow, r.redirect)
        if key in seen:
            rows[seen[key]][0] |= r.group
            continue
        seen[key] = len(rows)
        rows.append([r.group, '%d\t%d\t%s\t%s\t%s\t%s\t%s\t%s' % (r.flags, r.types,
                     jhash(tok) if tok else '', r.regex, r.domains, r.denyallow, literal(r), r.redirect)])
    lines = ['%d\t%s' % (m, t) for m, t in rows]
    stats['network rules'] = len(lines)
    stats['network rules without token'] = untok
    with open(os.path.join(OUT, 'net.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    with open(os.path.join(OUT, 'page.txt'), 'w', encoding='utf-8', newline='\n') as f:
        for r in page_rules:
            f.write('%d\t%d\t%s\t%s\n' % (r.page, r.flags, r.regex, r.domains))
    stats['page rules'] = len(page_rules)

    # cosmetic
    out = []
    keyed = unkeyed = 0
    for sel in sorted(generic):
        if sel in generic_exc:
            continue
        for part in top_level_split(sel):
            k = selector_key(part)
            if k:
                out.append('k\t%d\t%s\t%s' % (generic[sel], k, part))
                keyed += 1
            else:
                out.append('u\t%d\t%s' % (generic[sel], part))
                unkeyed += 1

    def by_lists(items, drop=()):
        # one line per (domain, list mask)
        inv = defaultdict(list)
        for x, mask in items.items():
            if x not in drop:
                inv[mask].append(x)
        return sorted((m, sorted(v)) for m, v in inv.items())

    for d in sorted(specific):
        for mask, sels in by_lists(specific[d], specific_exc.get(d, ())):
            out.append('s\t%d\t%s\t%s' % (mask, d, '\x1f'.join(sels)))
    for d, sels in sorted(specific_exc.items()):
        out.append('x\t0\t%s\t%s' % (d, '\x1f'.join(sorted(sels))))
    for d in sorted(css_inject):
        for mask, rules in by_lists(css_inject[d]):
            out.append('c\t%d\t%s\t%s' % (mask, d, '\x1f'.join(rules)))
    for d in sorted(scriptlets):
        for mask, calls in by_lists(scriptlets[d], sl_exc.get(d, ())):
            out.append('j\t%d\t%s\t%s' % (mask, d, '\x1f'.join(calls)))
    for d, calls in sorted(sl_exc.items()):
        out.append('y\t0\t%s\t%s' % (d, '\x1f'.join(sorted(calls))))
    for d in sorted(ext_css):
        for mask, rules in by_lists(ext_css[d]):
            out.append('e\t%d\t%s\t%s' % (mask, d, '\x1f'.join(rules)))
    js_ids = {}
    # ids in the order the group-based builds gave them (domain, then group, then code)
    for d in sorted(raw_js):
        for g in (0, 1, 2):
            for code in sorted(c for c, mask in raw_js[d].items() if g in groups_of(mask)):
                if code not in raw_js_exc.get(d, ()) and code not in js_ids:
                    js_ids[code] = len(js_ids)
    for d in sorted(raw_js):
        for mask, codes in by_lists(raw_js[d], raw_js_exc.get(d, ())):
            ids = [str(js_ids[c]) for c in codes]
            if ids:
                out.append('r\t%d\t%s\t%s' % (mask, d, '\x1f'.join(ids)))
    write_raw_js(js_ids)
    with open(os.path.join(OUT, 'resp.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(resp) + '\n')
    with open(os.path.join(OUT, 'params.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(params) + '\n')
    stats['scriptlet domains'] = len(scriptlets)
    write_scriptlet_lib()
    write_redirects()
    with open(os.path.join(OUT, 'cosm.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(out) + '\n')
    stats['generic keyed'] = keyed
    stats['generic unkeyed'] = unkeyed
    stats['specific domains'] = len(specific)

    # public suffix list
    psl = fetch('psl.dat', PSL_URL)
    rules = [l.strip() for l in psl.splitlines() if l.strip() and not l.startswith('//')]
    with open(os.path.join(OUT, 'psl.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(idna(r) for r in rules if idna(r)) + '\n')

    with open(os.path.join(OUT, 'info.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('built\t%s\n' % time.strftime('%Y-%m-%d'))
        f.write('\n'.join(info) + '\n')
    for k in sorted(stats):
        print('  %-40s %d' % (k, stats[k]))
    for fn in sorted(os.listdir(OUT)):
        print('  %-12s %8d KB' % (fn, os.path.getsize(os.path.join(OUT, fn)) // 1024))


if __name__ == '__main__':
    main()
