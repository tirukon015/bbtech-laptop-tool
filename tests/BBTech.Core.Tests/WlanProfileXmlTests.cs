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

        [Fact]
        public void Parse_keeps_connection_mode_when_the_ssid_hex_is_malformed()
        {
            string xml = "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>x</name>" +
                         "<SSIDConfig><SSID><hex>ZZ1</hex></SSID></SSIDConfig><connectionMode>auto</connectionMode>" +
                         "<MSM><security><authEncryption><authentication>WPA2PSK</authentication></authEncryption></security></MSM></WLANProfile>";
            var p = WlanProfileXml.Parse(xml);
            Assert.NotNull(p);
            Assert.Equal("", p.Ssid);
            Assert.Equal("auto", p.ConnectionMode);
            Assert.Equal("WPA2PSK", p.Authentication);
        }

        [Fact]
        public void Parse_reads_the_ssid_from_hex_when_the_name_is_missing()
        {
            string xml = "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><SSIDConfig><SSID>" +
                         "<hex>4242544543485F4558414D504C45</hex></SSID></SSIDConfig><connectionMode>manual</connectionMode></WLANProfile>";
            var p = WlanProfileXml.Parse(xml);
            Assert.Equal("BBTECH_EXAMPLE", p.Ssid);
            Assert.Equal("manual", p.ConnectionMode);
        }

        [Fact]
        public void Ssid_and_passphrase_are_xml_escaped_and_hex_encoded()
        {
            string xml = WlanProfileXml.Build(Net(ssid: "A&B <shop>", pw: "p&ss<word>\"'"));
            Assert.Contains("<name>A&amp;B &lt;shop&gt;</name>", xml);
            Assert.Contains("<hex>412642203C73686F703E</hex>", xml);
            Assert.Contains("<keyMaterial>p&amp;ss&lt;word&gt;&quot;&apos;</keyMaterial>", xml);
            Assert.NotNull(System.Xml.Linq.XDocument.Parse(xml));
            Assert.Equal("A&B <shop>", WlanProfileXml.Parse(xml).Ssid);
        }

        [Fact]
        public void Open_networks_have_no_shared_key()
        {
            string xml = WlanProfileXml.Build(Net(sec: WifiSecurity.Open, pw: ""));
            Assert.Contains("<authentication>open</authentication>", xml);
            Assert.Contains("<encryption>none</encryption>", xml);
            Assert.DoesNotContain("sharedKey", xml);
        }

        [Fact]
        public void Wpa3_profiles_use_sae_with_a_passphrase()
        {
            string xml = WlanProfileXml.Build(Net(sec: WifiSecurity.Wpa3Personal));
            Assert.Contains("<authentication>WPA3SAE</authentication>", xml);
            Assert.Contains("<encryption>AES</encryption>", xml);
            Assert.Contains("<keyType>passPhrase</keyType>", xml);
        }

        [Fact]
        public void Profiles_connect_automatically_and_do_not_roam_away()
        {
            string xml = WlanProfileXml.Build(Net(hidden: true));
            Assert.Contains("<connectionMode>auto</connectionMode>", xml);
            Assert.Contains("<autoSwitch>false</autoSwitch>", xml);
            Assert.Contains("<nonBroadcast>true</nonBroadcast>", xml);
            Assert.Contains("<protected>false</protected>", xml); // Windows protects the key when it stores the profile
            Assert.True(WlanProfileXml.Parse(xml).NonBroadcast);
        }

        [Fact]
        public void Ssid_length_is_limited_to_32_utf8_bytes()
        {
            Assert.Null(WlanProfileXml.Validate(new string('a', 32), WifiSecurity.Open, null));
            Assert.Contains("too long", WlanProfileXml.Validate(new string('a', 33), WifiSecurity.Open, null));
            Assert.Contains("too long", WlanProfileXml.Validate(new string('ব', 11), WifiSecurity.Open, null)); // 33 bytes
            Assert.Null(WlanProfileXml.Validate(new string('ব', 10), WifiSecurity.Open, null));
        }

        [Theory]
        [InlineData("", "Enter the network name")]
        [InlineData(null, "Enter the network name")]
        [InlineData("BBTECH\tEXAMPLE", "invalid characters")]
        public void Ssid_must_be_present_and_printable(string ssid, string message)
        {
            Assert.Contains(message, WlanProfileXml.Validate(ssid, WifiSecurity.Wpa2Personal, "example-pass"));
        }

    }
}
