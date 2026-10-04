using System;
using System.IO;
using System.Linq;
using BBTech.Core.Settings;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Managed Wi-Fi networks in the encrypted USB settings (example values only).</summary>
    public class WifiSettingsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-wifiset-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_dir, "config", "settings.dat");
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void Saving_an_unchanged_network_keeps_its_version()
        {
            var s = SettingsStore.Load(FilePath);
            var n = s.AddOrUpdateWifi(null, "BBTECH_EXAMPLE", WifiSecurity.Wpa2Personal, "example-pass", false);
            Assert.Equal(1, n.Version);
            s.AddOrUpdateWifi(n.NetworkId, "BBTECH_EXAMPLE", WifiSecurity.Wpa2Personal, "example-pass", false);
            Assert.Equal(1, SettingsStore.Load(FilePath).WifiNetworks.Single().Version);
            s.AddOrUpdateWifi(n.NetworkId, "BBTECH_EXAMPLE", WifiSecurity.Wpa2Personal, "changed-pass", false);
            Assert.Equal(2, SettingsStore.Load(FilePath).WifiNetworks.Single().Version);
        }
    }
}
