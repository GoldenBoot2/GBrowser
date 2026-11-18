using System;
using System.Linq;
using System.Windows.Forms;

namespace GBrowserSetup
{
    /// <summary>GBrowserSetup.exe
    ///   (no arguments)   install / update with the setup window
    ///   --uninstall      remove (Windows "Installed apps" runs this from the install folder)
    ///   --update         started by GBrowser's updater: installs at once, then starts GBrowser again
    ///   --quiet          no window (with --uninstall: keeps browsing data)
    ///   Tests only: --target &lt;dir&gt; --no-shortcuts --no-register --auto (start after 3 s)</summary>
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool Has(string a) => args.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));
            string Arg(string a) { int i = Array.FindIndex(args, x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

            bool uninstall = Has("--uninstall"), quiet = Has("--quiet"), update = Has("--update");
            var inst = new Installer();
            string target = Arg("--target");
            if (target != null) { inst.Dir = target; inst.Register = false; }
            else if (uninstall) inst.Dir = Installer.InstalledDir() ?? Installer.DefaultDir;
            else if (Installer.InstalledDir() != null) inst.Dir = Installer.InstalledDir();   // updates go where it already is
            if (Has("--no-shortcuts")) inst.DesktopShortcut = inst.StartShortcut = false;
            if (Has("--no-register")) inst.Register = false;

            // uninstalling from inside the install folder: continue from a temporary copy
            if (uninstall && !Has("--temp-copy") && target == null && Installer.RelaunchFromTemp(args)) return 0;

            if (quiet)
            {
                if (uninstall) inst.Uninstall();
                else { if (inst.Running().Count > 0) inst.CloseRunning(); inst.Install(); }
                if (Has("--temp-copy")) Installer.DeleteSelfLater();
                return inst.Error == null ? 0 : 1;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var f = new SetupForm(inst, uninstall) { AutoStart = Has("--auto"), UpdateMode = update })
                Application.Run(f);
            if (Has("--temp-copy")) Installer.DeleteSelfLater();
            return inst.Error == null ? 0 : 1;
        }
    }
}
