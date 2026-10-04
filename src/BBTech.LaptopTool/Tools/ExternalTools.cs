using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BBTech.Core.Models;
using BBTech.Core.Parsing;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.Tools
{
    /// <summary>Result wrapper for external tool calls: either data or a friendly message.</summary>
    public sealed class ToolResult<T>
    {
        public T Value { get; set; }
        public string Message { get; set; }
        public bool Ok => Value != null && Message == null;
    }

    /// <summary>
    /// NirSoft BatteryInfoView (reused from TEST-Laptop.zip, embedded in the EXE).
    /// Runs "BatteryInfoView.exe /sxml file" hidden (no admin needed) and parses the XML.
    /// </summary>
    public static class BatteryInfoViewTool
    {
        public const string Unavailable = "Battery information is currently unavailable.";

        public static bool IsAvailable => ToolBundle.IsAvailable(ToolBundle.BatteryInfoView);

        private static string Exe()
        {
            string dir = ToolBundle.GetToolDir(ToolBundle.BatteryInfoView);
            return dir == null ? null : Path.Combine(dir, "BatteryInfoView.exe");
        }

        /// <summary>Blocking (~0.1 s, plus one-time extraction); call from a background thread.</summary>
        public static ToolResult<BatteryDetails> Read()
        {
            string exe = Exe();
            if (exe == null) return new ToolResult<BatteryDetails> { Message = Unavailable };

            string xmlFile = AppPaths.NewTempFile(".xml");
            try
            {
                var run = ProcessRunner.Run(exe, "/sxml \"" + xmlFile + "\"", 20000);
                if (run.Error != null || !File.Exists(xmlFile)) return new ToolResult<BatteryDetails> { Message = Unavailable };

                var details = BatteryInfoViewParser.Parse(File.ReadAllText(xmlFile));
                AppLog.Info("BatteryInfoView: batteries=" + details.BatteryCount + " health=" + (details.HealthPercent?.ToString() ?? "n/a"));
                return new ToolResult<BatteryDetails> { Value = details };
            }
            catch (Exception ex)
            {
                AppLog.Error("BatteryInfoView read failed", ex);
                return new ToolResult<BatteryDetails> { Message = Unavailable };
            }
            finally { AppPaths.TryDelete(xmlFile); }
        }

        /// <summary>Opens the BatteryInfoView window. Returns a friendly error or null.</summary>
        public static string OpenGui()
        {
            string exe = Exe();
            return exe == null ? "BatteryInfoView is not available in this build." : ProcessRunner.Run(exe, "", 0, hidden: false).Error;
        }
    }

    /// <summary>
    /// Hard Disk Sentinel (reused from TEST-Laptop.zip, embedded in the EXE). It requires
    /// administrator rights, so Windows shows a UAC prompt. The XML report gives per-drive
    /// health and performance. It runs from a fresh temporary copy, so no previous laptop's
    /// disk data is ever present.
    /// </summary>
    public static class HdSentinelTool
    {
        public static bool IsAvailable => ToolBundle.IsAvailable(ToolBundle.HdSentinel);

        private static string Folder() => ToolBundle.GetToolDir(ToolBundle.HdSentinel);

        /// <summary>Blocking; call from a background thread. Up to ~2 minutes.</summary>
        public static ToolResult<List<HdSentinelDisk>> RunHealthReport()
        {
            string folder = Folder();
            if (folder == null) return new ToolResult<List<HdSentinelDisk>> { Message = "HD Sentinel is not available in this build." };
            string exe = Path.Combine(folder, "HDSentinel.exe");

            DateTime started = DateTime.Now.AddSeconds(-2);
            // /XMLREPORT writes "HDSentinel_report_<date>.xml"; /QUIT closes it afterwards.
            var run = ProcessRunner.Run(exe, "/XMLREPORT /QUIT", 0, hidden: false, shellExecute: true);
            if (run.Error != null) return new ToolResult<List<HdSentinelDisk>> { Message = run.Error };

            string report = null;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(120))
            {
                report = FindNewReport(folder, started);
                if (report != null) break;
                if (run.Process != null && run.Process.HasExited && sw.Elapsed > TimeSpan.FromSeconds(5))
                {
                    report = FindNewReport(folder, started);
                    break;
                }
                Thread.Sleep(1000);
            }

            if (report == null)
                return new ToolResult<List<HdSentinelDisk>> { Message = "HD Sentinel did not produce a report. Open HD Sentinel to view drive health manually." };

            try
            {
                Thread.Sleep(500); // let it finish writing
                var disks = HdSentinelReportParser.Parse(File.ReadAllText(report));
                AppLog.Info("HD Sentinel report parsed: " + disks.Count + " disk(s)");
                if (disks.Count == 0) return new ToolResult<List<HdSentinelDisk>> { Message = "HD Sentinel report could not be read." };
                return new ToolResult<List<HdSentinelDisk>> { Value = disks };
            }
            catch (Exception ex)
            {
                AppLog.Error("HD Sentinel report parse failed", ex);
                return new ToolResult<List<HdSentinelDisk>> { Message = "HD Sentinel report could not be read." };
            }
        }

        private static string FindNewReport(string folder, DateTime since)
        {
            try
            {
                var dirs = new[] { folder, Path.Combine(folder, "HDSData") }.Where(Directory.Exists);
                return dirs.SelectMany(d => Directory.GetFiles(d, "*.xml"))
                    .Select(f => new FileInfo(f))
                    .Where(f => f.Name.IndexOf("report", StringComparison.OrdinalIgnoreCase) >= 0 && f.LastWriteTime >= since && f.Length > 0)
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>Blocking on first use (extraction); call from a background thread.</summary>
        public static string OpenGui()
        {
            string folder = Folder();
            return folder == null ? "HD Sentinel is not available in this build."
                                  : ProcessRunner.Run(Path.Combine(folder, "HDSentinel.exe"), "", 0, hidden: false, shellExecute: true).Error;
        }
    }

    /// <summary>
    /// Free PC Audit (reused from TEST-Laptop.zip, embedded). It has no machine-readable export,
    /// so it is offered as an optional "full system audit" viewer; detection logic is our own.
    /// </summary>
    public static class FreePcAuditTool
    {
        public static bool IsAvailable => ToolBundle.IsAvailable(ToolBundle.FreePcAudit);

        public static string OpenGui()
        {
            string dir = ToolBundle.GetToolDir(ToolBundle.FreePcAudit);
            return dir == null ? "Free PC Audit is not available in this build." : ProcessRunner.Run(Path.Combine(dir, "freepcaudit.exe"), "", 0, hidden: false).Error;
        }
    }
}
