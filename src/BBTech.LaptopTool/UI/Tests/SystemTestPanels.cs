using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BBTech.Core.Models;
using BBTech.Core.Parsing;
using BBTech.LaptopTool.Detect;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.Test.Wireless;
using BBTech.LaptopTool.Tools;

namespace BBTech.LaptopTool.UI.Tests
{
    /// <summary>Wi-Fi: adapter + radio state + active scan for nearby networks.</summary>
    public sealed class WifiTestPanel : TestPanelBase
    {
        private readonly StackPanel _result = new StackPanel();
        private readonly Button _scan;
        private WifiScanResult _last;

        public override string Instructions => "Scans for nearby Wi-Fi networks. Finding networks proves the Wi-Fi adapter and antennas can receive. No connection or internet is needed.";

        public WifiTestPanel()
        {
            _scan = B("Scan for networks", async (s, e) => await ScanAsync(), "PrimaryButton");
            _result.Children.Add(T("Press Scan to start.", 14, false, "TextMuted"));
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { Card("Wi-Fi", Row(_scan), _result) } } };
        }

        public override async void Attach(TestHostWindow host)
        {
            base.Attach(host);
            await ScanAsync();
        }

        private async Task ScanAsync()
        {
            _scan.IsEnabled = false;
            _result.Children.Clear();
            _result.Children.Add(T("Scanning (about 5 seconds)...", 14, false, "TextMuted"));
            _last = await Task.Run(() => WifiScanner.Scan());
            _scan.IsEnabled = true;
            _result.Children.Clear();

            var r = _last;
            _result.Children.Add(KeyValue(new[]
            {
                KV("Adapter", r.AdapterFound ? r.AdapterName : "Not found"),
                KV("Radio", r.RadioOn == null ? "" : r.RadioOn.Value ? "On" : "Off"),
                KV("Connection", r.State),
                KV("Networks found", r.NetworksFound >= 0 ? r.NetworksFound.ToString() : ""),
                KV("Strongest signal", r.NetworksFound > 0 ? r.BestSignalPercent + "%" : ""),
            }));
            if (r.NetworkNames.Count > 0)
                _result.Children.Add(T("Visible networks: " + string.Join(", ", r.NetworkNames.Take(20)), 12, false, "TextMuted"));
            if (r.Error != null)
            {
                var e = T(r.Error, 14, true, "FailBrush"); e.Margin = new Thickness(0, 8, 0, 0);
                _result.Children.Add(e);
            }

            if (r.NetworksFound > 0 || r.Connected) Host?.SetSuggestion("Wi-Fi works: " + Math.Max(r.NetworksFound, 0) + " network(s) found" + (r.Connected ? ", connected." : "."), SuggestionKind.Good);
            else if (!r.AdapterFound) Host?.SetSuggestion("No Wi-Fi adapter found.", SuggestionKind.Bad);
            else Host?.SetSuggestion(r.Error ?? "Adapter found but no networks were seen. Test near a Wi-Fi router.", SuggestionKind.Bad);
        }

        public override string Notes
        {
            get
            {
                if (_last == null) return "";
                if (!_last.AdapterFound) return "No Wi-Fi adapter";
                return "Adapter OK; " + (_last.NetworksFound >= 0 ? _last.NetworksFound + " networks, best " + _last.BestSignalPercent + "%" : "scan unavailable") +
                       (_last.Connected ? "; connected" : "");
            }
        }
    }

    /// <summary>Bluetooth: radio presence + device inquiry.</summary>
    public sealed class BluetoothTestPanel : TestPanelBase
    {
        private readonly StackPanel _result = new StackPanel();
        private readonly Button _scan;
        private BluetoothScanResult _last;

        public override string Instructions => "Make a phone discoverable (open its Bluetooth settings screen) near the laptop, then scan. Finding a device proves the Bluetooth radio works.";

        public BluetoothTestPanel()
        {
            _scan = B("Scan for devices", async (s, e) => await ScanAsync(), "PrimaryButton");
            _result.Children.Add(T("Press Scan to start.", 14, false, "TextMuted"));
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { Card("Bluetooth", Row(_scan), _result) } } };
        }

        public override async void Attach(TestHostWindow host)
        {
            base.Attach(host);
            await ScanAsync();
        }

        private async Task ScanAsync()
        {
            _scan.IsEnabled = false;
            _result.Children.Clear();
            _result.Children.Add(T("Scanning (about 10 seconds)...", 14, false, "TextMuted"));
            _last = await Task.Run(() => BluetoothScanner.Scan());
            _scan.IsEnabled = true;
            _result.Children.Clear();

            var r = _last;
            _result.Children.Add(KeyValue(new[]
            {
                KV("Adapter", r.RadioFound ? (r.RadioName.Length > 0 ? r.RadioName : "Found") : "Not found"),
                KV("Status", r.RadioFound ? "On" + (r.Connectable ? ", connectable" : "") : ""),
                KV("Devices found", r.RadioFound ? r.DevicesFound.ToString() : ""),
            }));
            if (r.DeviceNames.Count > 0) _result.Children.Add(T("Devices: " + string.Join(", ", r.DeviceNames.Take(20)), 12, false, "TextMuted"));
            if (r.Error != null) { var e = T(r.Error, 14, true, "FailBrush"); e.Margin = new Thickness(0, 8, 0, 0); _result.Children.Add(e); }

            if (r.RadioFound && r.DevicesFound > 0) Host?.SetSuggestion("Bluetooth works: " + r.DevicesFound + " device(s) found.", SuggestionKind.Good);
            else if (r.RadioFound) Host?.SetSuggestion("Adapter is on but no devices were found. Make a phone discoverable and scan again.", SuggestionKind.Info);
            else Host?.SetSuggestion(r.Error ?? "No Bluetooth adapter.", SuggestionKind.Bad);
        }

        public override string Notes => _last == null ? "" : !_last.RadioFound ? "No Bluetooth adapter / turned off" : "Adapter OK; " + _last.DevicesFound + " device(s) found";
    }

    /// <summary>Battery: health from BatteryInfoView plus a live charger unplug / re-plug check.</summary>
    public sealed class BatteryTestPanel : TestPanelBase
    {
        private readonly StackPanel _info = new StackPanel();
        private readonly TextBlock _live = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _step1 = new TextBlock { FontSize = 15 }, _step2 = new TextBlock { FontSize = 15 };
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private bool _sawUnplugged, _sawReplugged;
        private BatteryDetails _battery;

        public override string Instructions => "Battery health is read with BatteryInfoView. Then unplug the charger and plug it back in so the tool can confirm the battery powers the laptop and charging is detected.";

        public BatteryTestPanel()
        {
            _info.Children.Add(T("Reading battery information...", 14, false, "TextMuted"));
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Children =
                    {
                        Card("Battery health", _info, Row(B("Read again", async (s, e) => await LoadAsync()), B("Open BatteryInfoView", (s, e) => UI.Views.DetectView.OpenTool((Button)s, BatteryInfoViewTool.OpenGui)))),
                        Card("Charger check", _live, new Border { Height = 8 }, _step1, _step2),
                    }
                }
            };
        }

        public override async void Attach(TestHostWindow host)
        {
            base.Attach(host);
            _timer.Tick += (s, e) => Poll();
            _timer.Start();
            Poll();
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            var r = new DetectionResult();
            await Task.Run(() =>
            {
                try { BatteryProvider.Fill(r); }
                catch (Exception ex) { Logging.AppLog.Error("Battery test read failed", ex); }
            });
            _battery = r.Battery;
            _info.Children.Clear();
            if (!_battery.Present)
            {
                _info.Children.Add(T(r.Warnings.FirstOrDefault() ?? BatteryInfoViewTool.Unavailable, 15, true, "FailBrush"));
                Host?.SetSuggestion("No battery information.", SuggestionKind.Bad);
                return;
            }
            string health = BatteryHealth.Format(_battery.HealthPercent);
            var big = T("Health: " + health, 26, true, _battery.HealthPercent == null ? "NeutralBrush" : _battery.HealthPercent >= 80 ? "PassBrush" : _battery.HealthPercent >= 60 ? "WarnBrush" : "FailBrush");
            _info.Children.Add(big);
            _info.Children.Add(KeyValue(new[]
            {
                KV("Design capacity", Mwh(_battery.DesignCapacityMWh)),
                KV("Full charge capacity", Mwh(_battery.FullChargeCapacityMWh)),
                KV("Current capacity", Mwh(_battery.CurrentCapacityMWh)),
                KV("Cycle count", _battery.CycleCount?.ToString() ?? ""),
                KV("Source", _battery.Source),
            }));
            UpdateSuggestion();
        }

        private static string Mwh(long? v) => v == null ? "" : v.Value.ToString("N0") + " mWh";

        private void Poll()
        {
            var p = PowerStatus.Read();
            if (p.NoSystemBattery) { _live.Text = "Windows reports no system battery."; return; }
            _live.Text = "Charger: " + (p.AcOnline == true ? "CONNECTED" : p.AcOnline == false ? "UNPLUGGED" : "unknown") +
                         "    Charging: " + (p.Charging ? "yes" : "no") + "    Charge: " + (p.ChargePercent?.ToString() ?? "?") + "%";
            if (p.AcOnline == false) _sawUnplugged = true;
            if (_sawUnplugged && p.AcOnline == true) _sawReplugged = true;
            _step1.Text = (_sawUnplugged ? "✓ " : "1. ") + "Unplug the charger - laptop keeps running on battery";
            _step1.Foreground = Res(_sawUnplugged ? "PassBrush" : "TextPrimary");
            _step2.Text = (_sawReplugged ? "✓ " : "2. ") + "Plug the charger back in - charger detected";
            _step2.Foreground = Res(_sawReplugged ? "PassBrush" : "TextPrimary");
            UpdateSuggestion();
        }

        private void UpdateSuggestion()
        {
            if (Host == null || _battery == null || !_battery.Present) return;
            if (_sawUnplugged && _sawReplugged) Host.SetSuggestion("Battery and charger checks done. Health " + BatteryHealth.Format(_battery.HealthPercent) + ".", SuggestionKind.Good);
            else Host.SetSuggestion("Complete the charger unplug / plug-in steps.", SuggestionKind.Info);
        }

        public override void Detach() => _timer.Stop();

        public override string Notes => _battery == null || !_battery.Present ? "Battery not detected"
            : "Health " + BatteryHealth.Format(_battery.HealthPercent) + (_battery.CycleCount != null ? ", " + _battery.CycleCount + " cycles" : "") +
              "; unplug " + (_sawUnplugged ? "OK" : "not done") + "; re-plug " + (_sawReplugged ? "OK" : "not done");
    }

    /// <summary>Storage: Windows drive health, plus HD Sentinel health/performance report (admin).</summary>
    public sealed class StorageTestPanel : TestPanelBase
    {
        private readonly StackPanel _drives = new StackPanel();
        private readonly TextBlock _hdsStatus = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        private readonly Button _hdsButton;
        private DetectionResult _r = new DetectionResult();
        private List<HdSentinelDisk> _hds;
        private bool _counters;

        public override string Instructions => "Shows each drive's health as reported by Windows. Run the HD Sentinel check for health % and performance % (Windows will ask for administrator permission).";

        public StorageTestPanel()
        {
            _hdsButton = B("Run HD Sentinel health check", async (s, e) => await RunHdsAsync(), "PrimaryButton");
            _hdsButton.IsEnabled = HdSentinelTool.IsAvailable;
            if (!HdSentinelTool.IsAvailable) _hdsStatus.Text = "HD Sentinel is not available in this build; only Windows health is shown.";
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Children =
                    {
                        Card("Drives", _drives),
                        Card("HD Sentinel", Row(_hdsButton, B("Open HD Sentinel", (s, e) => UI.Views.DetectView.OpenTool((Button)s, HdSentinelTool.OpenGui))), _hdsStatus),
                    }
                }
            };
        }

        public override async void Attach(TestHostWindow host)
        {
            base.Attach(host);
            _drives.Children.Add(T("Reading drives...", 14, false, "TextMuted"));
            await Task.Run(() =>
            {
                try
                {
                    StorageProvider.Fill(_r);
                    _counters = StorageProvider.ReadReliability(_r.StorageDevices); // Windows-native, no HD Sentinel needed
                }
                catch (Exception ex) { Logging.AppLog.Error("Storage test read failed", ex); }
            });
            Render();
        }

        private async Task RunHdsAsync()
        {
            _hdsButton.IsEnabled = false;
            _hdsStatus.Text = "Running HD Sentinel... accept the Windows permission prompt. This can take up to a minute.";
            _hdsStatus.Foreground = Res("TextMuted");
            var res = await Task.Run(() => HdSentinelTool.RunHealthReport());
            _hdsButton.IsEnabled = true;
            if (!res.Ok)
            {
                _hdsStatus.Text = res.Message;
                _hdsStatus.Foreground = Res("FailBrush");
                return;
            }
            _hds = res.Value;
            _hdsStatus.Text = "HD Sentinel report read for " + _hds.Count + " drive(s).";
            _hdsStatus.Foreground = Res("PassBrush");

            // Match HD Sentinel disks to Windows drives by model name; otherwise list separately.
            foreach (var d in _r.StorageDevices)
            {
                var m = _hds.FirstOrDefault(h => h.Model.Length > 0 && (d.Model.IndexOf(h.Model, StringComparison.OrdinalIgnoreCase) >= 0 || h.Model.IndexOf(d.Model, StringComparison.OrdinalIgnoreCase) >= 0));
                if (m == null) continue;
                d.HealthPercent = m.HealthPercent;
                d.PerformancePercent = m.PerformancePercent;
                d.PowerOnTime = m.PowerOnTime;
                d.Temperature = m.Temperature;
            }
            Render();
        }

        private void Render()
        {
            _drives.Children.Clear();
            if (_r.StorageDevices.Count == 0) { _drives.Children.Add(T("No drives detected.", 15, true, "FailBrush")); Host?.SetSuggestion("No drives detected.", SuggestionKind.Bad); return; }

            foreach (var d in _r.StorageDevices)
            {
                string title = (d.IsExternal ? "[External] " : "") + d.DisplayValue + "  ·  " + d.Model;
                _drives.Children.Add(T(title, 15, true));
                _drives.Children.Add(KeyValue(new[]
                {
                    KV("Interface", d.BusType),
                    KV("Windows health", d.WindowsHealth),
                    KV("Wear used (Windows)", d.WearPercentUsed != null ? d.WearPercentUsed + "%" : (_counters ? "Not reported" : "")),
                    KV("Power-on hours (Windows)", d.PowerOnHours?.ToString("N0")),
                    KV("Temperature (Windows)", d.TemperatureC != null ? d.TemperatureC + " °C" : ""),
                    KV("HD Sentinel health", d.HealthPercent?.ToString() + (d.HealthPercent != null ? "%" : "")),
                    KV("HD Sentinel performance", d.PerformancePercent?.ToString() + (d.PerformancePercent != null ? "%" : "")),
                    KV("Power-on time", d.PowerOnTime),
                    KV("Temperature", d.Temperature),
                }));
                _drives.Children.Add(new Border { Height = 10 });
            }
            if (_hds != null)
            {
                foreach (var h in _hds.Where(h => !_r.StorageDevices.Any(d => d.HealthPercent == h.HealthPercent && d.Model.IndexOf(h.Model, StringComparison.OrdinalIgnoreCase) >= 0)))
                    _drives.Children.Add(T("HD Sentinel: " + h.Model + " - health " + h.HealthPercent + "%, performance " + h.PerformancePercent + "%", 13, false, "TextMuted"));
            }

            var internalDrives = _r.StorageDevices.Where(d => !d.IsExternal).ToList();
            if (!_counters)
                _drives.Children.Add(T("Detailed wear / power-on counters are not available from Windows here (they usually need administrator rights). Windows health above is still valid; HD Sentinel is optional.", 12, false, "TextMuted"));
            bool bad = internalDrives.Any(d => d.WindowsHealth == "Unhealthy" || d.WindowsHealth == "Warning" || (d.HealthPercent != null && d.HealthPercent < 50) ||
                                               (d.WearPercentUsed != null && d.WearPercentUsed >= 90));
            if (bad) Host?.SetSuggestion("A drive reports a health problem.", SuggestionKind.Bad);
            else if (internalDrives.Any(d => d.HealthPercent != null)) Host?.SetSuggestion("Drive health looks good.", SuggestionKind.Good);
            else Host?.SetSuggestion("Windows reports the drives as " + string.Join("/", internalDrives.Select(d => d.WindowsHealth).Distinct()) + ". Run HD Sentinel for health %.", SuggestionKind.Info);
        }

        public override string Notes => string.Join("; ", _r.StorageDevices.Where(d => !d.IsExternal).Select(d =>
            d.DisplayValue + " Windows " + d.WindowsHealth + (d.HealthPercent != null ? ", HDS health " + d.HealthPercent + "%" : "") + (d.PerformancePercent != null ? ", perf " + d.PerformancePercent + "%" : "")));
    }
}

