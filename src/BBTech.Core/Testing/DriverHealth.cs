using System;
using System.Collections.Generic;
using System.Linq;

namespace BBTech.Core.Testing
{
    /// <summary>One Plug and Play device as Windows reports it (Win32_PnPEntity).</summary>
    public sealed class PnpDeviceInfo
    {
        public string Name { get; set; }
        public string PnpClass { get; set; }
        /// <summary>Win32_PnPEntity.ConfigManagerErrorCode: 0 = working; 28 = no driver; 22 = disabled; others = problem.</summary>
        public int ErrorCode { get; set; }
        /// <summary>Win32_PnPEntity.PNPDeviceID (used to tell the laptop's own hardware from paired accessories).</summary>
        public string PnpDeviceId { get; set; }

        /// <summary>
        /// A paired Bluetooth accessory (mouse, headset) that is switched off shows as "cannot start". It is not
        /// part of the laptop, so it is never a driver problem of the laptop.
        /// </summary>
        public bool IsRemoteAccessory =>
            PnpDeviceId != null && (PnpDeviceId.StartsWith("BTHENUM\\", System.StringComparison.OrdinalIgnoreCase) ||
                                    PnpDeviceId.StartsWith("BTHLE\\", System.StringComparison.OrdinalIgnoreCase) ||
                                    PnpDeviceId.StartsWith("BTHLEDEVICE\\", System.StringComparison.OrdinalIgnoreCase) ||
                                    PnpDeviceId.StartsWith("BTHHFENUM\\", System.StringComparison.OrdinalIgnoreCase) ||
                                    PnpDeviceId.StartsWith("HID\\{0000", System.StringComparison.OrdinalIgnoreCase));
    }

    public sealed class DriverHealthReport
    {
        public List<string> Problems { get; } = new List<string>();
        public List<string> DisplayAdapters { get; } = new List<string>();
        public bool BasicDisplayDriverOnly { get; set; }
        public bool Ok => Problems.Count == 0;
    }

    /// <summary>
    /// Replaces "open Device Manager and look for yellow triangles": every device with a driver
    /// problem is listed in plain language, and a laptop running on the "Microsoft Basic Display
    /// Adapter" (no real graphics driver) is flagged.
    /// </summary>
    public static class DriverHealth
    {
        public static string Describe(int code)
        {
            switch (code)
            {
                case 0: return "working";
                case 1: return "not configured correctly";
                case 10: return "cannot start";
                case 18: return "driver must be reinstalled";
                case 22: return "disabled";
                case 24: return "not present or not working";
                case 28: return "no driver installed";
                case 31: return "driver failed to load";
                case 43: return "stopped by Windows (reported a problem)";
                default: return "problem (code " + code + ")";
            }
        }

        public static DriverHealthReport Evaluate(IEnumerable<PnpDeviceInfo> devices)
        {
            var report = new DriverHealthReport();
            foreach (var d in devices ?? Enumerable.Empty<PnpDeviceInfo>())
            {
                if (d == null || d.IsRemoteAccessory) continue;
                string name = string.IsNullOrWhiteSpace(d.Name) ? "Unknown device" : d.Name.Trim();
                if (string.Equals(d.PnpClass, "Display", StringComparison.OrdinalIgnoreCase)) report.DisplayAdapters.Add(name);
                if (d.ErrorCode != 0) report.Problems.Add(name + ": " + Describe(d.ErrorCode));
            }
            report.BasicDisplayDriverOnly = report.DisplayAdapters.Count > 0 &&
                report.DisplayAdapters.All(a => a.IndexOf("Basic Display", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                a.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0);
            if (report.BasicDisplayDriverOnly) report.Problems.Add("Graphics: only the Microsoft Basic Display Adapter is active (graphics driver missing)");
            return report;
        }
    }
}
