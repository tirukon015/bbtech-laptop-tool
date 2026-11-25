using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Models;
using BBTech.Core.Settings;
using BBTech.Core.Software;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.UI.Tests;

namespace BBTech.LaptopTool.UI.Views
{
    public sealed class SoftwareCardDef
    {
        public HardwareTest Kind { get; set; }
        public string Title { get; set; }
        public string Glyph { get; set; }
        public string Description { get; set; }
    }

    public partial class SoftwareDashboardView : UserControl
    {
        private readonly MainWindow _shell;
        private readonly InspectionManager _mgr = new InspectionManager();
        private readonly SoftwareExecutionService _software = new SoftwareExecutionService();
        private bool _busy;

        private static readonly List<SoftwareCardDef> Cards = new List<SoftwareCardDef>
        {
            new SoftwareCardDef { Kind = HardwareTest.SoftwareDrivers, Title = "Drivers", Glyph = "", Description = "All device drivers working" },
            new SoftwareCardDef { Kind = HardwareTest.SoftwareDisplay, Title = "Display", Glyph = "\uE7F4", Description = "Resolution, driver & refresh" },
            new SoftwareCardDef { Kind = HardwareTest.SoftwareKeyboard, Title = "Keyboard", Glyph = "\uE765", Description = "Interactive key matrix test" },
            new SoftwareCardDef { Kind = HardwareTest.SoftwareMouse, Title = "Mouse", Glyph = "\uE962", Description = "Movement, left, right, middle" },
            new SoftwareCardDef { Kind = HardwareTest.SoftwareScroll, Title = "Scroll", Glyph = "\uE962", Description = "Bi-directional mouse wheel" },
            new SoftwareCardDef { Kind = HardwareTest.SoftwareHotkeys, Title = "Hotkeys", Glyph = "\uE765", Description = "Ctrl, Alt, Shift & Win shortcuts" },
            new SoftwareCardDef { Kind = HardwareTest.SoftwareTouchpad, Title = "Touchpad", Glyph = "\uEFA5", Description = "Tracking, taps & two-finger scroll" },
            new SoftwareCardDef { Kind = HardwareTest.SoftwareAudio, Title = "Audio", Glyph = "", Description = "Speaker / headphone output" },
            new SoftwareCardDef { Kind = HardwareTest.SoftwareMicrophone, Title = "Microphone", Glyph = "", Description = "Input level & recording" },
            new SoftwareCardDef { Kind = HardwareTest.SoftwareCamera, Title = "Camera", Glyph = "", Description = "Camera found & working" },
        };

        private readonly Dictionary<string, Border> _cardElements = new Dictionary<string, Border>();

        public SoftwareDashboardView(MainWindow shell)
        {
            InitializeComponent();
            _life = new LifecyclePanel(_mgr, BBTech.Core.Testing.Departments.Software, RefreshCardStatuses);
            LifecycleHost.Content = _life;
            _windows = new WindowsInstallPanel(_mgr, this, shell, () => _saveBlocked, RefreshCardStatuses);
            WindowsHost.Content = _windows;
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            BuildCards();
            Loaded += async (s, e) => await InitializeAsync();
        }

        private void BuildCards()
        {
            TestCardsContainer.Children.Clear();
            _cardElements.Clear();

            foreach (var card in Cards)
            {
                var border = CreateCardBorder(card);
                _cardElements[card.Kind.ToString()] = border;
                TestCardsContainer.Children.Add(border);
            }
        }

        private Border CreateCardBorder(SoftwareCardDef def)
        {
            var b = new Border
            {
                Style = (Style)FindResource("SoftwareCardBorder"),
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

            // Row 3: Action Button
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
            testBtn.Click += (s, e) => { e.Handled = true; RunSoftwareTest(def); };
            Grid.SetRow(testBtn, 3);
            grid.Children.Add(testBtn);

            b.Child = grid;

            b.MouseLeftButtonUp += (s, e) =>
            {
                if (e.OriginalSource is Button) return;
                RunSoftwareTest(def);
            };

            return b;
        }

        private async Task InitializeAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                DeviceTitle.Text = "Detecting software configuration...";
                DeviceSpecs.Text = "Reading operating system and hardware specifications...";
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
                await RefreshSoftwareStatusAsync();
                if (_saveBlocked != null) Dialogs.Info(Window.GetWindow(this), "Results cannot be saved", _saveBlocked);
            }
            catch (Exception ex)
            {
                _saveBlocked = "Laptop detection did not finish. Press Rescan.";
                AppLog.Error("Software dashboard initialisation failed", ex);
                DeviceTitle.Text = "Software status incomplete";
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
            _windows?.Refresh();
            int passed = 0, failed = 0, tested = 0;

            foreach (var card in Cards)
            {
                if (!_cardElements.TryGetValue(card.Kind.ToString(), out var border)) continue;
                var latest = _mgr.GetLatestTestResult(card.Kind.ToString(), "Software");
                UpdateCardView(border, latest);

                if (latest != null && latest.Status != "NOT TESTED")
                {
                    tested++;
                    if (latest.Status == "PASS") passed++;
                    else if (latest.Status == "FAIL") failed++;
                }
            }

            TestSummaryText.Text = tested > 0
                ? string.Format("{0} of {1} tests completed ({2} PASS, {3} FAIL)", tested, Cards.Count, passed, failed)
                : string.Format("0 of {0} tests completed", Cards.Count);
        }

        private void UpdateCardView(Border border, TestResultRecord latest)
        {
            var grid = (Grid)border.Child;
            var top = (Grid)grid.Children[0];
            var badge = (Border)top.Children[1];
            var statusLabel = (TextBlock)badge.Child;
            var latestSp = (StackPanel)grid.Children[2];
            var latestText = (TextBlock)latestSp.Children[0];
            var testBtn = (Button)grid.Children[3];

            if (latest == null || latest.Status == "NOT TESTED")
            {
                badge.Background = (Brush)FindResource("NeutralSoft");
                statusLabel.Text = "● Ready";
                statusLabel.Foreground = (Brush)FindResource("NeutralBrush");
                latestText.Text = "Ready to test";
                testBtn.Content = "Start Test";
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
            }
        }

        /// <summary>Why results cannot be saved for this laptop (null when they can).</summary>
        private string _saveBlocked = "Laptop detection has not finished yet.";

        private LifecyclePanel _life;
        private WindowsInstallPanel _windows;

        private void RunSoftwareTest(SoftwareCardDef def)
        {
            if (!DashboardGuard.CanStartTest(this, _shell, _saveBlocked, def.Kind.ToString(), "Software")) return;
            var store = SettingsService.Store;
            var op = store.CurrentOperator;
            if (!op.CanAccessSoftware)
            {
                MessageBox.Show("Access Denied: Your account does not have permission for software tests (software.test required).",
                    "Software Testing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

                    DashboardGuard.TrySave(this, () => _mgr.RecordDepartmentTest(def.Kind.ToString(), def.Title, status, "Software", outcome.Notes));
                    RefreshCardStatuses();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Software test error: " + def.Title, ex);
                MessageBox.Show("Could not run test: " + ex.Message, "Test Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task RefreshSoftwareStatusAsync()
        {
            await Task.Run(() =>
            {
                var officeInstall = _software.CheckOfficeInstallation();
                var officeAct = _software.CheckOfficeActivation();
                var winAct = _software.CheckWindowsActivation();

                Dispatcher.Invoke(() =>
                {
                    // Office Installation
                    if (officeInstall.IsInstalled)
                    {
                        OfficeInstallBadge.Background = (Brush)FindResource("PassSoft");
                        OfficeInstallBadgeText.Text = "Installed ✓";
                        OfficeInstallBadgeText.Foreground = (Brush)FindResource("PassBrush");
                        OfficeDetailsText.Text = string.Format("{0} ({1})", officeInstall.DisplayName, officeInstall.Version);
                    }
                    else
                    {
                        OfficeInstallBadge.Background = (Brush)FindResource("NeutralSoft");
                        OfficeInstallBadgeText.Text = "Not Installed";
                        OfficeInstallBadgeText.Foreground = (Brush)FindResource("NeutralBrush");
                        OfficeDetailsText.Text = "No Microsoft Office installation detected.";
                    }

                    // Office Activation
                    if (officeAct.IsActivated)
                    {
                        OfficeActivationBadge.Background = (Brush)FindResource("PassSoft");
                        OfficeActivationBadgeText.Text = "PASS ✓";
                        OfficeActivationBadgeText.Foreground = (Brush)FindResource("PassBrush");
                        OfficeActivationDetailsText.Text = officeAct.Details ?? "Microsoft Office is permanently activated.";
                    }
                    else
                    {
                        OfficeActivationBadge.Background = (Brush)FindResource("NeutralSoft");
                        OfficeActivationBadgeText.Text = "Not Activated";
                        OfficeActivationBadgeText.Foreground = (Brush)FindResource("NeutralBrush");
                        OfficeActivationDetailsText.Text = string.IsNullOrEmpty(officeAct.LicenseStatus) ? "Not Activated" : officeAct.LicenseStatus;
                    }

                    // Windows Activation
                    WindowsEditionText.Text = winAct.ProductName;
                    if (winAct.IsActivated)
                    {
                        WindowsActivationBadge.Background = (Brush)FindResource("PassSoft");
                        WindowsActivationBadgeText.Text = "PASS ✓";
                        WindowsActivationBadgeText.Foreground = (Brush)FindResource("PassBrush");
                        WindowsDetailsText.Text = winAct.Details ?? "Windows is permanently activated.";
                    }
                    else
                    {
                        WindowsActivationBadge.Background = (Brush)FindResource("WarnSoft");
                        WindowsActivationBadgeText.Text = "FAIL ✗";
                        WindowsActivationBadgeText.Foreground = (Brush)FindResource("WarnBrush");
                        WindowsDetailsText.Text = winAct.LicenseStatus;
                    }
                });
            });
        }

        private async void Rescan_Click(object sender, RoutedEventArgs e)
        {
            await InitializeAsync();
        }

        private async void InstallOffice_Click(object sender, RoutedEventArgs e)
        {
            if (!DashboardGuard.CanStartTest(this, _shell, _saveBlocked)) return;
            var op = SettingsService.Store.CurrentOperator;
            if (!op.CanInstallSoftware)
            {
                MessageBox.Show("Access Denied: You do not have permission to install software (software.install required).",
                    "Software Installation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var pkg = MasterDataService.OfficePackage; // approved package from master configuration (or the built-in default)
            InstallOfficeBtn.IsEnabled = false;
            InstallOfficeBtn.Content = "INSTALLING...";

            try
            {
                var progress = new Progress<string>(msg => OfficeDetailsText.Text = msg);
                var outcome = await _software.InstallPackageAsync(pkg, op, progress);
                DashboardGuard.TrySave(this, () => _mgr.RecordDepartmentTest(pkg.ResultTestId, pkg.Name + " Installation",
                    outcome.Success ? "PASS" : "FAIL", "Software", outcome.Message));
                Dialogs.Info(Window.GetWindow(this), "Office installation", outcome.Message);
            }
            catch (Exception ex)
            {
                AppLog.Error("Office install failed", ex);
                Dialogs.Info(Window.GetWindow(this), "Office installation", ex.Message);
            }
            finally
            {
                InstallOfficeBtn.IsEnabled = true;
                InstallOfficeBtn.Content = "INSTALL";
                await RefreshSoftwareStatusAsync();
            }
        }

        private async void CheckOfficeInstall_Click(object sender, RoutedEventArgs e)
        {
            var status = _software.CheckOfficeInstallation();
            await RefreshSoftwareStatusAsync();
            MessageBox.Show(status.IsInstalled ? "Microsoft Office is installed: " + status.DisplayName : "Microsoft Office is NOT installed.",
                "Office Check", MessageBoxButton.OK, status.IsInstalled ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private async void CheckOfficeActivation_Click(object sender, RoutedEventArgs e)
        {
            if (!DashboardGuard.CanStartTest(this, _shell, _saveBlocked)) return;
            var op = SettingsService.Store.CurrentOperator;
            if (!op.CanCheckActivation && !op.IsAdmin)
            {
                MessageBox.Show("Access Denied: You do not have permission to check software activation (software.activation.check required).",
                    "Office Activation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var act = _software.CheckOfficeActivation(op);
            await RefreshSoftwareStatusAsync();
            if (!act.Determinable)
            {
                // Unknown is not a result: nothing is recorded as PASS or FAIL.
                Dialogs.Info(Window.GetWindow(this), "Office activation", act.LicenseStatus + ".\n" + act.Details);
                return;
            }
            string passFail = act.IsActivated ? "PASS" : "FAIL";

            // Persist as BBTech Software Record (OperatorId, DeviceId, timestamp, action)
            DashboardGuard.TrySave(this, () => _mgr.RecordDepartmentTest("SoftwareOfficeActivation", "Microsoft Office Activation", passFail, "Software", act.Details ?? act.LicenseStatus));

            MessageBox.Show(act.IsActivated ? "Microsoft Office is ACTIVATED (PASS)!" : "Microsoft Office is NOT activated (" + act.LicenseStatus + ").",
                "Office Activation Check", MessageBoxButton.OK, act.IsActivated ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private async void ActivateOffice_Click(object sender, RoutedEventArgs e)
        {
            if (!DashboardGuard.CanStartTest(this, _shell, _saveBlocked)) return;
            var op = SettingsService.Store.CurrentOperator;
            if (!op.CanExecuteActivation && !op.IsAdmin)
            {
                MessageBox.Show("Access Denied: Only authorized administrators can trigger license activation (software.activation.execute required).",
                    "Office Activation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ActivateOfficeBtn.IsEnabled = false;
            ActivateOfficeBtn.Content = "ACTIVATING...";
            try
            {
                var result = await _software.ActivateOfficeAsync(op, visible: true);
                string passFail = result.Success ? "PASS" : "FAIL";

                // Persist as BBTech Software Record (OperatorId, DeviceId, timestamp, action)
                DashboardGuard.TrySave(this, () => _mgr.RecordDepartmentTest("SoftwareOfficeActivation", "Microsoft Office Activation", passFail, "Software",
                    string.Format("Exit code: {0} • {1}", result.ExitCode, result.Details)));

                OfficeActivationBadge.Background = (Brush)FindResource(result.Success ? "PassSoft" : "FailSoft");
                OfficeActivationBadgeText.Text = result.Success ? "PASS ✓" : "FAIL ✗";
                OfficeActivationBadgeText.Foreground = (Brush)FindResource(result.Success ? "PassBrush" : "FailBrush");
                OfficeActivationDetailsText.Text = string.Format("Exit code: {0} • {1}", result.ExitCode, result.Details);

                MessageBox.Show(string.Format("Office Activation Result: {0}\nExit Code: {1}\n{2}", passFail, result.ExitCode, result.Details),
                    "Office Activation", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppLog.Error("Office activation error", ex);
                MessageBox.Show("Activation error: " + ex.Message, "Office Activation", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                ActivateOfficeBtn.IsEnabled = true;
                ActivateOfficeBtn.Content = "ACTIVATE";
                await RefreshSoftwareStatusAsync();
            }
        }

        private async void CheckWindowsActivation_Click(object sender, RoutedEventArgs e)
        {
            if (!DashboardGuard.CanStartTest(this, _shell, _saveBlocked)) return;
            var op = SettingsService.Store.CurrentOperator;
            if (!op.CanCheckActivation && !op.IsAdmin)
            {
                MessageBox.Show("Access Denied: You do not have permission to check Windows activation (software.activation.check required).",
                    "Windows Activation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var act = _software.CheckWindowsActivation(op);
            string passFail = act.IsActivated ? "PASS" : "FAIL";

            // Persist as BBTech Software Record (OperatorId, DeviceId, timestamp, action)
            DashboardGuard.TrySave(this, () => _mgr.RecordDepartmentTest("SoftwareWindowsActivation", "Windows Activation", passFail, "Software", act.Details ?? act.LicenseStatus));

            await RefreshSoftwareStatusAsync();
            MessageBox.Show(act.IsActivated ? act.ProductName + " is ACTIVATED (PASS)!" : act.ProductName + " is NOT activated (" + act.LicenseStatus + ").",
                "Windows Activation Check", MessageBoxButton.OK, act.IsActivated ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private async void ActivateWindows_Click(object sender, RoutedEventArgs e)
        {
            if (!DashboardGuard.CanStartTest(this, _shell, _saveBlocked)) return;
            var op = SettingsService.Store.CurrentOperator;
            if (!op.CanExecuteActivation && !op.IsAdmin)
            {
                MessageBox.Show("Access Denied: Only authorized administrators can trigger license activation (software.activation.execute required).",
                    "Windows Activation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ActivateWindowsBtn.IsEnabled = false;
            ActivateWindowsBtn.Content = "ACTIVATING...";
            try
            {
                var result = await _software.ActivateWindowsAsync(op, visible: true);
                string passFail = result.Success ? "PASS" : "FAIL";

                // Persist as BBTech Software Record (OperatorId, DeviceId, timestamp, action)
                DashboardGuard.TrySave(this, () => _mgr.RecordDepartmentTest("SoftwareWindowsActivation", "Windows Activation", passFail, "Software",
                    string.Format("Exit code: {0} • {1}", result.ExitCode, result.Details)));

                WindowsActivationBadge.Background = (Brush)FindResource(result.Success ? "PassSoft" : "FailSoft");
                WindowsActivationBadgeText.Text = result.Success ? "PASS ✓" : "FAIL ✗";
                WindowsActivationBadgeText.Foreground = (Brush)FindResource(result.Success ? "PassBrush" : "FailBrush");
                WindowsDetailsText.Text = string.Format("Exit code: {0} • {1}", result.ExitCode, result.Details);

                MessageBox.Show(string.Format("Windows Activation Result: {0}\nExit Code: {1}\n{2}", passFail, result.ExitCode, result.Details),
                    "Windows Activation", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppLog.Error("Windows activation error", ex);
                MessageBox.Show("Activation error: " + ex.Message, "Windows Activation", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                ActivateWindowsBtn.IsEnabled = true;
                ActivateWindowsBtn.Content = "ACTIVATE";
                await RefreshSoftwareStatusAsync();
            }
        }
    }
}
