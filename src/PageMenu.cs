using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>The page right-click menu, in GBrowser's Material menu instead of the engine's (Microsoft Edge) one,
    /// laid out like Chrome's. Items such as Save as / Copy image / Paste run the engine's own command
    /// (SelectedCommandId), so they behave exactly as before; the engine's Edge-only extras ("More tools", "Share",
    /// "Magnify image", "Writing direction") are left out. Engine item names measured 19-11-2026 (WebView2 157).</summary>
    internal static class PageMenu
    {
        public sealed class Host
        {
            public Action<string> NewTab, NewPrivateTab;
            public Func<string, string> SearchUrl;
        }

        public static void Show(BrowserTab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e, Host host)
        {
            var target = e.ContextMenuTarget;
            // engine commands by their (stable, English) names
            var engine = new Dictionary<string, CoreWebView2ContextMenuItem>(StringComparer.Ordinal);
            void Collect(IList<CoreWebView2ContextMenuItem> items)
            {
                foreach (var i in items)
                {
                    if (i.Kind == CoreWebView2ContextMenuItemKind.Submenu) Collect(i.Children);
                    else if (i.Kind != CoreWebView2ContextMenuItemKind.Separator && !engine.ContainsKey(i.Name)) engine[i.Name] = i;
                }
            }
            Collect(e.MenuItems);

            var deferral = e.GetDeferral();
            e.Handled = true;
            bool done = false;
            void Finish() { if (done) return; done = true; try { deferral.Complete(); } catch { } }

            var m = new MaterialMenu();
            // an engine command: chosen in GBrowser's menu, run by the engine
            void Cmd(string name, string label, string icon, string keys = null)
            {
                if (!engine.TryGetValue(name, out var it)) return;
                var entry = m.Add(label, icon, keys, () => { e.SelectedCommandId = it.CommandId; Finish(); });
                entry.Enabled = it.IsEnabled;
            }
            void Own(string label, string icon, string keys, Action a, bool enabled = true) =>
                m.Add(label, icon, keys, () => { Finish(); a(); }).Enabled = enabled;
            void Sep() { if (m.Items.Count > 0 && !m.Items[m.Items.Count - 1].Separator) m.AddSeparator(); }

            bool link = target.HasLinkUri && !string.IsNullOrEmpty(target.LinkUri);
            bool media = target.HasSourceUri && !string.IsNullOrEmpty(target.SourceUri);
            string selection = target.HasSelection ? (target.SelectionText ?? "").Trim() : "";

            if (target.IsEditable)
            {
                // text field: spelling suggestions from the engine first (any command we do not otherwise list)
                Cmd("undo", "Undo", IconData.Restore, "Ctrl+Z");
                Cmd("redo", "Redo", IconData.Refresh, "Ctrl+Y");
                Sep();
                Cmd("cut", "Cut", null, "Ctrl+X");
                Cmd("copy", "Copy", IconData.Copy, "Ctrl+C");
                Cmd("paste", "Paste", null, "Ctrl+V");
                Cmd("pasteAndMatchStyle", "Paste as plain text", null, "Ctrl+Shift+V");
                Cmd("selectAll", "Select all", null, "Ctrl+A");
                Sep();
                Cmd("emoji", "Emoji", null, "Win+.");
            }
            else
            {
                if (link)
                {
                    string url = target.LinkUri;
                    Own("Open link in new tab", IconData.OpenNew, null, () => host.NewTab(url));
                    Own("Open link in private tab", IconData.Private, null, () => host.NewPrivateTab(url));
                    Sep();
                    Cmd("saveLinkAs", "Save link as...", IconData.Download);
                    Cmd("copyLinkLocation", "Copy link address", IconData.Link);
                    Sep();
                }
                bool image = target.Kind == CoreWebView2ContextMenuTargetKind.Image;
                if (image)
                {
                    // (images given inline as data: have no source address; the engine still saves / copies them)
                    string src = media ? target.SourceUri : null;
                    if (src != null && (src.StartsWith("http://") || src.StartsWith("https://")))
                        Own("Open image in new tab", IconData.OpenNew, null, () => host.NewTab(src));
                    Cmd("saveImageAs", "Save image as...", IconData.Download);
                    Cmd("copyImage", "Copy image", IconData.Copy);
                    Cmd("copyImageLocation", "Copy image address", IconData.Link);
                    Sep();
                }
                else if ((target.Kind == CoreWebView2ContextMenuTargetKind.Video || target.Kind == CoreWebView2ContextMenuTargetKind.Audio) && media)
                {
                    string src = target.SourceUri;
                    // the engine's media commands (loop, controls, picture-in-picture, save) as it offers them
                    foreach (var it in e.MenuItems.Where(i => i.Kind != CoreWebView2ContextMenuItemKind.Separator && i.Kind != CoreWebView2ContextMenuItemKind.Submenu &&
                                 i.Name != "inspectElement" && i.Name != "share"))
                    {
                        var item = it;
                        var entry = m.Add(item.Label.Replace("&", ""), null, null, () => { e.SelectedCommandId = item.CommandId; Finish(); });
                        entry.Enabled = item.IsEnabled;
                        if (item.Kind == CoreWebView2ContextMenuItemKind.CheckBox) { entry.Toggle = true; entry.Checked = item.IsChecked; }
                    }
                    if (!src.StartsWith("blob:")) Own("Open " + (target.Kind == CoreWebView2ContextMenuTargetKind.Video ? "video" : "audio") + " in new tab", IconData.OpenNew, null, () => host.NewTab(src));
                    Sep();
                }
                if (selection.Length > 0)
                {
                    Cmd("copy", "Copy", IconData.Copy, "Ctrl+C");
                    string shown = selection.Length > 30 ? selection.Substring(0, 29) + "…" : selection;
                    Own("Search " + Search.EngineName() + " for “" + shown + "”", IconData.Search, null, () => host.NewTab(host.SearchUrl(selection)));
                    Sep();
                }
                if (!link && !media && !image && selection.Length == 0)
                {
                    Own("Back", IconData.Back, "Alt+Left", () => { if (core.CanGoBack) core.GoBack(); }, core.CanGoBack);
                    Own("Forward", IconData.Forward, "Alt+Right", () => { if (core.CanGoForward) core.GoForward(); }, core.CanGoForward);
                    Own("Reload", IconData.Refresh, "Ctrl+R", () => core.Reload());
                    Sep();
                    Cmd("saveAs", "Save as...", IconData.Download, "Ctrl+S");
                    Cmd("print", "Print...", IconData.Print, "Ctrl+P");
                    Sep();
                    string page = core.Source;
                    if (page != null && (page.StartsWith("http://") || page.StartsWith("https://")))
                        Own("View page source", IconData.Code, "Ctrl+U", () => host.NewTab("view-source:" + page));
                }
                else if (selection.Length > 0) Cmd("print", "Print...", IconData.Print, "Ctrl+P");
            }
            Sep();
            Cmd("inspectElement", "Inspect", IconData.Code, "Ctrl+Shift+I");
            while (m.Items.Count > 0 && m.Items[m.Items.Count - 1].Separator) m.Items.RemoveAt(m.Items.Count - 1);

            if (m.Items.Count == 0) { Finish(); return; }
            // closing without a choice lets the engine finish the event with nothing to run; a choice runs first
            m.FormClosed += (s, a) => tab.View.BeginInvoke((Action)Finish);
            var at = e.Location;
            if (!m.ShowAt(tab.View, new Rectangle(at.X, at.Y - 4, 1, 1), false, null)) Finish();
        }
    }
}
