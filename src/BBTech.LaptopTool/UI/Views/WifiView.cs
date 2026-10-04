using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Identity;
using BBTech.Core.Settings;
using BBTech.Core.Wifi;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.Test.Wireless;

namespace BBTech.LaptopTool.UI.Views
{
    /// <summary>
    /// BBTech Wi-Fi utility: installs the managed BBTech networks as native Windows auto-connect
    /// profiles ("Provision"), shows their status on this laptop, and can connect now. After
    /// provisioning the USB is not needed: Windows connects automatically whenever in range.
    /// </summary>
    public sealed class WifiView : UserControl
    {
        private readonly StackPanel _list = new StackPanel();
        private readonly TextBlock _status = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        private readonly Button _provision;
        private bool _busy;

        private static Brush Res(string k) => (Brush)Application.Current.FindResource(k);
        private static Style St(string k) => (Style)Application.Current.FindResource(k);

        public WifiView()
        {
            _provision = new Button { Content = "Provision BBTech Wi-Fi", Style = St("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 8, 0) };
            _provision.Click += async (s, e) => await ProvisionAsync();
            var refresh = new Button { Content = "Refresh", Style = St("BaseButton") };
            refresh.Click += async (s, e) => await RefreshAsync();

            var top = new StackPanel();
            top.Children.Add(new TextBlock { Text = "BBTech Wi-Fi", Style = (Style)FindResource("H1") });
            top.Children.Add(new TextBlock
            {
                Text = "Installs the BBTech networks on this laptop with automatic connection. Afterwards the USB is not needed: Windows connects by itself whenever a BBTech network is in range.",
                Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 14),
            });
            var buttons = new WrapPanel();
            buttons.Children.Add(_provision);
            buttons.Children.Add(refresh);
            top.Children.Add(buttons);
            top.Children.Add(_status);
            top.Children.Add(new Border { Height = 14 });
            top.Children.Add(_list);

            Content = new ScrollViewer { Content = new Border { Padding = new Thickness(24, 18, 24, 24), Child = top }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Loaded += async (s, e) => await RefreshAsync();
        }

        private List<WifiNetwork> Networks => SettingsService.Store.WifiNetworks.ToList();

        internal static WifiProvisioner NewProvisioner()
        {
            var repo = QcStoreService.Current; // provisioning state lives in the laptop's encrypted store
            return new WifiProvisioner(new WlanNativeApi(),
                ssid => repo?.GetWifiState(ssid),
                rec => { try { repo?.SetWifiState(rec); } catch (Exception ex) { AppLog.Warn("Could not save Wi-Fi state", ex); } });
        }

        internal static async Task EnsureStoreAsync()
        {
            if (QcStoreService.Current != null) return;
            try
            {
                var d = await DetectionSession.GetOrDetectAsync();
                await QcStoreService.OpenAsync(IdentityFactory.From(d));
            }
            catch (Exception ex) { AppLog.Warn("Store not available for Wi-Fi state", ex); }
        }

        private async Task RefreshAsync()
        {
            if (_busy) return;
            _busy = true;
            var nets = Networks;
            _provision.IsEnabled = nets.Count > 0;
            if (SettingsService.Store.LoadFailed) { _status.Text = "The BBTech settings on this USB are damaged and cannot be used."; _busy = false; return; }
            if (nets.Count == 0)
            {
                _status.Text = "No BBTech networks are configured on this USB. An admin can add them in Settings (Home → Settings).";
                _list.Children.Clear();
                _busy = false;
                return;
            }
            _status.Text = "Checking Wi-Fi on this laptop...";
            var info = await Task.Run(() =>
            {
                var api = new WlanNativeApi();
                try
                {
                    var iface = api.GetInterface();
                    if (iface == null) return Tuple.Create<string, Dictionary<string, string>, HashSet<string>, string>("Wi-Fi is not available on this laptop (no Wi-Fi adapter found, or it is disabled).", null, null, null);
                    // Status comes from the Windows profile itself: "auto" / "manual" / missing.
                    var installed = nets.ToDictionary(n => n.Ssid, n => WlanProfileXml.Parse(api.GetProfileXml(iface.Id, n.Ssid))?.ConnectionMode ?? "");
                    var visible = iface.RadioOn ? api.VisibleSsids(iface.Id) : null;
                    string msg = iface.RadioOn ? null : "Wi-Fi is switched off on this laptop. Profiles can still be installed.";
                    return Tuple.Create(msg, installed, visible, api.ConnectedProfile(iface.Id));
                }
                catch (DllNotFoundException) { return Tuple.Create<string, Dictionary<string, string>, HashSet<string>, string>("Wi-Fi is not available on this laptop.", null, null, null); }
            });

            _status.Text = info.Item1 ?? "";
            _list.Children.Clear();
            foreach (var n in nets)
            {
                string mode = info.Item2 != null && info.Item2.TryGetValue(n.Ssid, out string m) ? m : "";
                bool inRange = info.Item3 != null && info.Item3.Contains(n.Ssid);
                bool connected = info.Item4 == n.Ssid;
                _list.Children.Add(Row(n, mode, inRange, connected, info.Item2 != null));
            }
            _busy = false;
        }

        /// <param name="windowsMode">connectionMode read from the Windows profile: "auto", "manual" or "" (not installed).</param>
        private UIElement Row(WifiNetwork n, string windowsMode, bool inRange, bool connected, bool adapter)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            if (windowsMode == "auto") chips.Children.Add(Chip("WINDOWS: CONNECT AUTOMATICALLY ✓", "Pass"));
            else if (windowsMode.Length > 0) chips.Children.Add(Chip("INSTALLED · MANUAL ONLY – click Provision to fix", "Warn"));
            else chips.Children.Add(Chip("NOT INSTALLED", "Neutral"));
            if (connected) chips.Children.Add(Chip("CONNECTED", "Pass"));
            else if (inRange) chips.Children.Add(Chip("IN RANGE", "Warn"));
            else if (adapter) chips.Children.Add(Chip("NOT IN RANGE", "Neutral"));

            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = n.Ssid, FontSize = 17, FontWeight = FontWeights.SemiBold });
            left.Children.Add(chips);

            var connect = new Button { Content = connected ? "Connected" : "CONNECT", Style = St(connected ? "BaseButton" : "PrimaryButton"), MinWidth = 120, IsEnabled = adapter && !connected };
            connect.Click += async (s, e) => await ConnectAsync(n, connect);

            var dock = new DockPanel();
            DockPanel.SetDock(connect, Dock.Right);
            dock.Children.Add(connect);
            dock.Children.Add(left);
            return new Border { Style = St("Card"), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 8), Child = dock };
        }

        private static Border Chip(string text, string kind) => new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0),
            Background = Res(kind + "Soft"),
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Res(kind + "Brush") },
        };

        private async Task ProvisionAsync()
        {
            if (_busy) return;
            _busy = true;
            _provision.IsEnabled = false;
            _status.Text = "Installing BBTech Wi-Fi profiles...";
            await EnsureStoreAsync();
            var nets = Networks;
            var results = await Task.Run(() => NewProvisioner().ProvisionAll(nets));
            AppLog.Info("Wi-Fi provisioning: " + string.Join(", ", results.Select(r => r.Outcome)));
            _status.Text = string.Join("\n", results.Select(r => (r.Outcome == ProvisionOutcome.Failed ? "✗ " : "✓ ") + r.Ssid + ": " + r.Message));
            _provision.IsEnabled = true;
            _busy = false;
            string summary = _status.Text;
            await RefreshAsync();
            _status.Text = summary;
        }

        private async Task ConnectAsync(WifiNetwork n, Button b)
        {
            b.IsEnabled = false;
            b.Content = "Connecting...";
            await EnsureStoreAsync();
            string err = await Task.Run(() => NewProvisioner().ConnectNow(n));
            AppLog.Info("Wi-Fi connect requested" + (err == null ? "" : " (not connected)"));
            _status.Text = err ?? "Connecting to " + n.Ssid + "...";
            await Task.Delay(err == null ? 4000 : 0);
            await RefreshAsync();
            if (err != null) _status.Text = err;
        }
    }
}
