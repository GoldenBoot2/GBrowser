using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GBrowser
{
    /// <summary>The modern Windows folder picker (IFileOpenDialog with FOS_PICKFOLDERS, as in File Explorer).
    /// WinForms' FolderBrowserDialog on .NET Framework is the old tree dialog. Also the user's Downloads folder.</summary>
    internal static class FolderPicker
    {
        /// <summary>The user's Downloads folder (Known Folder, so a moved Downloads folder is found too).</summary>
        public static string WindowsDownloads()
        {
            try
            {
                var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");   // FOLDERID_Downloads
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
                    try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        /// <summary>Shows the picker; returns the chosen folder or null.</summary>
        public static string Pick(IntPtr owner, string title, string start)
        {
            var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
            try
            {
                dlg.GetOptions(out uint o);
                dlg.SetOptions(o | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                {
                    var iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(start, IntPtr.Zero, ref iid, out var item) == 0) dlg.SetFolder(item);
                }
                if (dlg.Show(owner) != 0) return null;   // cancelled
                dlg.GetResult(out var res);
                res.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                return path;
            }
            finally { Marshal.FinalReleaseComObject(dlg); }
        }

        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogRCW { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr specs);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si);
            void SetFolder(IShellItem si);
            void GetFolder(out IShellItem si);
            void GetCurrentSelection(out IShellItem si);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem si);
            void AddPlace(IShellItem si, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem si);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attrs);
            void Compare(IShellItem si, uint hint, out int order);
        }
    }
}
