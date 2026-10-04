using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BBTech.Core.Identity
{
    /// <summary>
    /// Stable identity of a PHYSICAL laptop, built only from firmware identifiers that do not change
    /// at runtime: BIOS serial number, SMBIOS system UUID and motherboard (baseboard) serial, plus the
    /// manufacturer/model for display and sanity checks.
    ///
    /// Deliberately NOT used: IP/MAC address, Wi-Fi network, RAM/CPU usage, battery level, storage
    /// size or anything else that varies or can be swapped.
    ///
    /// Placeholder values that vendors leave in unconfigured firmware ("To be filled by O.E.M.",
    /// "Default string", all-zero UUIDs ...) are discarded, because they are shared by many machines
    /// and would make different laptops look identical.
    /// </summary>
    public sealed class DeviceIdentity
    {
        // Not a secret: a fixed domain separator so identifier hashes are specific to this tool.
        private const string HashDomain = "BBTech.LaptopTool.DeviceIdentity.v1";

        public string Manufacturer { get; }
        public string Model { get; }
        public string ModelNumber { get; }
        /// <summary>Cleaned BIOS serial, or null when missing/placeholder.</summary>
        public string SerialNumber { get; }
        /// <summary>Cleaned SMBIOS UUID (upper-case, dashed), or null when missing/placeholder.</summary>
        public string SystemUuid { get; }
        /// <summary>Cleaned motherboard serial, or null when missing/placeholder.</summary>
        public string BoardSerial { get; }

        public DeviceIdentity(string manufacturer, string model, string modelNumber, string serial, string uuid, string boardSerial)
        {
            Manufacturer = (manufacturer ?? "").Trim();
            Model = (model ?? "").Trim();
            ModelNumber = (modelNumber ?? "").Trim();
            SerialNumber = CleanSerial(serial);
            SystemUuid = CleanUuid(uuid);
            BoardSerial = CleanSerial(boardSerial);
        }

        public bool HasStrongIdentifier => SerialNumber != null || SystemUuid != null || BoardSerial != null;

        public string SerialHash => Hash("serial", SerialNumber);
        public string UuidHash => Hash("uuid", SystemUuid);
        public string BoardSerialHash => Hash("board", BoardSerial);

        /// <summary>
        /// Hardware fingerprint: SHA-256 over the manufacturer and the stable identifier hashes.
        /// Null when the laptop reports no stable identifier (a model-only fingerprint would match
        /// every laptop of that model, so it is never produced).
        /// </summary>
        public string Fingerprint
        {
            get
            {
                if (!HasStrongIdentifier) return null;
                string material = string.Join("|", "fp", NormalizeName(Manufacturer), SerialHash ?? "-", UuidHash ?? "-", BoardSerialHash ?? "-");
                return Sha256Hex(HashDomain + "|" + material);
            }
        }

        /// <summary>Each available stable identifier as (kind, value): used to derive recovery keys.</summary>
        public (string Kind, string Value)[] StableIdentifiers =>
            new[] { ("serial", SerialNumber), ("uuid", SystemUuid), ("board", BoardSerial) }
                .Where(p => p.Item2 != null).ToArray();

        public static string Hash(string kind, string value) =>
            value == null ? null : Sha256Hex(HashDomain + "|" + kind + "|" + value);

        public static string NormalizeName(string s) => Regex.Replace((s ?? "").Trim().ToUpperInvariant(), @"[^A-Z0-9]", "");

        private static readonly string[] SerialPlaceholders =
        {
            "TOBEFILLEDBYOEM", "DEFAULTSTRING", "SYSTEMSERIALNUMBER", "CHASSISSERIALNUMBER", "BASEBOARDSERIALNUMBER",
            "SERIALNUMBER", "SERIAL", "NONE", "NA", "NOTAPPLICABLE", "NOTAVAILABLE", "NOTSPECIFIED", "UNKNOWN", "INVALID",
            "OEM", "EMPTY", "NULL", "DEFAULT", "123456789", "1234567890", "0123456789", "12345678", "ABCDEFGHIJ",
        };

        /// <summary>Upper-cased, trimmed serial; null for empty or placeholder values.</summary>
        public static string CleanSerial(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = Regex.Replace(raw.Trim(), @"\s+", " ").ToUpperInvariant();
            string compact = Regex.Replace(s, @"[^A-Z0-9]", "");
            if (compact.Length < 3) return null;
            if (SerialPlaceholders.Contains(compact)) return null;
            if (compact.Distinct().Count() == 1) return null; // "00000000", "XXXXXXX", "FFFFFFFF"
            return s;
        }

        private static readonly string[] UuidPlaceholders =
        {
            "00000000-0000-0000-0000-000000000000",
            "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
            "03000200-0400-0500-0006-000700080009", // common AMI/OEM default
            "00020003-0004-0005-0006-000700080009",
            "12345678-1234-5678-90AB-CDDEEFAABBCC",
        };

        /// <summary>Canonical UUID; null for empty, malformed or well-known placeholder values.</summary>
        public static string CleanUuid(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!Guid.TryParse(raw.Trim(), out Guid g)) return null;
            string s = g.ToString("D").ToUpperInvariant();
            if (UuidPlaceholders.Contains(s)) return null;
            if (s.Replace("-", "").Distinct().Count() == 1) return null;
            return s;
        }

        private static string Sha256Hex(string s)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2")));
        }

        public override string ToString() =>
            Manufacturer + " " + Model + " " + ModelNumber + " [serial=" + (SerialNumber ?? "-") + ", uuid=" + (SystemUuid != null ? "yes" : "-") + ", board=" + (BoardSerial != null ? "yes" : "-") + "]";
    }
}
