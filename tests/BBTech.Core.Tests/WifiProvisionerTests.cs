using System;
using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Settings;
using BBTech.Core.Storage;
using BBTech.Core.Wifi;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Provisioning edge cases (example SSID and password only).</summary>
    public class WifiProvisionerTests
    {
        private const string Pass = "example-pass";

        private sealed class ScriptedWlan : IWlanApi
        {
            public WlanInterface Iface = new WlanInterface { Id = Guid.NewGuid(), RadioOn = true };
            public Dictionary<string, string> Profiles = new Dictionary<string, string>();
            public Func<string, bool, int> OnSet = (xml, all) => 0;
            public Func<string, int> OnConnect = name => 0;
            public HashSet<string> Visible = new HashSet<string>();
            public WlanInterface GetInterface() => Iface;
            public string GetProfileXml(Guid iface, string name) => Profiles.TryGetValue(name, out var x) ? x : null;
            public int SetProfile(Guid iface, string xml, bool allUsers, out string reason)
            {
                reason = "";
                int rc = OnSet(xml, allUsers);
                if (rc == 0) Profiles[WlanProfileXml.Parse(xml).Ssid] = xml.Replace(Pass, "PROTECTED");
                return rc;
            }
            public int Connect(Guid iface, string name) => OnConnect(name);
            public HashSet<string> VisibleSsids(Guid iface) => Visible;
            public string ConnectedProfile(Guid iface) => null;
        }

        private static WifiNetwork Net(string ssid = "BBTECH_EXAMPLE") =>
            new WifiNetwork { NetworkId = ssid, Ssid = ssid, Security = WifiSecurity.Wpa2Personal, Password = Pass, Version = 1 };

        private static WifiProvisioner P(IWlanApi api, Dictionary<string, WifiProvisionRecord> state = null)
        {
            state = state ?? new Dictionary<string, WifiProvisionRecord>();
            return new WifiProvisioner(api, s => state.TryGetValue(s, out var r) ? r : null, r => state[r.Ssid] = r);
        }

        [Fact]
        public void A_crashing_native_call_is_reported_as_failed_without_leaking_the_password()
        {
            var api = new ScriptedWlan { OnSet = (xml, all) => throw new InvalidOperationException("driver error " + xml) };
            var r = P(api).ProvisionAll(new[] { Net() }).Single();
            Assert.Equal(ProvisionOutcome.Failed, r.Outcome);
            Assert.DoesNotContain(Pass, r.Message);
            Assert.False(r.AutoConnectVerified);
        }

        [Fact]
        public void Connect_now_survives_a_crashing_connect_call_and_keeps_the_profile()
        {
            var api = new ScriptedWlan { OnConnect = name => throw new InvalidOperationException("service stopped") };
            api.Visible.Add("BBTECH_EXAMPLE");
            string msg = P(api).ConnectNow(Net());
            Assert.Contains("Could not connect", msg);
            Assert.Contains("stays installed", msg);
            Assert.True(api.Profiles.ContainsKey("BBTECH_EXAMPLE"));
        }
    }
}
