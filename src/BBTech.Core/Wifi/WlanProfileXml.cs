using System;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml.Linq;
using BBTech.Core.Settings;

namespace BBTech.Core.Wifi
{
    /// <summary>Parsed essentials of an installed Windows WLAN profile.</summary>
    public sealed class InstalledProfileInfo
    {
        public string Ssid { get; set; } = "";
        public string Authentication { get; set; } = "";
        public string ConnectionMode { get; set; } = "";
        public bool NonBroadcast { get; set; }
    }

    /// <summary>
    /// Builds native Windows WLAN profile XML (WLANProfile v1 schema) for a managed BBTech network,
    /// configured for automatic connection. The passphrase is placed in the XML only in memory and is
    /// handed directly to WlanSetProfile; Windows then encrypts it in its own protected profile store.
    /// </summary>
    public static class WlanProfileXml
    {
        private const string Ns = "http://www.microsoft.com/networking/WLAN/profile/v1";

        public static string Validate(string ssid, WifiSecurity security, string password)
        {
            if (string.IsNullOrEmpty(ssid)) return "Enter the network name (SSID).";
            int bytes = Encoding.UTF8.GetByteCount(ssid);
            if (bytes > 32) return "The network name is too long (max 32 bytes).";
            if (ssid.Any(char.IsControl)) return "The network name contains invalid characters.";
            if (security == WifiSecurity.Open) return null;
            if (string.IsNullOrEmpty(password)) return "Enter the Wi-Fi password.";
            bool hex64 = password.Length == 64 && password.All(Uri.IsHexDigit);
            // WPA3-Personal (SAE) only accepts a passphrase; a raw 64-hex PSK is a WPA2 feature.
            if (hex64 && security == WifiSecurity.Wpa3Personal) return "WPA3 networks need a passphrase of 8 to 63 characters, not a 64-digit hex key.";
            if (!hex64 && (password.Length < 8 || password.Length > 63)) return "The Wi-Fi password must be 8 to 63 characters.";
            if (!hex64 && password.Any(c => c < 32 || c > 126)) return "The Wi-Fi password may only contain standard keyboard characters.";
            return null;
        }

        public static string AuthName(WifiSecurity s) =>
            s == WifiSecurity.Wpa3Personal ? "WPA3SAE" : s == WifiSecurity.Open ? "open" : "WPA2PSK";

        public static string Build(WifiNetwork n)
        {
            string err = Validate(n.Ssid, n.Security, n.Password);
            if (err != null) throw new ArgumentException(err);

            string hex = string.Concat(Encoding.UTF8.GetBytes(n.Ssid).Select(b => b.ToString("X2")));
            string encryption = n.Security == WifiSecurity.Open ? "none" : "AES";
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>");
            sb.Append("<WLANProfile xmlns=\"").Append(Ns).Append("\">");
            sb.Append("<name>").Append(Esc(n.Ssid)).Append("</name>");
            sb.Append("<SSIDConfig><SSID><hex>").Append(hex).Append("</hex><name>").Append(Esc(n.Ssid)).Append("</name></SSID>");
            sb.Append("<nonBroadcast>").Append(n.Hidden ? "true" : "false").Append("</nonBroadcast></SSIDConfig>");
            sb.Append("<connectionType>ESS</connectionType>");
            sb.Append("<connectionMode>auto</connectionMode>"); // Windows connects automatically when in range
            sb.Append("<autoSwitch>false</autoSwitch>");
            sb.Append("<MSM><security><authEncryption>");
            sb.Append("<authentication>").Append(AuthName(n.Security)).Append("</authentication>");
            sb.Append("<encryption>").Append(encryption).Append("</encryption>");
            sb.Append("<useOneX>false</useOneX></authEncryption>");
            if (n.Security != WifiSecurity.Open)
            {
                bool hex64 = n.Password.Length == 64 && n.Password.All(Uri.IsHexDigit);
                sb.Append("<sharedKey><keyType>").Append(hex64 ? "networkKey" : "passPhrase").Append("</keyType>");
                sb.Append("<protected>false</protected><keyMaterial>").Append(Esc(n.Password)).Append("</keyMaterial></sharedKey>");
            }
            sb.Append("</security></MSM></WLANProfile>");
            return sb.ToString();
        }

        /// <summary>Reads SSID / authentication / connection mode from an installed profile (key material is ignored).</summary>
        public static InstalledProfileInfo Parse(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var doc = XDocument.Parse(xml);
                string V(string local) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value ?? "";
                var ssidName = doc.Descendants().Where(e => e.Name.LocalName == "SSID").Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
                var ssidHex = doc.Descendants().Where(e => e.Name.LocalName == "SSID").Elements().FirstOrDefault(e => e.Name.LocalName == "hex")?.Value;
                if (string.IsNullOrEmpty(ssidName)) ssidName = FromHex(ssidHex);
                return new InstalledProfileInfo
                {
                    Ssid = ssidName ?? "",
                    Authentication = V("authentication"),
                    ConnectionMode = V("connectionMode"),
                    NonBroadcast = V("nonBroadcast").Equals("true", StringComparison.OrdinalIgnoreCase),
                };
            }
            catch { return null; }
        }

        /// <summary>SSID from its hex form; null when the hex is missing or malformed (the rest of the profile is still read).</summary>
        private static string FromHex(string hex)
        {
            hex = (hex ?? "").Trim();
            if (hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit)) return null;
            return Encoding.UTF8.GetString(Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray());
        }

        private static string Esc(string s) => SecurityElement.Escape(s);
    }
}
