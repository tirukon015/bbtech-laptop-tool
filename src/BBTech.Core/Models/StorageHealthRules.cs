using System;
using System.Collections.Generic;
using System.Globalization;

namespace BBTech.Core.Models
{
    public sealed class StorageHealthVerdict
    {
        /// <summary>HEALTHY, WARNING, CRITICAL or UNKNOWN.</summary>
        public string Status { get; set; }
        /// <summary>The measured facts that decided the status (e.g. "percentage_used=84 >= 80").</summary>
        public List<string> Reasons { get; } = new List<string>();
        /// <summary>The rule set that was applied (stored with the verdict so it can be audited later).</summary>
        public string Rule { get; set; }
    }

    /// <summary>
    /// The ONLY place a drive's health word (HEALTHY / WARNING / CRITICAL) is decided. It is derived from the raw
    /// values the drive reported, using the documented thresholds below; nobody can choose it by hand. Without raw
    /// values the status is UNKNOWN. The raw values always stay the source of truth next to the verdict.
    /// </summary>
    public static class StorageHealthRules
    {
        public const int WearWarningPercentUsed = 80;     // NVMe percentage_used / SATA (100 - life remaining)
        public const int WearCriticalPercentUsed = 100;
        public const int TemperatureWarningC = 70;
        public const int ReallocatedWarning = 1;           // any reallocated sector
        public const int ReallocatedCritical = 100;

        public const string RuleDescription =
            "CRITICAL: NVMe critical_warning != 0, available spare below its threshold, wear >= 100% used, or >= 100 reallocated sectors. " +
            "WARNING: wear >= 80% used, any media/data-integrity error, any reallocated/pending/uncorrectable sector, or temperature >= 70 °C. " +
            "HEALTHY: health values read and none of the above. UNKNOWN: the drive's health values could not be read.";

        private static long? Num(IDictionary<string, string> raw, string key)
        {
            if (raw == null || !raw.TryGetValue(key, out var v) || string.IsNullOrEmpty(v)) return null;
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (long?)null;
        }

        /// <summary>
        /// <paramref name="raw"/>: values the drive reported (NVMe health log / ATA SMART).
        /// <paramref name="percentageUsed"/>/<paramref name="temperatureC"/>: from another measured source
        /// (Windows reliability counters, HD Sentinel) when the raw log was not readable.
        /// </summary>
        public static StorageHealthVerdict Evaluate(IDictionary<string, string> raw, int? percentageUsed = null, int? temperatureC = null)
        {
            var v = new StorageHealthVerdict { Rule = RuleDescription };
            long? crit = Num(raw, "critical_warning"), spare = Num(raw, "available_spare_percent"), spareThr = Num(raw, "available_spare_threshold_percent");
            long? used = Num(raw, "percentage_used") ?? (Num(raw, "life_remaining_percent") is long life ? 100 - life : (long?)null) ?? percentageUsed;
            long? media = Num(raw, "media_and_data_integrity_errors"), realloc = Num(raw, "reallocated_sector_count");
            long? pending = Num(raw, "current_pending_sector_count"), uncorrectable = Num(raw, "offline_uncorrectable_sector_count") ?? Num(raw, "reported_uncorrectable_errors");
            long? tempK = Num(raw, "composite_temperature_k");
            long? temp = Num(raw, "temperature_c") ?? (tempK.HasValue && tempK > 200 && tempK < 400 ? tempK - 273 : null) ?? temperatureC;

            if (crit == null && used == null && media == null && realloc == null && pending == null && uncorrectable == null && temp == null)
            {
                v.Status = "UNKNOWN";
                v.Reasons.Add("no health values were readable");
                return v;
            }

            bool critical = false, warning = false;
            void Crit(string why) { critical = true; v.Reasons.Add(why); }
            void Warn(string why) { warning = true; v.Reasons.Add(why); }

            if (crit.HasValue && crit.Value != 0) Crit("critical_warning=" + crit);
            if (spare.HasValue && spareThr.HasValue && spareThr.Value > 0 && spare.Value < spareThr.Value) Crit("available_spare " + spare + "% < threshold " + spareThr + "%");
            if (used.HasValue && used.Value >= WearCriticalPercentUsed) Crit("percentage_used=" + used + " >= " + WearCriticalPercentUsed);
            else if (used.HasValue && used.Value >= WearWarningPercentUsed) Warn("percentage_used=" + used + " >= " + WearWarningPercentUsed);
            if (realloc.HasValue && realloc.Value >= ReallocatedCritical) Crit("reallocated_sector_count=" + realloc + " >= " + ReallocatedCritical);
            else if (realloc.HasValue && realloc.Value >= ReallocatedWarning) Warn("reallocated_sector_count=" + realloc);
            if (media.HasValue && media.Value > 0) Warn("media_and_data_integrity_errors=" + media);
            if (pending.HasValue && pending.Value > 0) Warn("current_pending_sector_count=" + pending);
            if (uncorrectable.HasValue && uncorrectable.Value > 0) Warn("uncorrectable sectors/errors=" + uncorrectable);
            if (temp.HasValue && temp.Value >= TemperatureWarningC) Warn("temperature=" + temp + " °C >= " + TemperatureWarningC);

            v.Status = critical ? "CRITICAL" : warning ? "WARNING" : "HEALTHY";
            if (!critical && !warning) v.Reasons.Add("no threshold reached");
            return v;
        }
    }
}
