using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BBTech.Core.Settings;
using BBTech.Core.Storage;
using BBTech.Core.Wifi;
using Xunit;

namespace BBTech.Core.Tests
{
    public class OperatorTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-settings-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_dir, "config", "settings.dat");
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void Select_operator_once_and_it_persists_for_the_session()
        {
            var s = SettingsStore.Load(FilePath);
            var jimmy = s.AddOperator("Jimmy", "QC");
            s.AddOperator("Rahim", "QC");
            s.SelectOperator(jimmy.OperatorId);

            // Next laptop / app restart from the same USB: Jimmy is still selected.
            var again = SettingsStore.Load(FilePath);
            Assert.Equal("Jimmy", again.CurrentOperator.Name);
            Assert.Equal("QC", again.CurrentOperator.Role);
        }

        [Fact]
        public void Change_operator_affects_only_the_current_selection()
        {
            var s = SettingsStore.Load(FilePath);
            var jimmy = s.AddOperator("Jimmy", "QC");
            var rahim = s.AddOperator("Rahim", "QC");
            s.SelectOperator(jimmy.OperatorId);
            s.SelectOperator(rahim.OperatorId);
            Assert.Equal("Rahim", SettingsStore.Load(FilePath).CurrentOperator.Name);
            Assert.Equal(2, SettingsStore.Load(FilePath).ActiveOperators.Count());
        }

        [Fact]
        public void Deactivated_operator_is_hidden_for_new_work_but_kept()
        {
            var s = SettingsStore.Load(FilePath);
            var jimmy = s.AddOperator("Jimmy", "QC");
            s.SelectOperator(jimmy.OperatorId);
            s.SetOperatorActive(jimmy.OperatorId, false);
            var again = SettingsStore.Load(FilePath);
            Assert.Null(again.CurrentOperator);
            Assert.Empty(again.ActiveOperators);
            Assert.Equal("Jimmy", again.FindOperator(jimmy.OperatorId).Name); // still known for history
            Assert.Throws<ArgumentException>(() => again.SelectOperator(jimmy.OperatorId));
        }

        [Fact]
        public void Duplicate_and_empty_names_are_rejected()
        {
            var s = SettingsStore.Load(FilePath);
            s.AddOperator("Jimmy", "QC");
            Assert.Throws<ArgumentException>(() => s.AddOperator(" jimmy ", "QC"));
            Assert.Throws<ArgumentException>(() => s.AddOperator("  ", "QC"));
        }

        [Fact]
        public void Admin_pin_is_hashed_and_verified()
        {
            var s = SettingsStore.Load(FilePath);
            Assert.False(s.HasPin);
            Assert.Throws<ArgumentException>(() => s.SetPin("12"));
            Assert.Throws<ArgumentException>(() => s.SetPin("abcd"));
            s.SetPin("4821");
            var again = SettingsStore.Load(FilePath);
            Assert.True(again.VerifyPin("4821"));
            Assert.False(again.VerifyPin("4822"));
            Assert.False(again.VerifyPin(""));
        }

        [Fact]
        public void Settings_file_is_encrypted_and_tamper_evident()
        {
            var s = SettingsStore.Load(FilePath);
            s.AddOperator("Jimmy", "QC");
            s.SetPin("4821");
            s.AddOrUpdateWifi(null, "BBTechShop_5G", WifiSecurity.Wpa2Personal, "SuperSecretPass123", false);

            byte[] raw = File.ReadAllBytes(FilePath);
            string text = Encoding.UTF8.GetString(raw) + Encoding.Unicode.GetString(raw);
            foreach (var w in new[] { "Jimmy", "BBTechShop", "SuperSecretPass123", "4821", "Operators" })
                Assert.DoesNotContain(w, text);

            // Hand-editing breaks authentication: the damaged file is not used and not overwritten.
            raw[raw.Length - 40] ^= 0x55;
            File.WriteAllBytes(FilePath, raw);
            File.Delete(FilePath + ".bak");
            var damaged = SettingsStore.Load(FilePath);
            Assert.True(damaged.LoadFailed);
            Assert.False(damaged.HasPin);
            Assert.Throws<InvalidOperationException>(() => damaged.Save());
        }

        [Fact]
        public void Previous_copy_is_used_if_the_latest_file_is_damaged()
        {
            var s = SettingsStore.Load(FilePath);
            s.AddOperator("Jimmy", "QC");
            s.AddOperator("Rahim", "QC"); // second save -> previous version kept as .bak
            var raw = File.ReadAllBytes(FilePath);
            raw[raw.Length - 5] ^= 0x01;
            File.WriteAllBytes(FilePath, raw);
            var again = SettingsStore.Load(FilePath);
            Assert.False(again.LoadFailed);
            Assert.Contains(again.ActiveOperators, o => o.Name == "Jimmy");
        }
    }

    public class WifiTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-wifi-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_dir, "settings.dat");
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private sealed class FakeWlan : IWlanApi
        {
            public WlanInterface Iface = new WlanInterface { Id = Guid.NewGuid(), Description = "Fake Wi-Fi", RadioOn = true };
            public Dictionary<string, string> Profiles = new Dictionary<string, string>();
            public HashSet<string> Visible = new HashSet<string>();
            public bool DenyAllUsers;
            public bool StoreAsManual; // simulates Windows keeping the profile without automatic connection
            public string ConnectedSsid;
            public int SetCalls;
            public int ForceError;
            public List<string> Connected = new List<string>();
            public WlanInterface GetInterface() => Iface;
            public string GetProfileXml(Guid iface, string name) => Profiles.TryGetValue(name, out var x) ? x : null;
            public int SetProfile(Guid iface, string xml, bool allUsers, out string reason)
            {
                SetCalls++; reason = "";
                if (ForceError != 0) { reason = "bad"; return ForceError; }
                if (allUsers && DenyAllUsers) return WifiProvisioner.ErrorAccessDenied;
                var info = WlanProfileXml.Parse(xml);
                // Windows stores the key protected; simulate by stripping keyMaterial
                string stored = System.Text.RegularExpressions.Regex.Replace(xml, "<keyMaterial>.*?</keyMaterial>", "<keyMaterial>01000000D08C9DDF...</keyMaterial>");
                if (StoreAsManual) stored = stored.Replace("<connectionMode>auto</connectionMode>", "<connectionMode>manual</connectionMode>");
                Profiles[info.Ssid] = stored;
                return 0;
            }
            public int Connect(Guid iface, string name) { Connected.Add(name); return 0; }
            public HashSet<string> VisibleSsids(Guid iface) => Visible;
            public string ConnectedProfile(Guid iface) => ConnectedSsid ?? Connected.LastOrDefault();
        }

        private static WifiNetwork Net(string ssid, string pw = "Password123", WifiSecurity sec = WifiSecurity.Wpa2Personal, int ver = 1) =>
            new WifiNetwork { NetworkId = ssid, Ssid = ssid, Password = pw, Security = sec, Version = ver };

        private static (WifiProvisioner P, Dictionary<string, WifiProvisionRecord> State) Provisioner(IWlanApi api)
        {
            var state = new Dictionary<string, WifiProvisionRecord>();
            return (new WifiProvisioner(api, s => state.TryGetValue(s, out var r) ? r : null, r => state[r.Ssid] = r), state);
        }

        [Fact]
        public void Add_edit_remove_managed_networks()
        {
            var s = SettingsStore.Load(FilePath);
            var n = s.AddOrUpdateWifi(null, "BBTechShop_5G", WifiSecurity.Wpa2Personal, "Password123", false);
            s.AddOrUpdateWifi(null, "BBTechShop_2.4G", WifiSecurity.Wpa2Personal, "Password123", false);
            s.AddOrUpdateWifi(null, "BBTech_Office", WifiSecurity.Wpa3Personal, "OfficePass99", false);
            Assert.Equal(1, n.Version);

            s.AddOrUpdateWifi(n.NetworkId, "BBTechShop_5G", WifiSecurity.Wpa2Personal, "NewPassword456", false);
            var again = SettingsStore.Load(FilePath);
            var edited = again.WifiNetworks.Single(x => x.Ssid == "BBTechShop_5G");
            Assert.Equal(2, edited.Version);
            Assert.Equal("NewPassword456", edited.Password);

            again.RemoveWifi(edited.NetworkId);
            Assert.Equal(2, SettingsStore.Load(FilePath).WifiNetworks.Count());
        }

        [Theory]
        [InlineData("", WifiSecurity.Wpa2Personal, "Password123")]
        [InlineData("BBTech", WifiSecurity.Wpa2Personal, "short")]
        [InlineData("BBTech", WifiSecurity.Wpa2Personal, "")]
        [InlineData("ThisSsidIsWayTooLongForWifiStandard_123", WifiSecurity.Wpa2Personal, "Password123")]
        public void Invalid_network_settings_are_rejected(string ssid, WifiSecurity sec, string pw) =>
            Assert.NotNull(WlanProfileXml.Validate(ssid, sec, pw));

        [Fact]
        public void Profile_xml_is_auto_connect_and_escaped()
        {
            string xml = WlanProfileXml.Build(Net("BBTech & <Shop>", "p&ss<word>\"1"));
            Assert.Contains("<connectionMode>auto</connectionMode>", xml);
            Assert.Contains("<authentication>WPA2PSK</authentication>", xml);
            var info = WlanProfileXml.Parse(xml);
            Assert.Equal("BBTech & <Shop>", info.Ssid);
            Assert.Equal("auto", info.ConnectionMode);
            Assert.Contains("<keyMaterial>p&amp;ss&lt;word&gt;&quot;1</keyMaterial>", xml);
        }

        [Fact]
        public void Provisioning_installs_for_all_users_and_skips_unchanged()
        {
            var api = new FakeWlan();
            var (p, _) = Provisioner(api);
            var nets = new[] { Net("BBTechShop_5G"), Net("BBTech_Office", "OfficePass99", WifiSecurity.Wpa3Personal) };

            var first = p.ProvisionAll(nets);
            Assert.All(first, r => Assert.Equal(ProvisionOutcome.Installed, r.Outcome));
            Assert.All(first, r => Assert.True(r.AllUsers));
            Assert.Equal(2, api.SetCalls);

            var second = p.ProvisionAll(nets); // already installed, unchanged
            Assert.All(second, r => Assert.Equal(ProvisionOutcome.AlreadyInstalled, r.Outcome));
            Assert.Equal(2, api.SetCalls); // not reinstalled
        }

        [Fact]
        public void Edited_network_is_updated_on_next_provision()
        {
            var api = new FakeWlan();
            var (p, _) = Provisioner(api);
            p.ProvisionAll(new[] { Net("BBTechShop_5G", "Password123") });
            var r = p.ProvisionAll(new[] { Net("BBTechShop_5G", "ChangedPass789", ver: 2) }).Single();
            Assert.Equal(ProvisionOutcome.Updated, r.Outcome);
        }

        [Fact]
        public void Out_of_range_network_is_still_installed()
        {
            var api = new FakeWlan(); // nothing visible
            var (p, _) = Provisioner(api);
            var r = p.ProvisionAll(new[] { Net("BBTech_Office") }).Single();
            Assert.Equal(ProvisionOutcome.Installed, r.Outcome);
            Assert.False(r.InRange);
            Assert.Contains("automatically when in range", r.Message);
            Assert.True(api.Profiles.ContainsKey("BBTech_Office"));
        }

        [Fact]
        public void Falls_back_to_current_user_when_all_users_is_denied()
        {
            var api = new FakeWlan { DenyAllUsers = true };
            var (p, state) = Provisioner(api);
            var r = p.ProvisionAll(new[] { Net("BBTechShop_5G") }).Single();
            Assert.Equal(ProvisionOutcome.Installed, r.Outcome);
            Assert.False(r.AllUsers);
            Assert.False(state["BBTechShop_5G"].AllUsers);
        }

        [Fact]
        public void Missing_or_disabled_adapter_fails_gracefully()
        {
            var api = new FakeWlan { Iface = null };
            var (p, _) = Provisioner(api);
            var r = p.ProvisionAll(new[] { Net("BBTechShop_5G") }).Single();
            Assert.Equal(ProvisionOutcome.Failed, r.Outcome);
            Assert.Contains("not available on this laptop", r.Message);

            var off = new FakeWlan();
            off.Iface.RadioOn = false;
            Assert.Contains("switched off", Provisioner(off).P.ConnectNow(Net("BBTechShop_5G")));
        }

        [Fact]
        public void Invalid_profile_rejected_by_windows_is_reported()
        {
            var api = new FakeWlan { ForceError = WifiProvisioner.ErrorBadProfile };
            var r = Provisioner(api).P.ProvisionAll(new[] { Net("BBTechShop_5G") }).Single();
            Assert.Equal(ProvisionOutcome.Failed, r.Outcome);
            Assert.Contains("rejected", r.Message);
            Assert.DoesNotContain("Password123", r.Message);
        }

        [Fact]
        public void Connect_to_visible_network_and_report_unavailable_ssid()
        {
            var api = new FakeWlan();
            api.Visible.Add("BBTechShop_5G");
            var (p, _) = Provisioner(api);
            Assert.Null(p.ConnectNow(Net("BBTechShop_5G")));
            Assert.Equal("BBTechShop_5G", api.Connected.Single());

            string msg = p.ConnectNow(Net("BBTech_Office"));
            Assert.Contains("not in range", msg);
            Assert.True(api.Profiles.ContainsKey("BBTech_Office")); // still installed for auto-connect
        }

        [Fact]
        public void BBTech_shop_networks_are_installed_with_verified_automatic_connection()
        {
            var api = new FakeWlan();
            var (p, _) = Provisioner(api);
            var results = p.ProvisionAll(new[] { Net("bbtech.shop_5G"), Net("bbtech.shop") });

            Assert.All(results, r =>
            {
                Assert.Equal(ProvisionOutcome.Installed, r.Outcome);
                Assert.True(r.AutoConnectVerified);
                Assert.True(r.AllUsers);
                Assert.Contains("Connect automatically", r.Message);
            });
            foreach (var ssid in new[] { "bbtech.shop_5G", "bbtech.shop" })
            {
                // What Windows actually holds (read back), not what the UI shows.
                var stored = WlanProfileXml.Parse(api.Profiles[ssid]);
                Assert.Equal(ssid, stored.Ssid);
                Assert.Equal("auto", stored.ConnectionMode);
                Assert.Equal("WPA2PSK", stored.Authentication);
                Assert.True(p.IsAutoConnect(ssid));
            }
            // Profile name = SSID; SSID hex encodes the dot/underscore correctly.
            Assert.Contains("<name>bbtech.shop_5G</name>", api.Profiles["bbtech.shop_5G"]);
            Assert.Contains("<hex>6262746563682E73686F705F3547</hex>", api.Profiles["bbtech.shop_5G"]);
        }

        [Fact]
        public void Profile_without_automatic_connection_is_reported_as_failure()
        {
            var api = new FakeWlan { StoreAsManual = true };
            var r = Provisioner(api).P.ProvisionAll(new[] { Net("bbtech.shop") }).Single();
            Assert.Equal(ProvisionOutcome.Failed, r.Outcome);
            Assert.False(r.AutoConnectVerified);
            Assert.Contains("without automatic connection", r.Message);
        }

        [Fact]
        public void Profile_switched_to_manual_later_is_reprovisioned()
        {
            var api = new FakeWlan();
            var (p, _) = Provisioner(api);
            p.ProvisionAll(new[] { Net("bbtech.shop") });
            api.Profiles["bbtech.shop"] = api.Profiles["bbtech.shop"].Replace("<connectionMode>auto</connectionMode>", "<connectionMode>manual</connectionMode>");
            Assert.False(p.IsAutoConnect("bbtech.shop"));

            var r = p.ProvisionAll(new[] { Net("bbtech.shop") }).Single();
            Assert.Equal(ProvisionOutcome.Updated, r.Outcome);
            Assert.True(p.IsAutoConnect("bbtech.shop"));
        }

        [Fact]
        public void Working_connected_profile_is_kept_not_overwritten()
        {
            var api = new FakeWlan();
            // The laptop already has its own auto-connect profile and is connected through it.
            api.Profiles["bbtech.shop_5G"] = WlanProfileXml.Build(Net("bbtech.shop_5G", "TheRealPassword1"));
            api.ConnectedSsid = "bbtech.shop_5G";
            var (p, state) = Provisioner(api);

            var r = p.ProvisionAll(new[] { Net("bbtech.shop_5G", "PossiblyWrong123") }).Single();
            Assert.Equal(ProvisionOutcome.AlreadyInstalled, r.Outcome);
            Assert.True(r.AutoConnectVerified);
            Assert.Equal(0, api.SetCalls);           // existing working profile untouched
            Assert.True(state.ContainsKey("bbtech.shop_5G"));

            // A later password change by the admin is still applied.
            var changed = p.ProvisionAll(new[] { Net("bbtech.shop_5G", "NewPassword789", ver: 2) }).Single();
            Assert.Equal(ProvisionOutcome.Updated, changed.Outcome);
        }

        [Fact]
        public void Provision_state_stores_no_password()
        {
            var api = new FakeWlan();
            var (p, state) = Provisioner(api);
            p.ProvisionAll(new[] { Net("BBTechShop_5G", "Password123") });
            string json = Encoding.UTF8.GetString(Json.ToBytes(state["BBTechShop_5G"]));
            Assert.DoesNotContain("Password123", json);
        }
    }
}
