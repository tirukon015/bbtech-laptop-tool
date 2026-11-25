using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BBTech.Core.Security;

namespace BBTech.Core.Storage
{
    /// <summary>
    /// A JSON list file (audit log, USB sync queue) that never loses data silently.
    ///
    /// If the file cannot be read, it is NOT treated as empty and overwritten on the next write: the
    /// damaged file is moved aside as "*.corrupt-yyyyMMddHHmmss" (kept for recovery) and the last good
    /// copy (".bak", kept by <see cref="SafeFile"/>) is used instead. If the damaged file cannot be
    /// moved aside, the read throws, so the caller fails instead of destroying it.
    /// </summary>
    public static class JsonListFile
    {
        public static List<T> Read<T>(string path, out string quarantinedTo)
        {
            quarantinedTo = null;
            if (!File.Exists(path)) return new List<T>();

            if (TryParse(path, out List<T> items)) return items;

            // Damaged: keep it, never overwrite it.
            string aside = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            int n = 1;
            while (File.Exists(aside)) aside = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + n++;
            try { File.Move(path, aside); }
            catch (Exception ex)
            {
                throw new IOException("The file " + Path.GetFileName(path) + " is damaged and could not be set aside; it was not changed.", ex);
            }
            quarantinedTo = aside;

            string bak = path + ".bak";
            if (File.Exists(bak) && TryParse(bak, out List<T> fromBackup))
            {
                // Restore the last good copy as the live file so later writes continue from it.
                File.Copy(bak, path, true);
                return fromBackup;
            }
            return new List<T>();
        }

        public static void Write<T>(string path, List<T> items)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            SafeFile.WriteAllBytesAtomic(path, Json.ToBytes(items ?? new List<T>()));
        }

        private static bool TryParse<T>(string path, out List<T> items)
        {
            items = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0) { items = new List<T>(); return true; }
                items = Json.FromBytes<List<T>>(bytes);
                return items != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
