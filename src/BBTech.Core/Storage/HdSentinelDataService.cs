using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BBTech.Core.Models;

namespace BBTech.Core.Storage
{
    /// <summary>
    /// Service for extracting persistent hardware telemetry from Hard Disk Sentinel data files.
    /// Reads HDSentinel.sta (historical status and universal health) and DISKDATA_*.hdd
    /// (detailed NVMe log page SMART attributes) across known and portable locations.
    /// </summary>
    public static class HdSentinelDataService
    {
        /// <summary>
        /// Probes candidate directories and enriches storage devices with real health,
        /// wear percentage, temperature, and endurance statistics.
        /// </summary>
        public static bool TryEnrichAll(IEnumerable<StorageDevice> devices, IEnumerable<string> extraDirectories = null)
        {
            if (devices == null) return false;
            bool anyEnriched = false;

            var candidateDirs = GetCandidateDirectories(extraDirectories);
            foreach (var dev in devices)
            {
                if (dev == null) continue;
                // If device already has a verified non-empty HealthPercent, skip unless HD Sentinel offers richer data
                if (dev.HealthPercent.HasValue && !string.IsNullOrEmpty(dev.DiagnosticDetails) &&
                    dev.HealthSource != null && dev.HealthSource.IndexOf("NVMe Protocol", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                if (TryEnrichDevice(dev, candidateDirs))
                {
                    anyEnriched = true;
                }
            }

            return anyEnriched;
        }

        public static bool TryEnrichDevice(StorageDevice drive, IEnumerable<string> candidateDirs = null)
        {
            if (drive == null) return false;
            var dirs = candidateDirs != null ? candidateDirs.ToList() : GetCandidateDirectories(null);

            foreach (var dir in dirs)
            {
                if (TryEnrichFromDirectory(dir, drive))
                {
                    return true;
                }
            }

            return false;
        }

        public static List<string> GetCandidateDirectories(IEnumerable<string> extraDirs = null)
        {
            var list = new List<string>();

            void AddDir(string d)
            {
                if (!string.IsNullOrWhiteSpace(d) && Directory.Exists(d) && !list.Contains(d, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(d);
                }
            }

            if (extraDirs != null)
            {
                foreach (var ed in extraDirs)
                {
                    if (!string.IsNullOrWhiteSpace(ed))
                    {
                        AddDir(ed);
                        AddDir(Path.Combine(ed, "HDSData"));
                    }
                }
            }

            // User profile directories (Downloads, Projects)
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                AddDir(Path.Combine(userProfile, "Downloads", "hdsentinel_pro_portable", "HDSData"));
                AddDir(Path.Combine(userProfile, "Downloads", "hdsentinel_pro_portable"));
                AddDir(Path.Combine(userProfile, "Desktop", "Project", "BBTech SSD Tools", "HDSentinel", "HDSData"));
                AddDir(Path.Combine(userProfile, "Desktop", "Project", "BBTech Laptop Tool Latest", "third_party", "tools", "HDSentinel", "HDSData"));
                AddDir(Path.Combine(userProfile, "Desktop", "Project", "BBTech Laptop Tool Latest", "third_party", "tools", "HDSentinel"));
            }

            // Standard installation directories
            string pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pfX86))
            {
                AddDir(Path.Combine(pfX86, "Hard Disk Sentinel", "HDSData"));
                AddDir(Path.Combine(pfX86, "Hard Disk Sentinel"));
            }

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf))
            {
                AddDir(Path.Combine(pf, "Hard Disk Sentinel", "HDSData"));
                AddDir(Path.Combine(pf, "Hard Disk Sentinel"));
            }

            return list;
        }

        public static bool TryEnrichFromDirectory(string hdsDataDir, StorageDevice drive)
        {
            try
            {
                if (!Directory.Exists(hdsDataDir)) return false;

                bool gotHdd = false;

                // 1. Search for matching .hdd files (NVMe log page records)
                var hddFiles = Directory.GetFiles(hdsDataDir, "*.hdd");
                string bestMatch = FindBestMatchFile(hddFiles, drive);

                if (bestMatch != null && File.Exists(bestMatch))
                {
                    gotHdd = ParseHddFile(bestMatch, drive);
                }

                // 2. Search HDSentinel.sta (daily historical wear and universal health status)
                string staPath = Path.Combine(hdsDataDir, "HDSentinel.sta");
                if (!File.Exists(staPath))
                {
                    string parentSta = Path.Combine(Path.GetDirectoryName(hdsDataDir) ?? "", "HDSentinel.sta");
                    if (File.Exists(parentSta)) staPath = parentSta;
                }

                if (File.Exists(staPath))
                {
                    bool staEnriched = EnrichFromStaFile(staPath, drive);
                    if (staEnriched || gotHdd) return true;
                }

                return gotHdd;
            }
            catch
            {
                return false;
            }
        }

        public static string FindBestMatchFile(string[] files, StorageDevice drive)
        {
            if (files == null || files.Length == 0) return null;

            string cleanModel = CleanString(drive.Model);
            string cleanSerial = CleanString(drive.SerialNumber);

            // 1. Exact or partial serial match (e.g. 3CC4631F)
            if (!string.IsNullOrEmpty(cleanSerial) && cleanSerial.Length >= 4)
            {
                string serialTail = cleanSerial.Length > 8 ? cleanSerial.Substring(cleanSerial.Length - 8) : cleanSerial;
                var serialMatch = files.FirstOrDefault(f => CleanString(Path.GetFileName(f)).Contains(serialTail));
                if (serialMatch != null) return serialMatch;
            }

            // 2. Specific Model / Substring Match (e.g. "MTFDKBA512TFK" or "870EVO")
            var words = (drive.Model ?? "").Split(new[] { '_', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(CleanString)
                .Where(w => w.Length >= 4 &&
                            !w.Equals("SATA", StringComparison.OrdinalIgnoreCase) &&
                            !w.Equals("NVME", StringComparison.OrdinalIgnoreCase) &&
                            !w.Equals("SSD", StringComparison.OrdinalIgnoreCase) &&
                            !w.Equals("DISK", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var word in words)
            {
                var match = files.FirstOrDefault(f => CleanString(Path.GetFileName(f)).Contains(word));
                if (match != null) return match;
            }

            // 3. Fallback: Check if filename contains cleanModel
            if (!string.IsNullOrEmpty(cleanModel) && cleanModel.Length >= 6)
            {
                var mainMatch = files.FirstOrDefault(f => CleanString(Path.GetFileName(f)).Contains(cleanModel));
                if (mainMatch != null) return mainMatch;
            }

            return null;
        }

        public static bool ParseHddFile(string filePath, StorageDevice drive)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                return ParseHddLines(lines, drive);
            }
            catch
            {
                return false;
            }
        }

        public static bool ParseHddLines(IEnumerable<string> lines, StorageDevice drive)
        {
            if (lines == null) return false;

            var smartLines = lines.Where(l => l.StartsWith("SMART=FF_", StringComparison.OrdinalIgnoreCase) ||
                                              l.StartsWith("SMART=", StringComparison.OrdinalIgnoreCase)).ToList();

            if (smartLines.Count == 0) return false;

            string latestLine = smartLines.Last();
            string rawContent = latestLine.StartsWith("SMART=", StringComparison.OrdinalIgnoreCase) ? latestLine.Substring(6) : latestLine;

            var kvs = rawContent.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in kvs)
            {
                var parts = kv.Split('=');
                if (parts.Length == 2)
                {
                    dict[parts[0].Trim()] = parts[1].Trim();
                }
            }

            if (dict.ContainsKey("FF_1205") || dict.ContainsKey("FF_1201"))
            {
                return ParseNvmeFromDict(dict, drive);
            }

            return false;
        }

        private static bool ParseNvmeFromDict(Dictionary<string, string> dict, StorageDevice drive)
        {
            byte criticalWarning = 0;
            int? tempC = null;
            byte? availableSpare = null;
            byte? percentageUsed = null;
            ulong dataUnitsRead = 0;
            ulong dataUnitsWritten = 0;
            ulong powerOnHours = 0;
            ulong mediaErrors = 0;

            // Critical Warning (FF_1200)
            if (dict.TryGetValue("FF_1200", out var cwVal))
            {
                string hex = cwVal.Contains('_') ? cwVal.Split('_')[1] : cwVal;
                if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    criticalWarning = b;
            }

            // Temperature (FF_1201 or FF_12C8)
            if (dict.TryGetValue("FF_1201", out var tempVal) || dict.TryGetValue("FF_12C8", out tempVal))
            {
                string hex = tempVal.Contains('_') ? tempVal.Split('_')[1] : tempVal;
                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var tempK))
                {
                    if (tempK > 200 && tempK < 400)
                        tempC = tempK - 273;
                }
            }

            // Available Spare (FF_1203)
            if (dict.TryGetValue("FF_1203", out var spareVal))
            {
                string hex = spareVal.Contains('_') ? spareVal.Split('_')[1] : spareVal;
                if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    availableSpare = b;
            }

            // Percentage Used (FF_1205)
            if (dict.TryGetValue("FF_1205", out var usedVal))
            {
                string hex = usedVal.Contains('_') ? usedVal.Split('_')[1] : usedVal;
                if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    percentageUsed = b;
            }

            // Total Host Reads (FF_1220)
            if (dict.TryGetValue("FF_1220", out var readsVal))
            {
                string hex = readsVal.Contains('_') ? readsVal.Split('_')[1] : readsVal;
                if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var units))
                    dataUnitsRead = units;
            }

            // Total Host Writes (FF_1230)
            if (dict.TryGetValue("FF_1230", out var writesVal))
            {
                string hex = writesVal.Contains('_') ? writesVal.Split('_')[1] : writesVal;
                if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var units))
                    dataUnitsWritten = units;
            }

            // Power-On Hours (FF_1280)
            if (dict.TryGetValue("FF_1280", out var pohVal))
            {
                string hex = pohVal.Contains('_') ? pohVal.Split('_')[1] : pohVal;
                if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hrs))
                    powerOnHours = hrs;
            }

            // Media Errors (FF_12B0)
            if (dict.TryGetValue("FF_12B0", out var meVal))
            {
                string hex = meVal.Contains('_') ? meVal.Split('_')[1] : meVal;
                if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var errs))
                    mediaErrors = errs;
            }

            if (!percentageUsed.HasValue && !tempC.HasValue && powerOnHours == 0) return false;

            int healthPct = percentageUsed.HasValue ? Math.Max(0, Math.Min(100, 100 - percentageUsed.Value)) : 100;

            drive.HealthPercent = healthPct;
            if (percentageUsed.HasValue) drive.WearPercentUsed = percentageUsed.Value;

            if (tempC.HasValue && tempC.Value > 0)
            {
                drive.TemperatureC = tempC.Value;
                drive.Temperature = tempC.Value + " °C";
            }

            if (powerOnHours > 0)
            {
                drive.PowerOnHours = (long)powerOnHours;
                drive.PowerOnTime = string.Format(CultureInfo.InvariantCulture, "{0:N0} hours", powerOnHours);
            }

            string writesStr = "";
            if (dataUnitsWritten > 0)
            {
                double tb = (dataUnitsWritten * 512000.0) / 1e12;
                writesStr = tb >= 1.0 ? string.Format(CultureInfo.InvariantCulture, "{0:F1} TB written", tb) :
                                         string.Format(CultureInfo.InvariantCulture, "{0:F0} GB written", tb * 1000);
            }

            drive.HealthAssessment = EvaluateAssessment(healthPct, criticalWarning, mediaErrors);
            drive.HealthSource = "HD Sentinel Pro SMART Telemetry";
            drive.DiagnosticDetails = string.Format(
                CultureInfo.InvariantCulture,
                "HD Sentinel NVMe: {0}% health ({1}% endurance used). Temp: {2}. Power-on: {3}. {4}",
                healthPct, percentageUsed ?? 0, drive.Temperature, drive.PowerOnTime, writesStr).Trim();

            return true;
        }

        public static bool EnrichFromStaFile(string staPath, StorageDevice drive)
        {
            try
            {
                var lines = File.ReadAllLines(staPath);
                return EnrichFromStaLines(lines, drive);
            }
            catch
            {
                return false;
            }
        }

        public static bool EnrichFromStaLines(string[] lines, StorageDevice drive)
        {
            if (lines == null || lines.Length == 0 || drive == null) return false;

            string cleanModel = CleanString(drive.Model);
            string cleanSerial = CleanString(drive.SerialNumber);
            string serialTail = (!string.IsNullOrEmpty(cleanSerial) && cleanSerial.Length >= 4)
                ? (cleanSerial.Length > 8 ? cleanSerial.Substring(cleanSerial.Length - 8) : cleanSerial)
                : null;

            // Find section like [Sta_Micron MTFDKBA512TFK (488382 MB)_22453CC4631F]
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (!l.StartsWith("[Sta_", StringComparison.OrdinalIgnoreCase)) continue;

                string headerClean = CleanString(l);
                bool match = false;

                if (!string.IsNullOrEmpty(serialTail) && headerClean.Contains(serialTail))
                {
                    match = true;
                }
                else if (!string.IsNullOrEmpty(cleanModel) && (headerClean.Contains(cleanModel) ||
                         (cleanModel.Length >= 8 && headerClean.Contains(cleanModel.Substring(0, 8)))))
                {
                    match = true;
                }

                if (match)
                {
                    int? latestHealth = null;
                    int? latestTemp = null;
                    int? latestPerf = null;

                    // Read records inside this section until next section
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        string sl = lines[j].Trim();
                        if (sl.StartsWith("[")) break;

                        // Format: 46270=51,39.99,101,96,25,18176247
                        // parts[0] = Max temp, parts[1] = Avg temp, parts[2] = Perf, parts[3] = Health, parts[4] = Min temp
                        if (sl.Length > 0 && char.IsDigit(sl[0]) && sl.Contains('=') && sl.Contains(','))
                        {
                            var parts = sl.Split('=')[1].Split(',');
                            if (parts.Length >= 4)
                            {
                                if (int.TryParse(parts[3], out int healthVal) && healthVal >= 0 && healthVal <= 100)
                                {
                                    latestHealth = healthVal;
                                }
                                if (int.TryParse(parts[2], out int perfVal) && perfVal >= 0 && perfVal <= 100)
                                {
                                    latestPerf = perfVal;
                                }
                                if (double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double avgTemp) && avgTemp > 0 && avgTemp < 120)
                                {
                                    latestTemp = (int)Math.Round(avgTemp);
                                }
                            }
                        }
                    }

                    if (latestHealth.HasValue)
                    {
                        drive.HealthPercent = latestHealth.Value;
                        if (latestPerf.HasValue) drive.PerformancePercent = latestPerf.Value;
                        if (latestTemp.HasValue && !drive.TemperatureC.HasValue)
                        {
                            drive.TemperatureC = latestTemp.Value;
                            drive.Temperature = latestTemp.Value + " °C";
                        }

                        drive.HealthAssessment = EvaluateAssessment(latestHealth.Value, 0, 0);
                        drive.HealthSource = "HD Sentinel Pro Historical Analysis";
                        drive.DiagnosticDetails = string.Format(
                            CultureInfo.InvariantCulture,
                            "HD Sentinel: {0}% health ({1}). Temp: {2}.",
                            latestHealth.Value, drive.HealthAssessment, drive.Temperature).Trim();

                        return true;
                    }
                    break;
                }
            }

            return false;
        }

        private static string EvaluateAssessment(int healthPercent, byte criticalWarning, ulong mediaErrors)
        {
            if (criticalWarning != 0 || mediaErrors > 50 || healthPercent < 20) return "CRITICAL";
            if (healthPercent < 80 || mediaErrors > 0) return "WARNING";
            if (healthPercent >= 90) return "OPTIMAL";
            return "GOOD";
        }

        public static string CleanString(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }
    }
}
