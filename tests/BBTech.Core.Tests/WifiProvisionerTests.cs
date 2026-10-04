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
            public bool Keep = true;           // false: Windows accepts the call but does not keep the profile
            public bool StoreAsManual;         // Windows keeps the profile without automatic connection
            public string ConnectedSsid;
            public int SetCalls, ScanCalls;
            public List<string> ConnectCalls = new List<string>();
            public WlanInterface GetInterface() => Iface;
            public string GetProfileXml(Guid iface, string name) => Profiles.TryGetValue(name, out var x) ? x : null;
            public int SetProfile(Guid iface, string xml, bool allUsers, out string reason)
            {
                reason = "";
                SetCalls++;
                int rc = OnSet(xml, allUsers);
                if (rc == 0 && Keep)
                {
                    string stored = xml.Replace(Pass, "PROTECTED").Replace("changed-pass", "PROTECTED");
                    if (StoreAsManual) stored = stored.Replace("<connectionMode>auto</connectionMode>", "<connectionMode>manual</connectionMode>");
                    Profiles[WlanProfileXml.Parse(xml).Ssid] = stored;
                }
                return rc;
            }
            public int Connect(Guid iface, string name) { ConnectCalls.Add(name); return OnConnect(name); }
            public HashSet<string> VisibleSsids(Guid iface) { ScanCalls++; return Visible; }
            public string ConnectedProfile(Guid iface) => ConnectedSsid;
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

        private sealed class BrokenWlan : IWlanApi
        {
            public WlanInterface GetInterface() => throw new System.ComponentModel.Win32Exception(1062); // service not started
            public string GetProfileXml(Guid iface, string name) => null;
            public int SetProfile(Guid iface, string xml, bool allUsers, out string reason) { reason = ""; return 0; }
            public int Connect(Guid iface, string name) => 0;
            public HashSet<string> VisibleSsids(Guid iface) => null;
            public string ConnectedProfile(Guid iface) => null;
        }

        [Fact]
        public void A_stopped_wlan_service_gives_a_friendly_message_everywhere()
        {
            var p = P(new BrokenWlan());
            Assert.Contains("not available", p.CheckAdapter(out _));
            Assert.Equal(ProvisionOutcome.Failed, p.ProvisionAll(new[] { Net() }).Single().Outcome);
            Assert.Contains("not available", p.ConnectNow(Net()));
            Assert.Null(p.IsAutoConnect("BBTECH_EXAMPLE"));
        }

        [Fact]
        public void A_new_network_is_installed_for_all_users_and_verified()
        {
            var api = new ScriptedWlan();
            var state = new Dictionary<string, WifiProvisionRecord>();
            var r = P(api, state).ProvisionAll(new[] { Net() }).Single();
            Assert.Equal(ProvisionOutcome.Installed, r.Outcome);
            Assert.True(r.AllUsers);
            Assert.True(r.AutoConnectVerified);
            Assert.Contains("verified in Windows", r.Message);
            Assert.Contains("<connectionMode>auto</connectionMode>", api.Profiles["BBTECH_EXAMPLE"]);
            Assert.True(state.ContainsKey("BBTECH_EXAMPLE"));
        }

        [Fact]
        public void Provisioning_twice_does_not_rewrite_an_unchanged_profile()
        {
            var api = new ScriptedWlan();
            var state = new Dictionary<string, WifiProvisionRecord>();
            P(api, state).ProvisionAll(new[] { Net() });
            var again = P(api, state).ProvisionAll(new[] { Net() }).Single();
            Assert.Equal(ProvisionOutcome.AlreadyInstalled, again.Outcome);
            Assert.Equal(1, api.SetCalls);
        }

        [Fact]
        public void A_changed_password_updates_the_installed_profile()
        {
            var api = new ScriptedWlan();
            var state = new Dictionary<string, WifiProvisionRecord>();
            P(api, state).ProvisionAll(new[] { Net() });
            var changed = Net();
            changed.Password = "changed-pass";
            changed.Version = 2;
            var r = P(api, state).ProvisionAll(new[] { changed }).Single();
            Assert.Equal(ProvisionOutcome.Updated, r.Outcome);
            Assert.Equal(2, api.SetCalls);
        }

        [Fact]
        public void Access_denied_for_all_users_falls_back_to_the_current_user()
        {
            var api = new ScriptedWlan { OnSet = (xml, all) => all ? WifiProvisioner.ErrorAccessDenied : 0 };
            var r = P(api).ProvisionAll(new[] { Net() }).Single();
            Assert.Equal(ProvisionOutcome.Installed, r.Outcome);
            Assert.False(r.AllUsers);
            Assert.Contains("current Windows user only", r.Message);
        }

    }
}
