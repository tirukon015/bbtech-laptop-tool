using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using BBTech.Core.Models;
using BBTech.Core.Parsing;
using BBTech.Core.Storage;
using BBTech.LaptopTool.Detect;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// Native integrated SSD Health Detection Engine inside BBTech Laptop Tool.
    /// Uses HD Sentinel Pro as the underlying diagnostic engine where available,
    /// with automatic fallback to Windows Native NVMe SMART reliability diagnostics.
    /// </summary>
    public static class SsdHealthEngine
    {
        private static readonly object Sync = new object();
        private static SsdHealthEngineResult _lastCachedResult;
        private static DateTime _lastInspectTime = DateTime.MinValue;

        /// <summary>
        /// Inspects all installed physical drives and produces a unified SSD health assessment.
        /// </summary>
        public static async Task<SsdHealthEngineResult> InspectAsync(DetectionResult detection, bool forceRefresh = false)
        {
            if (detection == null) detection = new DetectionResult();

            // Return cached result if fresh within 30 seconds and not forced
            if (!forceRefresh && _lastCachedResult != null && (DateTime.Now - _lastInspectTime).TotalSeconds < 30)
            {
                return _lastCachedResult;
            }

            return await Task.Run(() =>
            {
                lock (Sync)
                {
                    try
                    {
                        AppLog.Info("SSD Health Engine inspection started");

                        // 1. Ensure physical storage devices are discovered
                        if (detection.StorageDevices == null || detection.StorageDevices.Count == 0)
                        {
                            StorageProvider.Fill(detection);
                        }

                        // 2. Read Windows-native NVMe & SMART reliability counters (no external tool needed)
                        StorageProvider.ReadReliability(detection.StorageDevices);

                        // 3. Attempt to invoke or communicate with HD Sentinel Pro
                        List<HdSentinelDisk> hdsDisks = null;
                        string providerName = null;

                        try
                        {
                            var hdsOutcome = ExecuteHdSentinelReport();
                            if (hdsOutcome.Success && hdsOutcome.Disks != null && hdsOutcome.Disks.Count > 0)
                            {
                                hdsDisks = hdsOutcome.Disks;
                                providerName = "HD Sentinel Pro (v6.30)";
                                AppLog.Info(string.Format("SSD Health Engine: HD Sentinel report successfully acquired for {0} drive(s).", hdsDisks.Count));
                            }
                            else if (!string.IsNullOrEmpty(hdsOutcome.ErrorMessage))
                            {
                                AppLog.Info("SSD Health Engine: HD Sentinel notice: " + hdsOutcome.ErrorMessage);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn("SSD Health Engine: HD Sentinel query failed, utilizing Windows diagnostics", ex);
                        }

                        // 4. Correlate and normalize into unified SsdHealthEngineResult
                        var engineResult = SsdHealthMatcher.Correlate(detection.StorageDevices, hdsDisks, providerName, detection);

                        _lastCachedResult = engineResult;
                        _lastInspectTime = DateTime.Now;

                        AppLog.Info(string.Format("SSD Health Engine inspection complete: {0} drive(s), overall status={1}",
                            engineResult.Disks.Count, engineResult.OverallStatus));

                        return engineResult;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("SSD Health Engine execution failed", ex);
                        return new SsdHealthEngineResult
                        {
                            Success = false,
                            SummaryMessage = "SSD health analysis encountered an unexpected error: " + ex.Message,
                            OverallStatus = SsdHealthStatus.Unknown,
                            ProviderUsed = "Diagnostic Fallback",
                        };
                    }
                }
            });
        }

        private sealed class HdSentinelRunOutcome
        {
            public bool Success { get; set; }
            public List<HdSentinelDisk> Disks { get; set; }
            public string ErrorMessage { get; set; }
        }

        private static HdSentinelRunOutcome ExecuteHdSentinelReport()
        {
            string exePath = FindHdSentinelExecutable();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return new HdSentinelRunOutcome
                {
                    Success = false,
                    ErrorMessage = "HD Sentinel is not installed or bundled in this environment. Falling back to Windows Storage Reliability API.",
                };
            }

            string reportXml = AppPaths.NewTempFile(".xml");
            try
            {
                bool isAdmin = IsProcessElevated();
                string args = string.Format("/SILENT /XMLREPORT \"{0}\" /QUIT", reportXml);

                var psi = new ProcessStartInfo(exePath, args)
                {
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                    UseShellExecute = !isAdmin, // Needs ShellExecute for UAC prompt if non-admin
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                AppLog.Info(string.Format("SSD Health Engine starting HD Sentinel ({0})", isAdmin ? "elevated/hidden" : "UAC request"));
                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        return new HdSentinelRunOutcome { Success = false, ErrorMessage = "Failed to launch HD Sentinel process." };
                    }

                    // Poll for report file creation up to 15 seconds
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed < TimeSpan.FromSeconds(15))
                    {
                        if (File.Exists(reportXml) && new FileInfo(reportXml).Length > 100)
                        {
                            // Allow a brief moment for stream write buffer flush
                            Thread.Sleep(300);
                            break;
                        }

                        if (proc.HasExited && sw.Elapsed > TimeSpan.FromSeconds(2))
                        {
                            // Check one more time after process exit
                            Thread.Sleep(500);
                            if (File.Exists(reportXml) && new FileInfo(reportXml).Length > 100) break;
                            break;
                        }

                        Thread.Sleep(250);
                    }
                }

                // If explicit target file was not generated, check working directory for default HDSentinel.xml or report.xml
                string resolvedReport = reportXml;
                if (!File.Exists(resolvedReport) || new FileInfo(resolvedReport).Length < 100)
                {
                    resolvedReport = FindAlternativeReport(Path.GetDirectoryName(exePath));
                }

                if (string.IsNullOrEmpty(resolvedReport) || !File.Exists(resolvedReport) || new FileInfo(resolvedReport).Length < 100)
                {
                    return new HdSentinelRunOutcome
                    {
                        Success = false,
                        ErrorMessage = "HD Sentinel completed without generating an XML report.",
                    };
                }

                string content = File.ReadAllText(resolvedReport);
                var disks = HdSentinelReportParser.Parse(content);
                return new HdSentinelRunOutcome
                {
                    Success = disks != null && disks.Count > 0,
                    Disks = disks,
                };
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // UAC cancelled by user
            {
                return new HdSentinelRunOutcome
                {
                    Success = false,
                    ErrorMessage = "Administrator permission was declined for HD Sentinel. Using native Windows SMART diagnostics.",
                };
            }
            catch (Exception ex)
            {
                return new HdSentinelRunOutcome
                {
                    Success = false,
                    ErrorMessage = "HD Sentinel execution exception: " + ex.Message,
                };
            }
            finally
            {
                AppPaths.TryDelete(reportXml);
            }
        }

        private static string FindHdSentinelExecutable()
        {
            // 1. Check embedded tool bundle extraction or external tools/ directory next to EXE
            try
            {
                string toolDir = ToolBundle.GetToolDir(ToolBundle.HdSentinel);
                if (!string.IsNullOrEmpty(toolDir))
                {
                    string candidate = Path.Combine(toolDir, "HDSentinel.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }

            // 2. Check standard system installation locations
            var standardPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Hard Disk Sentinel", "HDSentinel.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hard Disk Sentinel", "HDSentinel.exe"),
                Path.Combine(AppPaths.BaseDir ?? "", "HDSentinel.exe"),
                Path.Combine(AppPaths.BaseDir ?? "", "tools", "HDSentinel", "HDSentinel.exe"),
            };

            foreach (var p in standardPaths)
            {
                if (File.Exists(p)) return p;
            }

            // 3. Check active running process
            try
            {
                var procs = Process.GetProcessesByName("HDSentinel");
                if (procs.Length > 0 && procs[0].MainModule != null)
                {
                    string procPath = procs[0].MainModule.FileName;
                    if (File.Exists(procPath)) return procPath;
                }
            }
            catch { }

            return null;
        }

        private static string FindAlternativeReport(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            try
            {
                var candidates = new[]
                {
                    Path.Combine(folder, "HDSentinel.xml"),
                    Path.Combine(folder, "HDSData", "HDSentinel.xml"),
                    Path.Combine(folder, "HDSentinel_report.xml"),
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c) && new FileInfo(c).Length > 100) return c;
                }

                // Check newest report file created in last 60 seconds
                var cutoff = DateTime.Now.AddSeconds(-60);
                var newest = Directory.GetFiles(folder, "*.xml", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .Where(fi => fi.Length > 100 && fi.LastWriteTime >= cutoff)
                    .OrderByDescending(fi => fi.LastWriteTime)
                    .FirstOrDefault();

                return newest != null ? newest.FullName : null;
            }
            catch { return null; }
        }

        private static bool IsProcessElevated() => ElevationHelper.IsAdministrator;
    }
}
