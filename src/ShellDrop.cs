#if AUTOMATION
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace GBrowser
{
    /// <summary>Test builds: drops a data object on a folder through Explorer's own IDropTarget (what the desktop and
    /// folders use when something is dropped on them), to check that dragged pages become website shortcuts.</summary>
    internal static class ShellDrop
    {
        [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDropTarget
        {
            void DragEnter([MarshalAs(UnmanagedType.Interface)] ComDataObject data, int keys, long pt, ref int effect);
            void DragOver(int keys, long pt, ref int effect);
            void DragLeave();
            void Drop([MarshalAs(UnmanagedType.Interface)] ComDataObject data, int keys, long pt, ref int effect);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        static string logFile;
        static void Step(string s) { try { File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine); } catch { } }

        public static async Task<string> Drop(string folder, System.Windows.Forms.DataObject data)
        {
            logFile = Path.Combine(Path.GetTempPath(), "gb-shelldrop.log");
            Directory.CreateDirectory(folder);
            var before = Directory.GetFiles(folder);
            Step("item");
            var iidItem = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iidItem, out var item);
            Step("drop target");
            var bhid = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5");   // BHID_SFUIObject
            var iidDt = typeof(IDropTarget).GUID;
            int hr = item.BindToHandler(IntPtr.Zero, ref bhid, ref iidDt, out var pdt);
            if (hr != 0) return "no drop target: 0x" + hr.ToString("X8");
            var dt = (IDropTarget)Marshal.GetObjectForIUnknown(pdt);
            Marshal.Release(pdt);
            var com = (ComDataObject)data;
            int eff = 1 | 4;   // copy | link
            Step("drag enter");
            dt.DragEnter(com, 1, 0, ref eff);
            int eff2 = 1 | 4;
            Step("drag over " + eff);
            dt.DragOver(1, 0, ref eff2);
            int eff3 = eff2 != 0 ? eff2 : 1;
            Step("drop " + eff2);
            dt.Drop(com, 1, 0, ref eff3);
            Step("dropped " + eff3);
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(200);
                var now = Directory.GetFiles(folder);
                if (now.Length > before.Length)
                {
                    string f = Array.Find(now, x => Array.IndexOf(before, x) < 0);
                    return "created " + Path.GetFileName(f) + " (effects " + eff + "/" + eff2 + "/" + eff3 + "): " + File.ReadAllText(f).Replace("\r\n", " / ");
                }
            }
            return "nothing created (effects " + eff + "/" + eff2 + "/" + eff3 + ")";
        }
    }
}
#endif
