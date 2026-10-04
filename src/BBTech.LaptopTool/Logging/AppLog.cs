using System;
using System.IO;
using System.Text;

namespace BBTech.LaptopTool.Logging
{
    /// <summary>
    /// Simple thread-safe file logger (logs\app.log, rotated at 2 MB).
    /// Only technical information is logged: no user names, serial numbers,
    /// Wi-Fi network names or file contents.
    /// </summary>
    public static class AppLog
    {
        private static readonly object Sync = new object();
        private static string _path;
        private const long MaxBytes = 2 * 1024 * 1024;

        public static string LogFilePath => _path;

        public static void Init(string logsDir)
        {
            try
            {
                Directory.CreateDirectory(logsDir);
                _path = Path.Combine(logsDir, "app.log");
                Rotate();
            }
            catch
            {
                _path = null; // logging is best-effort; never block the app
            }
        }

        public static void Info(string message) => Write("INFO ", message, null);
        public static void Warn(string message, Exception ex = null) => Write("WARN ", message, ex);
        public static void Error(string message, Exception ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception ex)
        {
            if (_path == null) return;
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)).Append(" [").Append(level).Append("] ").Append(Sanitize(message));
            if (ex != null) sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            sb.AppendLine();
            if (ex != null && level == "ERROR") sb.AppendLine("    " + (ex.StackTrace ?? "").Replace(Environment.NewLine, Environment.NewLine + "    "));

            lock (Sync)
            {
                try { File.AppendAllText(_path, sb.ToString(), Encoding.UTF8); }
                catch { /* disk full / write-protected: ignore */ }
            }
        }

        /// <summary>Strips the app folder, the user profile path and the Windows user name from log text.</summary>
        private static string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            string s = message;
            try
            {
                string app = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                if (app.Length > 3) s = s.Replace(app, "<app>");
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(profile)) s = s.Replace(profile, "%USERPROFILE%");
                string user = Environment.UserName;
                if (!string.IsNullOrEmpty(user) && user.Length > 2) s = s.Replace(user, "<user>");
            }
            catch { }
            return s;
        }

        private static void Rotate()
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = Path.Combine(fi.DirectoryName, "app.1.log");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
            }
            catch { }
        }
    }
}
