using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace GBrowser
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Report(e.Exception, false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception, true);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Store.Load();
            // one running GBrowser per data folder: a second start hands its addresses over and ends
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            bool restarted = args.Contains("--restarted");
            var mutex = new Mutex(false, App.MutexName);
            bool mine = Own(mutex, 0);
            if (!mine && !restarted && App.HandOver(args)) return;
            // a restart, or the running copy is closing: wait until it has gone
            if (!mine) mine = Own(mutex, restarted ? 20000 : 10000);
            App.Listen(new WindowsFormsSynchronizationContext());
            var first = new MainForm(null, null);
#if AUTOMATION
            Automation.Start();
#endif
            Application.Run(new BrowserContext(first));
            GC.KeepAlive(mutex);
        }

        static bool Own(Mutex m, int ms)
        {
            try { return m.WaitOne(ms); }
            catch (AbandonedMutexException) { return true; }   // the previous GBrowser ended without closing
        }

        /// <summary>Writes the full error to crash.log in the data folder (for fixing the cause) and tells the user.</summary>
        static void Report(Exception ex, bool fatal)
        {
            try
            {
                Directory.CreateDirectory(Store.DataDir);
                File.AppendAllText(Path.Combine(Store.DataDir, "crash.log"),
                    DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + (fatal ? " FATAL " : " ") + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
            if (!fatal) MessageBox.Show("Something went wrong:\n" + (ex?.Message ?? "unknown error"), "GBrowser", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
