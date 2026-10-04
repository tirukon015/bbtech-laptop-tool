using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Services
{
    public sealed class ProcessResult
    {
        public bool Started { get; set; }
        public bool TimedOut { get; set; }
        public int ExitCode { get; set; } = -1;
        /// <summary>Friendly error for the UI (null when OK).</summary>
        public string Error { get; set; }
        public Process Process { get; set; }
    }

    /// <summary>Starts external utilities with timeouts and friendly error messages.</summary>
    public static class ProcessRunner
    {
        private const int ErrorCancelled = 1223;      // user declined UAC
        private const int ErrorElevationRequired = 740;

        /// <param name="shellExecute">
        /// true for tools whose manifest requires administrator rights (Windows shows UAC).
        /// </param>
        public static ProcessResult Run(string exe, string args, int timeoutMs, bool hidden = true, bool shellExecute = false)
        {
            var result = new ProcessResult();
            if (!File.Exists(exe))
            {
                result.Error = "Tool not found: " + Path.GetFileName(exe);
                AppLog.Warn("Tool missing: " + exe);
                return result;
            }

            var psi = new ProcessStartInfo(exe, args ?? "")
            {
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = shellExecute,
                CreateNoWindow = hidden,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            };

            try
            {
                AppLog.Info("Starting " + Path.GetFileName(exe) + " " + args);
                var p = Process.Start(psi);
                result.Started = p != null;
                result.Process = p;
                if (p == null) { result.Error = "The tool could not be started."; return result; }
                if (timeoutMs <= 0) return result;

                if (!p.WaitForExit(timeoutMs))
                {
                    result.TimedOut = true;
                    result.Error = Path.GetFileName(exe) + " did not finish in time.";
                    AppLog.Warn(result.Error);
                    return result;
                }
                result.ExitCode = p.ExitCode;
                return result;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                result.Error = "Administrator permission was declined.";
                AppLog.Warn("UAC declined for " + Path.GetFileName(exe));
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
            {
                result.Error = "This tool needs administrator rights.";
                AppLog.Warn("Elevation required for " + Path.GetFileName(exe));
            }
            catch (Exception ex)
            {
                result.Error = Path.GetFileName(exe) + " failed to start.";
                AppLog.Error("Failed to start " + exe, ex);
            }
            return result;
        }

        /// <summary>Open a file/URI with its default handler (e.g. the camera app).</summary>
        public static bool OpenShell(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not open " + target, ex);
                return false;
            }
        }
    }
}
