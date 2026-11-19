using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Links dragged out of GBrowser (address bar) and links / text / files dropped on it (tab strip, address
    /// bar, bookmarks bar), in the formats Chrome uses: text/x-moz-url (address + title), UniformResourceLocator(W),
    /// plain text, and a virtual "Title.url" file so the desktop or a folder gets a website shortcut.</summary>
    internal static class UrlDrag
    {
        /// <summary>Drag data for a page.</summary>
        public static DataObject Make(string url, string title)
        {
            var d = new DataObject();
            title = string.IsNullOrWhiteSpace(title) ? url : title.Trim();
            d.SetData("text/x-moz-url", new MemoryStream(Encoding.Unicode.GetBytes(url + "\n" + title + "\0")));
            d.SetData("UniformResourceLocatorW", new MemoryStream(Encoding.Unicode.GetBytes(url + "\0")));
            d.SetData("UniformResourceLocator", new MemoryStream(Encoding.Default.GetBytes(AsciiUrl(url) + "\0")));
            d.SetData(DataFormats.UnicodeText, url);
            d.SetData(DataFormats.Text, url);
            // a website shortcut where files are dropped (desktop, folders), as a virtual file like Chrome's
            byte[] shortcut = Encoding.ASCII.GetBytes("[InternetShortcut]\r\nURL=" + AsciiUrl(url) + "\r\n");
            d.SetData("FileGroupDescriptorW", new MemoryStream(Descriptor(FileName(title), shortcut.Length)));
            d.SetData("FileContents", new MemoryStream(shortcut));
            return d;
        }

        /// <summary>Drag data for plain text (a part of the address that is not the whole address).</summary>
        public static DataObject MakeText(string text)
        {
            var d = new DataObject();
            d.SetData(DataFormats.UnicodeText, text);
            d.SetData(DataFormats.Text, text);
            return d;
        }

        /// <summary>A web address in the drop (http / https), with its title when the source gives one.</summary>
        public static string ReadUrl(IDataObject d, out string title)
        {
            title = null;
            try
            {
                if (d.GetDataPresent("text/x-moz-url"))
                {
                    var o = d.GetData("text/x-moz-url");
                    string s = o is MemoryStream ms ? Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0') : o as string;
                    if (!string.IsNullOrEmpty(s))
                    {
                        var parts = s.Split('\n');
                        if (parts.Length > 1) title = parts[1].Trim();
                        if (Web(parts[0].Trim(), out var u0)) return u0;
                    }
                }
                foreach (var f in new[] { "UniformResourceLocatorW", "UniformResourceLocator", DataFormats.UnicodeText, DataFormats.Text })
                {
                    if (!d.GetDataPresent(f)) continue;
                    var o = d.GetData(f);
                    string s = o is MemoryStream ms ? (f.EndsWith("W") ? Encoding.Unicode : Encoding.Default).GetString(ms.ToArray()).TrimEnd('\0') : o as string;
                    if (s != null && Web(s.Trim(), out var u)) return u;
                }
            }
            catch { }
            return null;
        }

        /// <summary>What a drop on the tab strip / address bar opens: a web address, a dropped file (file:///), or
        /// any other text (searched, like Chrome). Null when the drop carries none of these.</summary>
        public static string ReadTarget(IDataObject d)
        {
            string url = ReadUrl(d, out _);
            if (url != null) return url;
            try
            {
                if (d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 &&
                    !d.GetDataPresent("FileGroupDescriptorW"))
                    return new Uri(files[0]).AbsoluteUri;
                foreach (var f in new[] { DataFormats.UnicodeText, DataFormats.Text })
                    if (d.GetDataPresent(f) && d.GetData(f) is string s && s.Trim().Length > 0) return s.Trim();
            }
            catch { }
            return null;
        }

        static bool Web(string s, out string url)
        {
            url = null;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return false;
            url = u.AbsoluteUri;
            return true;
        }

        /// <summary>The address with an ASCII host and escaped path (shortcut files and the ANSI format are not Unicode).</summary>
        static string AsciiUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                var b = new UriBuilder(u) { Host = u.IdnHost };
                string s = b.Uri.AbsoluteUri;
                return s.All(c => c < 128) ? s : Uri.EscapeUriString(s);
            }
            catch { return url; }
        }

        /// <summary>A file name from the page title (Windows' forbidden characters removed), like Chrome's "Title.url".</summary>
        static string FileName(string title)
        {
            var bad = Path.GetInvalidFileNameChars();
            string n = new string(title.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
            if (n.Length > 100) n = n.Substring(0, 100).Trim();
            return (n.Length == 0 ? "Website" : n) + ".url";
        }

        /// <summary>FILEGROUPDESCRIPTORW with one FILEDESCRIPTORW (592 bytes): name, size, write time.</summary>
        static byte[] Descriptor(string name, int size)
        {
            var b = new byte[4 + 592];
            BitConverter.GetBytes(1).CopyTo(b, 0);                                  // cItems
            const int o = 4;
            BitConverter.GetBytes(0x40 | 0x20).CopyTo(b, o);                         // FD_FILESIZE | FD_WRITESTIME
            BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(b, o + 56);      // ftLastWriteTime
            BitConverter.GetBytes(size).CopyTo(b, o + 68);                           // nFileSizeLow
            var nb = Encoding.Unicode.GetBytes(name);
            Array.Copy(nb, 0, b, o + 72, Math.Min(nb.Length, 518));                  // cFileName[260]
            return b;
        }
    }
}
