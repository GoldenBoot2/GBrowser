using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GBrowser
{
    /// <summary>Marks a file GBrowser downloaded itself as "from the internet" the way browsers do: Windows Attachment
    /// Services (IAttachmentExecute.Save) writes the Mark of the Web (Zone.Identifier with the download and referrer
    /// addresses) and lets the antivirus scan the file. Without the mark, SmartScreen and Office Protected View do not
    /// check the file when it is opened. (The engine's own downloads are marked by the engine.)</summary>
    internal static class Attachment
    {
        /// <summary>Returns null when done, else the error text (the file is kept either way).</summary>
        public static string Mark(string path, string source, string referrer)
        {
            string error = null;
            // a COM object of the shell: needs a single-threaded apartment
            var t = new Thread(() =>
            {
                IAttachmentExecute ae = null;
                try
                {
                    ae = (IAttachmentExecute)new AttachmentServices();
                    var guid = new Guid("6E9D2C1C-6B6F-4B9F-9B7A-6F8D3C4A1B20");   // GBrowser's client id for Attachment Services
                    ae.SetClientGuid(ref guid);
                    ae.SetClientTitle("GBrowser");
                    ae.SetLocalPath(path);
                    if (!string.IsNullOrEmpty(source)) ae.SetSource(source);
                    if (!string.IsNullOrEmpty(referrer)) ae.SetReferrer(referrer);
                    int hr = ae.Save();
                    if (hr < 0) error = "Attachment Services 0x" + hr.ToString("X8");
                }
                catch (Exception ex) { error = ex.Message; }
                finally { if (ae != null) Marshal.FinalReleaseComObject(ae); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(60000);   // the antivirus scan can take a moment on big files
            return error;
        }

        [ComImport, Guid("4125dd96-e03a-4103-8f70-e0597d803b9c")] class AttachmentServices { }

        [ComImport, Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAttachmentExecute
        {
            void SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetClientGuid(ref Guid guid);
            void SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string path);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
            void SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);
            [PreserveSig] int CheckPolicy();
            [PreserveSig] int Prompt(IntPtr hwnd, int prompt, out int action);
            [PreserveSig] int Save();
            [PreserveSig] int Execute(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string verb, out IntPtr process);
            [PreserveSig] int SaveWithUI(IntPtr hwnd);
            void ClearClientState();
        }
    }
}
