using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GBrowser
{
    /// <summary>Your own rules and custom filter lists (Settings > Ad blocking), parsed at run time next to the compiled
    /// lists: network rules (||host^, address patterns, /regex/, @@ exceptions, $third-party, $domain=, $important,
    /// types, $redirect), element hiding (##, #@#, #?# extended CSS), CSS rules (#$#, trusted only) and scriptlets
    /// (#%#//scriptlet(...), ##+js(...)). Like AdGuard, a list that is not trusted cannot use trusted-* scriptlets,
    /// CSS rules or JavaScript rules; your own rules are trusted.
    /// Precedence: your / custom exceptions allow, your / custom blocks block (even what a list allows), else the lists.</summary>
    internal sealed partial class FilterEngine
    {
        public sealed class FilterSource
        {
            public string Name, Text;
            public bool Trusted;
        }

        public sealed class Problem { public string Source; public int Line; public string Text, Reason; }

        public sealed class UserRules
        {
            internal readonly Dictionary<string, List<URule>> byHost = new Dictionary<string, List<URule>>(StringComparer.Ordinal);
            // plain "||host^" blocks (most of a hosts-style list): sorted FNV-1a 64 hashes, 8 bytes each
            internal readonly List<long> plainList = new List<long>();
            internal long[] plainHosts = new long[0];
            internal readonly Dictionary<string, List<URule>> byToken = new Dictionary<string, List<URule>>(StringComparer.Ordinal);
            internal readonly List<URule> noToken = new List<URule>();
            internal readonly List<string> generic = new List<string>();
            internal readonly HashSet<string> genericExc = new HashSet<string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, List<string>> specific = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, List<string>> specificExc = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, List<string>> css = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, List<string>> ext = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, List<string>> scriptlets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, List<string>> scriptletExc = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public int NetworkCount, CosmeticCount, ScriptletCount;
            public readonly List<Problem> Problems = new List<Problem>();
            public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();   // source -> rules taken
            public int Version;
            internal string genericCss;
        }

        internal sealed class URule
        {
            public Regex Re; public bool Exc, Imp; public int Party, Types = T_ALLTYPES; public string[] Pos = None, Neg = None;
            public string Redirect = "";
        }
        const int T_ALLTYPES = 0xFFF & ~T_POPUP;   // a rule without types: everything but popups (like the lists)

        // hosts of the built-in lists downloaded again today (HostUpdates): sorted hashes + list masks
        static volatile long[] extraHash = new long[0];
        static volatile int[] extraMask = new int[0];
        public static void SetExtraHosts(Dictionary<long, int> map)
        {
            var k = map.Keys.ToArray(); Array.Sort(k);
            var m = new int[k.Length];
            for (int i = 0; i < k.Length; i++) m[i] = map[k[i]];
            extraMask = m; extraHash = k;
        }
        public static int ExtraHostCount => extraHash.Length;

        static volatile UserRules user;
        static int userVersion;
        public static UserRules User => user;

        /// <summary>New rules apply to requests from now on and to pages opened from now on.</summary>
        public static void SetUser(UserRules r)
        {
            if (r != null) r.Version = ++userVersion;
            user = r;
            var e = Instance;
            if (e != null) lock (e.cosCache) { e.cosCache.Clear(); e.cosOrder.Clear(); }
        }

        // ------------------------------------------------------------ parsing

        static readonly Regex CosmeticRx = new Regex(@"^([^#$\s][^#$]*?|)(##|#@#|#\?#|#@\?#|#\$#|#@\$#|#%#|#@%#)(.+)$", RegexOptions.Compiled);
        static readonly Regex HostOnlyRx = new Regex(@"^\|\|([a-z0-9.\-*]+)\^?$", RegexOptions.Compiled);
        static readonly Dictionary<string, string> UboAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "aopr", "abort-on-property-read" }, { "aopw", "abort-on-property-write" }, { "acis", "abort-current-inline-script" },
            { "acs", "abort-current-inline-script" }, { "aost", "abort-on-stack-trace" }, { "nostif", "prevent-setTimeout" },
            { "no-setTimeout-if", "prevent-setTimeout" }, { "nosiif", "prevent-setInterval" }, { "no-setInterval-if", "prevent-setInterval" },
            { "set", "set-constant" }, { "aeld", "prevent-addEventListener" }, { "addEventListener-defuser", "prevent-addEventListener" },
            { "ra", "remove-attr" }, { "rc", "remove-class" }, { "rmnt", "remove-node-text" }, { "nowoif", "prevent-window-open" },
            { "window.open-defuser", "prevent-window-open" }, { "no-fetch-if", "prevent-fetch" }, { "no-xhr-if", "prevent-xhr" },
            { "noeval-if", "prevent-eval-if" }, { "nano-sib", "adjust-setInterval" }, { "nano-stb", "adjust-setTimeout" },
            { "cookie-remover", "remove-cookie" }, { "set-local-storage-item", "set-local-storage-item" },
        };

        HashSet<string> libNames;
        HashSet<string> LibNames()
        {
            if (libNames != null) return libNames;
            var h = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(Scriptlets ?? "", "\"([a-z0-9.-]+)\":function")) h.Add(m.Groups[1].Value);
            return libNames = h;
        }

        /// <summary>Builds the rule set from your rules and the custom lists (Problems: lines that were not taken, with why).</summary>
        public UserRules BuildUser(IEnumerable<FilterSource> sources)
        {
            var u = new UserRules();
            foreach (var src in sources)
            {
                int n = 0, ln = 0;
                foreach (var raw in (src.Text ?? "").Split('\n'))
                {
                    ln++;
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '!' || line[0] == '[' || (line.StartsWith("# ") || line == "#")) continue;
                    string why = AddRule(u, line, src.Trusted);
                    if (why == null) n++;
                    else if (u.Problems.Count < 500) u.Problems.Add(new Problem { Source = src.Name, Line = ln, Text = line, Reason = why });
                }
                u.Counts[src.Name] = n;
            }
            u.plainHosts = u.plainList.Distinct().ToArray();
            Array.Sort(u.plainHosts);
            u.plainList.Clear(); u.plainList.TrimExcess();
            var sb = new StringBuilder();
            foreach (var s in u.generic) if (!u.genericExc.Contains(s)) sb.Append(s).Append(HIDE);
            u.genericCss = sb.ToString();
            return u;
        }

        static void Add(Dictionary<string, List<string>> d, string k, string v)
        {
            if (!d.TryGetValue(k, out var l)) d[k] = l = new List<string>();
            l.Add(v);
        }

        static List<string> Doms(string s, out List<string> neg)
        {
            var pos = new List<string>(); neg = new List<string>();
            foreach (var d0 in s.Split(','))
            {
                string d = d0.Trim().ToLowerInvariant();
                if (d.Length == 0) continue;
                if (d[0] == '~') neg.Add(d.Substring(1)); else pos.Add(d);
            }
            return pos;
        }

        /// <summary>null = taken; else why not.</summary>
        string AddRule(UserRules u, string line, bool trusted)
        {
            var m = CosmeticRx.Match(line);
            if (m.Success && !line.StartsWith("||") && !line.StartsWith("@@") && !line.StartsWith("/"))
                return AddCosmetic(u, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, trusted);
            if (line.Contains("##") || line.Contains("#@#")) return "Not a valid element rule";
            return AddNetwork(u, line, trusted);
        }

        string AddCosmetic(UserRules u, string domains, string sep, string body, bool trusted)
        {
            var pos = Doms(domains, out var neg);
            bool exc = sep.Contains("@");
            // scriptlets
            string call = null;
            if ((sep == "#%#" || sep == "#@%#") && body.StartsWith("//scriptlet(")) call = AgScriptlet(body);
            else if ((sep == "##" || sep == "#@#") && body.StartsWith("+js(") && body.EndsWith(")")) call = UboScriptlet(body);
            if (call != null || body.StartsWith("//scriptlet(") || body.StartsWith("+js("))
            {
                if (call == null) return "Scriptlet not understood";
                if (call.StartsWith("[\"trusted-") && !trusted) return "Trusted scriptlets need a trusted list";
                string name = call.Substring(2, call.IndexOf('"', 2) - 2);
                if (!LibNames().Contains(name)) return "Scriptlet \"" + name + "\" is not available";
                foreach (var d in pos.Count == 0 ? new List<string> { "*" } : pos) Add(exc ? u.scriptletExc : u.scriptlets, d, call);
                foreach (var d in neg) Add(u.scriptletExc, d, call);
                u.ScriptletCount++;
                return null;
            }
            if (sep == "#%#" || sep == "#@%#") return "JavaScript rules are not supported here";
            if (sep == "#$#" || sep == "#@$#")
            {
                if (!trusted) return "CSS rules need a trusted list";
                if (exc) return "CSS rule exceptions are not supported";
                var mm = Regex.Match(body, @"^(.+?)\s*\{([^{}]*)\}\s*$");
                if (!mm.Success || pos.Count == 0 || body.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0) return "CSS rule not valid (needs a site, no url())";
                foreach (var d in pos) Add(u.css, d, mm.Groups[1].Value.Trim() + "{" + mm.Groups[2].Value.Trim() + "}");
                u.CosmeticCount++;
                return null;
            }
            bool extended = sep.Contains("?") || Regex.IsMatch(body, @":(has-text|contains|-abp-contains|-abp-has|matches-css|matches-attr|matches-property|xpath|nth-ancestor|upward|remove|is|if|if-not|min-text-length)\(");
            if (body.IndexOf('{') >= 0 || body.IndexOf('}') >= 0 || body.Contains("\n")) return "Not a valid selector";
            if (extended)
            {
                if (exc) return "Extended CSS exceptions are not supported";
                foreach (var d in pos.Count == 0 ? new List<string> { "*" } : pos) Add(u.ext, d, body + " { display: none !important; }");
                u.CosmeticCount++;
                return null;
            }
            if (!SelectorOk(body)) return "Not a valid selector";
            if (exc)
            {
                if (pos.Count == 0) u.genericExc.Add(body);
                foreach (var d in pos) Add(u.specificExc, d, body);
            }
            else if (pos.Count == 0)
            {
                u.generic.Add(body);
                foreach (var d in neg) Add(u.specificExc, d, body);
            }
            else foreach (var d in pos) Add(u.specific, d, body);
            u.CosmeticCount++;
            return null;
        }

        /// <summary>A cheap syntax check (the browser checks it for real when it applies the CSS).</summary>
        static bool SelectorOk(string s)
        {
            int depth = 0; char q = '\0';
            foreach (char c in s)
            {
                if (q != '\0') { if (c == q) q = '\0'; continue; }
                if (c == '"' || c == '\'') q = c;
                else if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') { if (--depth < 0) return false; }
            }
            return depth == 0 && q == '\0' && s.Trim().Length > 0;
        }

        static string JsonCall(List<string> parts)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < parts.Count; i++) { if (i > 0) sb.Append(','); JsonString(sb, parts[i]); }
            return sb.Append(']').ToString();
        }

        /// <summary>//scriptlet('name', 'a', "b") -> ["name","a","b"]</summary>
        static string AgScriptlet(string body)
        {
            var m = Regex.Match(body, @"^//scriptlet\((.*)\)\s*$");
            if (!m.Success) return null;
            var parts = new List<string>();
            string s = m.Groups[1].Value; int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == ',')) i++;
                if (i >= s.Length) break;
                char q = s[i];
                if (q != '\'' && q != '"') return null;
                var sb = new StringBuilder(); i++;
                while (i < s.Length && s[i] != q) { if (s[i] == '\\' && i + 1 < s.Length) i++; sb.Append(s[i]); i++; }
                if (i >= s.Length) return null;
                i++;
                parts.Add(sb.ToString());
            }
            return parts.Count == 0 ? null : JsonCall(parts);
        }

        /// <summary>+js(name, a, b) (uBO) -> ["agname","a","b"]</summary>
        static string UboScriptlet(string body)
        {
            string inner = body.Substring(4, body.Length - 5);
            var parts = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '\\' && i + 1 < inner.Length && inner[i + 1] == ',') { sb.Append(','); i++; continue; }
                if (inner[i] == ',') { parts.Add(sb.ToString().Trim()); sb.Clear(); continue; }
                sb.Append(inner[i]);
            }
            parts.Add(sb.ToString().Trim());
            if (parts.Count == 0 || parts[0].Length == 0) return null;
            string n = parts[0];
            if (n.EndsWith(".js")) n = n.Substring(0, n.Length - 3);
            if (UboAlias.TryGetValue(n, out var ag)) n = ag;
            parts[0] = n;
            return JsonCall(parts);
        }

        string AddNetwork(UserRules u, string line, bool trusted)
        {
            var r = new URule();
            string body = line;
            if (body.StartsWith("@@")) { r.Exc = true; body = body.Substring(2); }
            string pat = body, opts = null;
            // options after the last $ that is not part of a /regex/
            int dollar = body.LastIndexOf('$');
            if (dollar >= 0 && !(body.StartsWith("/") && body.LastIndexOf('/') > dollar)) { pat = body.Substring(0, dollar); opts = body.Substring(dollar + 1); }
            bool matchCase = false;
            if (opts != null)
            {
                int types = 0, notTypes = 0;
                foreach (var o0 in opts.Split(','))
                {
                    string o = o0.Trim(); if (o.Length == 0) continue;
                    bool not = o[0] == '~'; string k = not ? o.Substring(1) : o; string v = null;
                    int eq = k.IndexOf('=');
                    if (eq >= 0) { v = k.Substring(eq + 1); k = k.Substring(0, eq); }
                    k = k.ToLowerInvariant();
                    int t = TypeBit(k);
                    if (t != 0) { if (not) notTypes |= t; else types |= t; continue; }
                    switch (k)
                    {
                        case "third-party": case "3p": r.Party = not ? 1 : 2; break;
                        case "first-party": case "1p": r.Party = not ? 2 : 1; break;
                        case "important": r.Imp = true; break;
                        case "match-case": matchCase = true; break;
                        case "domain": case "from":
                        {
                            var pos = new List<string>(); var neg = new List<string>();
                            foreach (var d in (v ?? "").Split('|')) { if (d.Length == 0) continue; if (d[0] == '~') neg.Add(d.Substring(1).ToLowerInvariant()); else pos.Add(d.ToLowerInvariant()); }
                            r.Pos = pos.ToArray(); r.Neg = neg.ToArray();
                            break;
                        }
                        case "redirect": case "redirect-rule":
                            if (v == null || !resources.ContainsKey(v)) return "Unknown $redirect resource";
                            r.Redirect = v; break;
                        case "all": types |= T_ALLTYPES | T_POPUP | T_DOCUMENT; break;
                        case "badfilter": return "$badfilter is not supported here";
                        default: return "Option $" + k + " is not supported";
                    }
                }
                if (types != 0) r.Types = types;
                if (notTypes != 0) r.Types &= ~notTypes;
            }
            if (pat.Length == 0 || pat == "*" ) { if (r.Pos.Length == 0) return "Rule would match every address"; pat = "*"; }
            var hm = HostOnlyRx.Match(pat.ToLowerInvariant());
            if (hm.Success && !hm.Groups[1].Value.Contains("*"))
            {
                string h = hm.Groups[1].Value.TrimEnd('.');
                if (h.Length < 2) return "Host too short";
                if (opts == null && !r.Exc) { u.plainList.Add(Fnv64(h)); u.NetworkCount++; return null; }
                r.Re = null;
                if (!u.byHost.TryGetValue(h, out var l)) u.byHost[h] = l = new List<URule>();
                l.Add(r);
                u.NetworkCount++;
                return null;
            }
            string rx;
            if (pat.Length > 2 && pat.StartsWith("/") && pat.EndsWith("/")) rx = pat.Substring(1, pat.Length - 2);
            else rx = PatternRegex(pat);
            try { r.Re = new Regex(rx, RegexOptions.CultureInvariant | (matchCase ? 0 : RegexOptions.IgnoreCase), RegexTimeout); }
            catch (ArgumentException) { return "Not a valid pattern"; }
            string tok = BestToken(pat);
            if (tok == null) u.noToken.Add(r);
            else { if (!u.byToken.TryGetValue(tok, out var l)) u.byToken[tok] = l = new List<URule>(); l.Add(r); }
            u.NetworkCount++;
            return null;
        }

        static int TypeBit(string k)
        {
            switch (k)
            {
                case "script": return T_SCRIPT;
                case "image": return T_IMAGE;
                case "stylesheet": case "css": return T_CSS;
                case "xmlhttprequest": case "xhr": return T_XHR;
                case "media": return T_MEDIA;
                case "font": return T_FONT;
                case "subdocument": case "frame": return T_SUBDOC;
                case "websocket": return T_WEBSOCKET;
                case "other": return T_OTHER;
                case "object": return T_OBJECT;
                case "ping": case "beacon": return T_PING;
                case "document": case "doc": return T_DOCUMENT;
                case "popup": return T_POPUP;
                default: return 0;
            }
        }

        /// <summary>Adblock address pattern -> regex (|| host anchor, | start / end, ^ separator, * anything).</summary>
        static string PatternRegex(string p)
        {
            var sb = new StringBuilder();
            int i = 0;
            if (p.StartsWith("||")) { sb.Append(@"^[a-z][a-z0-9+.\-]*://(?:[^/?#]*\.)?"); i = 2; }
            else if (p.StartsWith("|")) { sb.Append('^'); i = 1; }
            bool endAnchor = p.EndsWith("|") && p.Length > i;
            int end = endAnchor ? p.Length - 1 : p.Length;
            for (; i < end; i++)
            {
                char c = p[i];
                if (c == '*') sb.Append(".*");
                else if (c == '^') sb.Append(@"(?:[^\w\-.%]|$)");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            if (endAnchor) sb.Append('$');
            return sb.ToString();
        }

        static readonly Regex TokenRx = new Regex("[a-z0-9%]{3,}", RegexOptions.Compiled);
        /// <summary>The longest literal piece of the pattern not touching a wildcard (index key), or null.</summary>
        static string BestToken(string pat)
        {
            if (pat.StartsWith("/") && pat.EndsWith("/")) return null;
            string p = pat.ToLowerInvariant();
            string best = null;
            foreach (Match m in TokenRx.Matches(p))
            {
                int a = m.Index, b = m.Index + m.Length;
                if (a > 0 && p[a - 1] == '*') continue;
                if (b < p.Length && p[b] == '*') continue;
                if (best == null || m.Length > best.Length) best = m.Value;
            }
            return best;
        }

        // ------------------------------------------------------------ matching

        /// <summary>Your / custom rules for a request: null = no decision, "" = allow (exception), "!" + "" = block,
        /// "!" + name = block with a redirect resource.</summary>
        string UserMatch(UserRules u, string url, string host, string pageHost, int type)
        {
            int party = 0;
            if (!string.IsNullOrEmpty(pageHost)) party = psl.Registrable(host) == psl.Registrable(pageHost) ? 1 : 2;
            string page = string.IsNullOrEmpty(pageHost) ? host : pageHost;
            bool block = false, imp = false, exc = false, impExc = false; string redirect = null;
            void Check(URule r)
            {
                if ((r.Types & type) == 0) return;
                if (r.Party == 1 && party == 2 || r.Party == 2 && party == 1) return;
                if ((r.Pos.Length > 0 || r.Neg.Length > 0) && !DomainsOk(r.Pos, r.Neg, page)) return;
                if (r.Re != null && !Find(r.Re, url)) return;
                if (r.Exc) { if (r.Imp) impExc = true; else exc = true; return; }
                block = true; if (r.Imp) imp = true;
                if (r.Redirect.Length > 0 && redirect == null) redirect = r.Redirect;
            }
            int start = 0;
            while (true)
            {
                string hs = start == 0 ? host : host.Substring(start);
                if (u.plainHosts.Length > 0 && Array.BinarySearch(u.plainHosts, Fnv64(hs)) >= 0) block = true;
                if (u.byHost.TryGetValue(hs, out var l)) foreach (var r in l) Check(r);
                int dot = host.IndexOf('.', start);
                if (dot < 0) break;
                start = dot + 1;
            }
            if (u.byToken.Count > 0)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in TokenRx.Matches(url.ToLowerInvariant()))
                    if (seen.Add(m.Value) && u.byToken.TryGetValue(m.Value, out var l)) foreach (var r in l) Check(r);
            }
            foreach (var r in u.noToken) Check(r);
            if (impExc) return "";
            if (block && imp) return "!" + (redirect ?? "");
            if (exc) return "";
            if (block) return "!" + (redirect ?? "");
            return null;
        }

        static List<string> HostKeys(string host, PublicSuffix psl)
        {
            var keys = new List<string>();
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
            return keys;
        }

        /// <summary>Your / custom element rules for a site, into the page context being built.</summary>
        void CollectUser(UserRules u, string host, List<string> sels, List<string> cssRules, HashSet<string> exc,
            List<string> sl, HashSet<string> slExc, List<string> ext)
        {
            var keys = HostKeys(host, psl);
            foreach (var k in keys)
            {
                if (u.specific.TryGetValue(k, out var a)) sels.AddRange(a);
                if (u.specificExc.TryGetValue(k, out var b)) foreach (var x in b) exc.Add(x);
                if (u.css.TryGetValue(k, out var c)) cssRules.AddRange(c);
                if (u.ext.TryGetValue(k, out var e)) ext.AddRange(e);
                if (u.scriptlets.TryGetValue(k, out var s)) sl.AddRange(s);
                if (u.scriptletExc.TryGetValue(k, out var y)) foreach (var x in y) slExc.Add(x);
            }
            foreach (var x in u.genericExc) exc.Add(x);
            if (u.scriptlets.TryGetValue("*", out var gs)) sl.AddRange(gs);
            if (u.ext.TryGetValue("*", out var ge)) ext.AddRange(ge);
            if (u.scriptletExc.TryGetValue("*", out var gy)) foreach (var x in gy) slExc.Add(x);
        }
    }
}
