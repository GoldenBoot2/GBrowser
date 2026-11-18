using System;
using System.Collections.Generic;
using System.IO;
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
        long[] hostHash; int[] hostMeta;
        Rule[] rules; Dictionary<int, int[]> index; int[] untokenized;
        PageRule[] pageRules;
        readonly Dictionary<string, string[]>[] keyed = new Dictionary<string, string[]>[3];
        readonly string[][] unkeyed = new string[3][];
        readonly string[] unkeyedCss = new string[3];
        byte[] cosm; long[] specHash; int[] specOff, specLen; byte[] specKind, specGroup;
        ParamRule[] paramRules = new ParamRule[0];
        RespRule[] respRules = new RespRule[0];
        readonly Dictionary<string, Resource> resources = new Dictionary<string, Resource>();
        int cosmeticCount;
        public readonly int RuleCount;
        public readonly string ListInfo;
        public readonly string Scriptlets, RawJs;

        FilterEngine(string dir)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int g = 0; g < 3; g++) keyed[g] = new Dictionary<string, string[]>(g == 0 ? 16384 : 1024, StringComparer.Ordinal);
            LoadPsl(File.ReadAllBytes(Path.Combine(dir, "psl.txt")));
            LoadHosts(File.ReadAllBytes(Path.Combine(dir, "hosts.bin")));
            LoadRules(File.ReadAllBytes(Path.Combine(dir, "net.txt")));
            LoadPageRules(File.ReadAllText(Path.Combine(dir, "page.txt"), Encoding.UTF8));
            LoadCosmetic(File.ReadAllBytes(Path.Combine(dir, "cosm.txt")));
            LoadRedirects(File.ReadAllText(Path.Combine(dir, "redirects.txt"), Encoding.UTF8));
            LoadParams(File.ReadAllText(Path.Combine(dir, "params.txt"), Encoding.UTF8));
            LoadResp(File.ReadAllText(Path.Combine(dir, "resp.txt"), Encoding.UTF8));
            ListInfo = File.ReadAllText(Path.Combine(dir, "info.txt"), Encoding.UTF8);
            Scriptlets = File.ReadAllText(Path.Combine(dir, "scriptlets.js"), Encoding.UTF8);
            RawJs = File.ReadAllText(Path.Combine(dir, "rawjs.js"), Encoding.UTF8);
            RuleCount = hostHash.Length + rules.Length + pageRules.Length + cosmeticCount;
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

        void LoadHosts(byte[] d)
        {
            // big-endian (Java ByteBuffer): int count, long[count] hashes (sorted, signed), int[count] meta
            int n = (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3];
            hostHash = new long[n]; hostMeta = new int[n];
            int p = 4;
            for (int i = 0; i < n; i++, p += 8)
            {
                long v = 0;
                for (int k = 0; k < 8; k++) v = (v << 8) | d[p + k];
                hostHash[i] = v;
            }
            for (int i = 0; i < n; i++, p += 4) hostMeta[i] = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
        }

        void LoadRules(byte[] data)
        {
            var list = new List<Rule>(40000);
            var byToken = new Dictionary<int, List<int>>(32768);
            var untok = new List<int>();
            foreach (var line in Lines(data))
            {
                var f = Split(line, 9);
                var r = new Rule(int.Parse(f[0]), int.Parse(f[1]), int.Parse(f[2]), f[4], f[5], f[6], f[7], f[8]);
                int id = list.Count;
                list.Add(r);
                if (f[3].Length == 0) untok.Add(id);
                else
                {
                    int tok = int.Parse(f[3]);
                    if (!byToken.TryGetValue(tok, out var ids)) byToken[tok] = ids = new List<int>(1);
                    ids.Add(id);
                }
            }
            rules = list.ToArray();
            untokenized = untok.ToArray();
            index = new Dictionary<int, int[]>(byToken.Count);
            foreach (var kv in byToken) index[kv.Key] = kv.Value.ToArray();
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

        void LoadCosmetic(byte[] data)
        {
            cosm = data;
            var unk = new[] { new List<string>(), new List<string>(), new List<string>() };
            var key = new[] { new Dictionary<string, List<string>>(StringComparer.Ordinal), new Dictionary<string, List<string>>(StringComparer.Ordinal), new Dictionary<string, List<string>>(StringComparer.Ordinal) };
            var hs = new List<long>(1 << 17); var offs = new List<int>(1 << 17); var lens = new List<int>(1 << 17);
            var kinds = new List<byte>(1 << 17); var grps = new List<byte>(1 << 17);
            int s = 0;
            for (int i = 0; i <= data.Length; i++)
            {
                if (i != data.Length && data[i] != '\n') continue;
                if (i - s > 4)
                {
                    byte kind = data[s];
                    int g = data[s + 2] - '0';
                    int a = s + 4;
                    if (kind == 'u') { unk[g].Add(Encoding.UTF8.GetString(data, a, i - a)); cosmeticCount++; }
                    else
                    {
                        int tab = a;
                        while (tab < i && data[tab] != '\t') tab++;
                        string k = Encoding.UTF8.GetString(data, a, tab - a);
                        if (kind == 'k')
                        {
                            if (!key[g].TryGetValue(k, out var l)) key[g][k] = l = new List<string>(2);
                            l.Add(Encoding.UTF8.GetString(data, tab + 1, i - tab - 1));
                        }
                        else
                        {
                            hs.Add(Fnv64(k)); offs.Add(tab + 1); lens.Add(i - tab - 1); kinds.Add(kind); grps.Add((byte)g);
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
            specHash = new long[n]; specOff = new int[n]; specLen = new int[n]; specKind = new byte[n]; specGroup = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int o = order[i];
                specHash[i] = hArr[o]; specOff[i] = offs[o]; specLen[i] = lens[o]; specKind[i] = kinds[o]; specGroup[i] = grps[o];
            }
            for (int g = 0; g < 3; g++)
            {
                unkeyed[g] = unk[g].ToArray();
                unkeyedCss[g] = Css(unkeyed[g], null);
                foreach (var kv in key[g]) keyed[g][kv.Key] = kv.Value.ToArray();
            }
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
                try { list.Add(new ParamRule(int.Parse(f[0]), f[1] == "1", f[2], f[3], f[4])); } catch { }
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
                try { list.Add(new RespRule(int.Parse(f[0]), int.Parse(f[1]), int.Parse(f[2]), f[3], f[4], f[5], f[6])); } catch { }
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
                int idx = Array.BinarySearch(hostHash, hh);
                if (idx >= 0)
                {
                    while (idx > 0 && hostHash[idx - 1] == hostHash[idx]) idx--;
                    for (int i = idx; i < hostHash.Length && hostHash[i] == hh; i++)
                    {
                        int m = hostMeta[i];
                        int types = m & 0xFFF, flags = (m >> 12) & 0x1F, g = (m >> 20) & 3;
                        if ((groups & (1 << g)) == 0 || (types & type) == 0 || !PartyOk(flags, party)) continue;
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
                if (tl >= 2 && index.TryGetValue(h, out var ids))
                    foreach (int id in ids)
                    {
                        var r = rules[id];
                        if ((groups & (1 << r.Group)) == 0 || st.Useless(r)) continue;
                        if (r.Matches(this, url, lower, host, pageHost, type, party)) st.Add(r);
                    }
                h = 0; tl = 0;
            }
            foreach (int id in untokenized)
            {
                var r = rules[id];
                if ((groups & (1 << r.Group)) == 0 || st.Useless(r)) continue;
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
            public readonly string Name; public readonly Regex NameRe, UrlRe;
            public readonly string[] DomPos, DomNeg, Skip;

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
                    bool ci = end > 0 && spec.Substring(end + 1).Contains("i");
                    NameRe = Compile(body, ci);
                }
                else Name = spec;
                if (urlRegex.Length > 0) UrlRe = Compile(urlRegex, false);
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
                if (r.Skip == null || r.Exc || (groups & (1 << r.Group)) == 0) continue;
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
                if ((groups & (1 << r.Group)) == 0) continue;
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
            public readonly Regex UrlRe; public readonly string[] DomPos, DomNeg;
            public RespRule(int group, int flags, int types, string urlRegex, string domains, string kind, string value)
            {
                Group = group; Flags = flags; Types = types; Kind = kind; Value = value;
                if (urlRegex.Length > 0) UrlRe = Compile(urlRegex, false);
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
                if ((groups & (1 << r.Group)) == 0 || (r.Types & type) == 0 || !PartyOk(r.Flags, party)) continue;
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
            if (c.Generic)
                for (int g = 0; g < 3; g++)
                {
                    if ((groups & (1 << g)) == 0) continue;
                    sb.Append(exc.Count == 0 ? unkeyedCss[g] : Css(unkeyed[g], exc));
                }
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
                while (cosOrder.Count > 48) { cosCache.Remove(cosOrder.First.Value); cosOrder.RemoveFirst(); }
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
                    int kind = specKind[i], g = specGroup[i];
                    if ((groups & (1 << g)) == 0) continue;
                    var parts = Encoding.UTF8.GetString(cosm, specOff[i], specLen[i]).Split('\u001f');
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
                for (int g = 0; g < 3; g++)
                {
                    if ((groups & (1 << g)) == 0 || !keyed[g].TryGetValue(k, out var sels)) continue;
                    foreach (var sel in sels)
                    {
                        if (c.Exceptions.Contains(sel)) continue;
                        if (sb == null) sb = new StringBuilder(256);
                        sb.Append(sel).Append(HIDE);
                    }
                }
            }
            return sb?.ToString() ?? "";
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
