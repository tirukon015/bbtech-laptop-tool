using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Export;
using BBTech.Core.Models;
using BBTech.Core.Settings;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using BBTech.LaptopTool.Detect;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.UI.Tests;

namespace BBTech.LaptopTool.UI.Views
{
    public sealed class HardwareCardDef
    {
        public HardwareTest Kind { get; set; }
        public string Title { get; set; }
        public string Glyph { get; set; }
        public string Description { get; set; }
    }

    public partial class HardwareDashboardView : UserControl
    {
        private readonly MainWindow _shell;
        private readonly InspectionManager _mgr = new InspectionManager();
        private bool _busy;
        private string _activeHistoryTestId;

        private static readonly List<HardwareCardDef> Cards = new List<HardwareCardDef>
        {
            new HardwareCardDef { Kind = HardwareTest.Battery, Title = "Battery", Glyph = "\uE83F", Description = "Health, wear & cycle count" },
            new HardwareCardDef { Kind = HardwareTest.Storage, Title = "Storage / SSD", Glyph = "\uEDA2", Description = "SMART health, wear & temp" },
            new HardwareCardDef { Kind = HardwareTest.Ram, Title = "RAM", Glyph = "\uE950", Description = "Capacity, speed & slots" },
            new HardwareCardDef { Kind = HardwareTest.Display, Title = "Display", Glyph = "\uE7F4", Description = "Colors, dead pixels & resolution" },
            new HardwareCardDef { Kind = HardwareTest.Keyboard, Title = "Keyboard", Glyph = "\uE765", Description = "Key press detection & layout" },
            new HardwareCardDef { Kind = HardwareTest.Touchpad, Title = "Touchpad", Glyph = "\uEFA5", Description = "Gestures, tracking & buttons" },
            new HardwareCardDef { Kind = HardwareTest.Mouse, Title = "Mouse", Glyph = "", Description = "External mouse & buttons" },
            new HardwareCardDef { Kind = HardwareTest.Speaker, Title = "Audio / Speaker", Glyph = "\uE767", Description = "Stereo output & audio tones" },
            new HardwareCardDef { Kind = HardwareTest.Headphone, Title = "Headphone", Glyph = "", Description = "Headphone jack left & right" },
            new HardwareCardDef { Kind = HardwareTest.Microphone, Title = "Microphone", Glyph = "\uE720", Description = "Voice input & audio recording" },
            new HardwareCardDef { Kind = HardwareTest.Camera, Title = "Camera", Glyph = "\uE722", Description = "Webcam video feed" },
            new HardwareCardDef { Kind = HardwareTest.Usb, Title = "Ports / USB", Glyph = "\uE88E", Description = "USB insertion & port count" },
            new HardwareCardDef { Kind = HardwareTest.WiFi, Title = "Network (Wi-Fi)", Glyph = "\uE701", Description = "Nearby network scanning" },
            new HardwareCardDef { Kind = HardwareTest.PhysicalInspection, Title = "Physical Inspection", Glyph = "\uE9D9", Description = "Chassis, hinges, ports & screws" },
            new HardwareCardDef { Kind = HardwareTest.Bluetooth, Title = "Bluetooth", Glyph = "\uE702", Description = "Radio state & inquiry" },
            new HardwareCardDef { Kind = HardwareTest.OtherInspection, Title = "Other Inspection", Glyph = "\uE71D", Description = "Additional hardware checks" },
        };

        private readonly Dictionary<string, Border> _cardElements = new Dictionary<string, Border>();

        public HardwareDashboardView(MainWindow shell)
        {
            InitializeComponent();
            _life = new LifecyclePanel(_mgr, BBTech.Core.Testing.Departments.Hardware, RefreshCardStatuses);
            LifecycleHost.Content = _life;
            _shell = shell;
            BuildCards();
            Loaded += async (s, e) => await InitializeAsync();
        }

        private void BuildCards()
        {
            CardsContainer.Children.Clear();
            _cardElements.Clear();

            foreach (var card in Cards)
            {
                var border = CreateCardBorder(card);
                _cardElements[card.Kind.ToString()] = border;
                CardsContainer.Children.Add(border);
            }
        }

        private Border CreateCardBorder(HardwareCardDef def)
        {
            var b = new Border
            {
                Style = (Style)FindResource("TestCardBorder"),
                Tag = def,
            };

            b.MouseEnter += (s, e) =>
            {
                b.Background = (Brush)FindResource("NeutralSoft");
                b.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
            };
            b.MouseLeave += (s, e) =>
            {
                b.Background = Brushes.White;
                b.BorderBrush = (Brush)FindResource("CardBorder");
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Row 0: Icon + Status Badge
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconBox = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("NeutralSoft"),
                Child = new TextBlock
                {
                    Text = def.Glyph,
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 16,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            };

            var badge = new Border
            {
                Name = "StatusBadge",
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)FindResource("NeutralSoft"),
                Child = new TextBlock
                {
                    Name = "StatusLabel",
                    Text = "● Ready",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("NeutralBrush"),
                }
            };
            Grid.SetColumn(badge, 1);
            top.Children.Add(iconBox);
            top.Children.Add(badge);
            grid.Children.Add(top);

            // Row 1: Title & Description
            var textSp = new StackPanel { Margin = new Thickness(0, 10, 0, 4) };
            textSp.Children.Add(new TextBlock
            {
                Text = def.Title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            textSp.Children.Add(new TextBlock
            {
                Text = def.Description,
                Style = (Style)FindResource("Muted"),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetRow(textSp, 1);
            grid.Children.Add(textSp);

            // Row 2: Latest Result Info
            var latestSp = new StackPanel { Name = "LatestInfoPanel", Margin = new Thickness(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center };
            var latestText = new TextBlock
            {
                Name = "LatestText",
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 32,
            };
            latestSp.Children.Add(latestText);
            Grid.SetRow(latestSp, 2);
            grid.Children.Add(latestSp);

            // Row 3: Action Buttons (Start Test / Retest + History)
            var actionGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var testBtn = new Button
            {
                Name = "TestBtn",
                Content = "Start Test",
                Style = (Style)FindResource("BaseButton"),
                Padding = new Thickness(10, 5, 10, 5),
                MinHeight = 30,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            testBtn.Click += (s, e) => { e.Handled = true; RunTest(def); };
            actionGrid.Children.Add(testBtn);

            var histBtn = new Button
            {
                Name = "HistBtn",
                Content = "History",
                Style = (Style)FindResource("LinkButton"),
                FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            Grid.SetColumn(histBtn, 1);
            histBtn.Click += (s, e) => { e.Handled = true; ShowHistory(def); };
            actionGrid.Children.Add(histBtn);

            Grid.SetRow(actionGrid, 3);
            grid.Children.Add(actionGrid);

            b.Child = grid;

            b.MouseLeftButtonUp += (s, e) =>
            {
                if (e.OriginalSource is Button || (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null))
                    return;
                RunTest(def);
            };

            return b;
        }

        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null && !(parent is T))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            return parent as T;
        }

        private async Task InitializeAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                DeviceTitle.Text = "Detecting laptop hardware...";
                DeviceSpecs.Text = "Reading system specifications...";
                var readiness = await _mgr.InitializeAsync();
                _saveBlocked = _mgr.SaveBlockedReason(readiness);

                var d = _mgr.Detection;
                if (d != null)
                {
                    string fullName = (d.Brand + " " + d.Model + " " + d.ModelNumber).Replace("Unknown", "").Trim();
                    DeviceTitle.Text = !string.IsNullOrEmpty(fullName) ? fullName : "Detected Laptop";
                    string specs = (!string.IsNullOrEmpty(d.Cpu) ? d.Cpu : "") +
                                   (!string.IsNullOrEmpty(d.Ram) ? "  •  " + d.Ram : "") +
                                   (!string.IsNullOrEmpty(d.Storage) ? "  •  " + d.Storage : "") +
                                   (!string.IsNullOrEmpty(d.System?.SerialNumber) ? "  •  S/N: " + d.System.SerialNumber : "");
                    DeviceSpecs.Text = specs.TrimStart(' ', '•');
                }

                RefreshCardStatuses();
                if (_saveBlocked != null) Dialogs.Info(Window.GetWindow(this), "Results cannot be saved", _saveBlocked);
            }
            catch (Exception ex)
            {
                _saveBlocked = "Laptop detection did not finish. Press Rescan.";
                AppLog.Error("Hardware dashboard initialisation failed", ex);
                DeviceTitle.Text = "Hardware detection incomplete";
                DeviceSpecs.Text = ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private void RefreshCardStatuses()
        {
            _life?.Refresh();
            int passed = 0, failed = 0, tested = 0;

            foreach (var card in Cards)
            {
                if (!_cardElements.TryGetValue(card.Kind.ToString(), out var border)) continue;
                var latest = _mgr.GetLatestTestResult(card.Kind.ToString(), "Hardware");
                var history = _mgr.GetTestHistory(card.Kind.ToString(), "Hardware");

                UpdateCardView(border, latest, history.Count);

                if (latest != null && latest.Status != "NOT TESTED")
                {
                    tested++;
                    if (latest.Status == "PASS") passed++;
                    else if (latest.Status == "FAIL") failed++;
                }
            }

            SummaryText.Text = tested > 0
                ? string.Format("{0} of {1} tests completed ({2} PASS, {3} FAIL)", tested, Cards.Count, passed, failed)
                : string.Format("0 of {0} tests completed", Cards.Count);
        }

        private void UpdateCardView(Border border, TestResultRecord latest, int historyCount)
        {
            var grid = (Grid)border.Child;
            var top = (Grid)grid.Children[0];
            var badge = (Border)top.Children[1];
            var statusLabel = (TextBlock)badge.Child;
            var latestSp = (StackPanel)grid.Children[2];
            var latestText = (TextBlock)latestSp.Children[0];
            var actionGrid = (Grid)grid.Children[3];
            var testBtn = (Button)actionGrid.Children[0];
            var histBtn = (Button)actionGrid.Children[1];

            if (latest == null || latest.Status == "NOT TESTED")
            {
                badge.Background = (Brush)FindResource("NeutralSoft");
                statusLabel.Text = "● Ready";
                statusLabel.Foreground = (Brush)FindResource("NeutralBrush");
                latestText.Text = "Ready to test";
                testBtn.Content = "Start Test";
                histBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                string status = latest.Status.ToUpper();
                testBtn.Content = "Retest";

                if (status == "PASS")
                {
                    badge.Background = (Brush)FindResource("PassSoft");
                    statusLabel.Text = "✓ Passed";
                    statusLabel.Foreground = (Brush)FindResource("PassBrush");
                }
                else if (status == "FAIL")
                {
                    badge.Background = (Brush)FindResource("FailSoft");
                    statusLabel.Text = "✕ Failed";
                    statusLabel.Foreground = (Brush)FindResource("FailBrush");
                }
                else
                {
                    badge.Background = (Brush)FindResource("WarnSoft");
                    statusLabel.Text = "— Skipped";
                    statusLabel.Foreground = (Brush)FindResource("WarnBrush");
                }

                string time = latest.CompletedAt.HasValue ? latest.CompletedAt.Value.ToString("hh:mm tt") : "";
                string op = !string.IsNullOrEmpty(latest.OperatorName) ? latest.OperatorName : "Technician";
                string note = !string.IsNullOrEmpty(latest.Comment) ? " • " + latest.Comment : "";
                latestText.Text = string.Format("{0} at {1}{2}", op, time, note);

                if (historyCount > 0)
                {
                    histBtn.Content = "History (" + historyCount + ")";
                    histBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    histBtn.Visibility = Visibility.Collapsed;
                }
            }
        }

        /// <summary>Why results cannot be saved for this laptop (null when they can).</summary>
        private string _saveBlocked = "Laptop detection has not finished yet.";

        private LifecyclePanel _life;

        private void RunTest(HardwareCardDef def)
        {
            if (!DashboardGuard.CanStartTest(this, _shell, _saveBlocked, def.Kind.ToString(), "Hardware")) return;

            try
            {
                var item = new TestItem(def.Kind, def.Description);
                var panel = TestPanelFactory.Create(def.Kind);
                var host = new TestHostWindow(item, panel) { Owner = Window.GetWindow(this) };
                host.ShowDialog();

                var outcome = host.Outcome;
                if (outcome != null)
                {
                    string status = outcome.State.ToString().ToUpper();
                    if (status == "NOTTESTED") status = "NOT TESTED";

                    // Save result immediately
                    DashboardGuard.TrySave(this, () => _mgr.RecordDepartmentTest(def.Kind.ToString(), def.Title, status, "Hardware", outcome.Notes));
                    RefreshCardStatuses();

                    if (HistoryFlyout.Visibility == Visibility.Visible && _activeHistoryTestId == def.Kind.ToString())
                    {
                        ShowHistory(def);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Hardware test execution failed: " + def.Title, ex);
                Dialogs.Info(Window.GetWindow(this), "Test Error", "Could not run test: " + ex.Message);
            }
        }

        private void AllHistory_Click(object sender, RoutedEventArgs e)
        {
            _activeHistoryTestId = null;
            HistoryTitle.Text = "Hardware Testing — Device Test History";
            var history = new List<TestResultRecord>();
            foreach (var card in Cards)
            {
                var h = _mgr.GetTestHistory(card.Kind.ToString(), "Hardware");
                if (h != null && h.Count > 0)
                {
                    history.AddRange(h);
                }
            }

            var sp = new StackPanel();
            if (history.Count == 0)
            {
                sp.Children.Add(new TextBlock { Text = "No hardware test records recorded for this device yet.", Style = (Style)FindResource("Muted") });
            }
            else
            {
                int num = history.Count;
                foreach (var rec in history.OrderByDescending(r => r.CompletedAt))
                {
                    var item = CreateHistoryRow(rec, num);
                    sp.Children.Add(item);
                    num--;
                }
            }

            DisplayHistoryPanel(sp);
        }

        private void ShowHistory(HardwareCardDef def)
        {
            _activeHistoryTestId = def.Kind.ToString();
            HistoryTitle.Text = def.Title + " — Test History";
            var history = _mgr.GetTestHistory(def.Kind.ToString(), "Hardware");

            var sp = new StackPanel();
            if (history == null || history.Count == 0)
            {
                sp.Children.Add(new TextBlock { Text = "No test records found for this component.", Style = (Style)FindResource("Muted") });
            }
            else
            {
                int num = history.Count;
                foreach (var rec in history)
                {
                    var item = CreateHistoryRow(rec, num);
                    sp.Children.Add(item);
                    num--;
                }
            }

            DisplayHistoryPanel(sp);
        }

        private void DisplayHistoryPanel(StackPanel content)
        {
            HistoryFlyout.Visibility = Visibility.Visible;
            var parent = (StackPanel)HistoryFlyout.Child;
            if (parent.Children.Contains(HistoryList))
            {
                parent.Children.Remove(HistoryList);
            }
            while (parent.Children.Count > 2)
            {
                parent.Children.RemoveAt(2);
            }
            parent.Children.Add(content);
        }

        private UIElement CreateHistoryRow(TestResultRecord rec, int number)
        {
            var border = new Border
            {
                Background = (Brush)FindResource("CardBackground"),
                BorderBrush = (Brush)FindResource("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Run #
            var runText = new TextBlock
            {
                Text = "Run #" + number,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(runText);

            // Status Badge
            string status = rec.Status?.ToUpper() ?? "NOT TESTED";
            string statusDisplay = status == "PASS" ? "✓ Passed" : status == "FAIL" ? "✕ Failed" : status;
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = status == "PASS" ? (Brush)FindResource("PassSoft") : status == "FAIL" ? (Brush)FindResource("FailSoft") : (Brush)FindResource("WarnSoft"),
                Child = new TextBlock
                {
                    Text = statusDisplay,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Foreground = status == "PASS" ? (Brush)FindResource("PassBrush") : status == "FAIL" ? (Brush)FindResource("FailBrush") : (Brush)FindResource("WarnBrush"),
                }
            };
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);

            // Date & Technician
            string dateStr = rec.CompletedAt.HasValue ? rec.CompletedAt.Value.ToString("yyyy-MM-dd HH:mm") : "Unknown time";
            var techText = new TextBlock
            {
                Text = rec.OperatorName + " • " + dateStr,
                FontSize = 12,
                Foreground = (Brush)FindResource("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(techText, 2);
            grid.Children.Add(techText);

            // Notes / details
            var notesText = new TextBlock
            {
                Text = !string.IsNullOrEmpty(rec.Comment) ? rec.Comment : "",
                FontSize = 12,
                Foreground = (Brush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(notesText, 3);
            grid.Children.Add(notesText);

            // Admin Delete Button
            var delBtn = new Button
            {
                Content = "Delete (Admin)",
                Style = (Style)FindResource("LinkButton"),
                Foreground = (Brush)FindResource("FailBrush"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Authorized administrators only: delete this duplicate/unnecessary record",
            };
            delBtn.Click += (s, e) => AdminDeleteRecord(rec);
            // Convenience only: the service refuses deletes from anyone without records.delete.
            if (SettingsService.Store.CurrentOperator?.CanDeleteRecords != true) delBtn.Visibility = Visibility.Collapsed;
            Grid.SetColumn(delBtn, 4);
            grid.Children.Add(delBtn);

            border.Child = grid;
            return border;
        }

        private void AdminDeleteRecord(TestResultRecord rec)
        {
            var owner = Window.GetWindow(this);
            var actor = SettingsService.Store.CurrentOperator;
            string why = BBTech.Core.Security.ResultAuthorization.CheckDelete(actor, rec.TestId);
            if (why != null) { Dialogs.Info(owner, "Delete Record", why); return; }
            if (!AdminGate.Unlock(owner)) return;

            if (Dialogs.Choice(owner, "Delete Record", "Delete test record?",
                    "Permanently delete this " + rec.TestName + " test record (" + rec.Status + " by " + rec.OperatorName + ")?",
                    "Delete", "Cancel") != 0) return;

            bool success = false;
            if (!DashboardGuard.TrySave(this, () => success = _mgr.DeleteTestResult(rec, actor))) return;
            if (success)
            {
                RefreshCardStatuses();
                var card = Cards.FirstOrDefault(c => c.Kind.ToString() == rec.TestId);
                if (card != null) ShowHistory(card);
            }
            else
            {
                Dialogs.Info(owner, "Delete Error", "Could not delete this test record.");
            }
        }

        private void CloseHistory_Click(object sender, RoutedEventArgs e)
        {
            HistoryFlyout.Visibility = Visibility.Collapsed;
            _activeHistoryTestId = null;
        }

        private async void Rescan_Click(object sender, RoutedEventArgs e)
        {
            await InitializeAsync();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var results = _mgr.GetLatestDepartmentResults("Hardware");
                if (results.Count == 0)
                {
                    Dialogs.Info(Window.GetWindow(this), "Export", "No test results recorded to export yet.");
                    return;
                }
                ProcessRunner.OpenShell(AppPaths.Ensure(AppPaths.DataDir));
            }
            catch (Exception ex)
            {
                Dialogs.Info(Window.GetWindow(this), "Export", "Could not open exports folder: " + ex.Message);
            }
        }
    }
}
