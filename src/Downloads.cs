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
            // multi-part downloads (FastDownload): saved so they resume after a restart
            public string Engine { get; set; }        // null = the engine's own download, "multi" = FastDownload
            public string Referer { get; set; }
            public string ETag { get; set; }
            public string LastModified { get; set; }
            public string Parts { get; set; }         // "start,end,pos;..."
            public long ActiveMs { get; set; }        // time spent downloading (pauses not counted)
            public bool OpenWhenDone { get; set; }    // open the file as soon as it has finished
            public string ReplaceTarget { get; set; } // "Replace": downloaded beside it, then moved over this file when done
            [System.Web.Script.Serialization.ScriptIgnore] public DateTime FullAt;   // all bytes in, engine still not done
            [System.Web.Script.Serialization.ScriptIgnore] public CoreWebView2 Core; // the tab that started it
            [System.Web.Script.Serialization.ScriptIgnore] public bool Blocking;     // GBrowser is cancelling it because SmartScreen held it
            [System.Web.Script.Serialization.ScriptIgnore] public DateTime? RunningSince;
            // speed of the engine's own downloads (multi-part ones report theirs): bytes per second, smoothed
            [System.Web.Script.Serialization.ScriptIgnore] public double EngineSpeed;
            [System.Web.Script.Serialization.ScriptIgnore] public long SpeedBytes;
            [System.Web.Script.Serialization.ScriptIgnore] public DateTime SpeedAt;
            /// <summary>Downloading time so far, including the running stretch.</summary>
            public long DurationMs => ActiveMs + (RunningSince.HasValue ? (long)(DateTime.UtcNow - RunningSince.Value).TotalMilliseconds : 0);
            [System.Web.Script.Serialization.ScriptIgnore] public CoreWebView2DownloadOperation Op;
            [System.Web.Script.Serialization.ScriptIgnore] public FastDownload Fast;
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
                    // the engine cannot resume its downloads after a restart; multi-part ones can (progress is saved)
                    foreach (var i in items)
                        if (i.State == "in_progress" || i.State == "paused")
                            i.State = i.Engine == "multi" && !string.IsNullOrEmpty(i.Parts) ? "paused" : "interrupted";
                    nextId = items.Count == 0 ? 1 : items.Max(i => i.Id) + 1;
                    if (Store.WasPlain(FileName)) Save();   // encrypt a plain file from an older version
                }
                return items;
            }
        }

        static readonly object saveGate = new object();

        public static void Save()
        {
            // download threads save too: if the list changes while it is copied (a new or removed entry), copy again
            lock (saveGate)
                for (int i = 0; ; i++)
                    try { Store.WriteText(FileName, Store.ToJson(All.Take(500).ToList())); return; }
                    catch (InvalidOperationException) when (i < 5) { System.Threading.Thread.Sleep(20); }
        }

        /// <summary>A new list entry for a download GBrowser runs itself.</summary>
        public static Item NewItem(string url, string path, long total)
        {
            var it = new Item { Id = nextId++, Url = url, Path = path, Total = total, State = "in_progress", Time = Store.Now() };
            All.Insert(0, it);
            Save();
            return it;
        }

        public static void Notify(Item it) { try { Changed?.Invoke(); } catch { } }

        /// <summary>"Replace": the finished file takes the place of the existing one (only after it downloaded fully,
        /// so a failed download never costs the old file).</summary>
        public static void CompleteReplace(Item it)
        {
            string target = it.ReplaceTarget;
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(it.Path) || !File.Exists(it.Path)) return;
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(it.Path, target);   // same folder: the internet mark (an NTFS stream) moves with it
                it.Path = target;
                it.ReplaceTarget = null;
            }
            catch { }
        }

        /// <summary>A download finished: "Open when done" opens it (through the shell, so SmartScreen checks it).</summary>
        public static void Finished(Item it)
        {
            if (!it.OpenWhenDone || string.IsNullOrEmpty(it.Path) || !File.Exists(it.Path)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(it.Path) { UseShellExecute = true }); } catch { }
        }

        /// <summary>Downloading time: counts while a download runs, stops while it is paused or done.</summary>
        public static void MarkRunning(Item it) { if (!it.RunningSince.HasValue) it.RunningSince = DateTime.UtcNow; }
        public static void MarkStopped(Item it)
        {
            if (!it.RunningSince.HasValue) return;
            it.ActiveMs += (long)(DateTime.UtcNow - it.RunningSince.Value).TotalMilliseconds;
            it.RunningSince = null;
        }

        public static Item Track(CoreWebView2DownloadOperation op, string path = null)
        {
            var it = new Item
            {
                Id = nextId++, Url = op.Uri, Path = path ?? op.ResultFilePath, Total = (long)(op.TotalBytesToReceive ?? 0),
                Received = op.BytesReceived, State = "in_progress", Time = Store.Now(), Op = op,
            };
            All.Insert(0, it);
            MarkRunning(it);
            op.BytesReceivedChanged += (s, e) =>
            {
                it.Received = op.BytesReceived; it.Total = (long)(op.TotalBytesToReceive ?? (ulong)it.Total);
                var now = DateTime.UtcNow;
                if (it.Total > 0 && it.Received >= it.Total && it.FullAt == default(DateTime)) it.FullAt = now;
                double sec = (now - it.SpeedAt).TotalSeconds;
                if (it.SpeedAt == default(DateTime)) { it.SpeedAt = now; it.SpeedBytes = it.Received; }
                else if (sec >= 0.5)
                {
                    double inst = Math.Max(0, it.Received - it.SpeedBytes) / sec;
                    it.EngineSpeed = it.EngineSpeed <= 0 ? inst : it.EngineSpeed * 0.7 + inst * 0.3;
                    it.SpeedAt = now; it.SpeedBytes = it.Received;
                }
                Changed?.Invoke();
            };
            op.StateChanged += (s, e) =>
            {
                it.Path = op.ResultFilePath;
                if (op.State == CoreWebView2DownloadState.InProgress) MarkRunning(it); else MarkStopped(it);
                switch (op.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        it.State = "completed"; it.Received = op.BytesReceived; it.Op = null; it.Path = op.ResultFilePath;
                        CompleteReplace(it); Finished(it); break;
                    case CoreWebView2DownloadState.Interrupted:
                        if (it.Blocking) { it.State = "blocked"; it.Reason = "SmartScreen"; it.Op = null; break; }
                        it.State = op.CanResume ? "paused" : "interrupted";
                        it.Reason = op.InterruptReason.ToString();
                        if (!op.CanResume) it.Op = null;
                        break;
                    default: if (it.State != "blocked") it.State = "in_progress"; break;
                }
                Save();
                Changed?.Invoke();
            };
            Save();
            Changed?.Invoke();
            return it;
        }

        public static Item Find(int id) => All.FirstOrDefault(i => i.Id == id);
    }
}
