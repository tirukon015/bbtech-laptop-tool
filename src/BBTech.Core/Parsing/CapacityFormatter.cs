using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BBTech.Core.Models;

namespace BBTech.Core.Parsing
{
    /// <summary>Human-readable RAM and storage sizes ("16 GB", "512 GB", "1 TB").</summary>
    public static class CapacityFormatter
    {
        private const double GiB = 1024d * 1024 * 1024;

        private static readonly int[] RamSizesGb = { 1, 2, 3, 4, 6, 8, 12, 16, 20, 24, 32, 36, 40, 48, 64, 96, 128, 192, 256 };

        // Marketing (decimal) drive sizes in GB.
        private static readonly int[] DriveSizesGb =
        {
            16, 32, 60, 64, 80, 120, 128, 160, 180, 200, 240, 250, 256, 275, 320, 360, 400, 480, 500, 512,
            525, 640, 750, 800, 960, 1000, 1024, 1200, 1500, 1600, 1920, 2000, 2048, 3000, 3840, 4000, 4096,
            5000, 6000, 7680, 8000, 8192, 10000, 12000, 14000, 16000, 18000, 20000,
        };

        /// <summary>RAM from the sum of installed modules (exact binary GB).</summary>
        public static string FormatRamFromModules(long totalBytes)
        {
            if (totalBytes <= 0) return DetectionResult.Unknown;
            double gb = totalBytes / GiB;
            if (gb < 1) return Math.Round(totalBytes / (1024d * 1024)) + " MB";
            return Math.Round(gb).ToString(CultureInfo.InvariantCulture) + " GB";
        }

        /// <summary>
        /// RAM from the OS-visible total, which is slightly below the installed amount
        /// (reserved for firmware / integrated graphics): round up to the next common size.
        /// </summary>
        public static string FormatRamFromVisible(long visibleBytes)
        {
            if (visibleBytes <= 0) return DetectionResult.Unknown;
            double gb = visibleBytes / GiB;
            foreach (int size in RamSizesGb)
                if (gb <= size * 1.0001) return size + " GB";
            return Math.Ceiling(gb) + " GB";
        }

        /// <summary>Drive size as printed on the box, e.g. 512110190592 bytes -> "512 GB".</summary>
        public static string FormatDrive(long bytes)
        {
            if (bytes <= 0) return DetectionResult.Unknown;
            double gb = bytes / 1e9;

            int best = 0;
            double bestDiff = double.MaxValue;
            foreach (int size in DriveSizesGb)
            {
                double diff = Math.Abs(gb - size) / size;
                if (diff < bestDiff) { bestDiff = diff; best = size; }
            }

            // Accept a standard size if within 5% (partition/firmware reserve differences).
            if (best > 0 && bestDiff <= 0.05) return GbLabel(best);

            if (gb >= 1000) return (gb / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " TB";
            return Math.Round(gb).ToString(CultureInfo.InvariantCulture) + " GB";
        }

        private static string GbLabel(int gb)
        {
            if (gb < 1000) return gb + " GB";
            // 1000 and 1024 are both sold as "1 TB"; 1920 as "1.92 TB".
            double tb = (gb % 1024 == 0) ? gb / 1024d : gb / 1000d;
            return tb.ToString("0.##", CultureInfo.InvariantCulture) + " TB";
        }

        /// <summary>ITMS storage value for one or more internal drives, e.g. "512 GB SSD + 1 TB HDD".</summary>
        public static string StorageSummary(IEnumerable<StorageDevice> devices)
        {
            var list = (devices ?? Enumerable.Empty<StorageDevice>()).Where(d => !d.IsExternal).ToList();
            if (list.Count == 0) return DetectionResult.Unknown;
            return string.Join(" + ", list.Select(d => d.DisplayValue));
        }
    }
}
