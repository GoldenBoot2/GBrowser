using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GBrowser
{
    /// <summary>Minimal READ-ONLY reader for SQLite database files (format: https://www.sqlite.org/fileformat2.html),
    /// enough to read whole tables of another browser's profile (Chrome / Edge Favicons, later History) without a native
    /// SQLite library. Opens the file shared (the browser may be running), reads only the pages it needs, never writes,
    /// ignores journals (a row being written right now may be missed). Table b-trees only, UTF-8 databases only.</summary>
    internal sealed class SqliteReader : IDisposable
    {
        readonly FileStream fs;
        readonly int pageSize, usable;
        readonly Dictionary<long, byte[]> pages = new Dictionary<long, byte[]>();
        const int MaxCachedPages = 512;

        public SqliteReader(string path)
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var h = new byte[100];
            if (fs.Read(h, 0, 100) != 100 || Encoding.ASCII.GetString(h, 0, 15) != "SQLite format 3") throw new InvalidDataException("Not an SQLite database");
            int ps = (h[16] << 8) | h[17];
            pageSize = ps == 1 ? 65536 : ps;
            if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0) throw new InvalidDataException("Bad page size");
            usable = pageSize - h[20];
            int enc = (h[56] << 24) | (h[57] << 16) | (h[58] << 8) | h[59];
            if (enc != 0 && enc != 1) throw new InvalidDataException("Only UTF-8 databases are read");
        }

        public void Dispose() => fs.Dispose();

        byte[] Page(long n)
        {
            if (pages.TryGetValue(n, out var p)) return p;
            if (n < 1 || (n - 1) * pageSize >= fs.Length) throw new InvalidDataException("Page out of range");
            p = new byte[pageSize];
            fs.Position = (n - 1) * pageSize;
            int got = 0;
            while (got < pageSize) { int r = fs.Read(p, got, pageSize - got); if (r <= 0) break; got += r; }
            if (pages.Count >= MaxCachedPages) pages.Clear();
            pages[n] = p;
            return p;
        }

        static long Varint(byte[] b, ref int i)
        {
            long v = 0;
            for (int k = 0; k < 8; k++)
            {
                byte c = b[i++];
                v = (v << 7) | (long)(c & 0x7F);
                if ((c & 0x80) == 0) return v;
            }
            return (v << 8) | b[i++];
        }

        /// <summary>Root page of a table, from sqlite_master (columns: type, name, tbl_name, rootpage, sql); 0 when missing.</summary>
        public long RootPage(string table)
        {
            foreach (var row in Scan(1))
                if (row.Values.Length >= 4 && row.Values[0] as string == "table" && string.Equals(row.Values[1] as string, table, StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt64(row.Values[3]);
            return 0;
        }

        public sealed class Row { public long RowId; public object[] Values; }

        /// <summary>Every row of a table (an INTEGER PRIMARY KEY column reads as null: use RowId).</summary>
        public IEnumerable<Row> Rows(string table)
        {
            long root = RootPage(table);
            if (root == 0) yield break;
            foreach (var r in Scan(root)) yield return r;
        }

        IEnumerable<Row> Scan(long root)
        {
            // iterative walk of a table b-tree (interior 0x05 / leaf 0x0D)
            var stack = new Stack<long>();
            stack.Push(root);
            int guard = 0;
            while (stack.Count > 0)
            {
                if (++guard > 1000000) yield break;   // a damaged file must not loop forever
                long pn = stack.Pop();
                var p = Page(pn);
                int off = pn == 1 ? 100 : 0;
                byte kind = p[off];
                int cells = (p[off + 3] << 8) | p[off + 4];
                if (kind == 0x05)
                {
                    long right = ((long)p[off + 8] << 24) | ((long)p[off + 9] << 16) | ((long)p[off + 10] << 8) | p[off + 11];
                    // children are pushed in reverse so rows come out in rowid order
                    stack.Push(right);
                    for (int c = cells - 1; c >= 0; c--)
                    {
                        int ptr = (p[off + 12 + c * 2] << 8) | p[off + 13 + c * 2];
                        long child = ((long)p[ptr] << 24) | ((long)p[ptr + 1] << 16) | ((long)p[ptr + 2] << 8) | p[ptr + 3];
                        stack.Push(child);
                    }
                }
                else if (kind == 0x0D)
                {
                    for (int c = 0; c < cells; c++)
                    {
                        Row row = null;
                        try
                        {
                            int ptr = (p[off + 8 + c * 2] << 8) | p[off + 9 + c * 2];
                            int i = ptr;
                            long size = Varint(p, ref i);
                            long rowid = Varint(p, ref i);
                            row = new Row { RowId = rowid, Values = Record(Payload(p, i, size)) };
                        }
                        catch { }
                        if (row != null) yield return row;
                    }
                }
            }
        }

        /// <summary>A cell's whole payload: the part on the page plus its overflow pages.</summary>
        byte[] Payload(byte[] p, int start, long size)
        {
            if (size < 0 || size > 64L * 1024 * 1024) throw new InvalidDataException("Bad payload size");
            int x = usable - 35;
            var data = new byte[size];
            if (size <= x) { Buffer.BlockCopy(p, start, data, 0, (int)size); return data; }
            int m = ((usable - 12) * 32 / 255) - 23;
            int k = (int)(m + ((size - m) % (usable - 4)));
            int local = k <= x ? k : m;
            Buffer.BlockCopy(p, start, data, 0, local);
            long next = ((long)p[start + local] << 24) | ((long)p[start + local + 1] << 16) | ((long)p[start + local + 2] << 8) | p[start + local + 3];
            int done = local, hops = 0;
            while (done < size && next != 0 && hops++ < 100000)
            {
                var o = Page(next);
                next = ((long)o[0] << 24) | ((long)o[1] << 16) | ((long)o[2] << 8) | o[3];
                int n = (int)Math.Min(usable - 4, size - done);
                Buffer.BlockCopy(o, 4, data, done, n);
                done += n;
            }
            if (done < size) throw new InvalidDataException("Overflow chain cut");
            return data;
        }

        /// <summary>Decodes a record: header of serial types, then the values (null, long, double, byte[], string).</summary>
        static object[] Record(byte[] d)
        {
            int i = 0;
            long hsize = Varint(d, ref i);
            var types = new List<long>();
            while (i < hsize) types.Add(Varint(d, ref i));
            int pos = (int)hsize;
            var vals = new object[types.Count];
            for (int c = 0; c < types.Count; c++)
            {
                long t = types[c];
                switch (t)
                {
                    case 0: vals[c] = null; break;
                    case 1: vals[c] = (long)(sbyte)d[pos]; pos += 1; break;
                    case 2: vals[c] = (long)(short)((d[pos] << 8) | d[pos + 1]); pos += 2; break;
                    case 3: vals[c] = SignedBE(d, pos, 3); pos += 3; break;
                    case 4: vals[c] = SignedBE(d, pos, 4); pos += 4; break;
                    case 5: vals[c] = SignedBE(d, pos, 6); pos += 6; break;
                    case 6: vals[c] = SignedBE(d, pos, 8); pos += 8; break;
                    case 7: vals[c] = BitConverter.Int64BitsToDouble(SignedBE(d, pos, 8)); pos += 8; break;
                    case 8: vals[c] = 0L; break;
                    case 9: vals[c] = 1L; break;
                    default:
                        if (t >= 12)
                        {
                            int len = (int)((t - (t % 2 == 0 ? 12 : 13)) / 2);
                            if (t % 2 == 0) { var b = new byte[len]; Buffer.BlockCopy(d, pos, b, 0, len); vals[c] = b; }
                            else vals[c] = Encoding.UTF8.GetString(d, pos, len);
                            pos += len;
                        }
                        break;
                }
            }
            return vals;
        }

        static long SignedBE(byte[] d, int pos, int n)
        {
            long v = (sbyte)d[pos];   // sign from the first byte
            for (int k = 1; k < n; k++) v = (v << 8) | d[pos + k];
            return v;
        }
    }
}
