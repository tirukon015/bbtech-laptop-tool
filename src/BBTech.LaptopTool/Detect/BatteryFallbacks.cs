using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BBTech.Core.Models;
using BBTech.Core.Parsing;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.Detect
{
    /// <summary>Battery sources built into Windows, used when BatteryInfoView is missing or fails.</summary>
    internal static class BatteryFallbacks
    {
        public static BatteryDetails FromWmi()
        {
            try
            {
                var design = Wmi.Query("SELECT DesignedCapacity FROM BatteryStaticData", @"root\wmi");
                var full = Wmi.Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\wmi");
                if (design.Count == 0 && full.Count == 0) return null;

                long d = design.Sum(o => Wmi.Long(o, "DesignedCapacity"));
                long f = full.Sum(o => Wmi.Long(o, "FullChargedCapacity"));
                var cycles = Wmi.Query("SELECT CycleCount FROM BatteryCycleCount", @"root\wmi").Select(o => Wmi.Int(o, "CycleCount")).Where(c => c > 0).ToList();
                var b = new BatteryDetails
                {
                    Present = d > 0 || f > 0,
                    BatteryCount = Math.Max(design.Count, full.Count),
                    DesignCapacityMWh = d > 0 ? d : (long?)null,
                    FullChargeCapacityMWh = f > 0 ? f : (long?)null,
                    CycleCount = cycles.Count > 0 ? cycles.Max() : (int?)null,
                    Source = "Windows WMI",
                };
                b.HealthPercent = BatteryHealth.Compute(b.DesignCapacityMWh, b.FullChargeCapacityMWh);
                return b;
            }
            catch (Exception ex)
            {
                AppLog.Warn("WMI battery fallback failed", ex);
                return null;
            }
        }

        public static BatteryDetails FromPowercfg()
        {
            string file = AppPaths.NewTempFile(".xml");
            try
            {
                string exe = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
                var run = ProcessRunner.Run(exe, "/batteryreport /xml /output \"" + file + "\"", 30000);
                if (run.Error != null || !File.Exists(file)) return null;

                var doc = XDocument.Load(file);
                var batteries = doc.Descendants().Where(e => e.Name.LocalName == "Battery").ToList();
                if (batteries.Count == 0) return null;

                long Sum(string name) => batteries.Sum(bt =>
                {
                    var el = bt.Elements().FirstOrDefault(e => e.Name.LocalName == name);
                    return el != null && long.TryParse(el.Value, out long v) ? v : 0;
                });

                long d = Sum("DesignCapacity"), f = Sum("FullChargeCapacity");
                int cycles = (int)batteries.Max(bt =>
                {
                    var el = bt.Elements().FirstOrDefault(e => e.Name.LocalName == "CycleCount");
                    return el != null && long.TryParse(el.Value, out long v) ? v : 0;
                });

                var b = new BatteryDetails
                {
                    Present = d > 0 || f > 0,
                    BatteryCount = batteries.Count,
                    DesignCapacityMWh = d > 0 ? d : (long?)null,
                    FullChargeCapacityMWh = f > 0 ? f : (long?)null,
                    CycleCount = cycles > 0 ? cycles : (int?)null,
                    Source = "Windows battery report",
                };
                b.HealthPercent = BatteryHealth.Compute(b.DesignCapacityMWh, b.FullChargeCapacityMWh);
                return b;
            }
            catch (Exception ex)
            {
                AppLog.Warn("powercfg battery fallback failed", ex);
                return null;
            }
            finally { AppPaths.TryDelete(file); }
        }
    }
}
