using System;
using System.Text.RegularExpressions;
using BBTech.Core.Models;

namespace BBTech.Core.Parsing
{
    public sealed class CpuParseResult
    {
        /// <summary>Simple ITMS value: "i5", "Ryzen 7", "Core Ultra 7", "Celeron"...</summary>
        public string Family { get; set; } = DetectionResult.Unknown;
        /// <summary>"11th Gen", "5000 Series", "Series 1" or "Unknown".</summary>
        public string Generation { get; set; } = DetectionResult.Unknown;
        public string Vendor { get; set; } = "";
        /// <summary>Model code such as "1145G7" (details view only).</summary>
        public string ModelCode { get; set; } = "";
        /// <summary>Name without (R)/(TM)/clock speed (details view only).</summary>
        public string CleanName { get; set; } = "";
    }

    /// <summary>
    /// Turns Win32_Processor.Name into a simple family + generation.
    /// Generation is only reported when it can be derived from the model number with confidence.
    /// </summary>
    public static class CpuParser
    {
        private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        public static CpuParseResult Parse(string rawName)
        {
            var r = new CpuParseResult();
            if (string.IsNullOrWhiteSpace(rawName)) return r;

            string name = CleanName(rawName);
            r.CleanName = name;

            if (Regex.IsMatch(name, @"\bIntel\b|GenuineIntel|\bCore\b|Celeron|Pentium|Xeon|\bAtom\b", I)) r.Vendor = "Intel";
            else if (Regex.IsMatch(name, @"\bAMD\b|Ryzen|Athlon|Threadripper|Opteron", I)) r.Vendor = "AMD";
            else if (Regex.IsMatch(name, @"Snapdragon|Qualcomm", I)) r.Vendor = "Qualcomm";
            else if (Regex.IsMatch(name, @"\bApple\b", I)) r.Vendor = "Apple";

            if (TryIntelCoreI(name, r)) return r;
            if (TryIntelCoreUltra(name, r)) return r;
            if (TryIntelCoreM(name, r)) return r;
            if (TryAmdRyzen(name, r)) return r;
            if (TryOtherFamilies(name, r)) return r;
            return r;
        }

        public static string CleanName(string raw)
        {
            string s = raw;
            s = Regex.Replace(s, @"\((R|TM|C)\)|®|™", " ", I);
            s = Regex.Replace(s, @"@\s*[\d\.]+\s*GHz", " ", I);
            s = Regex.Replace(s, @"\bCPU\b", " ", I);
            s = Regex.Replace(s, @"\b(\d+|Six|Eight|Quad|Dual)[- ]Core Processor\b", " ", I);
            s = Regex.Replace(s, @"\bwith Radeon( \w+)? Graphics\b", " ", I);
            s = Regex.Replace(s, @"\bProcessor\b(?!\s+N\d)", " ", I);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        // Intel Core i3/i5/i7/i9, e.g. "Core i5-1145G7", "Core i5-8250U", "Core i5 M 520".
        private static bool TryIntelCoreI(string name, CpuParseResult r)
        {
            var m = Regex.Match(name, @"\bCore\s*(?:\s|-)?(i[3579])\b(?:[\s-]+(?:M\s*)?([A-Z]?)(\d{3,5})([A-Z]{0,2}\d?[A-Z]?))?", I);
            if (!m.Success) m = Regex.Match(name, @"\b(i[3579])-(\d{3,5})([A-Z]{0,2}\d?)", I);
            if (!m.Success) return false;

            r.Vendor = "Intel";
            r.Family = m.Groups[1].Value.ToLowerInvariant();

            string letterPrefix = m.Groups.Count > 4 ? m.Groups[2].Value : "";
            string digits = m.Groups.Count > 4 ? m.Groups[3].Value : m.Groups[2].Value;
            string suffix = m.Groups.Count > 4 ? m.Groups[4].Value : m.Groups[3].Value;

            // "Core i3-N305" style (N-series) has a letter before the digits: no Core generation.
            if (digits.Length > 0)
            {
                r.ModelCode = letterPrefix + digits + suffix;
                if (letterPrefix.Length == 0)
                {
                    int gen = IntelGenerationFromDigits(digits);
                    if (gen > 0) r.Generation = Ordinal(gen) + " Gen";
                }
            }

            // "11th Gen Intel Core ..." prefix is authoritative when present.
            var g = Regex.Match(name, @"\b(\d{1,2})(st|nd|rd|th)\s+Gen\b", I);
            if (g.Success && int.TryParse(g.Groups[1].Value, out int prefGen) && prefGen >= 1 && prefGen <= 20)
                r.Generation = Ordinal(prefGen) + " Gen";

            return true;
        }

        /// <summary>
        /// Intel Core i-series model number to generation:
        ///   3 digits (i5-520M)          -> 1st
        ///   4 digits, first 2..9        -> first digit   (i5-8250U -> 8)
        ///   4 digits starting with "1"  -> first two     (i5-1145G7 -> 11, i5-1240P -> 12)
        ///   5 digits                    -> first two     (i5-10210U -> 10, i5-14400 -> 14)
        /// </summary>
        public static int IntelGenerationFromDigits(string digits)
        {
            if (string.IsNullOrEmpty(digits)) return 0;
            int gen;
            switch (digits.Length)
            {
                case 3: gen = 1; break;
                case 4: gen = digits[0] == '1' ? int.Parse(digits.Substring(0, 2)) : digits[0] - '0'; break;
                case 5: gen = int.Parse(digits.Substring(0, 2)); break;
                default: return 0;
            }
            return gen >= 1 && gen <= 14 ? gen : 0;
        }

        // Intel Core Ultra 5/7/9 (e.g. "Core Ultra 7 155H") and Core 3/5/7 (e.g. "Core 5 120U").
        private static bool TryIntelCoreUltra(string name, CpuParseResult r)
        {
            var m = Regex.Match(name, @"\bCore\s+Ultra\s+([3579])\s+(\d)(\d{2})([A-Z]{0,2})", I);
            string prefix = "Core Ultra ";
            if (!m.Success)
            {
                m = Regex.Match(name, @"\bCore\s+([3579])\s+(\d)(\d{2})([A-Z]{1,2})\b", I);
                prefix = "Core ";
            }
            if (!m.Success)
            {
                var bare = Regex.Match(name, @"\bCore\s+Ultra\s+([3579])\b", I);
                if (!bare.Success) return false;
                r.Vendor = "Intel";
                r.Family = "Core Ultra " + bare.Groups[1].Value;
                return true;
            }

            r.Vendor = "Intel";
            r.Family = prefix + m.Groups[1].Value;
            r.ModelCode = m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value;
            int series = m.Groups[2].Value[0] - '0';
            if (series >= 1 && series <= 3) r.Generation = "Series " + series;
            return true;
        }

        // Intel Core m3/m5/m7 (e.g. "Core m3-7Y30") and "Core M-5Y10".
        private static bool TryIntelCoreM(string name, CpuParseResult r)
        {
            var m = Regex.Match(name, @"\bCore\s*(m[357]?)[\s-]+(\d)Y(\d{2})", I);
            if (!m.Success) return false;
            r.Vendor = "Intel";
            string fam = m.Groups[1].Value;
            r.Family = fam.Length == 1 ? "Core M" : fam.ToLowerInvariant();
            r.ModelCode = m.Groups[2].Value + "Y" + m.Groups[3].Value;
            int gen = m.Groups[2].Value[0] - '0';
            if (gen >= 5 && gen <= 8) r.Generation = Ordinal(gen) + " Gen";
            return true;
        }

        // AMD Ryzen 3/5/7/9 (e.g. "Ryzen 5 5500U", "Ryzen 7 PRO 4750U"), Ryzen AI, Threadripper.
        private static bool TryAmdRyzen(string name, CpuParseResult r)
        {
            var ai = Regex.Match(name, @"\bRyzen\s+AI\s+(?:Max\+?\s+|PRO\s+)?([3579])\s+(?:PRO\s+|HX\s+)*(\d)(\d{2})", I);
            if (ai.Success)
            {
                r.Vendor = "AMD";
                r.Family = "Ryzen AI " + ai.Groups[1].Value;
                r.ModelCode = ai.Groups[2].Value + ai.Groups[3].Value;
                r.Generation = "AI " + ai.Groups[2].Value + "00 Series";
                return true;
            }

            if (Regex.IsMatch(name, @"\bThreadripper\b", I))
            {
                r.Vendor = "AMD";
                r.Family = "Threadripper";
                return true;
            }

            var m = Regex.Match(name, @"\bRyzen\s+([3579])(?:\s+PRO)?(?:\s+(\d)(\d{3})([A-Z]{0,3}\d?))?", I);
            if (!m.Success) return false;

            r.Vendor = "AMD";
            r.Family = "Ryzen " + m.Groups[1].Value;
            if (m.Groups[2].Success && m.Groups[2].Value.Length > 0)
            {
                r.ModelCode = m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value;
                int series = m.Groups[2].Value[0] - '0';
                if (series >= 1 && series <= 9) r.Generation = series + "000 Series";
            }
            return true;
        }

        private static bool TryOtherFamilies(string name, CpuParseResult r)
        {
            string[][] families =
            {
                new[] { @"\bCeleron\b", "Celeron", "Intel" },
                new[] { @"\bPentium\b", "Pentium", "Intel" },
                new[] { @"\bXeon\b", "Xeon", "Intel" },
                new[] { @"\bAtom\b", "Atom", "Intel" },
                new[] { @"\bCore\s*2\b", "Core 2", "Intel" },
                new[] { @"\bIntel\b.*\bN\d{2,3}\b", "Intel N-series", "Intel" },
                new[] { @"\bAthlon\b", "Athlon", "AMD" },
                new[] { @"\bAMD\s+A(\d{1,2})-", "AMD A$1", "AMD" },
                new[] { @"\bSnapdragon\s+X\s+Elite\b", "Snapdragon X Elite", "Qualcomm" },
                new[] { @"\bSnapdragon\s+X\s+Plus\b", "Snapdragon X Plus", "Qualcomm" },
                new[] { @"\bSnapdragon\b", "Snapdragon", "Qualcomm" },
            };
            foreach (var f in families)
            {
                var m = Regex.Match(name, f[0], I);
                if (!m.Success) continue;
                r.Family = m.Result(f[1]);
                r.Vendor = f[2];
                return true;
            }
            return false;
        }

        public static string Ordinal(int n)
        {
            int mod100 = n % 100;
            if (mod100 >= 11 && mod100 <= 13) return n + "th";
            switch (n % 10)
            {
                case 1: return n + "st";
                case 2: return n + "nd";
                case 3: return n + "rd";
                default: return n + "th";
            }
        }
    }
}
