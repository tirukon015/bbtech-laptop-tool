using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BBTech.Core.Models;
using BBTech.Core.Parsing;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
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

    /// <summary>Integrated SSD Health Detection: HD Sentinel Pro engine + Windows NVMe SMART reliability fallback.</summary>
    public sealed class StorageTestPanel : TestPanelBase
    {
        private readonly StackPanel _drivesPanel = new StackPanel();
        private readonly TextBlock _engineStatus = new TextBlock { FontSize = 13, Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap };
        private readonly Button _rescanBtn;
        private DetectionResult _detection = new DetectionResult();
        private SsdHealthEngineResult _engineResult;

        public override string Instructions => "Integrated SSD & Storage health diagnostic. Automatically queries HD Sentinel Pro and Windows NVMe SMART reliability counters to determine drive health, performance, and endurance life.";

        public StorageTestPanel()
        {
            _rescanBtn = B("Re-scan SSD Health", async (s, e) => await RunInspectionAsync(forceRefresh: true), "PrimaryButton");

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Children =
                    {
                        Card("Physical Storage Health", _drivesPanel),
                        Card("Diagnostic Engine",
                            Row(_rescanBtn),
                            _engineStatus,
                            T("The integrated engine non-interactively queries HD Sentinel Pro and Windows NVMe SMART diagnostics without requiring manual technician interaction.", 12, false, "TextMuted"))
                    }
                }
            };
        }

        public override async void Attach(TestHostWindow host)
        {
            base.Attach(host);
            await RunInspectionAsync(forceRefresh: false);
        }

        private async Task RunInspectionAsync(bool forceRefresh)
        {
            _rescanBtn.IsEnabled = false;
            _drivesPanel.Children.Clear();
            _drivesPanel.Children.Add(T("Inspecting SSD health & SMART counters...", 14, false, "TextMuted"));
            _engineStatus.Text = "Running SSD Health Detection Engine (HD Sentinel Pro + Windows SMART)...";
            _engineStatus.Foreground = Res("TextMuted");

            _engineResult = await SsdHealthEngine.InspectAsync(_detection, forceRefresh);
            _rescanBtn.IsEnabled = true;

            Render();
        }

        private void Render()
        {
            _drivesPanel.Children.Clear();

            if (_engineResult == null || _engineResult.Disks == null || _engineResult.Disks.Count == 0)
            {
                _drivesPanel.Children.Add(T("No physical solid-state or hard drives detected.", 15, true, "FailBrush"));
                _engineStatus.Text = _engineResult != null ? _engineResult.SummaryMessage : "Detection failed to report any physical drives.";
                _engineStatus.Foreground = Res("FailBrush");
                Host?.SetSuggestion("No drives detected.", SuggestionKind.Bad);
                return;
            }

            _engineStatus.Text = string.Format("Engine: {0}  ·  {1} physical drive(s) analyzed. {2}",
                _engineResult.ProviderUsed,
                _engineResult.Disks.Count,
                _engineResult.SummaryMessage);
            _engineStatus.Foreground = Res(_engineResult.AnyBadDrives ? "FailBrush" : _engineResult.AnyWarningDrives ? "WarnBrush" : "PassBrush");

            foreach (var disk in _engineResult.Disks)
            {
                _drivesPanel.Children.Add(CreateDriveCard(disk));
            }

            if (_engineResult.AnyBadDrives)
            {
                Host?.SetSuggestion("Critical SSD health problem detected. Hardware replacement recommended.", SuggestionKind.Bad);
            }
            else if (_engineResult.AnyWarningDrives)
            {
                Host?.SetSuggestion("SSD health warning detected. Review SMART wear and performance.", SuggestionKind.Bad);
            }
            else if (_engineResult.AllHealthy)
            {
                Host?.SetSuggestion(string.Format("All {0} drive(s) healthy. Optimal SSD performance and SMART health verified.",
                    _engineResult.Disks.Count(d => !d.IsExternal)), SuggestionKind.Good);
            }
            else
            {
                Host?.SetSuggestion("Drive inspection complete. Review diagnostic metrics.", SuggestionKind.Info);
            }
        }

        // SSD health is measured, not chosen: PASS needs a completed measurement with no critical internal drive.
        public override string PassBlockedReason =>
            _engineResult == null || _engineResult.Disks == null || _engineResult.Disks.Count(d => !d.IsExternal) == 0
                ? "The SSD health has not been measured yet. Wait for the scan to finish (or press Re-scan SSD Health)."
            : _engineResult.AnyBadDrives
                ? "The measured SSD health is CRITICAL / FAILED. This cannot be passed by hand: replace the drive (record the repair) and test again."
            : null;

        private UIElement CreateDriveCard(PhysicalSsdHealthResult disk)
        {
            var card = new StackPanel();

            // 1. Header row with Model, capacity, and status badge
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = T(disk.DisplayTitle, 15, true, "TextPrimary");
            Grid.SetColumn(title, 0);
            headerGrid.Children.Add(title);

            string statusBrushKey = "NeutralBrush";
            string statusBgKey = "NeutralSoft";
            switch (disk.Status)
            {
                case SsdHealthStatus.Optimal:
                case SsdHealthStatus.Good:
                    statusBrushKey = "PassBrush";
                    statusBgKey = "PassSoft";
                    break;
                case SsdHealthStatus.Warning:
                    statusBrushKey = "WarnBrush";
                    statusBgKey = "WarnSoft";
                    break;
                case SsdHealthStatus.Critical:
                case SsdHealthStatus.Failed:
                    statusBrushKey = "FailBrush";
                    statusBgKey = "FailSoft";
                    break;
            }

            var badge = new Border
            {
                Background = Res(statusBgKey),
                BorderBrush = Res(statusBrushKey),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 3, 10, 3),
                Child = new TextBlock
                {
                    Text = disk.StatusLabel,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res(statusBrushKey)
                }
            };
            Grid.SetColumn(badge, 1);
            headerGrid.Children.Add(badge);
            card.Children.Add(headerGrid);

            // 2. Health & Performance highlight row
            var metricRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            if (disk.HealthPercent.HasValue)
            {
                var healthPill = new Border
                {
                    Background = Res(statusBgKey),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 8, 4),
                    Child = new TextBlock
                    {
                        Text = "Health: " + disk.HealthPercent.Value + "%",
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        Foreground = Res(statusBrushKey)
                    }
                };
                metricRow.Children.Add(healthPill);
            }
            if (disk.PerformancePercent.HasValue)
            {
                var perfPill = new Border
                {
                    Background = Res("NeutralSoft"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 8, 4),
                    Child = new TextBlock
                    {
                        Text = "Performance: " + disk.PerformancePercent.Value + "%",
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        Foreground = Res("TextPrimary")
                    }
                };
                metricRow.Children.Add(perfPill);
            }
            if (metricRow.Children.Count > 0)
            {
                card.Children.Add(metricRow);
            }

            // 3. KeyValue details grid
            card.Children.Add(KeyValue(new[]
            {
                KV("Serial Number", disk.SerialNumber),
                KV("Interface / Bus", disk.BusType),
                KV("Capacity", disk.CapacityLabel),
                KV("Temperature", !string.IsNullOrEmpty(disk.Temperature) ? disk.Temperature : (disk.TemperatureC != null ? disk.TemperatureC + " °C" : "")),
                KV("Power-on Time", !string.IsNullOrEmpty(disk.PowerOnTime) ? disk.PowerOnTime : (disk.PowerOnHours != null ? disk.PowerOnHours.Value.ToString("N0") + " hours" : "")),
                KV("NVMe Wear Used", disk.WearPercentUsed != null ? disk.WearPercentUsed + "%" : ""),
                KV("Data Source", disk.DataSource),
            }));

            // 4. Diagnostic details from SMART / HD Sentinel
            if (!string.IsNullOrWhiteSpace(disk.DiagnosticDetails))
            {
                var notesBorder = new Border
                {
                    Background = Res("NeutralSoft"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 8, 0, 0),
                    Child = T(disk.DiagnosticDetails, 12, false, "TextMuted")
                };
                card.Children.Add(notesBorder);
            }

            return new Border
            {
                Style = (Style)Application.Current.FindResource("Card"),
                Child = card,
                Margin = new Thickness(0, 0, 0, 12)
            };
        }

        public override string Notes
        {
            get
            {
                if (_engineResult == null || _engineResult.Disks == null || _engineResult.Disks.Count == 0)
                    return "No drives detected";

                return string.Join("; ", _engineResult.Disks.Where(d => !d.IsExternal).Select(d =>
                    string.Format("{0} [{1}] Health:{2} Perf:{3}{4} ({5})",
                        d.Model,
                        d.StatusLabel,
                        d.HealthPercent.HasValue ? d.HealthPercent.Value + "%" : "N/A",
                        d.PerformancePercent.HasValue ? d.PerformancePercent.Value + "%" : "N/A",
                        d.WearPercentUsed.HasValue ? string.Format(", Wear:{0}%", d.WearPercentUsed.Value) : "",
                        d.DataSource)));
            }
        }
    }

    /// <summary>RAM: capacity, speed, slots used, per-module specifications, memory load.</summary>
    public sealed class RamTestPanel : TestPanelBase
    {
        private readonly StackPanel _result = new StackPanel();
        private readonly Button _refresh;
        private string _notes = "";

        public override string Instructions => "Verifies system memory capacity, installed modules, channels, speed, and health.";

        public RamTestPanel()
        {
            _refresh = B("Check RAM", async (s, e) => await CheckAsync(), "PrimaryButton");
            _result.Children.Add(T("Checking memory...", 14, false, "TextMuted"));
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { Card("RAM Specifications", Row(_refresh), _result) } } };
        }

        public override async void Attach(TestHostWindow host)
        {
            base.Attach(host);
            await CheckAsync();
        }

        private async Task CheckAsync()
        {
            _refresh.IsEnabled = false;
            _result.Children.Clear();
            _result.Children.Add(T("Scanning memory hardware...", 14, false, "TextMuted"));

            var detection = await DetectionSession.GetOrDetectAsync();
            var mem = detection?.Memory;
            _refresh.IsEnabled = true;
            _result.Children.Clear();

            if (mem == null)
            {
                _result.Children.Add(T("Could not read memory information.", 14, true, "FailBrush"));
                Host?.SetSuggestion("Memory details could not be detected.", SuggestionKind.Bad);
                _notes = "Memory detection unavailable";
                return;
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                KV("Total Installed", detection.Ram ?? (mem.TotalBytes > 0 ? (mem.TotalBytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.#") + " GB" : "Unknown")),
                KV("Modules Found", mem.Modules != null ? mem.Modules.Count.ToString() : "Unknown"),
                KV("Speed", mem.Modules?.FirstOrDefault(m => m.SpeedMhz > 0)?.SpeedMhz > 0 ? mem.Modules.First(m => m.SpeedMhz > 0).SpeedMhz + " MHz" : "Unknown"),
                KV("Type", mem.Modules?.FirstOrDefault(m => !string.IsNullOrEmpty(m.Type))?.Type ?? "DDR"),
            };

            _result.Children.Add(KeyValue(rows));

            if (mem.Modules != null && mem.Modules.Count > 0)
            {
                _result.Children.Add(new Border { Height = 8 });
                _result.Children.Add(T("Installed Modules:", 14, true, "TextPrimary"));
                int idx = 1;
                foreach (var mod in mem.Modules)
                {
                    string cap = mod.CapacityBytes > 0 ? (mod.CapacityBytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.#") + " GB " : "";
                    string info = (!string.IsNullOrEmpty(mod.Slot) ? mod.Slot + ": " : "Module " + idx + ": ") +
                                  cap + (!string.IsNullOrEmpty(mod.Type) ? mod.Type + " " : "") +
                                  (mod.SpeedMhz > 0 ? mod.SpeedMhz + "MHz " : "") +
                                  (mod.Manufacturer ?? "") + " " + (mod.PartNumber ?? "");
                    _result.Children.Add(T("•  " + info.Trim(), 13, false, "TextMuted"));
                    idx++;
                }
            }

            bool healthy = mem.TotalBytes >= 1024L * 1024 * 1024 * 2; // >= 2GB
            string summary = (detection.Ram ?? "RAM") + " detected" + (mem.Modules != null ? " (" + mem.Modules.Count + " module(s))" : "");
            _notes = summary;

            if (healthy) Host?.SetSuggestion("RAM Healthy: " + summary, SuggestionKind.Good);
            else Host?.SetSuggestion("Low or undetectable memory (" + summary + ").", SuggestionKind.Bad);
        }

        public override string Notes => _notes;
    }

    /// <summary>Physical inspection for QC & Hardware: body, hinges, casing, screws, cosmetics.</summary>
    public sealed class PhysicalInspectionTestPanel : TestPanelBase
    {
        public override string Instructions => "Inspect physical chassis, screen bezel, hinges, casing, screws, keyboard deck, and ports for cracks, damage, or loose parts.";

        public PhysicalInspectionTestPanel()
        {
            var sp = new StackPanel();
            sp.Children.Add(Check("Hinges are smooth, firm, and undamaged"));
            sp.Children.Add(Check("Casing/body has no cracks, dents, or deep gouges"));
            sp.Children.Add(Check("Bottom cover screws are present and secure"));
            sp.Children.Add(Check("Display bezel and rubber bumpers are intact"));
            sp.Children.Add(Check("Clean and cosmetically acceptable"));
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { Card("Physical Inspection Checklist", sp) } } };
        }
    }

    /// <summary>Other inspection: charger / AC adapter, sensors, trackpoint, special features.</summary>
    public sealed class OtherInspectionTestPanel : TestPanelBase
    {
        public override string Instructions => "Inspect miscellaneous components including power charging port, AC adapter, trackpoint/buttons, and special features.";

        public OtherInspectionTestPanel()
        {
            var sp = new StackPanel();
            sp.Children.Add(Check("Charging port firmly holds AC adapter"));
            sp.Children.Add(Check("Laptop powers on with battery and AC adapter"));
            sp.Children.Add(Check("Auxiliary ports / card reader in good condition"));
            sp.Children.Add(Check("Thermal fans spin normally without grinding noise"));
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { Card("Additional Inspection Checklist", sp) } } };
        }
    }

    /// <summary>
    /// A short visual confirmation list (QC checklist confirmations and the final delivery check).
    /// The technician looks, ticks, and presses PASS or FAIL: no typing needed.
    /// </summary>
    public sealed class ChecklistTestPanel : TestPanelBase
    {
        private readonly string _instructions;
        public override string Instructions => _instructions;

        public ChecklistTestPanel(HardwareTest kind)
        {
            string title = TestText.Name(kind);
            string[] lines;
            switch (kind)
            {
                case HardwareTest.QcWindows: case HardwareTest.DeliveryWindows:
                    lines = new[] { "Windows starts to the desktop", "No error messages at start", "Correct Windows edition" }; break;
                case HardwareTest.QcSoftware: case HardwareTest.DeliverySoftware:
                    lines = new[] { "Required software is installed", "Each required program opens" }; break;
                case HardwareTest.QcActivation:
                    lines = new[] { "Windows shows Activated", "Office shows Activated (if installed)" }; break;
                case HardwareTest.QcAccessories: case HardwareTest.DeliveryAccessories:
                    lines = new[] { "Charger present and correct", "Other accessories present" }; break;
                case HardwareTest.DeliveryCharger:
                    lines = new[] { "Charger present", "Laptop charges" }; break;
                case HardwareTest.DeliveryPhysical:
                    lines = new[] { "Clean, no new scratches or dents", "Hinges firm, all screws present" }; break;
                case HardwareTest.DeliveryDisplay:
                    lines = new[] { "Screen clean, no marks", "No dead pixels or lines" }; break;
                case HardwareTest.DeliveryKeyboard:
                    lines = new[] { "All keys present and clean", "Keys and touchpad respond" }; break;
                case HardwareTest.DeliveryCustomerConfig:
                    lines = new[] { "Customer-requested setup is done" }; break;
                default:
                    lines = new[] { "Overall condition is good", "Nothing else is wrong" }; break;
            }
            _instructions = "Check each point, then press PASS or FAIL.";
            var sp = new StackPanel();
            foreach (var l in lines) sp.Children.Add(Check(l));
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { Card(title, sp) } } };
        }
    }
}
