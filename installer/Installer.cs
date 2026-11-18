using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace GBrowserSetup
{
    /// <summary>Per-user install of GBrowser (no admin): files to %LOCALAPPDATA%\Programs\GBrowser, Start menu and
    /// optional desktop shortcuts, an "Installed apps" entry (HKCU) whose uninstall runs this setup with --uninstall.
    /// Browsing data (%LOCALAPPDATA%\GoldenBoot\GBrowser) is never touched, except on uninstall when asked to.</summary>
    internal sealed class Installer
    {
        public const string Product = "GBrowser", Publisher = "GoldenBoot", Exe = "GBrowser.exe", SetupCopy = "setup.exe";
        const string Marker = ".gbrowser-install";   // only folders with this file are ever replaced or deleted
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GBrowser";
        const string WebView2Guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        const string WebView2Bootstrapper = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product);
        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoldenBoot", Product);

        public string Dir = DefaultDir;
        public bool DesktopShortcut = true, StartShortcut = true, Register = true;

        // progress for the UI (read from the UI thread)
        public volatile float Progress;
        public volatile string Step = "";
        public volatile string Error;

        static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product + ".lnk");
        static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Product + ".lnk");

        public static string InstalledDir()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return k?.GetValue("InstallLocation") as string;
        }

        public static string InstalledVersion()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return k?.GetValue("DisplayVersion") as string;
        }

        /// <summary>GBrowser windows running from this install folder (they must close before files are replaced).</summary>
        public List<Process> Running()
        {
            var list = new List<Process>();
            string exe = Path.GetFullPath(Path.Combine(Dir, Exe));
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Exe)))
            {
                try { if (string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) { list.Add(p); continue; } } catch { }
                p.Dispose();
            }
            return list;
        }

        /// <summary>Asks running GBrowser windows to close (like clicking X: tabs are saved) and waits up to 15 s.</summary>
        public bool CloseRunning()
        {
            var list = Running();
            foreach (var p in list) try { p.CloseMainWindow(); } catch { }
            foreach (var p in list) try { p.WaitForExit(15000); } catch { }
            bool left = Running().Count > 0;
            foreach (var p in list) p.Dispose();
            // the browser engine processes of that window exit a moment later
            Thread.Sleep(1500);
            return !left;
        }

        // ------------------------------------------------------------ WebView2 runtime

        public static bool HasWebView2()
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var path in new[] { @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\", @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" })
                    try
                    {
                        using (var k = root.OpenSubKey(path + WebView2Guid))
                        {
                            var v = k?.GetValue("pv") as string;
                            if (!string.IsNullOrEmpty(v) && v != "0.0.0.0") return true;
                        }
                    }
                    catch { }
            return false;
        }

        void InstallWebView2()
        {
            Step = "Getting Microsoft Edge WebView2";
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;   // TLS 1.2 + 1.3
            string tmp = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            try
            {
                using (var wc = new WebClient()) wc.DownloadFile(WebView2Bootstrapper, tmp);
                Step = "Installing Microsoft Edge WebView2";
                using (var p = Process.Start(new ProcessStartInfo(tmp, "/silent /install") { UseShellExecute = false }))
                    p.WaitForExit(10 * 60 * 1000);
            }
            finally { try { File.Delete(tmp); } catch { } }
            if (!HasWebView2()) throw new Exception("WebView2 could not be installed");
        }

        // ------------------------------------------------------------ install

        public void Install()
        {
            try
            {
                Progress = 0.02f;
                if (!HasWebView2()) InstallWebView2();
                Progress = 0.06f;

                Step = "Copying files";
                string full = Path.GetFullPath(Dir).TrimEnd('\\');
                string parent = Path.GetDirectoryName(full);
                Directory.CreateDirectory(parent);
                if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !File.Exists(Path.Combine(full, Marker)))
                    throw new Exception("The folder is not empty: choose another one");
                string stage = full + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                Extract(stage, 0.06f, 0.86f);
                File.WriteAllText(Path.Combine(stage, Marker), Product + " " + Version);
                // this setup stays in the folder: Windows' "Uninstall" runs it
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(stage, SetupCopy), true);

                // swap the folders (an update replaces every file; leftovers of older versions go away)
                string old = null;
                if (Directory.Exists(full))
                {
                    old = full + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    Directory.Move(full, old);
                }
                Directory.Move(stage, full);
                if (old != null) try { Directory.Delete(old, true); } catch { }
                Progress = 0.9f;

                Step = "Creating shortcuts";
                string exe = Path.Combine(full, Exe);
                if (StartShortcut) Shortcut(StartMenuLink, exe, full);
                if (DesktopShortcut) Shortcut(DesktopLink, exe, full); else TryDelete(DesktopLink);
                Progress = 0.95f;

                if (Register)
                {
                    Step = "Registering";
                    long kb = new DirectoryInfo(full).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
                    using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                    {
                        k.SetValue("DisplayName", Product);
                        k.SetValue("DisplayVersion", Version);
                        k.SetValue("Publisher", Publisher);
                        k.SetValue("DisplayIcon", exe + ",0");
                        k.SetValue("InstallLocation", full);
                        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));   // format Windows expects
                        k.SetValue("UninstallString", "\"" + Path.Combine(full, SetupCopy) + "\" --uninstall");
                        k.SetValue("QuietUninstallString", "\"" + Path.Combine(full, SetupCopy) + "\" --uninstall --quiet");
                        k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
                        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    }
                }
                Step = "Done";
                Progress = 1f;
            }
            catch (Exception ex) { Error = ex.Message; }
        }

        void Extract(string target, float from, float to)
        {
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (res == null) throw new Exception("This setup has no GBrowser files inside");
                using (var zip = new ZipArchive(res, ZipArchiveMode.Read))
                {
                    long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
                    string root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
                    var buf = new byte[1 << 16];
                    foreach (var e in zip.Entries)
                    {
                        string dest = Path.GetFullPath(Path.Combine(target, e.FullName));
                        if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception("Bad file name in the package");
                        if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        using (var s = e.Open())
                        using (var o = File.Create(dest))
                        {
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                            {
                                o.Write(buf, 0, n);
                                done += n;
                                Progress = from + (to - from) * done / total;
                            }
                        }
                    }
                }
            }
        }

        static void Shortcut(string link, string target, string workDir)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            try
            {
                dynamic sc = shell.CreateShortcut(link);
                sc.TargetPath = target;
                sc.WorkingDirectory = workDir;
                sc.IconLocation = target + ",0";
                sc.Description = "GBrowser web browser";
                sc.Save();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        public void Launch()
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(Dir, Exe)) { WorkingDirectory = Dir, UseShellExecute = true }); } catch { }
        }

        // ------------------------------------------------------------ uninstall

        public bool DeleteData;

        public void Uninstall()
        {
            try
            {
                string full = Path.GetFullPath(Dir).TrimEnd('\\');
                Step = "Closing GBrowser";
                Progress = 0.1f;
                if (!CloseRunning()) throw new Exception("Close GBrowser first");
                Step = "Removing files";
                if (Directory.Exists(full))
                {
                    if (!File.Exists(Path.Combine(full, Marker))) throw new Exception("Not a GBrowser folder: nothing removed");
                    Directory.Delete(full, true);
                }
                Progress = 0.6f;
                Step = "Removing shortcuts";
                if (StartShortcut) TryDelete(StartMenuLink);       // (tests run with --no-shortcuts)
                if (DesktopShortcut) TryDelete(DesktopLink);
                if (Register) Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                Progress = 0.8f;
                if (DeleteData && Directory.Exists(DataDir))
                {
                    Step = "Removing browsing data";
                    Directory.Delete(DataDir, true);
                    string company = Path.GetDirectoryName(DataDir);
                    try { if (!Directory.EnumerateFileSystemEntries(company).Any()) Directory.Delete(company); } catch { }
                }
                Step = "Done";
                Progress = 1f;
            }
            catch (Exception ex) { Error = ex.Message; }
        }

        /// <summary>Windows cannot delete a running exe: the uninstall runs from a temporary copy, which removes
        /// itself afterwards.</summary>
        public static bool RelaunchFromTemp(string[] args)
        {
            string me = Assembly.GetExecutingAssembly().Location;
            string dir = InstalledDir();
            if (dir == null || !me.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) return false;
            string tmp = Path.Combine(Path.GetTempPath(), "GBrowserUninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            File.Copy(me, tmp);
            Process.Start(new ProcessStartInfo(tmp, string.Join(" ", args.Select(a => "\"" + a + "\"")) + " --temp-copy") { UseShellExecute = false });
            return true;
        }

        public static void DeleteSelfLater()
        {
            string me = Assembly.GetExecutingAssembly().Location;
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + me + "\"")
                    { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }
    }
}
