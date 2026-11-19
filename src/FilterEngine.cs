using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GBrowser
{
    /// <summary>Ad-block filter engine: a C# port of the WebView Adblocker Android app's FilterEngine.java. It reads the
    /// same compiled files (filters/*, written by tools/build_filters.py): host hash table, token-indexed URL rules,
    /// page exceptions, cosmetic rules, scriptlets, $redirect stubs, $removeparam/$urlskip and response rules.</summary>
    internal sealed class FilterEngine
    {
        // same values as tools/build_filters.py
        public const int F_EXC = 1, F_IMPORTANT = 2, F_THIRD = 4, F_FIRST = 8, F_MATCHCASE = 16, F_REGEX = 32;
        public const int G_ADS = 1, G_ANNOY = 2, G_TRACK = 4;
        public const int T_SCRIPT = 1, T_IMAGE = 2, T_CSS = 4, T_XHR = 8, T_MEDIA = 16, T_FONT = 32, T_SUBDOC = 64,
            T_WEBSOCKET = 128, T_OTHER = 256, T_OBJECT = 512, T_PING = 1024, T_DOCUMENT = 2048, T_POPUP = 4096;
        public const int T_UNKNOWN = T_SCRIPT | T_XHR | T_OTHER | T_PING;
        const int PAGE_URLBLOCK = 1, PAGE_ELEMHIDE = 2, PAGE_GENERICHIDE = 4, PAGE_SPECIFICHIDE = 8, PAGE_JSINJECT = 16;
        const string HIDE = "{display:none!important}";
        static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
        static readonly Regex Never = new Regex("(?!)");

        // ---------------------------------------------------------------- loading
        static Task<FilterEngine> loading;
        public static FilterEngine Instance { get; private set; }
        public static string Folder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "filters");

        /// <summary>Starts loading on a background thread (once).</summary>
        public static Task<FilterEngine> Preload()
        {
            if (loading == null)
                loading = Task.Run(() =>
                {
                    try { Instance = new FilterEngine(Folder); }
                    catch (Exception ex) { LoadError = ex.Message; }
                    return Instance;
                });
            return loading;
        }

        public static string LoadError;
        public static long LoadMs;

        readonly PublicSuffix psl = new PublicSuffix();
        // Big files stay on disk, memory-mapped (MappedFile); only small index arrays live on the heap.
        // hosts.bin: sorted hashes + meta, searched in place.
        MappedFile hosts; int hostCount;
        // net.txt: one line per rule; Rule objects are parsed on first use (most rules are never touched).
        MappedFile net; int[] ruleOff, ruleLen, ruleMeta; Rule[] ruleCache; int[] untokenized;
        int[] tokKeys, tokStart, tokIds;   // sorted URL tokens -> rule ids tokIds[tokStart[k] .. tokStart[k+1])
        PageRule[] pageRules;
        // "groups" in the API below = the bit mask of enabled lists (bit i = the i-th list of info.txt); every rule
        // carries the mask of the lists it comes from and applies when the two share a bit
        struct Sel { public string S; public int Lists; }
        readonly Dictionary<string, Sel[]> keyed = new Dictionary<string, Sel[]>(16384, StringComparer.Ordinal);
        Sel[] unkeyed = new Sel[0];
        readonly Dictionary<int, string> unkeyedCss = new Dictionary<int, string>();
        MappedFile cosm; long[] specHash; int[] specOff, specLen, specMask; byte[] specKind;
        int[] ruleMask;
        ParamRule[] paramRules = new ParamRule[0];
        RespRule[] respRules = new RespRule[0];
        readonly Dictionary<string, Resource> resources = new Dictionary<string, Resource>();
        int cosmeticCount;
        public readonly int RuleCount;
        public readonly string ListInfo;
        public readonly int[] ListGroups;      // per list (info.txt order = bit order): 0 ads, 1 annoyances, 2 trackers
        public readonly string[] ListFiles;    // per list: its file name (the id kept in settings)

        /// <summary>The lists that apply: those of the switched-on groups (G_ADS / G_ANNOY / G_TRACK) not turned off one by one.</summary>
        public int Mask(int groups, ICollection<string> off)
        {
            if (ListGroups.Length == 0) return -1;   // lists without group / file columns: all of them
            int m = 0;
            for (int i = 0; i < ListGroups.Length && i < 32; i++)
                if ((groups & (1 << ListGroups[i])) != 0 && (off == null || off.Count == 0 || !off.Contains(ListFiles[i]))) m |= 1 << i;
            return m;
        }
        public readonly string Scriptlets, RawJs;

        FilterEngine(string dir)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LoadPsl(File.ReadAllBytes(Path.Combine(dir, "psl.txt")));
            LoadHosts(new MappedFile(Path.Combine(dir, "hosts.bin")));
            LoadRules(new MappedFile(Path.Combine(dir, "net.txt")));
            LoadPageRules(File.ReadAllText(Path.Combine(dir, "page.txt"), Encoding.UTF8));
            LoadCosmetic(new MappedFile(Path.Combine(dir, "cosm.txt")));
            LoadRedirects(File.ReadAllText(Path.Combine(dir, "redirects.txt"), Encoding.UTF8));
            LoadParams(File.ReadAllText(Path.Combine(dir, "params.txt"), Encoding.UTF8));
            LoadResp(File.ReadAllText(Path.Combine(dir, "resp.txt"), Encoding.UTF8));
            ListInfo = File.ReadAllText(Path.Combine(dir, "info.txt"), Encoding.UTF8);
            var lg = new List<int>(); var lf = new List<string>();
            foreach (var l in ListInfo.Split('\n'))
            {
                var f = l.TrimEnd('\r').Split('\t');
                if (f.Length >= 5 && f[0] != "built") { lg.Add(int.TryParse(f[3], out var g) ? g : 0); lf.Add(f[4]); }
            }
            ListGroups = lg.ToArray(); ListFiles = lf.ToArray();
            Scriptlets = File.ReadAllText(Path.Combine(dir, "scriptlets.js"), Encoding.UTF8);
            RawJs = File.ReadAllText(Path.Combine(dir, "rawjs.js"), Encoding.UTF8);
            RuleCount = hostCount + ruleOff.Length + pageRules.Length + cosmeticCount;
            // give back the memory used while parsing (temporary strings and lists) instead of keeping it as heap reserve
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            LoadMs = sw.ElapsedMilliseconds;
        }

        static string[] Split(string line, int n)
        {
            var o = new string[n];
            int s = 0;
            for (int k = 0; k < n; k++)
            {
                int t = k == n - 1 ? -1 : line.IndexOf('\t', s);
                if (t < 0)
                {
                    o[k] = s <= line.Length ? line.Substring(s) : "";
                    for (int j = k + 1; j < n; j++) o[j] = "";
                    return o;
                }
                o[k] = line.Substring(s, t - s);
                s = t + 1;
            }
            return o;
        }

        static IEnumerable<string> Lines(byte[] data)
        {
            int s = 0;
            for (int i = 0; i <= data.Length; i++)
            {
                if (i != data.Length && data[i] != '\n') continue;
                if (i > s) yield return Encoding.UTF8.GetString(data, s, i - s);
                s = i + 1;
            }
        }

        void LoadPsl(byte[] data) { foreach (var l in Lines(data)) psl.Add(l.Trim()); }

        // hosts.bin, big-endian: int count, long[count] hashes (sorted, signed), int[count] meta, uint[count] list masks
        void LoadHosts(MappedFile f) { hosts = f; hostCount = f.Length >= 4 ? f.BE32(0) : 0; }

        long HostHash(int i) => hosts.BE64(4 + 8L * i);
        int HostMeta(int i) => hosts.BE32(4 + 8L * hostCount + 4L * i);
        int HostLists(int i) => hosts.BE32(4 + 12L * hostCount + 4L * i);

        /// <summary>First index whose hash is h, or -1.</summary>
        int HostFirst(long h)
        {
            int lo = 0, hi = hostCount - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (int)((uint)(lo + hi) >> 1);
                long v = HostHash(mid);
                if (v < h) lo = mid + 1;
                else { if (v == h) found = mid; hi = mid - 1; }
            }
            return found;
        }

        /// <summary>Integer in bytes [s, e) (optional leading '-').</summary>
        static unsafe int ParseInt(byte* d, long s, long e)
        {
            bool neg = s < e && d[s] == '-';
            if (neg) s++;
            int v = 0;
            for (; s < e; s++) unchecked { v = v * 10 + (d[s] - '0'); }
            return neg ? -v : v;
        }

        // net.txt line: lists, flags, types, token, regex, domains, deny, literal, redirect (tab separated)
        unsafe void LoadRules(MappedFile f)
        {
            net = f;
            byte* d = f.P; long n = f.Length;
            var offs = new List<int>(48000); var lens = new List<int>(48000); var metas = new List<int>(48000); var masks = new List<int>(48000);
            var pairs = new List<long>(48000); var untok = new List<int>();
            var v = new int[3];
            long s = 0;
            for (long i = 0; i <= n; i++)
            {
                if (i != n && d[i] != '\n') continue;
                if (i > s)
                {
                    int id = offs.Count;
                    long p = s;
                    for (int k = 0; k < 3; k++)
                    {
                        long b = p;
                        while (p < i && d[p] != '\t') p++;
                        v[k] = ParseInt(d, b, p);
                        if (p < i) p++;
                    }
                    long t = p;
                    while (p < i && d[p] != '\t') p++;
                    if (p == t) untok.Add(id);
                    else pairs.Add(((long)ParseInt(d, t, p) << 32) | (uint)id);
                    offs.Add((int)s); lens.Add((int)(i - s));
                    metas.Add((v[1] << 2) | (v[2] << 8));   // flags 6 bits, types 13 bits
                    masks.Add(v[0]);                        // source lists (ParseInt wraps to the same 32 bits)
                }
                s = i + 1;
            }
            ruleOff = offs.ToArray(); ruleLen = lens.ToArray(); ruleMeta = metas.ToArray(); ruleMask = masks.ToArray();
            ruleCache = new Rule[ruleOff.Length];
            untokenized = untok.ToArray();
            // sorted by token, then id: rules of one token keep their file order (as before)
            var pa = pairs.ToArray();
            Array.Sort(pa);
            var keys = new List<int>(32768); var starts = new List<int>(32768);
            tokIds = new int[pa.Length];
            for (int i = 0; i < pa.Length; i++)
            {
                int tok = (int)(pa[i] >> 32);
                if (keys.Count == 0 || keys[keys.Count - 1] != tok) { keys.Add(tok); starts.Add(i); }
                tokIds[i] = (int)pa[i];
            }
            starts.Add(pa.Length);
            tokKeys = keys.ToArray(); tokStart = starts.ToArray();
        }

        /// <summary>The rule object for id, parsed from net.txt on first use.</summary>
        Rule GetRule(int id)
        {
            var r = ruleCache[id];
            if (r != null) return r;
            var f = Split(net.Str(ruleOff[id], ruleLen[id]), 9);
            r = new Rule(unchecked((int)uint.Parse(f[0])), int.Parse(f[1]), int.Parse(f[2]), f[4], f[5], f[6], f[7], f[8]);
            ruleCache[id] = r;   // a race only parses the same line twice
            return r;
        }

        /// <summary>Cheap checks from the packed meta before a rule is parsed (Rule.Matches repeats them).</summary>
        bool MetaOk(int id, int type, int party, int groups)
        {
            int m = ruleMeta[id];
            return (groups & ruleMask[id]) != 0 && ((m >> 8) & type) != 0 && PartyOk((m >> 2) & 63, party);
        }

        void LoadPageRules(string text)
        {
            var list = new List<PageRule>();
            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                var f = Split(line, 4);
                list.Add(new PageRule(int.Parse(f[0]), int.Parse(f[1]), f[2], f[3]));
            }
            pageRules = list.ToArray();
        }

        unsafe void LoadCosmetic(MappedFile f)
        {
            cosm = f;
            byte* data = f.P;
            int dataLength = (int)f.Length;
            var unk = new List<Sel>();
            var key = new Dictionary<string, List<Sel>>(StringComparer.Ordinal);
            var hs = new List<long>(1 << 17); var offs = new List<int>(1 << 17); var lens = new List<int>(1 << 17);
            var kinds = new List<byte>(1 << 17); var grps = new List<int>(1 << 17);
            int s = 0;
            for (int i = 0; i <= dataLength; i++)
            {
                if (i != dataLength && data[i] != '\n') continue;
                if (i - s > 4)
                {
                    byte kind = data[s];
                    int a = s + 2;
                    while (a < i && data[a] != '\t') a++;
                    int g = ParseInt(data, s + 2, a);   // source lists
                    if (kind == 'x' || kind == 'y') g = -1;   // exceptions (cosmetic, scriptlet) apply whatever lists are on
                    a++;
                    if (kind == 'u') { unk.Add(new Sel { S = f.Str(a, i - a), Lists = g }); cosmeticCount++; }
                    else
                    {
                        int tab = a;
                        while (tab < i && data[tab] != '\t') tab++;
                        string k = f.Str(a, tab - a);
                        if (kind == 'k')
                        {
                            if (!key.TryGetValue(k, out var l)) key[k] = l = new List<Sel>(2);
                            l.Add(new Sel { S = f.Str(tab + 1, i - tab - 1), Lists = g });
                        }
                        else
                        {
                            hs.Add(Fnv64(k)); offs.Add(tab + 1); lens.Add(i - tab - 1); kinds.Add(kind); grps.Add(g);
                        }
                        cosmeticCount++;
                    }
                }
                s = i + 1;
            }
            int n = hs.Count;
            var order = Enumerable.Range(0, n).ToArray();
            var hArr = hs.ToArray();
            Array.Sort(order, (x, y) => hArr[x].CompareTo(hArr[y]));
            specHash = new long[n]; specOff = new int[n]; specLen = new int[n]; specKind = new byte[n]; specMask = new int[n];
            for (int i = 0; i < n; i++)
            {
                int o = order[i];
                specHash[i] = hArr[o]; specOff[i] = offs[o]; specLen[i] = lens[o]; specKind[i] = kinds[o]; specMask[i] = grps[o];
            }
            unkeyed = unk.ToArray();
            foreach (var kv in key) keyed[kv.Key] = kv.Value.ToArray();
        }

        void LoadRedirects(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                var f = line.Split('\t');
                if (f.Length < 3) continue;
                try { resources[f[0]] = new Resource(f[1], Convert.FromBase64String(f[2].Trim())); } catch (FormatException) { }
            }
        }

        void LoadParams(string text)
        {
            var list = new List<ParamRule>();
            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                var f = Split(line, 5);
                try { list.Add(new ParamRule(unchecked((int)uint.Parse(f[0])), f[1] == "1", f[2], f[3], f[4])); } catch { }
            }
            paramRules = list.ToArray();
        }

        void LoadResp(string text)
        {
            var list = new List<RespRule>();
            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                var f = Split(line, 7);
                try { list.Add(new RespRule(unchecked((int)uint.Parse(f[0])), int.Parse(f[1]), int.Parse(f[2]), f[3], f[4], f[5], f[6])); } catch { }
            }
            respRules = list.ToArray();
        }

        // ---------------------------------------------------------------- hashing (same as build_filters.py)
        public static long Fnv64(string s)
        {
            unchecked
            {
                ulong h = 0xcbf29ce484222325UL;
                foreach (char c in s)
                {
                    if (c < 0x80) { h ^= c; h *= 0x100000001b3UL; }
                    else foreach (byte b in Encoding.UTF8.GetBytes(c.ToString())) { h ^= b; h *= 0x100000001b3UL; }
                }
                return (long)h;
            }
        }

        // ---------------------------------------------------------------- network matching
        sealed class State
        {
            public bool Block, ImpBlock, Exc, ImpExc;
            public string Redirect, RedirectRule;

            public void Add(Rule r)
            {
                bool imp = (r.Flags & F_IMPORTANT) != 0;
                if ((r.Flags & F_EXC) != 0) { if (imp) ImpExc = true; else Exc = true; return; }
                if (r.IsRedirectRule) { if (RedirectRule == null) RedirectRule = r.Redirect; return; }
                if (imp) ImpBlock = true; else Block = true;
                if (r.Redirect.Length > 0 && Redirect == null) Redirect = r.Redirect;
            }

            public bool Useless(Rule r)
            {
                bool imp = (r.Flags & F_IMPORTANT) != 0;
                if ((r.Flags & F_EXC) != 0) return ImpExc || (Exc && !imp);
                if (r.IsRedirectRule) return RedirectRule != null;
                if (r.Redirect.Length > 0 && Redirect == null) return false;
                return ImpBlock || (Block && !imp);
            }
        }

        /// <summary>null = allow, "" = block, otherwise the name of a redirect resource to serve.</summary>
        public string Match(string url, string host, string pageHost, int type, int groups)
        {
            if (string.IsNullOrEmpty(host)) return null;
            int party = 0;
            if (!string.IsNullOrEmpty(pageHost)) party = psl.Registrable(host) == psl.Registrable(pageHost) ? 1 : 2;
            var st = new State();
            int start = 0;
            while (true)
            {
                int dot = host.IndexOf('.', start);
                if (dot < 0) break;
                long hh = Fnv64(start == 0 ? host : host.Substring(start));
                int idx = HostFirst(hh);
                if (idx >= 0)
                {
                    for (int i = idx; i < hostCount && HostHash(i) == hh; i++)
                    {
                        int m = HostMeta(i);
                        int types = m & 0xFFF, flags = (m >> 12) & 0x1F;
                        if ((groups & HostLists(i)) == 0 || (types & type) == 0 || !PartyOk(flags, party)) continue;
                        if ((flags & F_EXC) != 0) { if ((flags & F_IMPORTANT) != 0) st.ImpExc = true; else st.Exc = true; }
                        else if ((flags & F_IMPORTANT) != 0) st.ImpBlock = true;
                        else st.Block = true;
                    }
                }
                start = dot + 1;
            }
            string lower = url.ToLowerInvariant();
            int len = Math.Min(lower.Length, 4096);
            int h = 0, tl = 0;
            for (int i = 0; i <= len; i++)
            {
                char c = i < len ? lower[i] : '/';
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '%') { unchecked { h = 31 * h + c; } tl++; continue; }
                int k;
                if (tl >= 2 && (k = Array.BinarySearch(tokKeys, h)) >= 0)
                    for (int j = tokStart[k], end = tokStart[k + 1]; j < end; j++)
                    {
                        int id = tokIds[j];
                        if (!MetaOk(id, type, party, groups)) continue;
                        var r = GetRule(id);
                        if (st.Useless(r)) continue;
                        if (r.Matches(this, url, lower, host, pageHost, type, party)) st.Add(r);
                    }
                h = 0; tl = 0;
            }
            foreach (int id in untokenized)
            {
                if (!MetaOk(id, type, party, groups)) continue;
                var r = GetRule(id);
                if (st.Useless(r)) continue;
                if (r.Matches(this, url, lower, host, pageHost, type, party)) st.Add(r);
            }
            bool blocked = st.ImpExc ? false : st.ImpBlock ? true : !st.Exc && st.Block;
            if (!blocked) return null;
            return st.Redirect ?? st.RedirectRule ?? "";
        }

        public bool ShouldBlock(string url, string host, string pageHost, int type, int groups) => Match(url, host, pageHost, type, groups) != null;

        static bool PartyOk(int flags, int party)
        {
            if ((flags & F_THIRD) != 0 && party == 1) return false;
            return (flags & F_FIRST) == 0 || party != 2;
        }

        bool DomainMatches(string host, string d)
        {
            if (host == null) return false;
            if (d.EndsWith(".*"))
            {
                string b = d.Substring(0, d.Length - 2);
                string suf = psl.Suffix(host);
                if (host.Length <= suf.Length + 1) return false;
                string rest = host.Substring(0, host.Length - suf.Length - 1);
                return rest == b || rest.EndsWith("." + b, StringComparison.Ordinal);
            }
            return host == d || (host.EndsWith(d, StringComparison.Ordinal) && host.Length > d.Length && host[host.Length - d.Length - 1] == '.');
        }

        bool DomainsOk(string[] pos, string[] neg, string host)
        {
            foreach (var d in neg) if (DomainMatches(host, d)) return false;
            if (pos.Length == 0) return true;
            foreach (var d in pos) if (DomainMatches(host, d)) return true;
            return false;
        }

        static readonly string[] None = new string[0];

        static string[][] ParseDomains(string s)
        {
            if (string.IsNullOrEmpty(s)) return new[] { None, None };
            var pos = new List<string>(); var neg = new List<string>();
            foreach (var d in s.Split('|'))
            {
                if (d.Length == 0) continue;
                if (d[0] == '~') neg.Add(d.Substring(1)); else pos.Add(d);
            }
            return new[] { pos.ToArray(), neg.ToArray() };
        }

        static Regex Compile(string pattern, bool ignoreCase)
        {
            try
            {
                return new Regex(pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : 0), RegexTimeout);
            }
            catch (ArgumentException) { return Never; }   // Java-only syntax: the rule never matches
        }

        static bool Find(Regex re, string input)
        {
            try { return re.IsMatch(input); } catch (RegexMatchTimeoutException) { return false; }
        }

        sealed class Rule
        {
            public readonly int Group, Flags, Types;
            readonly string regex, literal;
            public readonly string Redirect;
            public readonly bool IsRedirectRule;
            readonly string[] domPos, domNeg, deny;
            Regex compiled;

            public Rule(int group, int flags, int types, string regex, string domains, string deny, string literal, string redirect)
            {
                this.literal = literal;
                IsRedirectRule = redirect.StartsWith("?");
                Redirect = IsRedirectRule ? redirect.Substring(1) : redirect;
                Group = group; Flags = flags; Types = types; this.regex = regex;
                var d = ParseDomains(domains); domPos = d[0]; domNeg = d[1];
                this.deny = deny.Length == 0 ? None : deny.Split('|');
            }

            public bool Matches(FilterEngine e, string url, string lower, string host, string pageHost, int type, int party)
            {
                if ((Types & type) == 0 || !PartyOk(Flags, party)) return false;
                if (domPos.Length > 0 || domNeg.Length > 0)
                {
                    if (pageHost == null) { if (domPos.Length > 0) return false; }
                    else if (!e.DomainsOk(domPos, domNeg, pageHost)) return false;
                }
                foreach (var d in deny) if (e.DomainMatches(host, d)) return false;
                if (regex.Length == 0) return true;
                if (literal.Length > 0 && lower.IndexOf(literal, StringComparison.Ordinal) < 0) return false;
                var p = compiled;
                if (p == null)
                    compiled = p = (Flags & F_REGEX) != 0 ? Compile(regex, (Flags & F_MATCHCASE) == 0) : Compile(regex, false);
                return Find(p, (Flags & (F_MATCHCASE | F_REGEX)) != 0 ? url : lower);
            }
        }

        sealed class PageRule
        {
            public readonly int Page, Flags;
            public readonly string Regex;
            public readonly string[] DomPos, DomNeg;
            public Regex Compiled;
            public PageRule(int page, int flags, string regex, string domains)
            {
                Page = page; Flags = flags; Regex = regex;
                var d = ParseDomains(domains); DomPos = d[0]; DomNeg = d[1];
            }
        }

        public int PageFlags(string pageUrl, string pageHost)
        {
            if (pageUrl == null || pageHost == null) return 0;
            string lower = pageUrl.ToLowerInvariant();
            int o = 0;
            foreach (var r in pageRules)
            {
                if ((o & r.Page) == r.Page) continue;
                if ((r.DomPos.Length > 0 || r.DomNeg.Length > 0) && !DomainsOk(r.DomPos, r.DomNeg, pageHost)) continue;
                if (r.Regex.Length > 0)
                {
                    if (r.Compiled == null) r.Compiled = Compile(r.Regex, false);
                    if (!Find(r.Compiled, (r.Flags & F_MATCHCASE) != 0 ? pageUrl : lower)) continue;
                }
                o |= r.Page;
            }
            return o;
        }

        public string Registrable(string host) => psl.Registrable(host);

        // ---------------------------------------------------------------- redirect stubs
        public sealed class Resource
        {
            public readonly string Mime; public readonly byte[] Body;
            public Resource(string mime, byte[] body) { Mime = mime; Body = body; }
        }

        public Resource Redirect(string name) => resources.TryGetValue(name, out var r) ? r : null;

        // ---------------------------------------------------------------- $removeparam / $urlskip
        sealed class ParamRule
        {
            public readonly int Group; public readonly bool Exc, All, Negate;
            public readonly string Name;
            public readonly string[] DomPos, DomNeg, Skip;
            // regexes are compiled on first use (most rules only ever need their domain check)
            readonly string nameBody, urlPattern; readonly bool nameCi; Regex nameRe, urlRe;
            public Regex NameRe => nameBody == null ? null : nameRe ?? (nameRe = Compile(nameBody, nameCi));
            public Regex UrlRe => urlPattern == null ? null : urlRe ?? (urlRe = Compile(urlPattern, false));

            public ParamRule(int group, bool exc, string urlRegex, string domains, string spec)
            {
                Group = group; Exc = exc;
                if (spec.StartsWith("!urlskip ")) { Skip = spec.Substring(9).Trim().Split(' '); spec = ""; }
                bool neg = spec.StartsWith("~");
                if (neg) spec = spec.Substring(1);
                Negate = neg; All = spec.Length == 0;
                if (spec.Length > 2 && spec.StartsWith("/"))
                {
                    int end = spec.LastIndexOf('/');
                    string body = end > 0 ? spec.Substring(1, end - 1) : spec.Substring(1);
                    nameCi = end > 0 && spec.Substring(end + 1).Contains("i");
                    nameBody = body;
                }
                else Name = spec;
                if (urlRegex.Length > 0) urlPattern = urlRegex;
                var d = ParseDomains(domains); DomPos = d[0]; DomNeg = d[1];
            }

            public bool Hits(string pair, string key)
            {
                bool m = All || (NameRe != null ? Find(NameRe, pair) : key == Name);
                return Negate != m;
            }
        }

        /// <summary>URL without tracking parameters and, for tracking redirect links ($urlskip), the destination.</summary>
        public string CleanUrl(string url, string pageHost, int groups)
        {
            for (int round = 0; round < 3; round++)
            {
                string next = SkipRedirect(url, pageHost, groups);
                if (next == null || next == url) break;
                url = next;
            }
            return RemoveParams(url, pageHost, groups);
        }

        static string HostOf(string url) { try { return new Uri(url).Host.ToLowerInvariant(); } catch { return null; } }

        static string QueryParam(string url, string name)
        {
            int q = url.IndexOf('?'); if (q < 0) return null;
            int hashAt = url.IndexOf('#', q);
            string query = hashAt < 0 ? url.Substring(q + 1) : url.Substring(q + 1, hashAt - q - 1);
            foreach (var pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                string k = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                if (k == name) return eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }
            return null;
        }

        string SkipRedirect(string url, string pageHost, int groups)
        {
            string lower = url.ToLowerInvariant();
            string site = pageHost ?? HostOf(url);
            foreach (var r in paramRules)
            {
                if (r.Skip == null || r.Exc || (groups & r.Group) == 0) continue;
                if ((r.DomPos.Length > 0 || r.DomNeg.Length > 0) && (site == null || !DomainsOk(r.DomPos, r.DomNeg, site))) continue;
                if (r.UrlRe != null && !Find(r.UrlRe, lower)) continue;
                string v = url;
                try
                {
                    foreach (var step in r.Skip)
                    {
                        if (v == null || step.Length == 0) continue;
                        if (step.StartsWith("?")) v = QueryParam(v, step.Substring(1));
                        else if (step.StartsWith("/") && step.Length > 2)
                        {
                            int end = step.LastIndexOf('/');
                            var m = Compile(step.Substring(1, (end > 0 ? end : step.Length) - 1), false).Match(v);
                            v = m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : null;
                        }
                        else if (step == "-uricomponent") v = Uri.UnescapeDataString(v);
                        else if (step == "-base64" || step == "-safebase64")
                        {
                            string b = step == "-safebase64" ? v.Replace('-', '+').Replace('_', '/') : v;
                            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
                            v = Encoding.UTF8.GetString(Convert.FromBase64String(b));
                        }
                        else if (step == "+https") { if (!v.Contains("://")) v = "https://" + v; }
                    }
                }
                catch { v = null; }
                if (v != null && (v.StartsWith("https://") || v.StartsWith("http://"))) return v;
            }
            return url;
        }

        string RemoveParams(string url, string pageHost, int groups)
        {
            int q = url.IndexOf('?');
            if (q < 0) return url;
            int hashAt = url.IndexOf('#', q);
            string b = url.Substring(0, q);
            string query = hashAt < 0 ? url.Substring(q + 1) : url.Substring(q + 1, hashAt - q - 1);
            string frag = hashAt < 0 ? "" : url.Substring(hashAt);
            if (query.Length == 0) return url;
            string lower = url.ToLowerInvariant();
            string site = pageHost ?? HostOf(url);
            var active = new List<ParamRule>(); var excs = new List<ParamRule>();
            foreach (var r in paramRules)
            {
                if ((groups & r.Group) == 0) continue;
                if ((r.DomPos.Length > 0 || r.DomNeg.Length > 0) && (site == null || !DomainsOk(r.DomPos, r.DomNeg, site))) continue;
                if (r.Skip != null) continue;
                if (r.UrlRe != null && !Find(r.UrlRe, lower)) continue;
                (r.Exc ? excs : active).Add(r);
            }
            if (active.Count == 0) return url;
            var o = new StringBuilder();
            bool changed = false;
            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                bool drop = active.Any(r => r.Hits(pair, key));
                if (drop && excs.Any(r => r.Hits(pair, key))) drop = false;
                if (drop) { changed = true; continue; }
                if (o.Length > 0) o.Append('&');
                o.Append(pair);
            }
            if (!changed) return url;
            return o.Length == 0 ? b + frag : b + "?" + o + frag;
        }

        // ---------------------------------------------------------------- response rules ($csp, $cookie ...)
        public sealed class RespRule
        {
            public readonly int Group, Flags, Types; public readonly string Kind, Value;
            public readonly string[] DomPos, DomNeg;
            readonly string urlPattern; Regex urlRe;
            public Regex UrlRe => urlPattern == null ? null : urlRe ?? (urlRe = Compile(urlPattern, false));
            public RespRule(int group, int flags, int types, string urlRegex, string domains, string kind, string value)
            {
                Group = group; Flags = flags; Types = types; Kind = kind; Value = value;
                if (urlRegex.Length > 0) urlPattern = urlRegex;
                var d = ParseDomains(domains); DomPos = d[0]; DomNeg = d[1];
            }
        }

        public List<RespRule> ResponseRules(string url, string host, string pageHost, int type, int groups)
        {
            var on = new List<RespRule>();
            if (respRules.Length == 0) return on;
            int party = string.IsNullOrEmpty(pageHost) ? 0 : psl.Registrable(host) == psl.Registrable(pageHost) ? 1 : 2;
            string lower = url.ToLowerInvariant(), site = pageHost ?? host;
            var exc = new List<RespRule>();
            foreach (var r in respRules)
            {
                if ((groups & r.Group) == 0 || (r.Types & type) == 0 || !PartyOk(r.Flags, party)) continue;
                if ((r.DomPos.Length > 0 || r.DomNeg.Length > 0) && !DomainsOk(r.DomPos, r.DomNeg, site)) continue;
                if (r.UrlRe != null && !Find(r.UrlRe, lower)) continue;
                ((r.Flags & F_EXC) != 0 ? exc : on).Add(r);
            }
            if (exc.Count > 0) on = on.Where(r => !exc.Any(x => x.Kind == r.Kind && (x.Value.Length == 0 || x.Value == r.Value))).ToList();
            return on;
        }

        // ---------------------------------------------------------------- cosmetic
        sealed class CosCtx
        {
            public bool Generic, Specific;
            public HashSet<string> Exceptions;
            public string InitCss, Js, Extra;
        }

        readonly Dictionary<string, CosCtx> cosCache = new Dictionary<string, CosCtx>();
        readonly LinkedList<string> cosOrder = new LinkedList<string>();

        CosCtx Ctx(string pageUrl, string host, int groups)
        {
            string key = groups + host;
            lock (cosCache)
                if (cosCache.TryGetValue(key, out var hit)) return hit;
            var c = new CosCtx();
            int pf = PageFlags(pageUrl, host);
            bool all = (pf & (PAGE_ELEMHIDE | PAGE_URLBLOCK)) == 0;
            bool pageOn = (pf & PAGE_URLBLOCK) == 0;
            c.Generic = all && (pf & PAGE_GENERICHIDE) == 0;
            c.Specific = all && (pf & PAGE_SPECIFICHIDE) == 0;
            var sels = new List<string>(); var cssRules = new List<string>(); var exc = new HashSet<string>(StringComparer.Ordinal);
            var sl = new List<string>(); var slExc = new HashSet<string>(StringComparer.Ordinal);
            var ext = new List<string>(); var jsIds = new List<string>();
            if (pageOn) CollectSpecific(host, groups, sels, cssRules, exc, sl, slExc, ext, jsIds);
            if (!all) { sels.Clear(); cssRules.Clear(); exc.Clear(); }
            c.Exceptions = exc;
            var js = new StringBuilder();
            if (pageOn && (pf & PAGE_JSINJECT) == 0)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var call in sl)
                {
                    if (slExc.Contains(call) || !seen.Add(call)) continue;
                    js.Append(js.Length == 0 ? '[' : ',').Append(call);
                }
                if (js.Length > 0) js.Append(']');
            }
            c.Js = js.ToString();
            var ex = new StringBuilder();
            if (pageOn && (all || jsIds.Count > 0))
            {
                ex.Append("{\"e\":[");
                if (all && c.Specific) for (int i = 0; i < ext.Count; i++) { if (i > 0) ex.Append(','); JsonString(ex, ext[i]); }
                ex.Append("],\"r\":[");
                if ((pf & PAGE_JSINJECT) == 0) ex.Append(string.Join(",", jsIds));
                ex.Append("]}");
            }
            c.Extra = ext.Count == 0 && jsIds.Count == 0 ? "" : ex.ToString();
            var sb = new StringBuilder(4096);
            if (c.Generic) sb.Append(exc.Count == 0 ? UnkeyedCss(groups) : Css(Unkeyed(groups), exc));
            if (c.Specific)
            {
                foreach (var s in sels) if (!exc.Contains(s)) sb.Append(s).Append(HIDE);
                foreach (var r in cssRules) sb.Append(r);
            }
            c.InitCss = sb.ToString();
            lock (cosCache)
            {
                cosCache[key] = c;
                cosOrder.AddLast(key);
                // ~0.3 MB per site; rebuilding one costs ~8 ms, so 16 recent sites (open tabs + their frames) are enough
                while (cosOrder.Count > 16) { cosCache.Remove(cosOrder.First.Value); cosOrder.RemoveFirst(); }
            }
            return c;
        }

        void CollectSpecific(string host, int groups, List<string> sels, List<string> cssRules, HashSet<string> exc,
            List<string> sl, HashSet<string> slExc, List<string> ext, List<string> jsIds)
        {
            var keys = new List<string> { "*" };
            int start = 0;
            while (true)
            {
                keys.Add(host.Substring(start));
                int dot = host.IndexOf('.', start);
                if (dot < 0) break;
                start = dot + 1;
            }
            string suf = psl.Suffix(host);
            if (host.Length > suf.Length + 1)
            {
                string rest = host.Substring(0, host.Length - suf.Length - 1);
                int s = 0;
                while (true)
                {
                    keys.Add(rest.Substring(s) + ".*");
                    int dot = rest.IndexOf('.', s);
                    if (dot < 0) break;
                    s = dot + 1;
                }
            }
            foreach (var k in keys)
            {
                long kh = Fnv64(k);
                int idx = Array.BinarySearch(specHash, kh);
                if (idx < 0) continue;
                while (idx > 0 && specHash[idx - 1] == kh) idx--;
                for (int i = idx; i < specHash.Length && specHash[i] == kh; i++)
                {
                    int kind = specKind[i];
                    if ((groups & specMask[i]) == 0) continue;
                    var parts = cosm.Str(specOff[i], specLen[i]).Split('\u001f');
                    switch ((char)kind)
                    {
                        case 's': sels.AddRange(parts); break;
                        case 'x': foreach (var p in parts) exc.Add(p); break;
                        case 'c': cssRules.AddRange(parts); break;
                        case 'j': sl.AddRange(parts); break;
                        case 'y': foreach (var p in parts) slExc.Add(p); break;
                        case 'e': ext.AddRange(parts); break;
                        case 'r': jsIds.AddRange(parts); break;
                    }
                }
            }
        }

        static string Css(string[] sels, HashSet<string> exc)
        {
            var sb = new StringBuilder(sels.Length * 40);
            foreach (var s in sels) if (exc == null || !exc.Contains(s)) sb.Append(s).Append(HIDE);
            return sb.ToString();
        }

        public static void JsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 0x20 || ch == 0x2028 || ch == 0x2029) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            sb.Append('"');
        }

        string[] Unkeyed(int groups) => unkeyed.Where(x => (groups & x.Lists) != 0).Select(x => x.S).ToArray();

        /// <summary>Generic selectors without a class / id key, for these lists (kept per list mask: few masks are used).</summary>
        string UnkeyedCss(int groups)
        {
            lock (unkeyedCss)
            {
                if (!unkeyedCss.TryGetValue(groups, out var css)) unkeyedCss[groups] = css = Css(Unkeyed(groups), null);
                return css;
            }
        }

        public string ScriptletCalls(string pageUrl, string host, int groups) => string.IsNullOrEmpty(host) ? "" : Ctx(pageUrl, host, groups).Js;
        public string Extra(string pageUrl, string host, int groups) => string.IsNullOrEmpty(host) ? "" : Ctx(pageUrl, host, groups).Extra;

        public string CosmeticInit(string pageUrl, string host, int groups)
        {
            if (string.IsNullOrEmpty(host)) return "0";
            var c = Ctx(pageUrl, host, groups);
            return (c.Generic ? "1" : "0") + c.InitCss;
        }

        public string CosmeticKeys(string pageUrl, string host, string keys, int groups)
        {
            if (host == null || string.IsNullOrEmpty(keys)) return "";
            var c = Ctx(pageUrl, host, groups);
            if (!c.Generic) return "";
            StringBuilder sb = null;
            foreach (var k in keys.Split(' '))
            {
                if (k.Length == 0) continue;
                if (!keyed.TryGetValue(k, out var sels)) continue;
                foreach (var sel in sels)
                {
                    if ((groups & sel.Lists) == 0 || c.Exceptions.Contains(sel.S)) continue;
                    if (sb == null) sb = new StringBuilder(256);
                    sb.Append(sel.S).Append(HIDE);
                }
            }
            return sb?.ToString() ?? "";
        }
    }

    /// <summary>A read-only memory-mapped filter file. Its pages are file-backed: Windows loads only the parts that are
    /// read and can drop them again under memory pressure without writing them to the page file (unlike heap memory).</summary>
    internal sealed unsafe class MappedFile
    {
        readonly MemoryMappedFile map;
        readonly MemoryMappedViewAccessor view;
        public readonly byte* P;
        public readonly long Length;

        public MappedFile(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Length = fs.Length;
                if (Length == 0) return;
                // the map keeps its own handle to the file, so the stream can be closed
                map = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, null, HandleInheritability.None, true);
            }
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);   // released never: the engine lives as long as the app
            P = p + view.PointerOffset;
        }

        public string Str(long off, int len) => len <= 0 ? "" : Encoding.UTF8.GetString(P + off, len);

        /// <summary>Big-endian (Java ByteBuffer) values, as written by tools/build_filters.py.</summary>
        public long BE64(long off)
        {
            byte* b = P + off;
            return ((long)b[0] << 56) | ((long)b[1] << 48) | ((long)b[2] << 40) | ((long)b[3] << 32) |
                   ((long)b[4] << 24) | ((long)b[5] << 16) | ((long)b[6] << 8) | b[7];
        }

        public int BE32(long off)
        {
            byte* b = P + off;
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }
    }

    /// <summary>Public suffix list lookups (registrable domain for first/third-party checks).</summary>
    internal sealed class PublicSuffix
    {
        readonly HashSet<string> rules = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> wildcards = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> exceptions = new HashSet<string>(StringComparer.Ordinal);

        public void Add(string rule)
        {
            if (rule.Length == 0) return;
            if (rule.StartsWith("!")) exceptions.Add(rule.Substring(1));
            else if (rule.StartsWith("*.")) wildcards.Add(rule.Substring(2));
            else rules.Add(rule);
        }

        public string Suffix(string host)
        {
            if (IsIp(host)) return host;
            int i = 0;
            while (true)
            {
                string s = host.Substring(i);
                if (exceptions.Contains(s)) { int d0 = s.IndexOf('.'); return d0 < 0 ? s : s.Substring(d0 + 1); }
                if (rules.Contains(s)) return s;
                int d = s.IndexOf('.');
                if (d < 0) return s;
                if (wildcards.Contains(s.Substring(d + 1))) return s;
                i += d + 1;
            }
        }

        public string Registrable(string host)
        {
            if (string.IsNullOrEmpty(host)) return "";
            if (IsIp(host)) return host;
            string suf = Suffix(host);
            if (suf.Length >= host.Length) return host;
            int end = host.Length - suf.Length - 1;
            int start = host.LastIndexOf('.', end - 1);
            return host.Substring(start + 1);
        }

        static bool IsIp(string host)
        {
            if (host.IndexOf(':') >= 0) return true;
            foreach (char c in host) if ((c < '0' || c > '9') && c != '.') return false;
            return true;
        }
    }
}
