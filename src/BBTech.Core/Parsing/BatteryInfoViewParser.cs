using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BBTech.Core.Models;

namespace BBTech.Core.Parsing
{
    /// <summary>
    /// Parses the XML written by NirSoft BatteryInfoView ("BatteryInfoView.exe /sxml file.xml").
    /// Each &lt;item&gt; has a &lt;description&gt; and one &lt;value&gt; per battery.
    /// </summary>
    public static class BatteryInfoViewParser
    {
        public static BatteryDetails Parse(string xml)
        {
            var result = new BatteryDetails { Source = "BatteryInfoView" };
            if (string.IsNullOrWhiteSpace(xml)) return result;

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { return result; }

            var rows = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.Descendants("item"))
            {
                string desc = Regex.Replace((string)item.Element("description") ?? "", @"\s+", " ").Trim();
                if (desc.Length == 0) continue;
                rows[desc] = item.Elements("value").Select(v => ((string)v ?? "").Trim()).ToList();
            }
            if (rows.Count == 0) return result;

            // Number of batteries = number of value columns that have a design or full capacity.
            int columns = rows.Values.Max(v => v.Count);
            long designSum = 0, fullSum = 0, currentSum = 0;
            int batteries = 0;
            for (int col = 0; col < columns; col++)
            {
                long? design = ParseEnergy(Get(rows, col, "Designed Capacity"));
                long? full = ParseEnergy(Get(rows, col, "Full Charged Capacity"));
                if (design == null && full == null) continue;
                batteries++;
                designSum += design ?? 0;
                fullSum += full ?? 0;
                currentSum += ParseEnergy(Get(rows, col, "Current Capacity Value")) ?? 0;

                if (batteries == 1)
                {
                    result.Name = Get(rows, col, "Battery Name");
                    result.Manufacturer = Get(rows, col, "Manufacture Name");
                    result.Chemistry = Get(rows, col, "Chemistry");
                    result.PowerState = Get(rows, col, "Power State");
                    result.ChargePercent = ParsePercent(Get(rows, col, "Current Capacity (in %)"));
                    result.CycleCount = ParseInt(Get(rows, col, "Number of charge/discharge cycles"));
                    result.HealthPercent = ParsePercent(Get(rows, col, "Battery Health"));
                }
            }

            result.BatteryCount = batteries;
            result.Present = batteries > 0;
            if (!result.Present) return result;

            if (designSum > 0) result.DesignCapacityMWh = designSum;
            if (fullSum > 0) result.FullChargeCapacityMWh = fullSum;
            if (currentSum > 0) result.CurrentCapacityMWh = currentSum;

            // Prefer our own calculation (locale-independent, covers multiple batteries).
            double? computed = BatteryHealth.Compute(result.DesignCapacityMWh, result.FullChargeCapacityMWh);
            if (computed != null) result.HealthPercent = computed;
            else if (batteries > 1) result.HealthPercent = null;
            return result;
        }

        private static string Get(Dictionary<string, List<string>> rows, int col, string description)
        {
            // Descriptions sometimes carry trailing spaces ("Chemistry ").
            if (!rows.TryGetValue(description, out var values)) return "";
            return col < values.Count ? values[col] : "";
        }

        /// <summary>"44,820 mWh" / "44.820 mWh" -> 44820. Returns null for empty/zero.</summary>
        public static long? ParseEnergy(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = Regex.Match(s, @"[\d][\d,\.\s ']*");
            if (!m.Success) return null;
            string digits = Regex.Replace(m.Value, @"[^\d]", "");
            return long.TryParse(digits, out long v) && v > 0 ? v : (long?)null;
        }

        public static int? ParseInt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string digits = Regex.Replace(s, @"[^\d]", "");
            return int.TryParse(digits, out int v) ? v : (int?)null;
        }

        /// <summary>"87.9%" or "87,9%" -> 87.9</summary>
        public static double? ParsePercent(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = Regex.Match(s, @"(\d+)(?:[\.,](\d+))?\s*%");
            if (!m.Success) return null;
            string norm = m.Groups[1].Value + (m.Groups[2].Success ? "." + m.Groups[2].Value : "");
            return double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
        }
    }

    public static class BatteryHealth
    {
        /// <summary>Full charge capacity as a percentage of design capacity.</summary>
        public static double? Compute(long? designMWh, long? fullMWh)
        {
            if (designMWh == null || fullMWh == null || designMWh <= 0 || fullMWh <= 0) return null;
            double pct = fullMWh.Value * 100.0 / designMWh.Value;
            // Values far outside a plausible range indicate bad firmware data.
            if (pct > 150 || pct < 1) return null;
            return Math.Round(pct, 1);
        }

        /// <summary>
        /// ITMS value, e.g. "87%". Rounded down so health is never overstated (87.9% -> "87%").
        /// Never invents a number.
        /// </summary>
        public static string Format(double? percent)
        {
            if (percent == null) return DetectionResult.Unknown;
            return Math.Floor(percent.Value + 1e-9).ToString(CultureInfo.InvariantCulture) + "%";
        }
    }
}
