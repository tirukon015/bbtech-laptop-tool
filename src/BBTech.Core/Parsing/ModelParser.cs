using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BBTech.Core.Models;

namespace BBTech.Core.Parsing
{
    public sealed class ModelParseResult
    {
        public string Brand { get; set; } = DetectionResult.Unknown;
        /// <summary>Product line, e.g. "Latitude", "EliteBook", "ThinkPad".</summary>
        public string Model { get; set; } = DetectionResult.Unknown;
        /// <summary>Model designation inside the line, e.g. "5420", "845 G11", "T14".</summary>
        public string ModelNumber { get; set; } = DetectionResult.Unknown;
        /// <summary>Firmware machine-type code when it is not the friendly name (e.g. "20W0S22200").</summary>
        public string ProductCode { get; set; } = "";
        /// <summary>The cleaned friendly name the split was made from.</summary>
        public string CleanName { get; set; } = "";

        public override string ToString() => Brand + " | " + Model + " | " + ModelNumber;
    }

    /// <summary>
    /// Splits the model strings that Windows/firmware report into Brand / Model / Model Number.
    ///
    /// The approach is generic rather than per-laptop:
    ///   1. Normalise the manufacturer to a brand.
    ///   2. Choose the most descriptive name among Win32_ComputerSystem.Model,
    ///      Win32_ComputerSystemProduct.Version and SystemFamily (Lenovo keeps the
    ///      friendly name in Version; Samsung/Huawei keep it in SystemFamily).
    ///   3. Remove brand prefixes and marketing noise ("Notebook PC", "14 inch", ...).
    ///   4. Leading purely-alphabetic words form the Model (product line); the rest,
    ///      starting with the first token that contains a digit, is the Model Number.
    /// </summary>
    public static class ModelParser
    {
        private static readonly string U = DetectionResult.Unknown;

        // Trailing marketing words to drop (longest first).
        private static readonly string[] TrailingNoise =
        {
            "Mobile Workstation PC", "Mobile Workstation", "Notebook PC", "Laptop PC", "Desktop PC",
            "Convertible PC", "Detachable PC", "Notebook", "Laptop", "PC", "Computer", "Series",
        };

        // Words in ALL CAPS that should get their usual marketing casing.
        private static readonly Dictionary<string, string> Casing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"LATITUDE","Latitude"},{"INSPIRON","Inspiron"},{"VOSTRO","Vostro"},{"PRECISION","Precision"},
            {"ELITEBOOK","EliteBook"},{"PROBOOK","ProBook"},{"ZBOOK","ZBook"},{"PAVILION","Pavilion"},
            {"THINKPAD","ThinkPad"},{"THINKBOOK","ThinkBook"},{"IDEAPAD","IdeaPad"},{"YOGA","Yoga"},{"LEGION","Legion"},
            {"ASPIRE","Aspire"},{"SWIFT","Swift"},{"NITRO","Nitro"},{"PREDATOR","Predator"},{"TRAVELMATE","TravelMate"},
            {"EXTENSA","Extensa"},{"VIVOBOOK","VivoBook"},{"ZENBOOK","ZenBook"},{"EXPERTBOOK","ExpertBook"},
            {"MATEBOOK","MateBook"},{"GALAXY","Galaxy"},{"SURFACE","Surface"},{"MODERN","Modern"},{"PRESTIGE","Prestige"},
            {"SATELLITE","Satellite"},{"TECRA","Tecra"},{"PORTEGE","Portege"},{"LIFEBOOK","LifeBook"},{"GAMING","Gaming"},
            {"CHROMEBOOK","Chromebook"},{"STRIX","Strix"},{"ZEPHYRUS","Zephyrus"},{"SPECTRE","Spectre"},{"VICTUS","Victus"},
            {"TOUGHBOOK","Toughbook"},{"BOOK","Book"},{"THIN","Thin"},{"SLIM","Slim"},{"PRO","Pro"},
        };

        /// <summary>Parse a single "Brand Model Number" string, e.g. "Dell Latitude 5420".</summary>
        public static ModelParseResult ParseFullName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return new ModelParseResult();
            string trimmed = fullName.Trim();
            string firstWord = trimmed.Split(' ')[0];
            string brand = BrandCatalog.Normalize(firstWord);
            // Clean() strips the brand prefix from the model string, so pass the whole name.
            return Parse(brand == U ? "" : firstWord, trimmed);
        }

        /// <summary>
        /// Parse firmware values.
        /// </summary>
        /// <param name="manufacturer">Win32_ComputerSystem.Manufacturer</param>
        /// <param name="model">Win32_ComputerSystem.Model</param>
        /// <param name="systemFamily">Win32_ComputerSystem.SystemFamily</param>
        /// <param name="productVersion">Win32_ComputerSystemProduct.Version</param>
        public static ModelParseResult Parse(string manufacturer, string model, string systemFamily = null, string productVersion = null)
        {
            var result = new ModelParseResult { Brand = BrandCatalog.Normalize(manufacturer) };

            // If the manufacturer is missing, the brand may still be the first word of the model.
            if (result.Brand == U && !string.IsNullOrWhiteSpace(model))
            {
                string first = model.Trim().Split(' ')[0];
                string guess = BrandCatalog.Normalize(first);
                if (guess != U && IsKnownBrand(guess)) result.Brand = guess;
            }

            string cModel = Clean(model, result.Brand);
            string cVersion = Clean(productVersion, result.Brand);
            string cFamily = Clean(systemFamily, result.Brand);

            // Pick the friendly name.
            var ordered = new List<string>();
            if (result.Brand == "Lenovo") ordered.AddRange(new[] { cVersion, cFamily, cModel });
            else ordered.AddRange(new[] { cModel, cFamily, cVersion });

            string friendly = ordered.FirstOrDefault(c => c.Length > 0 && !IsProductCode(c));
            string code = new[] { cModel, cVersion, cFamily }.FirstOrDefault(c => c.Length > 0 && IsProductCode(c)) ?? "";

            if (friendly == null)
            {
                // Only a machine-type code is available; it is the best "model number" we have.
                if (code.Length > 0)
                {
                    result.ModelNumber = code;
                    result.ProductCode = code;
                    result.CleanName = code;
                }
                return result;
            }

            // A firmware family can be less specific than the model (e.g. family "ThinkPad"
            // with version "ThinkPad T14 Gen 2i"): prefer the longer string that starts with it.
            foreach (var c in ordered)
            {
                if (c.Length > friendly.Length && !IsProductCode(c) &&
                    c.StartsWith(friendly + " ", StringComparison.OrdinalIgnoreCase))
                    friendly = c;
            }

            result.CleanName = friendly;
            if (code.Length > 0 && !string.Equals(code, friendly, StringComparison.OrdinalIgnoreCase))
                result.ProductCode = code;

            Split(friendly, out string line, out string number);
            result.Model = line.Length > 0 ? line : U;
            if (number.Length > 0) result.ModelNumber = number;
            else if (result.ProductCode.Length > 0) result.ModelNumber = result.ProductCode;
            return result;
        }

        private static bool IsKnownBrand(string brand) =>
            new[] { "Dell", "HP", "Lenovo", "Acer", "ASUS", "MSI", "Microsoft", "Huawei", "Samsung", "Toshiba",
                    "Fujitsu", "Apple", "Razer", "Alienware", "Gigabyte", "Dynabook", "Honor", "Xiaomi" }.Contains(brand);

        /// <summary>
        /// True for single-token machine codes such as "20W0S22200", "950XED", "BoDE-WXX9", "82H8".
        /// </summary>
        public static bool IsProductCode(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.Contains(' ')) return false;
            if (!s.Any(char.IsDigit)) return false;
            if (!s.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')) return false;
            // A short token like "G15" or "T14" alone is a legitimate model name, but
            // firmware codes are typically 4+ chars mixing letters and digits or all digits.
            return s.Length >= 4;
        }

        /// <summary>Remove brand prefix, placeholders and marketing noise.</summary>
        public static string Clean(string raw, string brand)
        {
            if (BrandCatalog.IsPlaceholder(raw)) return "";
            string s = raw.Trim();

            // Trademark symbols.
            s = Regex.Replace(s, @"\((R|TM|C)\)|®|™", "", RegexOptions.IgnoreCase);

            // ASUS: "VivoBook_ASUSLaptop X515EA_X515EA" -> "VivoBook X515EA"
            s = Regex.Replace(s, @"_?ASUS\s*Laptop", " ", RegexOptions.IgnoreCase);
            s = s.Replace('_', ' ');

            // Samsung: "Galaxy Book2 Pro" -> "Galaxy Book 2 Pro"
            s = Regex.Replace(s, @"\b(Galaxy\s+Book)(\d)", "$1 $2", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"\s+", " ").Trim();

            // Strip leading brand words (repeatedly: "HP HP ProBook").
            bool stripped = true;
            while (stripped && brand != U)
            {
                stripped = false;
                foreach (var prefix in BrandCatalog.BrandPrefixes(brand).Concat(new[] { "ASUS", "Lenovo", "Dell", "HP", "Acer", "MSI" }))
                {
                    if (s.Equals(prefix, StringComparison.OrdinalIgnoreCase)) { s = ""; break; }
                    if (s.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        s = s.Substring(prefix.Length).Trim();
                        stripped = true;
                    }
                }
            }

            // Screen size "14 inch" / "15.6-inch": only drop it when a number already
            // precedes it ("EliteBook 845 14 inch G11"), otherwise it *is* the model number
            // ("ZBook Firefly 14 inch G8" -> "ZBook Firefly 14 G8").
            s = Regex.Replace(s, @"(?<=\d[A-Za-z]*\s)\d{2}(\.\d)?[\s-]?inch\b\s*", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(\d{2}(\.\d)?)[\s-]?inch\b", "$1", RegexOptions.IgnoreCase);

            // Trailing noise, repeatedly ("Notebook PC").
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var n in TrailingNoise)
                {
                    if (s.EndsWith(" " + n, StringComparison.OrdinalIgnoreCase))
                    {
                        s = s.Substring(0, s.Length - n.Length - 1).Trim();
                        changed = true;
                    }
                }
            }

            // Middle "Laptop"/"Notebook" words ("Pavilion Laptop 15-eg0xxx") are noise unless they
            // are the first word ("HP Laptop 15s") or part of a product name ("Surface Laptop").
            var tokens = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            for (int i = tokens.Count - 1; i >= 1; i--)
            {
                bool noise = tokens[i].Equals("Laptop", StringComparison.OrdinalIgnoreCase) ||
                             tokens[i].Equals("Notebook", StringComparison.OrdinalIgnoreCase);
                if (noise && !tokens[i - 1].Equals("Surface", StringComparison.OrdinalIgnoreCase))
                    tokens.RemoveAt(i);
            }

            // De-duplicate consecutive identical tokens (ASUS "X515EA X515EA").
            for (int i = tokens.Count - 1; i >= 1; i--)
                if (tokens[i].Equals(tokens[i - 1], StringComparison.OrdinalIgnoreCase)) tokens.RemoveAt(i);

            // Fix ALL-CAPS product line names.
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Length > 3 && tokens[i] == tokens[i].ToUpperInvariant() &&
                    Casing.TryGetValue(tokens[i], out string fixedCase))
                    tokens[i] = fixedCase;
            }

            s = string.Join(" ", tokens);
            return BrandCatalog.IsPlaceholder(s) ? "" : s;
        }

        /// <summary>
        /// Leading alphabetic words = product line; remainder = model number.
        /// "Latitude 5420" -> ("Latitude", "5420"); "ZBook Firefly 14 G8" -> ("ZBook Firefly", "14 G8");
        /// "G15 5520" -> ("G15", "5520"); "Aspire" -> ("Aspire", "").
        /// </summary>
        public static void Split(string name, out string line, out string number)
        {
            var tokens = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            while (i < tokens.Length && IsAlphaWord(tokens[i])) i++;

            if (i == 0)
            {
                // First token already contains a digit ("G15 5520", "250 G8"): it is the line name.
                line = tokens.Length > 0 ? tokens[0] : "";
                number = string.Join(" ", tokens.Skip(1));
            }
            else
            {
                line = string.Join(" ", tokens.Take(i));
                number = string.Join(" ", tokens.Skip(i));
            }
        }

        private static bool IsAlphaWord(string t) => t.All(ch => char.IsLetter(ch) || ch == '-' || ch == '+' || ch == '\'');
    }
}
