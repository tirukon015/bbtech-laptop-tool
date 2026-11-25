using System;
using System.Diagnostics;
using System.IO;

namespace BBTech.Core.Software
{
    public sealed class ActivationStatus
    {
        public bool IsActivated { get; set; }
        /// <summary>
        /// False when Windows cannot tell (e.g. Microsoft 365 subscription licensing, which Software Protection
        /// does not report). Then the status is UNKNOWN: neither PASS nor FAIL may be recorded from it.
        /// </summary>
        public bool Determinable { get; set; } = true;
        public string ProductName { get; set; }
        public string LicenseStatus { get; set; }
        public string Details { get; set; }
    }

    public sealed class ActivationResult
    {
        public bool Success { get; set; }
        public string Target { get; set; }
        public string Status { get; set; }
        public int ExitCode { get; set; } = -1;
        public string LicenseStatus { get; set; }
        public string Details { get; set; }
        public string CommandOutput { get; set; }
    }

    /// <summary>
    /// Builds legitimate Microsoft activation process configurations for Windows (slmgr.vbs)
    /// and Microsoft Office (ospp.vbs), ensuring transparent, visible command execution for technicians.
    /// Strictly excludes any license-bypass or crack mechanisms.
    /// </summary>
    public static class ActivationProcessBuilder
    {
        /// <summary>
        /// Hook to check elevation; can be set by application or tests.
        /// </summary>
        public static Func<bool> IsElevatedCheck { get; set; } = () => false;

        public static string GetSystem32Directory()
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sysnative = Path.Combine(winDir, "Sysnative");
            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess && Directory.Exists(sysnative))
            {
                return sysnative;
            }
            return Environment.SystemDirectory;
        }

        public static string FindOsppVbsPath()
        {
            string[] searchPaths = new[]
            {
                @"C:\Program Files\Microsoft Office\root\Office16\OSPP.VBS",
                @"C:\Program Files\Microsoft Office\Office16\OSPP.VBS",
                @"C:\Program Files (x86)\Microsoft Office\root\Office16\OSPP.VBS",
                @"C:\Program Files (x86)\Microsoft Office\Office16\OSPP.VBS",
                @"C:\Program Files\Microsoft Office\Office15\OSPP.VBS",
                @"C:\Program Files (x86)\Microsoft Office\Office15\OSPP.VBS",
                @"C:\Program Files\Microsoft Office\Office14\OSPP.VBS",
                @"C:\Program Files (x86)\Microsoft Office\Office14\OSPP.VBS"
            };

            foreach (var p in searchPaths)
            {
                if (File.Exists(p)) return p;
            }

            try
            {
                string[] officeDirs = new[]
                {
                    @"C:\Program Files\Microsoft Office",
                    @"C:\Program Files (x86)\Microsoft Office"
                };
                foreach (var dir in officeDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    var files = Directory.GetFiles(dir, "ospp.vbs", SearchOption.AllDirectories);
                    if (files.Length > 0 && File.Exists(files[0])) return files[0];
                }
            }
            catch { }

            return null;
        }

        /// <summary>Microsoft product key format: five groups of five letters/digits (XXXXX-XXXXX-XXXXX-XXXXX-XXXXX).</summary>
        public static bool IsValidProductKey(string key) =>
            key != null && System.Text.RegularExpressions.Regex.IsMatch(key.Trim(), "^[A-Za-z0-9]{5}(-[A-Za-z0-9]{5}){4}$");

        /// <summary>The key is placed into an elevated command line, so anything but a well-formed key is refused.</summary>
        private static void RequireValidKey(string key)
        {
            if (!string.IsNullOrWhiteSpace(key) && !IsValidProductKey(key))
                throw new ArgumentException("The product key must look like XXXXX-XXXXX-XXXXX-XXXXX-XXXXX.");
        }

        public static ProcessStartInfo BuildWindowsActivationProcess(string customProductKey = null, bool visible = true)
        {
            RequireValidKey(customProductKey);
            string sysDir = GetSystem32Directory();
            string slmgr = Path.Combine(sysDir, "slmgr.vbs");
            string cscript = Path.Combine(sysDir, "cscript.exe");
            string cmd = Path.Combine(sysDir, "cmd.exe");

            string scriptArgs = !string.IsNullOrWhiteSpace(customProductKey)
                ? string.Format("//nologo \"{0}\" /ipk {1} & \"{2}\" //nologo \"{0}\" /ato", slmgr, customProductKey.Trim(), cscript)
                : string.Format("//nologo \"{0}\" /ato", slmgr);

            if (visible)
            {
                // Visible Command Prompt window allowing the technician to see the real Microsoft activation in progress
                string cmdArgs = string.Format(
                    "/c \"title BBTech - Windows License Activation (slmgr.vbs) & " +
                    "echo =================================================== & " +
                    "echo [BBTech] Windows License Activation Workflow & " +
                    "echo [Tool] Microsoft slmgr.vbs /ato & " +
                    "echo =================================================== & " +
                    "echo. & " +
                    "\"{0}\" {1} & " +
                    "set CODE=%errorlevel% & " +
                    "echo. & " +
                    "echo =================================================== & " +
                    "echo [BBTech] Activation process completed with exit code %CODE%. & " +
                    "echo =================================================== & " +
                    "timeout /t 3 >nul & " +
                    "exit /b %CODE%\"",
                    cscript, scriptArgs);

                var psi = new ProcessStartInfo(cmd, cmdArgs)
                {
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Normal
                };

                if (!IsElevatedCheck())
                {
                    psi.Verb = "runas";
                }

                return psi;
            }
            else
            {
                return new ProcessStartInfo(cscript, scriptArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
            }
        }

        public static ProcessStartInfo BuildOfficeActivationProcess(string customProductKey = null, bool visible = true, string customOsppPath = null)
        {
            RequireValidKey(customProductKey);
            string ospp = customOsppPath ?? FindOsppVbsPath();
            if (string.IsNullOrEmpty(ospp) || !File.Exists(ospp))
                throw new FileNotFoundException("ospp.vbs not found. Microsoft Office is not installed or licensing script is missing.");

            string sysDir = GetSystem32Directory();
            string cscript = Path.Combine(sysDir, "cscript.exe");
            string cmd = Path.Combine(sysDir, "cmd.exe");

            string scriptArgs = !string.IsNullOrWhiteSpace(customProductKey)
                ? string.Format("//nologo \"{0}\" /inpkey:{1} & \"{2}\" //nologo \"{0}\" /act", ospp, customProductKey.Trim(), cscript)
                : string.Format("//nologo \"{0}\" /act", ospp);

            if (visible)
            {
                // Visible Command Prompt window allowing the technician to see the real Microsoft activation in progress
                string cmdArgs = string.Format(
                    "/c \"title BBTech - Microsoft Office Activation (ospp.vbs) & " +
                    "echo =================================================== & " +
                    "echo [BBTech] Microsoft Office Activation Workflow & " +
                    "echo [Tool] Microsoft ospp.vbs /act & " +
                    "echo =================================================== & " +
                    "echo. & " +
                    "\"{0}\" {1} & " +
                    "set CODE=%errorlevel% & " +
                    "echo. & " +
                    "echo =================================================== & " +
                    "echo [BBTech] Activation process completed with exit code %CODE%. & " +
                    "echo =================================================== & " +
                    "timeout /t 3 >nul & " +
                    "exit /b %CODE%\"",
                    cscript, scriptArgs);

                var psi = new ProcessStartInfo(cmd, cmdArgs)
                {
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Normal
                };

                if (!IsElevatedCheck())
                {
                    psi.Verb = "runas";
                }

                return psi;
            }
            else
            {
                return new ProcessStartInfo(cscript, scriptArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
            }
        }
    }
}
