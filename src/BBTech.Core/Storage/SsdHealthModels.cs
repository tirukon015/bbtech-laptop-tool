using System;
using System.Collections.Generic;

namespace BBTech.Core.Storage
{
    /// <summary>
    /// Health status categorization for physical solid-state drives.
    /// </summary>
    public enum SsdHealthStatus
    {
        Unknown = 0,
        Optimal = 1,     // 90% - 100%
        Good = 2,        // 80% - 89%
        Warning = 3,     // 50% - 79% or Windows Warning
        Critical = 4,    // < 50% or Windows Unhealthy
        Failed = 5
    }

    /// <summary>
    /// Normalized health report for a single physical SSD.
    /// </summary>
    public sealed class PhysicalSsdHealthResult
    {
        public int DiskIndex { get; set; } = 0;
        public string Model { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string BusType { get; set; } = "";
        public string MediaType { get; set; } = "SSD";
        public long CapacityBytes { get; set; }
        public string CapacityLabel { get; set; } = "";
        public bool IsExternal { get; set; }
        public string DeviceId { get; set; } = "";
        public string WindowsHealth { get; set; } = "";

        // Health & Performance metrics
        public int? HealthPercent { get; set; }
        public int? PerformancePercent { get; set; }
        public SsdHealthStatus Status { get; set; } = SsdHealthStatus.Unknown;
        public string StatusLabel { get; set; } = "UNKNOWN";

        // Temperature & Lifespan
        public string Temperature { get; set; } = "";
        public int? TemperatureC { get; set; }
        public string PowerOnTime { get; set; } = "";
        public long? PowerOnHours { get; set; }
        public int? WearPercentUsed { get; set; }

        // Diagnostic details and attribution
        public string DiagnosticDetails { get; set; } = "";
        public string DataSource { get; set; } = "";
        public bool HasSmartErrors { get; set; }

        public string DisplayTitle
        {
            get
            {
                string ext = IsExternal ? "[External] " : "";
                string modelName = string.IsNullOrWhiteSpace(Model) ? "Solid State Drive" : Model;
                string cap = string.IsNullOrWhiteSpace(CapacityLabel) ? "" : " (" + CapacityLabel + (string.IsNullOrWhiteSpace(BusType) ? "" : " " + BusType) + ")";
                return ext + modelName + cap;
            }
        }

        public static SsdHealthStatus EvaluateStatus(int? healthPercent, string windowsHealth = null, int? wearPercent = null)
        {
            if (windowsHealth != null && (windowsHealth.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase) || windowsHealth.Equals("Failed", StringComparison.OrdinalIgnoreCase)))
            {
                return SsdHealthStatus.Critical;
            }

            if (healthPercent.HasValue)
            {
                int h = healthPercent.Value;
                if (h >= 90) return SsdHealthStatus.Optimal;
                if (h >= 80) return SsdHealthStatus.Good;
                if (h >= 50) return SsdHealthStatus.Warning;
                return SsdHealthStatus.Critical;
            }

            if (wearPercent.HasValue)
            {
                // NVMe wear percentage used according to specification
                if (wearPercent.Value >= 95) return SsdHealthStatus.Critical;
                if (wearPercent.Value >= 80) return SsdHealthStatus.Warning;
                if (windowsHealth != null && windowsHealth.Equals("Healthy", StringComparison.OrdinalIgnoreCase)) return SsdHealthStatus.Good;
            }

            if (windowsHealth != null)
            {
                if (windowsHealth.Equals("Warning", StringComparison.OrdinalIgnoreCase)) return SsdHealthStatus.Warning;
                if (windowsHealth.Equals("Healthy", StringComparison.OrdinalIgnoreCase)) return SsdHealthStatus.Good;
            }

            return SsdHealthStatus.Unknown;
        }

        public static string GetStatusLabel(SsdHealthStatus status)
        {
            switch (status)
            {
                case SsdHealthStatus.Optimal: return "OPTIMAL";
                case SsdHealthStatus.Good: return "GOOD";
                case SsdHealthStatus.Warning: return "WARNING";
                case SsdHealthStatus.Critical: return "CRITICAL";
                case SsdHealthStatus.Failed: return "FAILED";
                default: return "UNKNOWN";
            }
        }
    }

    /// <summary>
    /// Overall output of the SSD Health Detection Engine.
    /// </summary>
    public sealed class SsdHealthEngineResult
    {
        public bool Success { get; set; }
        public string SummaryMessage { get; set; } = "";
        public string ProviderUsed { get; set; } = "";
        public List<PhysicalSsdHealthResult> Disks { get; set; } = new List<PhysicalSsdHealthResult>();
        public SsdHealthStatus OverallStatus { get; set; } = SsdHealthStatus.Unknown;

        public bool AnyBadDrives => Disks.Exists(d => !d.IsExternal && (d.Status == SsdHealthStatus.Critical || d.Status == SsdHealthStatus.Failed));
        public bool AnyWarningDrives => Disks.Exists(d => !d.IsExternal && d.Status == SsdHealthStatus.Warning);
        public bool AllHealthy => Disks.Count > 0 && Disks.TrueForAll(d => d.IsExternal || d.Status == SsdHealthStatus.Optimal || d.Status == SsdHealthStatus.Good);
    }

    /// <summary>
    /// Pure, deterministic matching and normalization logic between detected physical drives and diagnostic reports.
    /// </summary>
    public static class SsdHealthMatcher
    {
        public static SsdHealthEngineResult Correlate(
            IEnumerable<BBTech.Core.Models.StorageDevice> discoveredDevices,
            IEnumerable<BBTech.Core.Parsing.HdSentinelDisk> hdsDisks,
            string providerName = null,
            BBTech.Core.Models.DetectionResult detection = null)
        {
            var result = new SsdHealthEngineResult
            {
                ProviderUsed = !string.IsNullOrWhiteSpace(providerName) ? providerName : "Integrated SSD Health Engine",
                Success = true,
            };

            var devList = discoveredDevices != null ? new List<BBTech.Core.Models.StorageDevice>(discoveredDevices) : new List<BBTech.Core.Models.StorageDevice>();
            var hdsList = hdsDisks != null ? new List<BBTech.Core.Parsing.HdSentinelDisk>(hdsDisks) : new List<BBTech.Core.Parsing.HdSentinelDisk>();
            var usedHds = new HashSet<BBTech.Core.Parsing.HdSentinelDisk>();

            int index = 1;
            foreach (var d in devList)
            {
                var h = FindMatchingHdsDisk(d, hdsList, usedHds, devList.Count);
                if (h != null) usedHds.Add(h);

                var r = new PhysicalSsdHealthResult
                {
                    DiskIndex = index++,
                    Model = !string.IsNullOrWhiteSpace(d.Model) ? d.Model : (h != null ? h.Model : "Unknown Drive"),
                    SerialNumber = d.SerialNumber,
                    BusType = d.BusType,
                    MediaType = !string.IsNullOrWhiteSpace(d.MediaType) ? d.MediaType : "SSD",
                    CapacityBytes = d.CapacityBytes,
                    CapacityLabel = d.CapacityLabel,
                    IsExternal = d.IsExternal,
                    DeviceId = d.DeviceId,
                    WearPercentUsed = d.WearPercentUsed,
                    PowerOnHours = d.PowerOnHours,
                    TemperatureC = d.TemperatureC,
                    WindowsHealth = d.WindowsHealth,
                };

                if (h != null)
                {
                    r.HealthPercent = h.HealthPercent;
                    r.PerformancePercent = h.PerformancePercent;
                    r.Temperature = !string.IsNullOrWhiteSpace(h.Temperature) ? h.Temperature : (d.TemperatureC != null ? d.TemperatureC + " °C" : "");
                    r.PowerOnTime = !string.IsNullOrWhiteSpace(h.PowerOnTime) ? h.PowerOnTime : (d.PowerOnHours != null ? d.PowerOnHours + " hours" : "");
                    r.DiagnosticDetails = !string.IsNullOrWhiteSpace(h.Description) ? h.Description : "Health report collected via HD Sentinel Pro.";
                    r.DataSource = "HD Sentinel Pro (v6.30)";
                    r.Status = PhysicalSsdHealthResult.EvaluateStatus(h.HealthPercent, d.WindowsHealth, d.WearPercentUsed);

                    // Sync back to StorageDevice instance
                    d.HealthPercent = h.HealthPercent;
                    d.PerformancePercent = h.PerformancePercent;
                    d.PowerOnTime = r.PowerOnTime;
                    d.Temperature = r.Temperature;
                    d.HealthAssessment = PhysicalSsdHealthResult.GetStatusLabel(r.Status);
                    d.DiagnosticDetails = r.DiagnosticDetails;
                    d.HealthSource = r.DataSource;
                }
                else
                {
                    // Fallback to Windows native SMART / NVMe reliability counters
                    r.Status = PhysicalSsdHealthResult.EvaluateStatus(null, d.WindowsHealth, d.WearPercentUsed);
                    // Do not fabricate health percentages from wear percent or volume status
                    r.HealthPercent = null;
                    if (d.WearPercentUsed.HasValue)
                    {
                        r.DataSource = "Windows Storage Reliability API (NVMe)";
                        r.DiagnosticDetails = string.Format("NVMe wear consumed: {0}%. SMART status: {1}. (Raw sector health % requires HD Sentinel Pro).",
                            d.WearPercentUsed.Value, !string.IsNullOrWhiteSpace(d.WindowsHealth) ? d.WindowsHealth : "Healthy");
                    }
                    else
                    {
                        r.DataSource = "Windows Basic Storage API";
                        r.DiagnosticDetails = string.Format("Drive operational status: {0}. Detailed SMART wear statistics require administrator elevation or an active HD Sentinel instance.",
                            !string.IsNullOrWhiteSpace(d.WindowsHealth) ? d.WindowsHealth : "Healthy");
                    }

                    r.Temperature = d.TemperatureC != null ? d.TemperatureC + " °C" : "";
                    r.PowerOnTime = d.PowerOnHours != null ? d.PowerOnHours + " hours" : "";

                    // Sync back to StorageDevice instance
                    d.HealthAssessment = PhysicalSsdHealthResult.GetStatusLabel(r.Status);
                    d.DiagnosticDetails = r.DiagnosticDetails;
                    d.HealthSource = r.DataSource;
                }

                r.StatusLabel = PhysicalSsdHealthResult.GetStatusLabel(r.Status);
                result.Disks.Add(r);
            }

            // Also include any HD Sentinel disks that were not correlated to Windows physical drives
            foreach (var h in hdsList)
            {
                if (usedHds.Contains(h)) continue;
                var orphan = new PhysicalSsdHealthResult
                {
                    DiskIndex = index++,
                    Model = h.Model,
                    BusType = h.Interface,
                    CapacityLabel = h.TotalSize,
                    HealthPercent = h.HealthPercent,
                    PerformancePercent = h.PerformancePercent,
                    Temperature = h.Temperature,
                    PowerOnTime = h.PowerOnTime,
                    DiagnosticDetails = h.Description,
                    DataSource = "HD Sentinel Pro (v6.30)",
                    Status = PhysicalSsdHealthResult.EvaluateStatus(h.HealthPercent),
                };
                orphan.StatusLabel = PhysicalSsdHealthResult.GetStatusLabel(orphan.Status);
                result.Disks.Add(orphan);
            }

            // Determine overall health status
            if (result.AnyBadDrives)
            {
                result.OverallStatus = SsdHealthStatus.Critical;
                result.SummaryMessage = "Critical health degradation detected on one or more drives. Hardware replacement recommended.";
            }
            else if (result.AnyWarningDrives)
            {
                result.OverallStatus = SsdHealthStatus.Warning;
                result.SummaryMessage = "Drive wear or minor warning detected. Monitor drive closely.";
            }
            else if (result.AllHealthy)
            {
                result.OverallStatus = SsdHealthStatus.Optimal;
                result.SummaryMessage = "All solid state drives report optimal health and normal performance.";
            }
            else
            {
                result.OverallStatus = SsdHealthStatus.Unknown;
                result.SummaryMessage = "Storage health check completed.";
            }

            // Set top-level SsdHealth on DetectionResult if provided
            if (detection != null)
            {
                var prime = result.Disks.Find(d => !d.IsExternal) ?? (result.Disks.Count > 0 ? result.Disks[0] : null);
                if (prime != null)
                {
                    if (prime.HealthPercent.HasValue)
                    {
                        detection.SsdHealth = prime.HealthPercent.Value + "%" +
                            (!string.IsNullOrEmpty(prime.StatusLabel) && prime.StatusLabel != "UNKNOWN" ? " (" + prime.StatusLabel + ")" : "");
                    }
                    else if (!string.IsNullOrEmpty(prime.StatusLabel) && prime.StatusLabel != "UNKNOWN")
                    {
                        detection.SsdHealth = prime.StatusLabel +
                            (prime.WearPercentUsed.HasValue ? " (" + prime.WearPercentUsed.Value + "% wear)" : "");
                    }
                    else if (!string.IsNullOrEmpty(prime.WindowsHealth))
                    {
                        detection.SsdHealth = prime.WindowsHealth;
                    }
                }
            }

            return result;
        }

        private static BBTech.Core.Parsing.HdSentinelDisk FindMatchingHdsDisk(
            BBTech.Core.Models.StorageDevice dev,
            List<BBTech.Core.Parsing.HdSentinelDisk> hdsList,
            HashSet<BBTech.Core.Parsing.HdSentinelDisk> used,
            int totalDiscovered)
        {
            if (hdsList.Count == 0) return null;

            // 1. Match by Disk Number if DeviceId is PHYSICALDRIVEx
            int diskNum = ExtractDiskNumber(dev.DeviceId);
            if (diskNum >= 0)
            {
                var matchByNum = hdsList.Find(h => !used.Contains(h) && h.DiskNumber.HasValue && h.DiskNumber.Value == diskNum);
                if (matchByNum != null) return matchByNum;
            }

            // 2. Match by Model Name (loose substring match)
            if (!string.IsNullOrWhiteSpace(dev.Model))
            {
                string devModelClean = CleanModelName(dev.Model);
                var matchByModel = hdsList.Find(h => !used.Contains(h) && !string.IsNullOrWhiteSpace(h.Model) &&
                    (devModelClean.IndexOf(CleanModelName(h.Model), StringComparison.OrdinalIgnoreCase) >= 0 ||
                     CleanModelName(h.Model).IndexOf(devModelClean, StringComparison.OrdinalIgnoreCase) >= 0));
                if (matchByModel != null) return matchByModel;
            }

            // 3. If there is only one discovered drive and one HD Sentinel disk, associate them
            if (totalDiscovered == 1 && hdsList.Count == 1 && !used.Contains(hdsList[0]))
            {
                return hdsList[0];
            }

            return null;
        }

        private static int ExtractDiskNumber(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return -1;
            var m = System.Text.RegularExpressions.Regex.Match(deviceId, @"PHYSICALDRIVE(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int num)) return num;

            // Also check standard integer string (e.g. DeviceId = "0")
            if (int.TryParse(deviceId.Trim(), out int directNum)) return directNum;
            return -1;
        }

        private static string CleanModelName(string m)
        {
            if (string.IsNullOrWhiteSpace(m)) return "";
            return System.Text.RegularExpressions.Regex.Replace(m, @"[^\w\d]", "").Trim();
        }
    }
}
