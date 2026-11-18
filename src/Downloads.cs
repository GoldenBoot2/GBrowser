using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Downloads list for gbrowser://downloads: every download of every tab, with live progress while the
    /// app runs; finished entries are kept in downloads.json (max 500).</summary>
    internal static class Downloads
    {
        public sealed class Item
        {
            public int Id { get; set; }
            public string Url { get; set; }
            public string Path { get; set; }
            public long Total { get; set; }
            public long Received { get; set; }
            public string State { get; set; }   // in_progress, paused, completed, interrupted
            public string Reason { get; set; }
            public long Time { get; set; }
            [System.Web.Script.Serialization.ScriptIgnore] public CoreWebView2DownloadOperation Op;
        }

        const string FileName = "downloads.json";   // encrypted (Store.ReadText / WriteText)
        static List<Item> items;
        static int nextId = 1;
        public static event Action Changed;

        public static List<Item> All
        {
            get
            {
                if (items == null)
                {
                    try { var t = Store.ReadText(FileName); items = t == null ? null : Store.FromJson<List<Item>>(t); } catch { }
                    if (items == null) items = new List<Item>();
                    foreach (var i in items) if (i.State == "in_progress" || i.State == "paused") i.State = "interrupted";
                    nextId = items.Count == 0 ? 1 : items.Max(i => i.Id) + 1;
                    if (Store.WasPlain(FileName)) Save();   // encrypt a plain file from an older version
                }
                return items;
            }
        }

        public static void Save()
        {
            Store.WriteText(FileName, Store.ToJson(All.Take(500).ToList()));
        }

        public static void Track(CoreWebView2DownloadOperation op)
        {
            var it = new Item
            {
                Id = nextId++, Url = op.Uri, Path = op.ResultFilePath, Total = (long)(op.TotalBytesToReceive ?? 0),
                Received = op.BytesReceived, State = "in_progress", Time = Store.Now(), Op = op,
            };
            All.Insert(0, it);
            op.BytesReceivedChanged += (s, e) => { it.Received = op.BytesReceived; it.Total = (long)(op.TotalBytesToReceive ?? (ulong)it.Total); Changed?.Invoke(); };
            op.StateChanged += (s, e) =>
            {
                it.Path = op.ResultFilePath;
                switch (op.State)
                {
                    case CoreWebView2DownloadState.Completed: it.State = "completed"; it.Received = op.BytesReceived; it.Op = null; break;
                    case CoreWebView2DownloadState.Interrupted:
                        it.State = op.CanResume ? "paused" : "interrupted";
                        it.Reason = op.InterruptReason.ToString();
                        if (!op.CanResume) it.Op = null;
                        break;
                    default: it.State = "in_progress"; break;
                }
                Save();
                Changed?.Invoke();
            };
            Save();
            Changed?.Invoke();
        }

        public static Item Find(int id) => All.FirstOrDefault(i => i.Id == id);
    }
}
