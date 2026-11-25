using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Testing;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.UI.Views
{
    /// <summary>
    /// The same strip on every department screen: where this laptop is in the BBTech lifecycle
    /// (Hardware → Software → QC → Ready), who did each stage and when, why it was returned, and the one
    /// action this department can take next (hand over). Icon first, few words.
    /// </summary>
    public sealed class LifecyclePanel : Border
    {
        private readonly InspectionManager _mgr;
        private readonly string _department;
        private readonly Action _changed;
        private readonly StackPanel _root = new StackPanel();

        private static Brush Res(string k) => (Brush)Application.Current.FindResource(k);
        private static FontFamily Icons => (FontFamily)Application.Current.FindResource("IconFont");

        public LifecyclePanel(InspectionManager mgr, string department, Action changed)
        {
            _mgr = mgr; _department = department; _changed = changed;
            Style = (Style)Application.Current.FindResource("Card");
            Padding = new Thickness(18, 14, 18, 14);
            Child = _root;
        }

        public LifecycleState State { get; private set; }
        private bool _snapshotStarted;

        /// <summary>Background capture of the technical record (identity, inventory, measured health); never blocks the technician.</summary>
        private async void CaptureSnapshot()
        {
            try { await _mgr.CaptureSnapshotAsync(_department == Departments.Qc, _department == Departments.Qc ? "QC_START" : "PERIODIC"); Refresh(); }
            catch (Exception ex) { BBTech.LaptopTool.Logging.AppLog.Warn("Snapshot capture failed: " + ex.Message); }
        }

        public void Refresh()
        {
            _root.Children.Clear();
            if (!_mgr.CanSave) { Visibility = Visibility.Collapsed; return; }
            Visibility = Visibility.Visible;
            var s = State = _mgr.Lifecycle();
            if (!_snapshotStarted) { _snapshotStarted = true; CaptureSnapshot(); }

            // ---- row 1: status + actions
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string tone = s.Status == LifecycleStatus.HardwareRework || s.Status == LifecycleStatus.SoftwareRework || s.Status == LifecycleStatus.QcRework || s.Status == LifecycleStatus.DeliveryHold ? "Fail"
                        : s.ReadyForItms ? "Pass" : "Neutral";
            var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            status.Children.Add(new Border
            {
                Background = Res(tone + "Soft"), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 5, 12, 5),
                Child = new TextBlock { Text = s.Label.ToUpperInvariant(), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Res(tone + "Brush") },
            });
            if (_mgr.RestoredRecords > 0)
                status.Children.Add(new TextBlock { Text = "History restored", Foreground = Res("TextMuted"), FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = _mgr.RestoredRecords + " earlier record(s) for this laptop were restored from the USB or the server." });
            top.Children.Add(status);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var historyBtn = new Button { Style = (Style)Application.Current.FindResource("BaseButton"), Padding = new Thickness(12, 6, 12, 6), MinHeight = 34, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Everything that happened to this laptop" };
            historyBtn.Content = IconText("", "History");
            historyBtn.Click += (o, e) => new TimelineWindow(_mgr) { Owner = Window.GetWindow(this) }.ShowDialog();
            actions.Children.Add(historyBtn);
            var evidenceBtn = new Button { Style = (Style)Application.Current.FindResource("BaseButton"), Padding = new Thickness(12, 6, 12, 6), MinHeight = 34, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Real hardware data and measured health of this laptop" };
            evidenceBtn.Content = IconText("\uE9D9", "Details");
            evidenceBtn.Click += (o, e) => new EvidenceWindow(_mgr) { Owner = Window.GetWindow(this) }.ShowDialog();
            actions.Children.Add(evidenceBtn);

            if (_department == Departments.Hardware || _department == Departments.Software)
            {
                bool hw = _department == Departments.Hardware;
                string handoverId = hw ? LifecycleEvents.HardwareHandover : LifecycleEvents.SoftwareHandover;
                bool alreadyDone = hw ? s.HardwareReady : s.SoftwareReady;
                string why = alreadyDone ? "Already handed over." : LaptopLifecycle.CheckEvent(s, _department, handoverId, "PASS", MasterDataService.Requirements);
                var handBtn = new Button
                {
                    Style = (Style)Application.Current.FindResource(alreadyDone ? "BaseButton" : "PassButton"), Padding = new Thickness(14, 6, 14, 6), MinHeight = 34,
                    IsEnabled = why == null, ToolTip = why ?? "All required work is done.",
                    Content = IconText(alreadyDone ? "" : "", alreadyDone ? (hw ? "Sent to Software" : "Sent to QC") : (hw ? "Send to Software" : "Send to QC")),
                };
                ToolTipService.SetShowOnDisabled(handBtn, true);
                handBtn.Click += (o, e) => { if (DashboardGuard.TrySave(this, () => _mgr.HandOver(_department))) { Refresh(); _changed?.Invoke(); } };
                actions.Children.Add(handBtn);
            }
            Grid.SetColumn(actions, 1);
            top.Children.Add(actions);
            _root.Children.Add(top);

            // ---- row 2: the four stages
            var steps = new UniformGrid2 { Margin = new Thickness(0, 12, 0, 0) };
            steps.Add(Step("", "Hardware", StageTone(s.HardwareReady, s.Status == LifecycleStatus.HardwareRework, LifecycleStatus.Owner(s.Status) == Departments.Hardware),
                s.Gate.Hardware, s.HardwareHandover));
            steps.Add(Step("", "Software", StageTone(s.SoftwareReady, s.Status == LifecycleStatus.SoftwareRework, LifecycleStatus.Owner(s.Status) == Departments.Software),
                s.Gate.Software, s.SoftwareHandover));
            steps.Add(Step("", "QC", StageTone(s.ReadyForItms, s.Status == LifecycleStatus.QcRework, s.AtQcStage && !s.ReadyForItms), s.Gate.QcChecklist, s.QcDecision));
            steps.Add(Step("", s.Status == LifecycleStatus.ReadyForDelivery ? "Delivery" : "Ready",
                s.Status == LifecycleStatus.ReadyForDelivery || s.ReadyForItms && s.Status != LifecycleStatus.DeliveryHold ? "Pass" : s.Status == LifecycleStatus.DeliveryHold ? "Fail" : "Neutral",
                null, s.DeliveryDecision ?? (s.ReadyForItms ? s.QcDecision : null)));
            _root.Children.Add(steps);

            // ---- a part was swapped after QC PASS
            if (s.HardwareChangedAfterQc != null)
                _root.Children.Add(Banner("Fail", "\uE7BA", "Hardware changed after QC PASS: " + s.HardwareChangedAfterQc.Comment + ". QC must check again.",
                    When(s.HardwareChangedAfterQc.CompletedAt)));

            // ---- returned by QC
            if (s.PendingReturn != null)
                _root.Children.Add(Banner("Fail", "", "Returned by QC to " + s.PendingReturnTarget + ": " + s.PendingReturn.Comment,
                    s.PendingReturn.OperatorName + ", " + When(s.PendingReturn.CompletedAt)));

            // ---- earlier problems (QC must see that a laptop was repaired, not "always perfect")
            if (_department == Departments.Qc && s.PreviousIssues.Count > 0)
            {
                var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                list.Children.Add(new TextBlock { Text = "Fixed earlier (" + s.PreviousIssues.Count + ")", FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Res("TextMuted"), Margin = new Thickness(0, 0, 0, 4) });
                foreach (var i in s.PreviousIssues)
                    list.Children.Add(new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 1, 0, 1),
                        Text = string.Format("{0} · {1}: failed {2} ({3}){4} → {5}passed {6} ({7})", i.Department, i.TestName, When(i.FailedAt), i.FailedBy,
                            string.IsNullOrWhiteSpace(i.FailureNote) ? "" : " \"" + i.FailureNote + "\"",
                            string.IsNullOrWhiteSpace(i.RepairNote) ? "" : "fixed: " + i.RepairNote + " → ", When(i.PassedAt), i.PassedBy),
                    });
                _root.Children.Add(list);
            }
        }

        private static string StageTone(bool done, bool rework, bool current) => rework ? "Fail" : done ? "Pass" : current ? "Warn" : "Neutral";

        private static string When(DateTime? t) => t.HasValue ? t.Value.ToString(t.Value.Date == DateTime.Today ? "HH:mm" : "dd MMM HH:mm") : "";

        private static UIElement IconText(string glyph, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            sp.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        private static Border Step(string glyph, string name, string tone, DepartmentStatus dept, BBTech.Core.Storage.TestResultRecord evt)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(8), Background = Brushes.White, BorderBrush = Res(tone + "Brush"), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 17, Foreground = Res(tone + "Brush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            string mark = tone == "Pass" ? "  ✓" : tone == "Fail" ? "  ✕" : "";
            text.Children.Add(new TextBlock { Text = name + mark, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = Res(tone == "Neutral" ? "TextMuted" : "TextPrimary") });
            string who = evt != null ? evt.OperatorName + " · " + When(evt.CompletedAt ?? evt.StartedAt)
                       : dept != null && dept.Started ? string.Join(", ", dept.Operators) + " · " + dept.Passed.Count + "/" + dept.Required.Count
                       : "—";
            text.Children.Add(new TextBlock { Text = who, FontSize = 11, Foreground = Res("TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(text);
            return new Border { Background = Res(tone + "Soft"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 8, 0), Child = sp };
        }

        internal static Border Banner(string tone, string glyph, string title, string sub)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 18, Foreground = Res(tone + "Brush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextPrimary") });
            if (!string.IsNullOrEmpty(sub)) t.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Res("TextMuted") });
            sp.Children.Add(t);
            return new Border { Background = Res(tone + "Soft"), BorderBrush = Res(tone + "Brush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 10, 0, 0), Child = sp };
        }

        /// <summary>Equal-width cells that wrap to two rows on narrow / highly scaled screens.</summary>
        private sealed class UniformGrid2 : WrapPanel
        {
            public UniformGrid2() { SizeChanged += (o, e) => { double w = ActualWidth < 620 ? ActualWidth / 2 : ActualWidth / 4; foreach (FrameworkElement c in Children) c.Width = Math.Max(140, w - 1); }; }
            public void Add(UIElement e) => Children.Add(e);
        }
    }

    /// <summary>"Laptop → Timeline": the complete history of this physical laptop, oldest first.</summary>
    public sealed class TimelineWindow : Window
    {
        private static Brush Res(string k) => (Brush)Application.Current.FindResource(k);

        public TimelineWindow(InspectionManager mgr)
        {
            Title = "Laptop history - BBTech Laptop Tool";
            FontFamily = new FontFamily("Segoe UI");
            Background = Res("AppBackground");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            MinWidth = 520; MinHeight = 380;
            MainWindow.FitToScreen(this, 880, 680);

            var state = mgr.Lifecycle();
            var entries = mgr.Timeline();
            var root = new StackPanel { Margin = new Thickness(20) };
            var d = mgr.Device;
            root.Children.Add(new TextBlock { Text = (d?.DisplayName ?? "Laptop") + "   ·   " + state.Label, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Res("TextPrimary") });
            root.Children.Add(new TextBlock
            {
                Text = "Serial " + (string.IsNullOrEmpty(d?.SerialNumber) ? "UNKNOWN" : d.SerialNumber) + "   ·   " + entries.Count + " events   ·   returned by QC " + state.ReturnCount + " time(s)",
                Foreground = Res("TextMuted"), FontSize = 12, Margin = new Thickness(0, 2, 0, 14),
            });

            string lastDay = null;
            foreach (var e in entries)
            {
                string day = e.At?.ToString("dd MMM yyyy") ?? "";
                if (day != lastDay) { root.Children.Add(new TextBlock { Text = day, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Res("TextMuted"), Margin = new Thickness(0, 8, 0, 4) }); lastDay = day; }

                string tone = e.Result == "PASS" || e.Result == "READY" || e.Result == "READY FOR DELIVERY" || e.Result == "FIXED" ? "Pass"
                            : e.Result == "FAIL" || e.Result == "RETURNED" || e.Result == "HOLD" ? "Fail" : e.Result == "SKIPPED" ? "Warn" : "Neutral";
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.Children.Add(new TextBlock { Text = e.At?.ToString("HH:mm") ?? "", FontSize = 12, Foreground = Res("TextMuted"), VerticalAlignment = VerticalAlignment.Top });
                var dept = new TextBlock { Text = e.Department, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res("TextMuted") };
                Grid.SetColumn(dept, 1); row.Children.Add(dept);

                var mid = new StackPanel();
                bool stage = e.Kind != "TEST";
                mid.Children.Add(new TextBlock { Text = e.Title, FontSize = stage ? 13 : 12, FontWeight = stage ? FontWeights.Bold : FontWeights.Normal, Foreground = Res("TextPrimary"), TextWrapping = TextWrapping.Wrap });
                string sub = string.Join("   ·   ", new[] { e.Operator, e.Notes, e.Evidence }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (sub.Length > 0) mid.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Res("TextMuted"), TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(mid, 2); row.Children.Add(mid);

                var badge = new Border
                {
                    Background = Res(tone + "Soft"), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = e.Result, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res(tone + "Brush") },
                };
                Grid.SetColumn(badge, 3); row.Children.Add(badge);
                root.Children.Add(row);
            }
            if (entries.Count == 0) root.Children.Add(new TextBlock { Text = "Nothing recorded for this laptop yet.", Foreground = Res("TextMuted") });
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        }
    }

    /// <summary>
    /// "Laptop → Details": the technical evidence behind the results. Real identifiers and raw measured values
    /// with their source, so QC never has to take "SSD is good" on trust. Unknown values say UNKNOWN.
    /// </summary>
    public sealed class EvidenceWindow : Window
    {
        private static Brush Res(string k) => (Brush)Application.Current.FindResource(k);

        private static readonly string[] Order = { "identity", "bios", "chassis", "baseboard", "firmware", "tpm", "cpu", "memory", "ram_module", "storage", "battery",
                                                   "gpu", "display", "network", "bluetooth", "audio", "audio_endpoint", "camera", "windows", "office", "driver_health", "driver" };
        private static readonly Dictionary<string, string> Titles = new Dictionary<string, string>
        {
            ["identity"] = "Identity", ["bios"] = "BIOS", ["chassis"] = "Chassis", ["baseboard"] = "Motherboard", ["firmware"] = "Boot / Secure Boot", ["tpm"] = "TPM",
            ["cpu"] = "Processor", ["memory"] = "Memory", ["ram_module"] = "RAM modules", ["storage"] = "Storage and health", ["battery"] = "Battery", ["gpu"] = "Graphics",
            ["display"] = "Display", ["network"] = "Network adapters", ["bluetooth"] = "Bluetooth", ["audio"] = "Audio", ["audio_endpoint"] = "Audio endpoints", ["camera"] = "Camera",
            ["windows"] = "Windows", ["office"] = "Office", ["driver_health"] = "Driver problems", ["driver"] = "Key drivers",
        };

        public EvidenceWindow(InspectionManager mgr)
        {
            Title = "Laptop details - BBTech Laptop Tool";
            FontFamily = new FontFamily("Segoe UI");
            Background = Res("AppBackground");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            MinWidth = 520; MinHeight = 380;
            MainWindow.FitToScreen(this, 900, 700);

            var inv = mgr.LatestInventory();
            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock { Text = mgr.Device?.DisplayName ?? "Laptop", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Res("TextPrimary") });
            if (inv == null)
            {
                root.Children.Add(new TextBlock { Text = "The technical details are still being read. Close this window and open it again in a moment.", Foreground = Res("TextMuted"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
                Content = root;
                return;
            }
            root.Children.Add(new TextBlock { Text = "Read " + inv.CapturedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm") + "   ·   values the laptop does not report are shown as UNKNOWN", Foreground = Res("TextMuted"), FontSize = 12, Margin = new Thickness(0, 2, 0, 12) });

            foreach (var cat in Order.Concat(inv.Items.Select(i => i.Category).Distinct().Where(c => !Order.Contains(c))))
            {
                var items = inv.Of(cat).ToList();
                if (items.Count == 0) continue;
                var card = new StackPanel();
                card.Children.Add(new TextBlock { Text = Titles.TryGetValue(cat, out var t) ? t : cat, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Res("TextPrimary"), Margin = new Thickness(0, 0, 0, 6) });
                foreach (var item in items)
                {
                    if (items.Count > 1 || cat == "storage" || cat == "ram_module")
                        card.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 6, 0, 2), TextWrapping = TextWrapping.Wrap });
                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    int row = 0;
                    foreach (var v in item.Values)
                    {
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        var k = new TextBlock { Text = v.Key.Replace('_', ' '), Foreground = Res("TextMuted"), FontSize = 12, Margin = new Thickness(0, 1, 8, 1) };
                        bool unknown = v.Value == BBTech.Core.Models.InventoryItem.UnknownValue;
                        var val = new TextBlock { Text = v.Value, FontSize = 12, Foreground = Res(unknown ? "TextMuted" : "TextPrimary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                        Grid.SetRow(k, row); Grid.SetRow(val, row); Grid.SetColumn(val, 1);
                        grid.Children.Add(k); grid.Children.Add(val);
                        row++;
                    }
                    card.Children.Add(grid);
                    card.Children.Add(new TextBlock { Text = "Source: " + item.Source + "   ·   " + item.Confidence, FontSize = 10, Foreground = Res("TextMuted"), Margin = new Thickness(0, 2, 0, 0) });
                }
                root.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Card"), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 10), Child = card });
            }
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        }
    }
}
