#if AUTOMATION
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>LOCAL TEST BUILDS ONLY (dotnet build -p:DefineConstants=AUTOMATION): a named pipe "GBrowserTest-&lt;pid&gt;"
    /// that drives the window without sending keys to the desktop. One command per connection, one reply line.
    /// Release builds from GitHub Actions do not contain this class.</summary>
    internal static class Automation
    {
        /// <summary>Commands go to the active window (the last one used, or the newest).</summary>
        public static void Start()
        {
            Task.Run(async () =>
            {
                string name = "GBrowserTest-" + System.Diagnostics.Process.GetCurrentProcess().Id;
                while (true)
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1))
                        {
                            await pipe.WaitForConnectionAsync();
                            var reader = new StreamReader(pipe);
                            var writer = new StreamWriter(pipe) { AutoFlush = true };
                            string line = await reader.ReadLineAsync();
                            var tcs = new TaskCompletionSource<string>();
                            var form = App.Active;
                            if (form == null || form.IsDisposed) { await writer.WriteLineAsync("no window"); continue; }
                            form.BeginInvoke((Action)(async () =>
                            {
                                try { tcs.SetResult(await form.TestCommand(line ?? "")); }
                                catch (Exception ex) { tcs.SetResult("error " + ex.Message); }
                            }));
                            await writer.WriteLineAsync((await tcs.Task).Replace("\r", " ").Replace("\n", " "));
                        }
                    }
                    catch { await Task.Delay(200); }
                }
            });
        }

        /// <summary>"^+t" style key names (SendKeys-like: ^ Ctrl, + Shift, % Alt) -> Keys.</summary>
        public static Keys ParseKeys(string s)
        {
            Keys mods = 0; int i = 0;
            for (; i < s.Length && "^+%".IndexOf(s[i]) >= 0; i++)
                mods |= s[i] == '^' ? Keys.Control : s[i] == '+' ? Keys.Shift : Keys.Alt;
            string k = s.Substring(i);
            Keys key;
            if (k.Length == 1 && char.IsLetter(k[0])) key = (Keys)char.ToUpperInvariant(k[0]);
            else if (k.Length == 1 && char.IsDigit(k[0])) key = Keys.D0 + (k[0] - '0');
            else key = (Keys)Enum.Parse(typeof(Keys), k, true);
            return key | mods;
        }
    }
}
#endif
