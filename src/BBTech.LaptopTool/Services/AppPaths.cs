using System;
using System.IO;
using System.Reflection;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// Layout next to the executable (USB root):
    ///   BBTech Laptop Tool.exe        the only file that has to be copied
    ///   BBTech Data\                  created on first use: exports\, reports\, logs\
    /// Embedded tools are extracted to %TEMP% on demand (see ToolBundle) and removed on exit.
    /// If the USB drive is write-protected, writable folders fall back to
    /// %LOCALAPPDATA%\BBTech Laptop Tool so the app keeps working.
    /// </summary>
    public static class AppPaths
    {
        public static string BaseDir { get; private set; }

        public static string DataDir { get; private set; }
        public static string LogsDir { get; private set; }
        public static string ExportsDir => Path.Combine(DataDir, "exports");
        public static string ReportsDir => Path.Combine(DataDir, "reports");
        public static string TempDir => Path.Combine(Path.GetTempPath(), "BBTechLaptopTool", "work");
        public static bool UsingFallbackStorage { get; private set; }

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        public static void Init()
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

            string data = Path.Combine(BaseDir, "BBTech Data");
            string logs = Path.Combine(data, "logs");
            if (!IsWritable(data) || !IsWritable(logs))
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BBTech Laptop Tool");
                data = Path.Combine(root, "data");
                logs = Path.Combine(root, "logs");
                UsingFallbackStorage = true;
            }
            DataDir = data;
            LogsDir = logs;
        }

        public static bool IsRunningFromRemovableDrive
        {
            get
            {
                try { return new DriveInfo(Path.GetPathRoot(BaseDir)).DriveType == DriveType.Removable; }
                catch { return false; }
            }
        }

        public static string DriveLetter
        {
            get { try { return Path.GetPathRoot(BaseDir); } catch { return ""; } }
        }

        public static string Ensure(string dir)
        {
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>A unique temp file inside data\temp (deleted by the caller).</summary>
        public static string NewTempFile(string extension)
        {
            Ensure(TempDir);
            return Path.Combine(TempDir, Guid.NewGuid().ToString("N") + extension);
        }

        public static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }
}

