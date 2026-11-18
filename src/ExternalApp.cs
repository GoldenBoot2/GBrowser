using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GBrowser
{
    /// <summary>Links that open another app (mailto:, tel:, zoommtg:, ms-settings: ...): the app Windows has
    /// registered for the link type, by its friendly name.</summary>
    internal static class ExternalApp
    {
        const int ASSOCF_IS_PROTOCOL = 0x00001000, ASSOCF_NOTRUNCATE = 0x00000020;
        const int ASSOCSTR_FRIENDLYAPPNAME = 4, ASSOCSTR_EXECUTABLE = 2;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder outBuf, ref uint outSize);

        public static string SchemeOf(string uri)
        {
            int i = uri?.IndexOf(':') ?? -1;
            return i > 0 ? uri.Substring(0, i).ToLowerInvariant() : null;
        }

        static string Query(string scheme, int what)
        {
            uint size = 512;
            var sb = new StringBuilder((int)size);
            try
            {
                int hr = AssocQueryString(ASSOCF_IS_PROTOCOL | ASSOCF_NOTRUNCATE, what, scheme, "open", sb, ref size);
                return hr == 0 && sb.Length > 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }

        /// <summary>The registered app's name ("Zoom Workplace", "Mail"), or null when no app handles the link type.</summary>
        public static string AppName(string scheme)
        {
            if (string.IsNullOrEmpty(scheme)) return null;
            string exe = Query(scheme, ASSOCSTR_EXECUTABLE);
            string name = Query(scheme, ASSOCSTR_FRIENDLYAPPNAME);
            if (string.IsNullOrEmpty(exe) && string.IsNullOrEmpty(name)) return null;
            // no default app chosen: Windows answers with its own "Pick an app" chooser (OpenWith.exe)
            if (!string.IsNullOrEmpty(exe) && exe.EndsWith("OpenWith.exe", StringComparison.OrdinalIgnoreCase)) return "app";
            if (string.IsNullOrEmpty(name)) name = System.IO.Path.GetFileNameWithoutExtension(exe);
            return name;
        }
    }
}
