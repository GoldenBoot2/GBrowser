using System;
using System.IO;
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
            Application.Run(new MainForm());
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
