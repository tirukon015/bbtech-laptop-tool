using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Export;
using BBTech.Core.Models;
using BBTech.Core.Parsing;
using BBTech.LaptopTool.Detect;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.Tools;

namespace BBTech.LaptopTool.UI.Views
{
    public partial class DetectView : UserControl
    {
        private DetectionResult _result;
        private bool _busy;

        public DetectView()
        {
            InitializeComponent();
            AuditButton.IsEnabled = FreePcAuditTool.IsAvailable;
            BivButton.IsEnabled = BatteryInfoViewTool.IsAvailable;
            HdsButton.IsEnabled = HdSentinelTool.IsAvailable;
            if (!FreePcAuditTool.IsAvailable) AuditButton.ToolTip = "Free PC Audit is not available in this build.";
            if (!BatteryInfoViewTool.IsAvailable) BivButton.ToolTip = "BatteryInfoView is not available in this build.";
            if (!HdSentinelTool.IsAvailable) HdsButton.ToolTip = "HD Sentinel is not available in this build.";
            SetActionsEnabled(false);
            SizeChanged += (s, e) => ApplyLayout(e.NewSize.Width);
        }

        /// <summary>Adapts the layout to the available width (small laptops, DPI scaling).</summary>
        private void ApplyLayout(double width)
        {
            bool narrowToolbar = width < 1080;
            Grid.SetRow(ToolbarButtons, narrowToolbar ? 1 : 0);
            Grid.SetColumn(ToolbarButtons, narrowToolbar ? 0 : 1);
            ToolbarButtons.Margin = new Thickness(0, narrowToolbar ? 12 : 0, 0, 0);

            FieldGrid.Columns = width >= 1000 ? 4 : 2;

            bool stackDetails = width < 900;
            Grid.SetColumnSpan(DetailsLeft, stackDetails ? 3 : 1);
            Grid.SetRow(DetailsRight, stackDetails ? 1 : 0);
            Grid.SetColumn(DetailsRight, stackDetails ? 0 : 2);
            Grid.SetColumnSpan(DetailsRight, stackDetails ? 3 : 1);
        }

        public async void StartDetection()
        {
            if (_busy) return;
            _busy = true;
            Notice.Visibility = Visibility.Collapsed;
            Overlay.Visibility = Visibility.Visible;
            SetActionsEnabled(false);
            RescanButton.IsEnabled = false;
            try
            {
                var progress = new Progress<string>(s => ProgressText.Text = s);
                _result = await HardwareDetector.DetectAsync(progress);
                DetectionSession.Set(_result); // shared with TEST (report name + header)
                Render(_result);
                _ = LoadHistoryAsync(_result); // automatic: no manual search, no serial typing
                SetActionsEnabled(true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Detection failed", ex);
                SubTitle.Text = "Detection could not be completed. See logs\\app.log for details.";
            }
            finally
            {
                Overlay.Visibility = Visibility.Collapsed;
                RescanButton.IsEnabled = true;
                _busy = false;
            }
        }

        private void SetActionsEnabled(bool on)
        {
            JsonButton.IsEnabled = on;
            CsvButton.IsEnabled = on;
            CopyButton.IsEnabled = on;
            ItmsButton.IsEnabled = on;
        }

        private void Render(DetectionResult r)
        {
            SubTitle.Text = "Detected " + DisplayFormat.DateTime(r.DetectedAt) + "  ·  values below are the ITMS fields";

            FieldGrid.Children.Clear();
            AddField("Brand", r.Brand);
            AddField("Model", r.Model);
            AddField("Model Number", r.ModelNumber);
            AddField("CPU", r.Cpu);
            AddField("Generation", r.Generation);
            AddField("RAM", r.Ram);
            var internalDrives = r.StorageDevices.Where(d => !d.IsExternal).ToList();
            AddField("Storage", internalDrives.Count > 1 ? string.Join("\n", internalDrives.Select(d => d.DisplayValue)) : r.Storage);
            AddField("Battery Health", r.BatteryHealth, BatteryAccent(r.Battery.HealthPercent));

            WarningsList.ItemsSource = r.Warnings;
            WarningsBox.Visibility = r.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            DetailsLeft.Children.Clear();
            DetailsRight.Children.Clear();

            var s = r.System;
            DetailsLeft.Children.Add(Section("System", new[]
            {
                Row("Manufacturer (firmware)", s.RawManufacturer),
                Row("Model (firmware)", s.RawModel),
                Row("Product version", s.ProductVersion),
                Row("System family", s.SystemFamily),
                Row("Product code", s.ProductCode),
                Row("Serial number", s.SerialNumber),
                Row("Chassis", s.ChassisType),
                Row("BIOS", s.BiosVersion),
                Row("Windows", s.OperatingSystem),
            }));
            DetailsLeft.Children.Add(Section("Processor", new[]
            {
                Row("Full name", r.CpuDetails.FullName),
                Row("Model code", r.CpuDetails.ModelCode),
                Row("Cores / threads", r.CpuDetails.Cores > 0 ? r.CpuDetails.Cores + " / " + r.CpuDetails.LogicalProcessors : ""),
                Row("Max clock", r.CpuDetails.MaxClockMhz > 0 ? (r.CpuDetails.MaxClockMhz / 1000.0).ToString("0.00") + " GHz" : ""),
            }));
            DetailsLeft.Children.Add(Section("Memory", new[] { Row("Total", r.Ram + (r.Memory.FromModules ? "" : " (estimated from Windows)")) }
                .Concat(r.Memory.Modules.Select((m, i) => Row("Module " + (i + 1) + (m.Slot.Length > 0 ? " (" + m.Slot + ")" : ""),
                    CapacityFormatter.FormatRamFromModules(m.CapacityBytes) + " " + m.Type + (m.SpeedMhz > 0 ? " " + m.SpeedMhz + " MHz" : "")))).ToArray()));

            DetailsRight.Children.Add(Section("Storage drives", r.StorageDevices.Count == 0
                ? new[] { Row("Drives", "None detected") }
                : r.StorageDevices.Select((d, i) => Row((d.IsExternal ? "External (not counted)" : "Drive " + (i + 1)),
                    d.DisplayValue + "  ·  " + (d.BusType.Length > 0 ? d.BusType + "  ·  " : "") + d.Model +
                    (d.WindowsHealth.Length > 0 ? "  ·  Windows health: " + d.WindowsHealth : ""))).ToArray()));

            var b = r.Battery;
            DetailsRight.Children.Add(Section("Battery", b.Present
                ? new[]
                {
                    Row("Health", r.BatteryHealth + (b.HealthPercent != null ? "  (exact " + b.HealthPercent.Value.ToString("0.0") + "%)" : "")),
                    Row("Design capacity", Mwh(b.DesignCapacityMWh)),
                    Row("Full charge capacity", Mwh(b.FullChargeCapacityMWh)),
                    Row("Current capacity", Mwh(b.CurrentCapacityMWh) + (b.ChargePercent != null ? "  (" + b.ChargePercent.Value.ToString("0") + "%)" : "")),
                    Row("Cycle count", b.CycleCount?.ToString() ?? ""),
                    Row("Batteries", b.BatteryCount > 0 ? b.BatteryCount.ToString() : ""),
                    Row("Chemistry", b.Chemistry),
                    Row("Power state", b.PowerState),
                    Row("Source", b.Source),
                }
                : new[] { Row("Battery", "Not detected") }));
        }

        private static string Mwh(long? v) => v == null ? "" : v.Value.ToString("N0") + " mWh";

        private static Brush BatteryAccent(double? pct)
        {
            if (pct == null) return null;
            if (pct >= 80) return (Brush)Application.Current.FindResource("PassBrush");
            if (pct >= 60) return (Brush)Application.Current.FindResource("WarnBrush");
            return (Brush)Application.Current.FindResource("FailBrush");
        }

        private void AddField(string label, string value, Brush accent = null)
        {
            bool unknown = value == DetectionResult.Unknown || string.IsNullOrEmpty(value);
            var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(6), MinHeight = 108 };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = (Style)FindResource("FieldLabel") });
            var tb = new TextBlock
            {
                Text = unknown ? "Unknown" : value,
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = unknown ? (Brush)FindResource("NeutralBrush") : accent ?? (Brush)FindResource("TextPrimary"),
            };
            if (value != null && value.Contains("\n")) tb.FontSize = 20;
            sp.Children.Add(tb);
            card.Child = sp;
            FieldGrid.Children.Add(card);
        }

        private Border Section(string title, UIElement[] rows)
        {
            var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 14) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var r in rows.Where(x => x != null)) sp.Children.Add(r);
            card.Child = sp;
            return card;
        }

        private UIElement Row(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Muted") });
            var v = new TextBlock { Text = value, FontSize = 13 };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            return g;
        }

        private void Rescan_Click(object sender, RoutedEventArgs e) => StartDetection();

        private void Json_Click(object sender, RoutedEventArgs e) => Save("json", DetectionExporter.ToJson(_result));
        private void Csv_Click(object sender, RoutedEventArgs e) => Save("csv", DetectionExporter.ToCsv(_result));

        private void Save(string ext, string content)
        {
            if (_result == null) return;
            try
            {
                string name = "detect_" + _result.DetectedAt.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "_" + Safe(_result.Brand) + "_" + Safe(_result.Model) + "_" + Safe(_result.ModelNumber) + "." + ext;
                string path = Path.Combine(AppPaths.Ensure(AppPaths.ExportsDir), name);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                NoticeText.Text = "Saved: " + path;
                Notice.Visibility = Visibility.Visible;
                AppLog.Info("Detection exported (" + ext + "): " + name);
            }
            catch (Exception ex)
            {
                AppLog.Error("Export failed", ex);
                MessageBox.Show("The file could not be saved. Check that the USB drive is not full or write-protected.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string Safe(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((s ?? "").Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());
            return clean.Length > 30 ? clean.Substring(0, 30) : clean;
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;
            var r = _result;
            string text = "Brand: " + r.Brand + "\r\nModel: " + r.Model + "\r\nModel Number: " + r.ModelNumber + "\r\nCPU: " + r.Cpu +
                          "\r\nGeneration: " + r.Generation + "\r\nRAM: " + r.Ram + "\r\nStorage: " + r.Storage + "\r\nBattery Health: " + r.BatteryHealth;
            try
            {
                Clipboard.SetText(text);
                NoticeText.Text = "Copied the ITMS fields to the clipboard.";
                Notice.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { AppLog.Warn("Clipboard copy failed", ex); }
        }

        private void Itms_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;
            new ItmsSendWindow(_result) { Owner = Window.GetWindow(this) }.ShowDialog();
        }

        private void OpenExports_Click(object sender, RoutedEventArgs e) => ProcessRunner.OpenShell(AppPaths.Ensure(AppPaths.ExportsDir));

        private void Audit_Click(object sender, RoutedEventArgs e) => OpenTool((Button)sender, FreePcAuditTool.OpenGui);
        private void Biv_Click(object sender, RoutedEventArgs e) => OpenTool((Button)sender, BatteryInfoViewTool.OpenGui);
        private void Hds_Click(object sender, RoutedEventArgs e) => OpenTool((Button)sender, HdSentinelTool.OpenGui);

        /// <summary>Starts a tool in the background (first use extracts it) so the UI never freezes.</summary>
        public static async void OpenTool(Button button, Func<string> open)
        {
            object label = button.Content;
            button.IsEnabled = false;
            button.Content = "Loading...";
            string error = await System.Threading.Tasks.Task.Run(open);
            button.Content = label;
            button.IsEnabled = true;
            if (error != null) MessageBox.Show(error, "BBTech Laptop Tool", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}


