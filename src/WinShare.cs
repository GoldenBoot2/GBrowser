using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;

namespace GBrowser
{
    /// <summary>Main menu > Share...: the page's title and address in the Windows share sheet (like Edge's Share),
    /// shown over GBrowser's window (DataTransferManager for a desktop window, IDataTransferManagerInterop).</summary>
    internal static class WinShare
    {
        [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDataTransferManagerInterop
        {
            IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
            void ShowShareUIForWindow(IntPtr appWindow);
        }

        static readonly Guid IDataTransferManagerId = new Guid("a5caee9b-8708-49d1-8d36-67d25a8da00c");
        static DataTransferManager manager;
        static IntPtr managerWindow;
        static string shareTitle, shareUrl;

        public static bool Share(IntPtr hwnd, string title, string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
                shareTitle = string.IsNullOrWhiteSpace(title) ? uri.Host : title;
                shareUrl = uri.AbsoluteUri;
                var interop = (IDataTransferManagerInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(DataTransferManager));
                if (manager == null || managerWindow != hwnd)
                {
                    var g = IDataTransferManagerId;
                    var ptr = interop.GetForWindow(hwnd, ref g);
                    manager = (DataTransferManager)Marshal.GetObjectForIUnknown(ptr);
                    Marshal.Release(ptr);
                    managerWindow = hwnd;
                    manager.DataRequested += (s, e) =>
                    {
                        var d = e.Request.Data;
                        d.Properties.Title = shareTitle;
                        d.Properties.Description = shareUrl;
                        d.SetWebLink(new Uri(shareUrl));
                        d.SetText(shareUrl);
                    };
                }
                interop.ShowShareUIForWindow(hwnd);
                return true;
            }
            catch { return false; }
        }
    }
}
