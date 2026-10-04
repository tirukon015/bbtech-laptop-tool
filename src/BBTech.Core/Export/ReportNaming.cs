using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BBTech.Core.Models;
using BBTech.Core.Testing;

namespace BBTech.Core.Export
{
    /// <summary>
    /// Inspection report file names that identify the laptop without opening the file:
    ///
    ///     {Brand}_{Model}_{ModelNumber}_{SerialNumber}_{YYYYMMDD}_{STATUS}.{ext}
    ///     Lenovo_ThinkPad_T14-Gen-2_SERIALEX01_20261004_INCOMPLETE.csv
    ///
    /// Rules: "_" separates the fields, so inside a value spaces and underscores become "-";
    /// characters that are invalid in Windows file names are removed; each value is length-capped;
    /// missing values become NOMODEL / NOSERIAL (brand/model: UNKNOWN); the status is always last.
    /// Only hardware identification from the detection result is used.
    /// </summary>
    public static class ReportNaming
    {
        public const string NoSerial = "NOSERIAL";
        public const string NoModelNumber = "NOMODEL";
        public const string UnknownValue = "UNKNOWN";

        private const int MaxBrand = 20, MaxModel = 30, MaxModelNumber = 30, MaxSerial = 30;

        public static string BuildFileName(DetectionResult detection, OverallResult overall, DateTime when, string extension)
        {
            return BuildFileName(detection?.Brand, detection?.Model, detection?.ModelNumber, detection?.System?.SerialNumber,
                                 overall, when, extension);
        }

        public static string BuildFileName(string brand, string model, string modelNumber, string serial,
                                           OverallResult overall, DateTime when, string extension, int duplicate = 0)
        {
            string date = when.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + (duplicate > 1 ? "-" + duplicate : "");
            string ext = (extension ?? "").TrimStart('.');
            var parts = new[]
            {
                Part(brand, UnknownValue, MaxBrand),
                Part(model, UnknownValue, MaxModel),
                Part(modelNumber, NoModelNumber, MaxModelNumber),
                Part(serial, NoSerial, MaxSerial),
                date,
                TestText.Label(overall),
            };
            return string.Join("_", parts) + (ext.Length > 0 ? "." + ext : "");
        }

        /// <summary>Makes one field safe for a Windows file name (and free of the "_" separator).</summary>
        public static string Part(string value, string fallback, int maxLength)
        {
            if (IsMissing(value)) return fallback;

            string s = value.Normalize(NormalizationForm.FormKC).Trim();
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            s = new string(s.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray());
            s = Regex.Replace(s, @"[\s_]+", "-");          // spaces/underscores -> single hyphen
            s = Regex.Replace(s, @"[^\w\-\.\+]", "");      // keep letters, digits, - . + only
            s = Regex.Replace(s, @"-{2,}", "-").Trim('-', '.');
            if (s.Length > maxLength) s = s.Substring(0, maxLength).TrimEnd('-', '.');
            if (s.Length == 0) return fallback;

            // Reserved device names (CON, NUL, COM1...) are not valid on their own.
            if (Regex.IsMatch(s, @"^(CON|PRN|AUX|NUL|COM\d|LPT\d)$", RegexOptions.IgnoreCase)) s += "-";
            return s;
        }

        private static bool IsMissing(string v) =>
            string.IsNullOrWhiteSpace(v) || v.Trim().Equals(DetectionResult.Unknown, StringComparison.OrdinalIgnoreCase);
    }
}
