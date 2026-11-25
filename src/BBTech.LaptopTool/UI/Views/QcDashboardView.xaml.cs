using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Models;
using BBTech.Core.Settings;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.UI.Tests;

namespace BBTech.LaptopTool.UI.Views
{
    public sealed class QcCardDef
    {
        public HardwareTest Kind { get; set; }
        public string Title { get; set; }
        public string Glyph { get; set; }
        public string Description { get; set; }
    }

    public partial class QcDashboardView : UserControl
    {
        private readonly MainWindow _shell;
        private readonly InspectionManager _mgr = new InspectionManager();
        private bool _busy;

        /// <summary>Icon + two-word hint per checklist item (icon first: usable with little English).</summary>
        private static readonly Dictionary<HardwareTest, string[]> Look = new Dictionary<HardwareTest, string[]>
        {
            [HardwareTest.Display] = new[] { "", "Screen" },
            [HardwareTest.Keyboard] = new[] { "", "Keys" },
            [HardwareTest.Touchpad] = new[] { "", "Touchpad" },
            [HardwareTest.Mouse] = new[] { "", "Mouse" },
            [HardwareTest.Battery] = new[] { "", "Battery" },
            [HardwareTest.Storage] = new[] { "", "SSD" },
            [HardwareTest.Camera] = new[] { "", "Camera" },
            [HardwareTest.Speaker] = new[] { "", "Sound" },
            [HardwareTest.Microphone] = new[] { "", "Microphone" },
            [HardwareTest.WiFi] = new[] { "", "Wi-Fi" },
            [HardwareTest.Bluetooth] = new[] { "", "Bluetooth" },
            [HardwareTest.Usb] = new[] { "", "Ports" },
            [HardwareTest.PhysicalInspection] = new[] { "", "Body" },
            [HardwareTest.OtherInspection] = new[] { "", "Other" },
            [HardwareTest.QcWindows] = new[] { "", "Windows" },
            [HardwareTest.QcSoftware] = new[] { "", "Software" },
            [HardwareTest.QcActivation] = new[] { "", "Activation" },
            [HardwareTest.QcAccessories] = new[] { "", "Accessories" },
            [HardwareTest.QcOverall] = new[] { "", "Overall" },
        };

        /// <summary>The QC checklist is configuration (master data), not a fixed list in code.</summary>
        private static List<QcCardDef> BuildDefs()
        {
            var list = new List<QcCardDef>();
            foreach (var id in MasterDataService.Requirements.All(Departments.Qc))
            {
                if (!Enum.TryParse(id, out HardwareTest kind)) continue;
                Look.TryGetValue(kind, out var look);
                list.Add(new QcCardDef { Kind = kind, Title = TestText.Name(kind) == "Sound" ? "Speaker" : TestText.Name(kind), Glyph = look?[0] ?? "", Description = look?[1] ?? "" });
            }
            return list;
        }

        private readonly List<QcCardDef> Cards = BuildDefs();
        private LifecyclePanel _life;
        private DeliveryPanel _delivery;

        private readonly Dictionary<string, Border> _cardElements = new Dictionary<string, Border>();

        public QcDashboardView(MainWindow shell)
        {
            InitializeComponent();
            _shell = shell;
            _life = new LifecyclePanel(_mgr, Departments.Qc, RefreshAll);
            LifecycleHost.Content = _life;
            _delivery = new DeliveryPanel(_mgr, this, shell, () => _saveBlocked, RefreshAll);
            DeliveryHost.Content = _delivery;
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

        private Border CreateCardBorder(QcCardDef def)
        {
            var b = new Border
            {
                Style = (Style)FindResource("QcCardBorder"),
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

            // Top: Icon + Badge
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconBox = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("NeutralSoft"),
                Child = new TextBlock
                {
                    Text = def.Glyph,
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 15,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            };
            top.Children.Add(iconBox);

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
            top.Children.Add(badge);
            grid.Children.Add(top);

            // Title & Description
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

            // Latest Result Info
            var latestSp = new StackPanel { Margin = new Thickness(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center };
            var latestText = new TextBlock
            {
                Name = "LatestText",
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            latestSp.Children.Add(latestText);
            Grid.SetRow(latestSp, 2);
            grid.Children.Add(latestSp);

            // Action Button
            var actionBtn = new Button
            {
                Content = "Inspect",
                Style = (Style)FindResource("BaseButton"),
                Padding = new Thickness(10, 5, 10, 5),
                MinHeight = 30,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            actionBtn.Click += (s, e) => { e.Handled = true; RunInspection(def); };
            Grid.SetRow(actionBtn, 3);
            grid.Children.Add(actionBtn);

            b.Child = grid;

            b.MouseLeftButtonUp += (s, e) =>
            {
                if (e.OriginalSource is Button || (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null))
                    return;
                RunInspection(def);
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
                DeviceTitle.Text = "Identifying laptop hardware...";
                DeviceSpecs.Text = "Reading specifications...";
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

                LoadPreparationStatus();
                RefreshCardStatuses();
                RefreshFinalStatus();
                if (_saveBlocked != null) Dialogs.Info(Window.GetWindow(this), "Results cannot be saved", _saveBlocked);
            }
            catch (Exception ex)
            {
                _saveBlocked = "Laptop detection did not finish. Press Rescan.";
                AppLog.Error("QC dashboard initialisation failed", ex);
                DeviceTitle.Text = "QC identification incomplete";
                DeviceSpecs.Text = ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// Shows QC what Hardware and Software did on THIS laptop (latest result per test, who, when),
        /// what is missing or failed, and whether a QC PASS is possible. History stays in the journal.
        /// </summary>
        private void LoadPreparationStatus()
        {
            HwResultsList.Children.Clear();
            QcGateReport report;
            try { report = _mgr.CanSave ? _mgr.EvaluateQcGate() : null; }
            catch (Exception ex) { AppLog.Error("QC gate evaluation failed", ex); report = null; }

            if (report == null)
            {
                SetHandoffTone("NeutralSoft", "CardBorder", "NeutralBrush", "");
                HwStatusHeading.Text = "Preparation results unavailable";
                HwStatusDetail.Text = _saveBlocked ?? "This laptop's records could not be read.";
                ToggleHwResultsBtn.Visibility = Visibility.Collapsed;
                FinalPassBtn.IsEnabled = false;
                return;
            }

            _life.Refresh();
            var state = _life.State ?? _mgr.Lifecycle();
            _delivery.Refresh(state);
            int issues = report.Hardware.Failed.Count + report.Software.Failed.Count + report.QcChecklist.Failed.Count;
            bool ready = state.ReadyForItms || report.CanPass;
            HwStatusHeading.FontSize = 22;
            HwStatusHeading.Text = state.ReadyForItms ? "READY" : report.CanPass ? "READY FOR QC PASS" : "NOT READY";
            string Mark(DepartmentStatus d, bool ok) => (ok && d.Missing.Count == 0 && !d.Failed.Any(f => d.Required.Contains(f.TestId)) ? "✓" : "✕");
            string summary = "Hardware " + Mark(report.Hardware, state.HardwareReady) + "      Software " + Mark(report.Software, state.SoftwareReady) +
                             "      QC checklist " + report.QcChecklist.Passed.Count(r => report.QcChecklist.Required.Contains(r.TestId)) + "/" + report.QcChecklist.Required.Count +
                             "      Issues " + issues;
            if (ready)
            {
                SetHandoffTone("PassSoft", "PassBrush", "PassBrush", "");
                HwStatusDetail.Text = summary;
            }
            else
            {
                bool anyFail = issues > 0 || state.PendingReturn != null;
                SetHandoffTone(anyFail ? "FailSoft" : "WarnSoft", anyFail ? "FailBrush" : "WarnBrush", anyFail ? "FailBrush" : "WarnBrush", "");
                HwStatusDetail.Text = summary + "\n" + string.Join("  •  ", report.BlockingReasons);
            }
            HwStatusDetail.TextWrapping = TextWrapping.Wrap;
            ToggleHwResultsBtn.Visibility = Visibility.Visible;
            FinalPassBtn.IsEnabled = report.CanPass;
            FinalPassBtn.ToolTip = report.CanPass ? null : "Complete or fix the items listed above first.";

            AddDepartmentRows(report.Hardware, report.Hardware.Required);
            AddDepartmentRows(report.Software, report.Software.Required);
        }

        private static string DeptLine(DepartmentStatus d)
        {
            if (!d.Started) return "not started";
            string by = d.Operators.Count > 0 ? " (by " + string.Join(", ", d.Operators) + ")" : "";
            return d.Summary.ToLowerInvariant() + by;
        }

        private void SetHandoffTone(string background, string border, string icon, string glyph)
        {
            HwHandoffBox.Background = (Brush)FindResource(background);
            HwHandoffBox.BorderBrush = (Brush)FindResource(border);
            HwHandoffIcon.Foreground = (Brush)FindResource(icon);
            HwHandoffIcon.Text = glyph;
        }

        private void AddDepartmentRows(DepartmentStatus dept, IReadOnlyList<string> required)
        {
            HwResultsList.Children.Add(new TextBlock
            {
                Text = dept.Department,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, HwResultsList.Children.Count == 0 ? 0 : 10, 0, 4),
            });

            var byTest = dept.Latest.ToDictionary(r => r.TestId, r => r);
            var ids = required.Concat(byTest.Keys.Where(k => !required.Contains(k))).ToList();
            foreach (var id in ids)
            {
                byTest.TryGetValue(id, out var r);
                string status = r == null || r.Status == "NOT TESTED" || r.Status == "TESTING" ? "NOT DONE" : r.Status;
                bool badSkip = dept.SkippedNotAllowed.Contains(id);
                string tone = status == "PASS" ? "Pass" : status == "FAIL" || badSkip ? "Fail" : status == "SKIPPED" ? "Warn" : "Neutral";
                if (badSkip) status = "SKIP NOT OK";
                else if (!required.Contains(id) && status == "NOT DONE") status = "OPTIONAL";

                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                row.Children.Add(new TextBlock { Text = r?.TestName ?? QcGate.Label(id), FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

                var b = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 1, 5, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = (Brush)FindResource(tone + "Soft"),
                    Child = new TextBlock { Text = status, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource(tone + "Brush") },
                };
                Grid.SetColumn(b, 1);
                row.Children.Add(b);

                DateTime? at = r == null ? null : (r.CompletedAt ?? r.StartedAt);
                string info = r == null ? "" : r.OperatorName + (at.HasValue ? ", " + at.Value.ToString("dd MMM HH:mm") : "") +
                                               (string.IsNullOrEmpty(r.Comment) ? "" : "  •  " + r.Comment);
                var noteText = new TextBlock
                {
                    Text = info,
                    Style = (Style)FindResource("Muted"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = string.IsNullOrEmpty(info) ? null : info,
                };
                Grid.SetColumn(noteText, 2);
                row.Children.Add(noteText);

                HwResultsList.Children.Add(row);
            }
        }

        private void RefreshAll()
        {
            LoadPreparationStatus();
            RefreshCardStatuses();
            RefreshFinalStatus();
        }

        private void ToggleHwResults_Click(object sender, RoutedEventArgs e)
        {
            bool visible = HwResultsPanel.Visibility == Visibility.Visible;
            HwResultsPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            ToggleHwResultsBtn.Content = visible ? "View details" : "Hide details";
        }

        private void RefreshCardStatuses()
        {
            int inspected = 0, passed = 0, failed = 0;

            foreach (var card in Cards)
            {
                if (!_cardElements.TryGetValue(card.Kind.ToString(), out var border)) continue;
                var latest = _mgr.GetLatestTestResult(card.Kind.ToString(), "QC");
                UpdateCardView(border, latest);

                if (latest != null && latest.Status != "NOT TESTED")
                {
                    inspected++;
                    if (latest.Status == "PASS") passed++;
                    else if (latest.Status == "FAIL") failed++;
                }
            }

            QcProgressText.Text = string.Format("{0} of {1} items inspected ({2} PASS, {3} FAIL)", inspected, Cards.Count, passed, failed);
        }

        private void UpdateCardView(Border border, TestResultRecord latest)
        {
            var grid = (Grid)border.Child;
            var top = (Grid)grid.Children[0];
            var badge = (Border)top.Children[1];
            var statusLabel = (TextBlock)badge.Child;
            var latestSp = (StackPanel)grid.Children[2];
            var latestText = (TextBlock)latestSp.Children[0];
            var actionBtn = (Button)grid.Children[3];

            if (latest == null || latest.Status == "NOT TESTED")
            {
                badge.Background = (Brush)FindResource("NeutralSoft");
                statusLabel.Text = "● Ready";
                statusLabel.Foreground = (Brush)FindResource("NeutralBrush");
                latestText.Text = "Ready to inspect";
                actionBtn.Content = "Inspect";
            }
            else
            {
                string status = latest.Status.ToUpper();
                actionBtn.Content = "Re-inspect";

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
                string op = !string.IsNullOrEmpty(latest.OperatorName) ? latest.OperatorName : "QC";
                latestText.Text = string.Format("{0} • {1}", op, time);
            }
        }

        /// <summary>Why results cannot be saved for this laptop (null when they can).</summary>
        private string _saveBlocked = "Laptop detection has not finished yet.";

        private void RunInspection(QcCardDef def)
        {
            if (!DashboardGuard.CanStartTest(this, _shell, _saveBlocked, def.Kind.ToString(), "QC")) return;

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

                    DashboardGuard.TrySave(this, () => _mgr.RecordDepartmentTest(def.Kind.ToString(), def.Title, status, "QC", outcome.Notes));
                    RefreshCardStatuses();
                    LoadPreparationStatus();
                    RefreshFinalStatus();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("QC inspection execution failed: " + def.Title, ex);
                Dialogs.Info(Window.GetWindow(this), "QC Error", "Could not run inspection: " + ex.Message);
            }
        }

        private void RefreshFinalStatus()
        {
            var lifecycle = _mgr.CanSave ? _mgr.Lifecycle() : null;
            var final = lifecycle?.QcDecision;   // only a decision that still counts (not one superseded by rework)
            if (final != null)
            {
                string status = final.Status == "PASS" ? "PASSED" : "FAILED";
                string time = final.CompletedAt.HasValue ? final.CompletedAt.Value.ToString("yyyy-MM-dd HH:mm") : "";
                FinalSummaryText.Text = string.Format("QC Inspection {0} by {1} on {2}", status, final.OperatorName, time);
                FinalSummaryText.Foreground = final.Status == "PASS" ? (Brush)FindResource("PassBrush") : (Brush)FindResource("FailBrush");
                FinalLastResultText.Text = final.Status == "PASS" ? "Ready for the next step (ITMS)." : (!string.IsNullOrEmpty(final.Comment) ? "Reasons: " + final.Comment : "Not ready.");
            }
            else
            {
                FinalSummaryText.Text = "Review hardware results and complete the QC checklist above.";
                FinalSummaryText.Foreground = (Brush)FindResource("Navy");
                FinalLastResultText.Text = "No final QC inspection recorded for this session yet.";
            }
        }

        private void FinalPass_Click(object sender, RoutedEventArgs e)
        {
            RecordFinalQc("PASS");
        }

        private void FinalFail_Click(object sender, RoutedEventArgs e)
        {
            RecordFinalQc("FAIL");
        }

        private void RecordFinalQc(string outcome)
        {
            if (!DashboardGuard.CanStartTest(this, _shell, _saveBlocked)) return;
            var owner = Window.GetWindow(this);
            var op = SettingsService.Store.CurrentOperator;
            string denied = BBTech.Core.Security.ResultAuthorization.CheckRecord(op, Departments.Qc, QcGate.QcDecisionTestId);
            if (denied != null) { Dialogs.Info(owner, "QC", denied); return; }

            var state = _mgr.Lifecycle();
            var report = state.Gate;
            if (outcome == "PASS")
            {
                if (!report.CanPass) { Dialogs.Info(owner, "QC PASS not possible yet", string.Join("\n", report.BlockingReasons)); return; }
                if (Dialogs.Choice(owner, "QC", "QC PASS?", "This laptop becomes ready for E-Commerce / ITMS.\nRecorded as " + op.Name + ". It cannot be edited or deleted.", "QC PASS", "Cancel") != 0) return;
                if (DashboardGuard.TrySave(this, () => _mgr.RecordQcDecision("PASS", ""))) RefreshAll();
                return;
            }

            // FAIL: the problem is what QC marked as failed (evidence first), then choose where the laptop goes.
            if (!state.AtQcStage) { Dialogs.Info(owner, "QC", "This laptop is not at QC yet. " + string.Join(" ", report.BlockingReasons.Take(1))); return; }
            var problems = report.QcChecklist.Failed.Concat(report.Hardware.Failed).Concat(report.Software.Failed)
                                 .Select(f => (f.TestName ?? f.TestId) + (string.IsNullOrWhiteSpace(f.Comment) ? "" : " (" + f.Comment + ")")).Distinct().ToList();
            if (problems.Count == 0) { Dialogs.Info(owner, "QC FAIL", "First mark the item that failed in the checklist."); return; }
            string reason = string.Join("; ", problems);
            int pick = Dialogs.Choice(owner, "QC FAIL", "Return the laptop to...", "Problem: " + reason, "Hardware", "Software", "Cancel");
            if (pick != 0 && pick != 1) return;
            if (DashboardGuard.TrySave(this, () => _mgr.ReturnFromQc(pick == 0 ? Departments.Hardware : Departments.Software, reason))) RefreshAll();
        }

        private async void Rescan_Click(object sender, RoutedEventArgs e)
        {
            await InitializeAsync();
        }
    }
}
