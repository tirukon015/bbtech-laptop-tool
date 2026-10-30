using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BBTech.Core.Export;
using BBTech.Core.Integration;
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
            ApplyLayout(ActualWidth > 0 ? ActualWidth : 1180);
            SizeChanged += (s, e) => ApplyLayout(e.NewSize.Width);
        }

        /// <summary>Adapts the layout to the available width (small laptops, DPI scaling).</summary>
        private void ApplyLayout(double width)
        {
            bool narrowToolbar = width < 1250;
            Grid.SetRow(ToolbarButtons, narrowToolbar ? 1 : 0);
            Grid.SetColumn(ToolbarButtons, narrowToolbar ? 0 : 1);
            Grid.SetColumnSpan(ToolbarButtons, narrowToolbar ? 2 : 1);
            ToolbarButtons.Margin = new Thickness(0, narrowToolbar ? 12 : 0, 0, 0);

            FieldGrid.Columns = width >= 1200 ? 5 : width >= 850 ? 3 : 2;

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
            QrButton.IsEnabled = on;
        }

        private void Render(DetectionResult r)
        {
            SubTitle.Text = "Detected " + DisplayFormat.DateTime(r.DetectedAt) + "  ·  values below are the ITMS fields";

            string fullName = ModelParser.BuildFullName(r);
            if (!string.IsNullOrEmpty(fullName))
            {
                FullNameValue.Text = fullName;
                FullNameBox.Visibility = Visibility.Visible;
            }
            else
            {
                FullNameBox.Visibility = Visibility.Collapsed;
            }

            FieldGrid.Children.Clear();
            AddField("Brand", r.Brand);
            AddField("Model", r.Model);
            AddField("Model Number", r.ModelNumber);
            AddField("CPU", r.Cpu);
            AddField("Generation", r.Generation);
            AddField("RAM", r.Ram);
            var internalDrives = r.StorageDevices.Where(d => !d.IsExternal).ToList();
            var primeDrive = internalDrives.FirstOrDefault() ?? r.StorageDevices.FirstOrDefault();
            AddField("Storage", internalDrives.Count > 1 ? string.Join("\n", internalDrives.Select(d => d.DisplayValue)) : r.Storage);
            AddField("Battery Health", r.BatteryHealth, BatteryAccent(r.Battery.HealthPercent));
            AddField("SSD Health", r.SsdHealth, SsdAccent(primeDrive), onSsdClick: async (tb) =>
            {
                try
                {
                    tb.Text = "Scanning...";
                    await SsdHealthEngine.InspectAsync(r, forceRefresh: true);
                    Render(r);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Manual SSD health re-scan failed", ex);
                }
            }, ssdTooltip: primeDrive != null ? $"{primeDrive.Model}\nStatus: {primeDrive.HealthAssessment} {primeDrive.HealthPercent}%\nSource: {primeDrive.HealthSource ?? "Windows NVMe SMART"}\nClick to re-scan SSD Health" : null);

            WarningsList.ItemsSource = r.Warnings;
            WarningsBox.Visibility = r.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            DetailsLeft.Children.Clear();
            DetailsRight.Children.Clear();

            var s = r.System;
            var sysSection = Section("System", new[]
            {
                Row("Manufacturer (firmware)", s.RawManufacturer),
                Row("Model (firmware)", s.RawModel),
                Row("Product version", s.ProductVersion),
                Row("System family", s.SystemFamily),
                Row("Product code", s.ProductCode),
                Row("Serial number", s.SerialNumber),
                Row("Motherboard serial", s.BoardSerial),
                Row("System UUID", s.SystemUuid),
                Row("Asset tag", s.FirmwareAssetTag),
                Row("Chassis", s.ChassisType),
                Row("BIOS", s.BiosVersion),
                Row("Windows", s.OperatingSystem),
            });
            if (sysSection != null) DetailsLeft.Children.Add(sysSection);

            var procSection = Section("Processor", new[]
            {
                Row("Full name", r.CpuDetails.FullName),
                Row("Model code", r.CpuDetails.ModelCode),
                Row("Cores / threads", r.CpuDetails.Cores > 0 ? r.CpuDetails.Cores + " / " + r.CpuDetails.LogicalProcessors : ""),
                Row("Max clock", r.CpuDetails.MaxClockMhz > 0 ? (r.CpuDetails.MaxClockMhz / 1000.0).ToString("0.00") + " GHz" : ""),
            });
            if (procSection != null) DetailsLeft.Children.Add(procSection);

            var memRows = new List<UIElement>();
            if (!string.IsNullOrWhiteSpace(r.Ram))
                memRows.Add(Row("Total", r.Ram + (r.Memory.FromModules ? "" : " (estimated from Windows)")));
            if (r.Memory.Modules != null)
            {
                for (int i = 0; i < r.Memory.Modules.Count; i++)
                {
                    var m = r.Memory.Modules[i];
                    memRows.Add(Row("Module " + (i + 1) + (m.Slot.Length > 0 ? " (" + m.Slot + ")" : ""),
                        CapacityFormatter.FormatRamFromModules(m.CapacityBytes) + " " + m.Type + (m.SpeedMhz > 0 ? " " + m.SpeedMhz + " MHz" : "")));
                }
            }
            var memSection = Section("Memory", memRows.ToArray());
            if (memSection != null) DetailsLeft.Children.Add(memSection);

            if (r.Gpus != null && r.Gpus.Count > 0)
            {
                var gpuRows = r.Gpus.Select((g, i) => Row(r.Gpus.Count > 1 ? "GPU " + (i + 1) : "GPU",
                    g.Name + (!string.IsNullOrEmpty(g.DedicatedVramLabel) ? "  ·  " + g.DedicatedVramLabel : "") +
                    (!string.IsNullOrEmpty(g.DriverVersion) ? "  ·  Driver: " + g.DriverVersion : ""))).ToArray();
                var gpuSection = Section("Graphics", gpuRows);
                if (gpuSection != null) DetailsLeft.Children.Add(gpuSection);
            }

            var storageRows = r.StorageDevices.Count == 0
                ? new UIElement[0]
                : r.StorageDevices.Select((d, i) => Row((d.IsExternal ? "External" : "Drive " + (i + 1)),
                    d.DisplayValue + "  ·  " + (d.BusType.Length > 0 ? d.BusType + "  ·  " : "") + d.Model +
                    (!string.IsNullOrEmpty(d.HealthAssessment) ? "  ·  Health: " + (d.HealthPercent.HasValue ? d.HealthPercent + "% (" + d.HealthAssessment + ")" : d.HealthAssessment) : (d.WindowsHealth.Length > 0 ? "  ·  Windows health: " + d.WindowsHealth : "")) +
                    (!string.IsNullOrEmpty(d.Temperature) ? "  ·  " + d.Temperature : (d.TemperatureC.HasValue ? "  ·  " + d.TemperatureC + " °C" : "")) +
                    (!string.IsNullOrEmpty(d.PowerOnTime) ? "  ·  " + d.PowerOnTime : (d.PowerOnHours.HasValue ? "  ·  " + d.PowerOnHours + " hrs" : "")) +
                    (d.WearPercentUsed.HasValue ? "  ·  Wear: " + d.WearPercentUsed + "%" : "") +
                    (!string.IsNullOrEmpty(d.HealthSource) ? "  ·  " + d.HealthSource : "")
                    )).ToArray();
            var storageSection = Section("Storage drives", storageRows);
            if (storageSection != null) DetailsRight.Children.Add(storageSection);

            var b = r.Battery;
            if (b.Present)
            {
                var batSection = Section("Battery", new[]
                {
                    Row("Health", r.BatteryHealth + (b.HealthPercent != null ? "  (exact " + b.HealthPercent.Value.ToString("0.0") + "%)" : "")),
                    Row("Design capacity", Mwh(b.DesignCapacityMWh)),
                    Row("Full charge capacity", Mwh(b.FullChargeCapacityMWh)),
                    Row("Current capacity", Mwh(b.CurrentCapacityMWh) + (b.ChargePercent != null ? "  (" + b.ChargePercent.Value.ToString("0") + "%)" : "")),
                    Row("Cycle count", b.CycleCount?.ToString()),
                    b.BatteryCount > 1 ? Row("Batteries", b.BatteryCount.ToString()) : null,
                    Row("Chemistry", b.Chemistry),
                    Row("Power state", b.PowerState),
                    Row("Source", b.Source),
                });
                if (batSection != null) DetailsRight.Children.Add(batSection);
            }
        }

        private static string Mwh(long? v) => v == null ? "" : v.Value.ToString("N0") + " mWh";

        private static Brush BatteryAccent(double? pct)
        {
            if (pct == null) return null;
            if (pct >= 80) return (Brush)Application.Current.FindResource("PassBrush");
            if (pct >= 60) return (Brush)Application.Current.FindResource("WarnBrush");
            return (Brush)Application.Current.FindResource("FailBrush");
        }

        private static Brush SsdAccent(StorageDevice d)
        {
            if (d == null) return null;
            if (d.HealthPercent.HasValue)
            {
                if (d.HealthPercent.Value >= 80) return (Brush)Application.Current.FindResource("PassBrush");
                if (d.HealthPercent.Value >= 50) return (Brush)Application.Current.FindResource("WarnBrush");
                return (Brush)Application.Current.FindResource("FailBrush");
            }
            if (d.HealthAssessment == "OPTIMAL" || d.HealthAssessment == "GOOD" || d.WindowsHealth == "Healthy")
                return (Brush)Application.Current.FindResource("PassBrush");
            if (d.HealthAssessment == "WARNING" || d.WindowsHealth == "Warning")
                return (Brush)Application.Current.FindResource("WarnBrush");
            if (d.HealthAssessment == "CRITICAL" || d.HealthAssessment == "FAILED" || d.WindowsHealth == "Unhealthy")
                return (Brush)Application.Current.FindResource("FailBrush");
            return null;
        }

        private void AddField(string label, string value, Brush accent = null, Func<TextBlock, Task> onSsdClick = null, string ssdTooltip = null)
        {
            bool isModelNumber = string.Equals(label, "Model Number", StringComparison.OrdinalIgnoreCase);
            bool unknown = value == DetectionResult.Unknown || string.IsNullOrEmpty(value) || value == "-" || value == "—";
            var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(6), MinHeight = 108 };
            if (!string.IsNullOrEmpty(ssdTooltip))
            {
                card.ToolTip = ssdTooltip;
            }
            if (onSsdClick != null)
            {
                card.Cursor = Cursors.Hand;
            }
            var sp = new StackPanel();

            // Header grid with label on left and hover-copy badge on right
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = label.ToUpperInvariant(),
                Style = (Style)FindResource("FieldLabel"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, 0);
            headerGrid.Children.Add(lbl);

            // Copy badge: pre-laid out with initial Opacity = 0 to ensure ZERO layout shift on hover
            bool isCopyable = IsCopyableValue(value);
            if (isCopyable)
            {
                var copyBadge = new Border
                {
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Opacity = 0.0,
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Copy " + label
                };

                var badgePanel = new StackPanel { Orientation = Orientation.Horizontal };
                var copyIcon = new TextBlock
                {
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    Text = "\uE8C8", // Copy icon
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextMuted"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var copyText = new TextBlock
                {
                    Text = "Copy",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextMuted"),
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badgePanel.Children.Add(copyIcon);
                badgePanel.Children.Add(copyText);
                copyBadge.Child = badgePanel;

                DispatcherTimer badgeResetTimer = null;

                copyBadge.MouseEnter += (s, e) =>
                {
                    if (badgeResetTimer == null || !badgeResetTimer.IsEnabled)
                    {
                        copyBadge.Background = (Brush)FindResource("NeutralSoft");
                    }
                };
                copyBadge.MouseLeave += (s, e) =>
                {
                    if (badgeResetTimer == null || !badgeResetTimer.IsEnabled)
                    {
                        copyBadge.Background = Brushes.Transparent;
                    }
                };

                copyBadge.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true; // Crucial: prevent triggering onSsdClick or card click
                    string copyVal = (value ?? "").Trim();
                    if (!IsCopyableValue(copyVal)) return;

                    if (SafeSetClipboard(copyVal))
                    {
                        copyIcon.Text = "\uE73E"; // Checkmark
                        copyIcon.Foreground = (Brush)FindResource("PassBrush");
                        copyText.Text = "Copied";
                        copyText.Foreground = (Brush)FindResource("PassBrush");
                        copyBadge.Background = (Brush)FindResource("PassSoft");
                        copyBadge.Opacity = 1.0;

                        if (badgeResetTimer != null) badgeResetTimer.Stop();
                        badgeResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                        badgeResetTimer.Tick += (ts, te) =>
                        {
                            badgeResetTimer.Stop();
                            copyIcon.Text = "\uE8C8";
                            copyIcon.Foreground = (Brush)FindResource("TextMuted");
                            copyText.Text = "Copy";
                            copyText.Foreground = (Brush)FindResource("TextMuted");
                            copyBadge.Background = Brushes.Transparent;
                            if (!card.IsMouseOver)
                            {
                                copyBadge.Opacity = 0.0;
                            }
                        };
                        badgeResetTimer.Start();
                    }
                };

                card.MouseEnter += (s, e) =>
                {
                    if (badgeResetTimer == null || !badgeResetTimer.IsEnabled)
                    {
                        copyBadge.Opacity = 1.0;
                    }
                };
                card.MouseLeave += (s, e) =>
                {
                    if (badgeResetTimer == null || !badgeResetTimer.IsEnabled)
                    {
                        copyBadge.Opacity = 0.0;
                    }
                };

                Grid.SetColumn(copyBadge, 1);
                headerGrid.Children.Add(copyBadge);
            }

            sp.Children.Add(headerGrid);
            
            string displayText;
            Brush displayBrush;
            if (isModelNumber && unknown)
            {
                displayText = "—";
                displayBrush = (Brush)FindResource("Muted");
            }
            else if (unknown)
            {
                displayText = "Unknown";
                displayBrush = (Brush)FindResource("NeutralBrush");
            }
            else
            {
                displayText = value;
                displayBrush = accent ?? (Brush)FindResource("TextPrimary");
            }

            var tb = new TextBlock
            {
                Text = displayText,
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = displayBrush,
            };
            if (value != null && value.Contains("\n")) tb.FontSize = 20;
            if (onSsdClick != null)
            {
                card.MouseLeftButtonUp += (s, e) => onSsdClick(tb);
            }
            sp.Children.Add(tb);
            card.Child = sp;
            FieldGrid.Children.Add(card);
        }

        private Border Section(string title, UIElement[] rows)
        {
            var validRows = rows.Where(x => x != null).ToList();
            if (validRows.Count == 0) return null;

            var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 14) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var r in validRows) sp.Children.Add(r);
            card.Child = sp;
            return card;
        }

        private UIElement Row(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || BrandCatalog.IsPlaceholder(value) ||
                value == "-" || value == "—" || value == "Unavailable" || value == "Not detected" || value == "None")
                return null;

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

        private void Itms_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null || _busy) return;
            new ItmsSendWindow(_result) { Owner = Window.GetWindow(this) }.ShowDialog();
        }

        private void QrCode_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null || _busy)
            {
                AppLog.Warn("[QR-Diag] QR Code button clicked, but _result is null or _busy=true.");
                return;
            }

            AppLog.Info($"[QR-Diag] Stage 0: QR Code button clicked on DetectView for device '{_result.Brand} {_result.Model}'. Initializing QrCodeWindow...");
            try
            {
                var win = new QrCodeWindow(_result) { Owner = Window.GetWindow(this) };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                AppLog.Error("[QR-Diag] Failed to open QrCodeWindow", ex);
                MessageBox.Show("Failed to open QR Code window: " + ex.Message, "QR Code Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

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

        private DispatcherTimer _fullNameCopyTimer;

        private void CopyFullName_Click(object sender, RoutedEventArgs e)
        {
            string fullName = FullNameValue?.Text;
            if (string.IsNullOrWhiteSpace(fullName)) return;

            if (SafeSetClipboard(fullName.Trim()))
            {
                CopyFullNameIcon.Text = "\uE73E"; // Checkmark
                CopyFullNameLabel.Text = "Copied!";
                CopyFullNameBtn.BorderBrush = (Brush)FindResource("PassBrush");

                if (_fullNameCopyTimer != null) _fullNameCopyTimer.Stop();
                _fullNameCopyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _fullNameCopyTimer.Tick += (s, ev) =>
                {
                    _fullNameCopyTimer.Stop();
                    CopyFullNameIcon.Text = "\uE8C8"; // Copy icon
                    CopyFullNameLabel.Text = "Copy Name";
                    CopyFullNameBtn.BorderBrush = (Brush)FindResource("CardBorder");
                };
                _fullNameCopyTimer.Start();
            }
        }

        private static bool SafeSetClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    System.Threading.Thread.Sleep(30);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Clipboard copy failed", ex);
                    return false;
                }
            }
            return false;
        }

        private static bool IsCopyableValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            if (string.Equals(v, DetectionResult.Unknown, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "Unknown", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "Not detected", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "Unavailable", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "-", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "—", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "None", StringComparison.OrdinalIgnoreCase)) return false;
            if (BrandCatalog.IsPlaceholder(v)) return false;
            return true;
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;
            var r = _result;
            string text = "Brand: " + r.Brand + "\r\nModel: " + r.Model + "\r\nModel Number: " + r.ModelNumber + "\r\nCPU: " + r.Cpu +
                          "\r\nGeneration: " + r.Generation + "\r\nRAM: " + r.Ram + "\r\nStorage: " + r.Storage + "\r\nBattery Health: " + r.BatteryHealth + "\r\nSSD Health: " + r.SsdHealth;
            if (SafeSetClipboard(text))
            {
                NoticeText.Text = "Copied the ITMS fields to the clipboard.";
                Notice.Visibility = Visibility.Visible;
            }
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


