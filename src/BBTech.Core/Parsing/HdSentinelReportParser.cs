using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BBTech.Core.Parsing
{
    public sealed class HdSentinelDisk
    {
        public int? DiskNumber { get; set; }
        public string Model { get; set; } = "";
        public string Interface { get; set; } = "";
        public string TotalSize { get; set; } = "";
        public int? HealthPercent { get; set; }
        public int? PerformancePercent { get; set; }
        public string Temperature { get; set; } = "";
        public string PowerOnTime { get; set; } = "";
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// Parses the XML report produced by Hard Disk Sentinel ("HDSentinel.exe /XMLREPORT").
    /// The report has one "Physical_Disk_Information_Disk_N" element per disk whose
    /// "Hard_Disk_Summary" child holds model, size, health and performance. Element names
    /// are matched loosely so minor version differences do not break parsing.
    /// </summary>
    public static class HdSentinelReportParser
    {
        public static List<HdSentinelDisk> Parse(string xml)
        {
            var disks = new List<HdSentinelDisk>();
            if (string.IsNullOrWhiteSpace(xml)) return disks;

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { return disks; }

            var diskNodes = doc.Descendants()
                .Where(e => e.Name.LocalName.StartsWith("Physical_Disk_Information", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var node in diskNodes)
            {
                var summary = node.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Hard_Disk_Summary", StringComparison.OrdinalIgnoreCase)) ?? node;
                string Val(params string[] names)
                {
                    foreach (var n in names)
                    {
                        var el = summary.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase));
                        if (el != null) return (el.Value ?? "").Trim();
                    }
                    return "";
                }

                var disk = new HdSentinelDisk
                {
                    Model = Val("Hard_Disk_Model_ID", "Model_ID", "Hard_Disk_Model"),
                    Interface = Val("Interface"),
                    TotalSize = Val("Total_Size"),
                    Temperature = Val("Current_Temperature"),
                    PowerOnTime = Val("Power_On_Time"),
                    Description = Val("Description"),
                    HealthPercent = Percent(Val("Health")),
                    PerformancePercent = Percent(Val("Performance")),
                };
                if (int.TryParse(Val("Hard_Disk_Number"), out int num)) disk.DiskNumber = num;
                else
                {
                    var m = Regex.Match(node.Name.LocalName, @"(\d+)$");
                    if (m.Success) disk.DiskNumber = int.Parse(m.Groups[1].Value);
                }

                if (disk.Model.Length > 0 || disk.HealthPercent != null) disks.Add(disk);
            }
            return disks;
        }

        private static int? Percent(string s)
        {
            var m = Regex.Match(s ?? "", @"(\d{1,3})\s*%");
            if (!m.Success) return null;
            int v = int.Parse(m.Groups[1].Value);
            return v >= 0 && v <= 100 ? v : (int?)null;
        }
    }
}
