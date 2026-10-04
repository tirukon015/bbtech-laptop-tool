using System;
using System.Linq;
using BBTech.Core.Settings;
using BBTech.Core.Wifi;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Native Windows WLAN profile XML built for managed networks (example SSIDs, no real passwords).</summary>
    public class WlanProfileXmlTests
    {
        private static readonly string Hex64 = new string('a', 32) + new string('7', 32);

        private static WifiNetwork Net(string ssid = "BBTECH_EXAMPLE", WifiSecurity sec = WifiSecurity.Wpa2Personal, string pw = "example-pass", bool hidden = false) =>
            new WifiNetwork { NetworkId = "n1", Ssid = ssid, Security = sec, Password = pw, Hidden = hidden, Version = 1 };

        [Fact]
        public void Wpa3_rejects_a_64_digit_hex_key_but_wpa2_accepts_it()
        {
            Assert.NotNull(WlanProfileXml.Validate("BBTECH_EXAMPLE", WifiSecurity.Wpa3Personal, Hex64));
            Assert.Null(WlanProfileXml.Validate("BBTECH_EXAMPLE", WifiSecurity.Wpa2Personal, Hex64));
            Assert.Contains("<keyType>networkKey</keyType>", WlanProfileXml.Build(Net(pw: Hex64)));
        }
    }
}
