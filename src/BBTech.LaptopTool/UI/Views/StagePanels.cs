using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Software;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.UI.Tests;

namespace BBTech.LaptopTool.UI.Views
{
    /// <summary>Software stage: Windows installation, tracked (started / verified / failed) and verified from the real system state.</summary>
    public sealed class WindowsInstallPanel : Border
    {
        private readonly InspectionManager _mgr;
        private readonly FrameworkElement _view;
        private readonly MainWindow _shell;
        private readonly Func<string> _blocked;
        private readonly Action _changed;

        private static Brush Res(string k) => (Brush)Application.Current.FindResource(k);

        public WindowsInstallPanel(InspectionManager mgr, FrameworkElement view, MainWindow shell, Func<string> blocked, Action changed)
        {
            _mgr = mgr; _view = view; _shell = shell; _blocked = blocked; _changed = changed;
            Style = (Style)Application.Current.FindResource("Card");
            Padding = new Thickness(20, 16, 20, 16);
        }

        public static WindowsInfo ReadWindows()
        {
            var info = new WindowsInfo();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate FROM Win32_OperatingSystem"))
                    foreach (ManagementObject o in s.Get())
                    {
                        info.Edition = (o["Caption"]?.ToString() ?? "").Replace("Microsoft ", "").Trim();
                        info.Version = o["Version"]?.ToString();
                        info.Build = o["BuildNumber"]?.ToString();
                        info.Architecture = o["OSArchitecture"]?.ToString();
                        var raw = o["InstallDate"]?.ToString();
                        if (!string.IsNullOrEmpty(raw)) info.InstallDate = ManagementDateTimeConverter.ToDateTime(raw);
                        break;
                    }
            }
            catch (Exception ex) { AppLog.Warn("Windows information could not be read", ex); }
            return info;
        }

        public void Refresh()
        {
            if (!_mgr.CanSave) { Visibility = Visibility.Collapsed; return; }
            Visibility = Visibility.Visible;
            var win = ReadWindows();
            var history = _mgr.GetTestHistory(LifecycleEvents.WindowsInstall, Departments.Software); // newest first
            var latest = history.FirstOrDefault();

            string tone = latest == null ? "Neutral" : latest.Status == "PASS" ? "Pass" : latest.Status == "FAIL" ? "Fail" : "Warn";
            string state = latest == null ? "Not checked" : latest.Status == "PASS" ? "Ready" : latest.Status == "FAIL" ? "Failed" : "Installing";

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 20, Foreground = Res("Navy"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = "Windows", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Res("TextPrimary"), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new Border
            {
                Background = Res(tone + "Soft"), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = state, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res(tone + "Brush") },
            });
            left.Children.Add(head);
            left.Children.Add(new TextBlock { Text = win.Describe(), Foreground = Res("TextMuted"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            if (latest != null)
                left.Children.Add(new TextBlock
                {
                    Text = latest.OperatorName + " · " + (latest.CompletedAt ?? latest.StartedAt)?.ToString("dd MMM HH:mm") + (string.IsNullOrEmpty(latest.Comment) ? "" : " · " + latest.Comment),
                    Foreground = Res("TextMuted"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
            grid.Children.Add(left);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(Btn("Start install", "BaseButton", "Press before installing Windows. The laptop's history is kept on this USB.", () =>
                Record("TESTING", "Windows installation started. Before: " + win.Describe())));
            buttons.Children.Add(Btn("Verify", "PrimaryButton", "Checks the Windows that is installed now.", () =>
            {
                // The most recent "started" not yet followed by a verdict.
                var open = latest != null && latest.Status == "TESTING" ? latest : null;
                var verdict = WindowsInstallCheck.Evaluate(open?.StartedAt, ReadWindows());
                Record(verdict.Status, verdict.Note);
            }));
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            Child = grid;
        }

        private Button Btn(string text, string style, string tip, Action click)
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.FindResource(style), Padding = new Thickness(14, 6, 14, 6), MinHeight = 34, Margin = new Thickness(8, 0, 0, 0), ToolTip = tip };
            b.Click += (o, e) => click();
            return b;
        }

        private void Record(string status, string note)
        {
            if (!DashboardGuard.CanStartTest(_view, _shell, _blocked(), LifecycleEvents.WindowsInstall, Departments.Software)) return;
            if (DashboardGuard.TrySave(_view, () => _mgr.RecordDepartmentTest(LifecycleEvents.WindowsInstall, "Windows Installation", status, Departments.Software, note)))
            {
                Refresh();
                _changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Final check before delivery: a separate event from the preparation QC, on the same laptop record,
    /// linked to the ITMS order reference when there is one. Result: READY FOR DELIVERY or HOLD (with reason).
    /// </summary>
    public sealed class DeliveryPanel : Border
    {
        private readonly InspectionManager _mgr;
        private readonly FrameworkElement _view;
        private readonly MainWindow _shell;
        private readonly Func<string> _blocked;
        private readonly Action _changed;
        private readonly TextBox _order = new TextBox { MinWidth = 150, Padding = new Thickness(6, 4, 6, 4), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox _reason = new TextBox { MinWidth = 220, Padding = new Thickness(6, 4, 6, 4), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };

        private static Brush Res(string k) => (Brush)Application.Current.FindResource(k);
        private static FontFamily Icons => (FontFamily)Application.Current.FindResource("IconFont");

        private static readonly Dictionary<string, string> Glyphs = new Dictionary<string, string>
        {
            ["DeliveryPhysical"] = "", ["DeliveryDisplay"] = "", ["DeliveryKeyboard"] = "", ["DeliveryCharger"] = "",
            ["DeliveryAccessories"] = "", ["DeliveryWindows"] = "", ["DeliverySoftware"] = "", ["DeliveryCustomerConfig"] = "",
        };

        public DeliveryPanel(InspectionManager mgr, FrameworkElement view, MainWindow shell, Func<string> blocked, Action changed)
        {
            _mgr = mgr; _view = view; _shell = shell; _blocked = blocked; _changed = changed;
            Style = (Style)Application.Current.FindResource("Card");
            Padding = new Thickness(20, 16, 20, 16);
        }

        private bool _checking;

        public void Refresh(LifecycleState s)
        {
            if (s == null || !s.ReadyForItms) { Visibility = Visibility.Collapsed; return; }
            Visibility = Visibility.Visible;
            // Before delivery the hardware is checked again against the QC PASS (a swapped part cancels the PASS).
            if (s.DeliverySnapshot == null && !_checking) RecheckHardware();
            var root = new StackPanel();
            string tone = s.Status == LifecycleStatus.ReadyForDelivery ? "Pass" : s.Status == LifecycleStatus.DeliveryHold ? "Fail" : "Neutral";
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 20, Foreground = Res("Navy"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = "Final check before delivery", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Res("TextPrimary"), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new Border
            {
                Background = Res(tone + "Soft"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Res(tone + "Brush"),
                    Text = s.Status == LifecycleStatus.ReadyForDelivery ? "READY FOR DELIVERY" : s.Status == LifecycleStatus.DeliveryHold ? "HOLD" : "NOT DONE",
                },
            });
            root.Children.Add(head);
            if (s.DeliverySnapshot == null)
                root.Children.Add(new TextBlock { Text = "Checking the hardware again...", Foreground = Res("TextMuted"), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
            if (s.DeliveryDecision != null)
                root.Children.Add(new TextBlock
                {
                    Foreground = Res("TextMuted"), FontSize = 12, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap,
                    Text = s.DeliveryDecision.OperatorName + " · " + s.DeliveryDecision.CompletedAt?.ToString("dd MMM HH:mm") +
                           (string.IsNullOrWhiteSpace(s.DeliveryDecision.Details) ? "" : " · order " + s.DeliveryDecision.Details) +
                           (string.IsNullOrWhiteSpace(s.DeliveryDecision.Comment) ? "" : " · " + s.DeliveryDecision.Comment),
                });

            // Icon-first checklist
            var items = new WrapPanel { Margin = new Thickness(-4, 0, -4, 8) };
            var latest = (s.DeliveryGate?.Latest ?? new List<TestResultRecord>()).ToDictionary(r => r.TestId, r => r);
            foreach (var id in MasterDataService.Requirements.All(LifecycleEvents.DeliveryGroup))
            {
                if (!Enum.TryParse(id, out HardwareTest kind)) continue;
                latest.TryGetValue(id, out var r);
                string t = r == null ? "Neutral" : r.Status == "PASS" ? "Pass" : r.Status == "FAIL" ? "Fail" : "Warn";
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = Glyphs.TryGetValue(id, out var g) ? g : "", FontFamily = Icons, FontSize = 16, Foreground = Res(t + "Brush"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = TestText.Name(kind) + (t == "Pass" ? "  ✓" : t == "Fail" ? "  ✕" : ""), FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
                var b = new Button { Content = sp, Style = (Style)Application.Current.FindResource("BaseButton"), Background = Res(t + "Soft"), BorderBrush = Res(t == "Neutral" ? "CardBorder" : t + "Brush"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(4), MinHeight = 40 };
                b.Click += (o, e) => RunItem(kind);
                items.Children.Add(b);
            }
            root.Children.Add(items);

            var bottom = new WrapPanel();
            bottom.Children.Add(new TextBlock { Text = "Order", Foreground = Res("TextMuted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            if (string.IsNullOrEmpty(_order.Text) && s.DeliveryDecision != null) _order.Text = s.DeliveryDecision.Details ?? "";
            bottom.Children.Add(Wrap(_order));
            bottom.Children.Add(new TextBlock { Text = "Hold reason", Foreground = Res("TextMuted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
            bottom.Children.Add(Wrap(_reason));
            var hold = new Button { Content = "HOLD", Style = (Style)Application.Current.FindResource("FailButton"), MinWidth = 100, Height = 38, Margin = new Thickness(14, 4, 8, 4), FontWeight = FontWeights.Bold };
            hold.Click += (o, e) => Decide(false, s);
            var ready = new Button { Content = "READY FOR DELIVERY", Style = (Style)Application.Current.FindResource("PassButton"), MinWidth = 180, Height = 38, Margin = new Thickness(0, 4, 0, 4), FontWeight = FontWeights.Bold };
            string why = LaptopLifecycle.CheckEvent(s, Departments.Qc, LifecycleEvents.DeliveryDecision, "PASS", MasterDataService.Requirements);
            ready.IsEnabled = why == null; ready.ToolTip = why;
            ToolTipService.SetShowOnDisabled(ready, true);
            ready.Click += (o, e) => Decide(true, s);
            bottom.Children.Add(hold);
            bottom.Children.Add(ready);
            root.Children.Add(bottom);
            Child = root;
        }

        private async void RecheckHardware()
        {
            _checking = true;
            try { await _mgr.CaptureSnapshotAsync(true, "FINAL_CHECK"); }
            catch (Exception ex) { AppLog.Warn("Final-check hardware snapshot failed: " + ex.Message); }
            finally { _checking = false; _changed?.Invoke(); }
        }

        private static UIElement Wrap(TextBox t) => new Border { Child = t, Margin = new Thickness(0, 4, 0, 4) };

        private void RunItem(HardwareTest kind)
        {
            if (!DashboardGuard.CanStartTest(_view, _shell, _blocked(), kind.ToString(), Departments.Qc)) return;
            var host = new TestHostWindow(new TestItem(kind, TestText.Name(kind)), TestPanelFactory.Create(kind)) { Owner = Window.GetWindow(_view) };
            host.ShowDialog();
            if (host.Outcome == null) return;
            string status = host.Outcome.State.ToString().ToUpper();
            if (status == "NOTTESTED") return;
            DashboardGuard.TrySave(_view, () => _mgr.RecordDepartmentTest(kind.ToString(), TestText.Name(kind), status, Departments.Qc, host.Outcome.Notes));
            _changed?.Invoke();
        }

        private void Decide(bool ready, LifecycleState s)
        {
            if (!DashboardGuard.CanStartTest(_view, _shell, _blocked())) return;
            string reason = _reason.Text?.Trim();
            if (!ready && string.IsNullOrEmpty(reason))
            {
                var failed = s.DeliveryGate?.Failed.Select(f => (f.TestName ?? f.TestId) + (string.IsNullOrEmpty(f.Comment) ? "" : ": " + f.Comment)).ToList() ?? new List<string>();
                reason = string.Join("; ", failed);
                if (reason.Length == 0) { Dialogs.Info(Window.GetWindow(_view), "Hold", "Type the reason, or mark the item that failed."); _reason.Focus(); return; }
            }
            if (DashboardGuard.TrySave(_view, () => _mgr.RecordDeliveryDecision(ready, _order.Text, reason)))
            {
                _reason.Text = "";
                _changed?.Invoke();
            }
        }
    }
}
