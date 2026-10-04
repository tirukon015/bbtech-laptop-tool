using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BBTech.Core.Parsing
{
    /// <summary>
    /// Normalises firmware manufacturer strings ("Dell Inc.", "ASUSTeK COMPUTER INC.",
    /// "Hewlett-Packard", ...) to a clean brand name.
    /// </summary>
    public static class BrandCatalog
    {
        // Keys are lower-case prefixes of the normalised manufacturer string.
        private static readonly KeyValuePair<string, string>[] Aliases =
        {
            P("dell", "Dell"),
            P("alienware", "Alienware"),
            P("hewlett-packard", "HP"),
            P("hewlett packard", "HP"),
            P("hp", "HP"),
            P("lenovo", "Lenovo"),
            P("acer", "Acer"),
            P("asustek", "ASUS"),
            P("asus", "ASUS"),
            P("micro-star", "MSI"),
            P("micro star", "MSI"),
            P("msi", "MSI"),
            P("microsoft", "Microsoft"),
            P("huawei", "Huawei"),
            P("honor", "Honor"),
            P("samsung", "Samsung"),
            P("toshiba", "Toshiba"),
            P("dynabook", "Dynabook"),
            P("fujitsu", "Fujitsu"),
            P("apple", "Apple"),
            P("gigabyte", "Gigabyte"),
            P("razer", "Razer"),
            P("lg electronics", "LG"),
            P("panasonic", "Panasonic"),
            P("xiaomi", "Xiaomi"),
            P("timi", "Xiaomi"),
            P("vaio", "VAIO"),
            P("sony", "Sony"),
            P("google", "Google"),
            P("framework", "Framework"),
            P("chuwi", "Chuwi"),
            P("medion", "Medion"),
            P("getac", "Getac"),
            P("walton", "Walton"),
            P("infinix", "Infinix"),
            P("avita", "Avita"),
            P("teclast", "Teclast"),
            P("system76", "System76"),
            P("clevo", "Clevo"),
            P("schenker", "Schenker"),
            P("positivo", "Positivo"),
        };

        // Values firmware vendors leave in unconfigured fields.
        private static readonly string[] Placeholders =
        {
            "to be filled by o.e.m.", "to be filled by oem", "system manufacturer", "system product name",
            "system version", "default string", "o.e.m.", "oem", "not applicable", "not available", "n/a",
            "none", "invalid", "unknown", "x.x", "0", "123456789", "type1productconfigid", "chassis manufacture",
            "base board product name", "sku", "default", "all series",
        };

        private static readonly string[] CorporateSuffixes =
        {
            "inc", "corp", "corporation", "co", "ltd", "limited", "llc", "gmbh", "ag", "sa", "plc",
            "technology", "technologies", "computer", "computers", "electronics", "international", "company",
        };

        private static KeyValuePair<string, string> P(string k, string v) => new KeyValuePair<string, string>(k, v);

        public static bool IsPlaceholder(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string v = value.Trim().ToLowerInvariant();
            return Placeholders.Contains(v);
        }

        /// <summary>Returns the clean brand name, or "Unknown".</summary>
        public static string Normalize(string manufacturer)
        {
            if (IsPlaceholder(manufacturer)) return Models.DetectionResult.Unknown;
            string m = Regex.Replace(manufacturer.Trim().ToLowerInvariant(), @"\s+", " ");

            foreach (var a in Aliases)
            {
                if (m == a.Key || m.StartsWith(a.Key + " ") || m.StartsWith(a.Key + ",") ||
                    m.StartsWith(a.Key + ".") || (a.Key.Length > 4 && m.StartsWith(a.Key)))
                    return a.Value;
            }

            // Unknown manufacturer: strip corporate suffixes and title-case what is left.
            var words = Regex.Split(manufacturer.Trim(), @"[\s,\.]+")
                .Where(w => w.Length > 0 && !CorporateSuffixes.Contains(w.ToLowerInvariant()))
                .ToList();
            if (words.Count == 0) return Models.DetectionResult.Unknown;
            string name = string.Join(" ", words.Take(2));
            if (name.Length <= 4 && name == name.ToUpperInvariant()) return name; // acronym, e.g. "LG"
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        }

        /// <summary>Words a model string may start with that duplicate the brand.</summary>
        public static IEnumerable<string> BrandPrefixes(string brand)
        {
            yield return brand;
            switch (brand)
            {
                case "HP": yield return "Hewlett-Packard"; yield return "Hewlett Packard"; break;
                case "ASUS": yield return "ASUSTeK"; break;
                case "MSI": yield return "Micro-Star"; break;
                case "Dell": yield return "Dell Inc."; break;
                case "Microsoft": yield return "Microsoft Corporation"; break;
            }
        }
    }
}
