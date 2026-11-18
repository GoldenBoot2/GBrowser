using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GBrowser
{
    /// <summary>GBrowser's own data files (history, bookmarks, session, settings, downloads, extension list) are stored
    /// encrypted with Windows DPAPI for the current user (AES-256 + HMAC-SHA512 on Windows 10/11), with
    /// GBrowser-specific entropy (a constant plus a random 32-byte salt made once per install). Only this Windows
    /// account on this PC can read them; another account, or the files copied to another PC, cannot.
    /// Format: "GBE1" + DPAPI blob. Plain files from older versions are read once and re-saved encrypted.</summary>
    internal static class SecureFile
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("GBE1");
        static byte[] entropy;

        static byte[] Entropy
        {
            get
            {
                if (entropy != null) return entropy;
                string saltFile = Path.Combine(Store.DataDir, "salt.bin");
                byte[] salt = null;
                try { if (File.Exists(saltFile)) salt = File.ReadAllBytes(saltFile); } catch { }
                if (salt == null || salt.Length != 32)
                {
                    salt = new byte[32];
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
                    Directory.CreateDirectory(Store.DataDir);
                    File.WriteAllBytes(saltFile, salt);
                }
                var k = Encoding.UTF8.GetBytes("GoldenBoot.GBrowser.v1/");
                entropy = new byte[k.Length + salt.Length];
                Buffer.BlockCopy(k, 0, entropy, 0, k.Length);
                Buffer.BlockCopy(salt, 0, entropy, k.Length, salt.Length);
                return entropy;
            }
        }

        public static bool IsEncrypted(byte[] data) =>
            data.Length > Magic.Length && data[0] == Magic[0] && data[1] == Magic[1] && data[2] == Magic[2] && data[3] == Magic[3];

        /// <summary>Text of a file (decrypted); null when missing or unreadable. wasPlain = an old unencrypted file.</summary>
        public static string Read(string path, out bool wasPlain)
        {
            wasPlain = false;
            if (!File.Exists(path)) return null;
            var data = File.ReadAllBytes(path);
            if (IsEncrypted(data))
            {
                var blob = new byte[data.Length - Magic.Length];
                Buffer.BlockCopy(data, Magic.Length, blob, 0, blob.Length);
                var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
                try { return Encoding.UTF8.GetString(plain); } finally { Array.Clear(plain, 0, plain.Length); }
            }
            wasPlain = true;
            return Encoding.UTF8.GetString(data).TrimStart('﻿');
        }

        /// <summary>Writes text encrypted, atomically (temp file + replace), so a crash never leaves a half-written file.</summary>
        public static void Write(string path, string text)
        {
            var plain = Encoding.UTF8.GetBytes(text);
            byte[] blob;
            try { blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
            finally { Array.Clear(plain, 0, plain.Length); }
            var data = new byte[Magic.Length + blob.Length];
            Buffer.BlockCopy(Magic, 0, data, 0, Magic.Length);
            Buffer.BlockCopy(blob, 0, data, Magic.Length, blob.Length);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
    }
}
