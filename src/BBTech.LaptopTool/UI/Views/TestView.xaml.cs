using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Export;
using BBTech.Core.Settings;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.Test;
using BBTech.LaptopTool.UI.Tests;

namespace BBTech.LaptopTool.UI.Views
{
    /// <summary>
    /// TEST (QC) workflow: operator selected once per session, laptop identified automatically,
    /// every result saved immediately to the laptop's encrypted history, unfinished inspections
    /// recovered, and COMPLETE INSPECTION turns the inspection into immutable history.
    /// </summary>
    public partial class TestView : UserControl
    {
        private TestSession _session = new TestSession();
        private readonly InspectionManager _mgr = new InspectionManager();
        private InspectionRecord _lastCompleted;
        private bool _initialized, _loadingOperators;
        /// <summary>False until the laptop is identified and any resume/conflict decision is made.</summary>
        private bool _ready;

        private SettingsStore Settings => SettingsService.Store;

        public TestView()
        {
            InitializeComponent();
            BindSession();
            LoadOperators();
            Loaded += async (s, e) =>
            {
                LoadOperators(); // operators may have changed in Settings meanwhile
                if (_initialized) return;
                _initialized = true;
                await InitializeAsync();
            };
        }

        private void BindSession()
        {
            foreach (var item in _session.Items) item.PropertyChanged += Item_PropertyChanged;
            RefreshLists();
        }

        // ---------------- Test mode / selection ----------------

        /// <summary>Shows the selected tests: REQUIRED = required section + additional section; ALL TESTS = one list.</summary>
        private void RefreshLists()
        {
            var plan = _session.Plan;
            var selected = _session.Selected.OrderBy(i => TestCatalog.Order(i.Kind)).ToList();
            bool all = plan.Mode == TestMode.AllTests;

            if (all)
            {
                RequiredHeader.Text = "All tests (" + selected.Count + ")";
                RequiredList.ItemsSource = selected;
                AdditionalList.ItemsSource = null;
                AdditionalHeader.Visibility = AdditionalEmpty.Visibility = Visibility.Collapsed;
            }
            else
            {
                RequiredHeader.Text = "Required tests";
                RequiredList.ItemsSource = selected.Where(i => i.Mandatory).ToList();
                var extra = selected.Where(i => !i.Mandatory).ToList();
                AdditionalList.ItemsSource = extra;
                AdditionalHeader.Text = "Additional tests" + (extra.Count > 0 ? " (" + extra.Count + ")" : "");
                AdditionalHeader.Visibility = Visibility.Visible;
                AdditionalEmpty.Visibility = extra.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            StyleModeButton(ModeRequiredButton, !all);
            StyleModeButton(ModeAllButton, all);
            ModeSummary.Text = selected.Count + " of " + TestCatalog.All.Count + " tests selected";
            AddTestsButton.IsEnabled = plan.Available.Count > 0 || plan.Additional.Count > 0;
            UpdateOverall();
        }

        private void StyleModeButton(Button b, bool active)
        {
            b.Style = (Style)FindResource(active ? "PrimaryButton" : "BaseButton");
            b.MinWidth = 130;
            b.Margin = new Thickness(0, 0, 8, 0);
        }

        /// <summary>Applies a new selection, confirming first if tests that already have results would be removed.</summary>
        private void ChangePlan(Action<TestPlan> change)
        {
            var before = _session.Plan;
            var next = TestPlan.FromStored(before.ModeName, before.SelectedNames);
            change(next);

            var dropped = _session.Items.Where(i => before.IsSelected(i.Kind) && !next.IsSelected(i.Kind)).ToList();
            var withResults = dropped.Where(i => i.State != TestState.NotTested).ToList();
            if (withResults.Count > 0 &&
                Dialogs.Choice(Window.GetWindow(this), "Remove tests", "Remove tests that already have results?",
                    string.Join(", ", withResults.Select(i => i.Name + " (" + i.StateLabel + ")")) +
                    "\n\nRemoved tests are not counted as tested, passed or failed for this inspection.",
                    "Remove", "Cancel") != 0)
                return;

            _session.ApplyPlan(next);
            try
            {
                foreach (var d in dropped) _mgr.RecordDeselected(d);
                _mgr.UpdateSelection(next);
            }
            catch (Exception ex)
            {
                AppLog.Error("Saving test selection failed", ex);
                ShowBanner("The test selection could not be saved to this laptop's QC history: " + ex.Message);
            }
            RefreshLists();
            UpdateHeader();
        }

        private void ModeRequired_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Plan.Mode == TestMode.Required) return;
            ChangePlan(p => p.SetMode(TestMode.Required));
        }

        private void ModeAll_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Plan.Mode == TestMode.AllTests) return;
            ChangePlan(p => p.SetMode(TestMode.AllTests));
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var item = (TestItem)((FrameworkElement)sender).Tag;
            if (item.Mandatory) return;
            ChangePlan(p => p.Remove(item.Kind));
        }

        private void AddTests_Click(object sender, RoutedEventArgs e)
        {
            var plan = _session.Plan;
            var choices = TestCatalog.All.Where(t => !TestCatalog.IsRequired(t))
                .Select(t => _session.Find(t))
                .Select(i => Tuple.Create(i.Kind, i.Name, i.Description, plan.IsSelected(i.Kind)))
                .ToList();
            var chosen = Dialogs.SelectTests(Window.GetWindow(this), choices);
            if (chosen == null) return;
            ChangePlan(p =>
            {
                foreach (var c in choices)
                {
                    if (chosen.Contains(c.Item1)) p.Add(c.Item1);
                    else p.Remove(c.Item1);
                }
            });
        }

        // ---------------- Operator ----------------

        private void LoadOperators()
        {
            _loadingOperators = true;
            var ops = Settings.ActiveOperators.ToList();
            OperatorBox.ItemsSource = ops;
            OperatorBox.SelectedItem = ops.FirstOrDefault(o => o.OperatorId == Settings.CurrentOperator?.OperatorId);
            RoleText.Text = Settings.CurrentOperator != null ? "Role: " + Settings.CurrentOperator.Role : (ops.Count == 0 ? "No operators configured" : "Select your name");
            _loadingOperators = false;
        }

        private void Operator_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingOperators || !(OperatorBox.SelectedItem is OperatorRecord op)) return;
            try { Settings.SelectOperator(op.OperatorId); }
            catch (Exception ex) { AppLog.Warn("Could not save operator selection", ex); }
            RoleText.Text = "Role: " + op.Role;
            AppLog.Info("Operator selected for this session");
            if (_mgr.Current != null && _mgr.Current.OperatorId != op.OperatorId)
                Dialogs.Info(Window.GetWindow(this), "Tested By",
                    op.Name + " will be used for new inspections.\n\nThe inspection in progress (#" + _mgr.Current.Number + ") was started by " +
                    _mgr.Current.OperatorNameSnapshot + " and stays recorded under that name.");
            UpdateHeader();
        }

        /// <summary>Returns the current operator, prompting once if none is selected.</summary>
        private OperatorRecord RequireOperator()
        {
            var op = Settings.CurrentOperator;
            if (op != null) return op;
            var owner = Window.GetWindow(this);
            if (!Settings.ActiveOperators.Any())
            {
                if (Dialogs.Choice(owner, "Tested By", "No QC operators yet",
                        "Before testing, an admin must add QC operators in Settings.", "Open Settings", "Cancel") == 0 && AdminGate.Unlock(owner))
                {
                    new SettingsWindow { Owner = owner }.ShowDialog();
                    LoadOperators();
                }
                return Settings.CurrentOperator;
            }
            Dialogs.Info(owner, "Tested By", "Select your name in \"Tested By\" first. It stays selected for the following laptops.");
            OperatorBox.IsDropDownOpen = true;
            return null;
        }

        // ---------------- Device / inspection ----------------

        private async System.Threading.Tasks.Task InitializeAsync()
        {
            var owner = Window.GetWindow(this);
            DeviceText.Text = "Identifying laptop...";
            InspectionReadiness state;
            try { state = await _mgr.InitializeAsync(); }
            catch (Exception ex)
            {
                AppLog.Error("TEST initialisation failed", ex);
                ShowBanner("The laptop could not be identified. Tests can run, but results cannot be saved to this laptop's QC history.");
                DeviceText.Text = "Laptop not identified";
                _ready = true;
                return;
            }

            var d = _mgr.Detection;
            DeviceText.Text = (d.Brand + " " + d.Model + " " + d.ModelNumber).Replace("Unknown", "").Trim() +
                              (string.IsNullOrEmpty(d.System.SerialNumber) ? "" : "  ·  Serial " + d.System.SerialNumber);
            if (!string.IsNullOrEmpty(_mgr.StoreMessage)) ShowBanner(_mgr.StoreMessage);

            switch (state)
            {
                case InspectionReadiness.NeedsResumeDecision:
                    var open = _mgr.PendingOpen;
                    var done = QcStoreService.Current.GetResults(open.InspectionId).Count(r => r.Status != "NOT TESTED");
                    int choice = Dialogs.Choice(owner, "Unfinished inspection", "Unfinished inspection found",
                        "Inspection #" + open.Number + " on this laptop was started by " + open.OperatorNameSnapshot + " on " +
                        DisplayFormat.DateTime(open.StartedAt) + " and was not completed (" + done + " test result(s) saved).\n\n" +
                        "Resume it, or start a new inspection? (The unfinished one is kept in history either way.)",
                        "Resume", "Start new inspection");
                    if (choice == 1) _mgr.AbandonPending();
                    else
                    {
                        var results = _mgr.ResumePending();
                        // Restore the test mode and selection of the unfinished inspection (older ones: tests counted then).
                        var plan = _mgr.Current.SelectedTests != null
                            ? TestPlan.FromStored(_mgr.Current.TestMode, _mgr.Current.SelectedTests)
                            : TestPlan.FromStored(null, results.Where(r => r.Required).Select(r => r.TestId));
                        NewSession(plan);
                        LoadResults(results.Where(r => plan.IsSelected((HardwareTest)Enum.Parse(typeof(HardwareTest), r.TestId))));
                    }
                    break;

                case InspectionReadiness.NeedsConflictDecision:
                    int c = Dialogs.Choice(owner, "Identity conflict", "This laptop's identity is uncertain",
                        _mgr.Match.Reason + "\n\nCreate a separate device record for this laptop (flagged as a conflict), or test without saving to the QC history?",
                        "Create separate record", "Test without saving");
                    if (c == 0) _mgr.CreateDeviceDespiteConflict();
                    else ShowBanner("Identity conflict: results are NOT being saved to this laptop's QC history. Use Export if you need a record.");
                    break;

                case InspectionReadiness.NoIdentifiers:
                    ShowBanner(_mgr.Match.Reason + " Tests can run, but results cannot be saved to the laptop's QC history (use Export).");
                    break;

                case InspectionReadiness.StoreUnavailable:
                    ShowBanner((_mgr.StoreMessage.Length > 0 ? _mgr.StoreMessage + " " : "") + "Tests can run, but results cannot be saved to this laptop's QC history.");
                    break;
            }
            _ready = true;
            UpdateHeader();
        }

        /// <summary>Tests cannot start before the laptop is identified (prevents saving to the wrong inspection).</summary>
        private bool EnsureReady()
        {
            if (_ready) return true;
            Dialogs.Info(Window.GetWindow(this), "Please wait", "The laptop is still being identified. Try again in a moment.");
            return false;
        }

        private void LoadResults(System.Collections.Generic.IEnumerable<TestResultRecord> results)
        {
            foreach (var r in results)
            {
                var item = _session.Items.FirstOrDefault(i => i.Kind.ToString() == r.TestId);
                if (item == null) continue;
                item.State = HistoryReport.Parse(r.Status);
                item.Notes = r.Comment ?? "";
                item.CompletedAt = r.CompletedAt;
            }
        }

        private void UpdateHeader()
        {
            var op = Settings.CurrentOperator;
            if (!_mgr.CanSave) InspectionText.Text = "Results are not saved to the laptop (see notice above).";
            else if (_mgr.Current != null)
                InspectionText.Text = "Inspection #" + _mgr.Current.Number + " in progress  ·  Tested by " + _mgr.Current.OperatorNameSnapshot +
                                      "  ·  every result is saved to this laptop immediately";
            else InspectionText.Text = "A new inspection starts with the first test" + (op != null ? " (Tested by " + op.Name + ")" : "") + ".";
        }

        private void ShowBanner(string text)
        {
            BannerText.Text = text;
            Banner.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Saves a test's state to the laptop now (crash/power-loss safe).</summary>
        private void Persist(TestItem item)
        {
            if (!_mgr.CanSave) return;
            try
            {
                _mgr.Record(item, Settings.CurrentOperator, _session.Plan);
                SavedText.Text = "✓ Saved on this laptop: " + item.Name + " " + item.StateLabel;
                UpdateHeader();
            }
            catch (Exception ex)
            {
                AppLog.Error("Saving result failed", ex);
                ShowBanner("A result could not be saved to this laptop's QC history: " + ex.Message);
            }
        }

        // ---------------- Overall ----------------

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TestItem.State) || e.PropertyName == nameof(TestItem.Required)) UpdateOverall();
        }

        private void UpdateOverall()
        {
            // Only the selected tests count (TestItem.Required mirrors "selected").
            var overall = OverallResultCalculator.Calculate(_session.Items);
            var selected = _session.Selected.ToList();
            OverallText.Text = TestText.Label(overall);
            OverallSub.Text = selected.Count(i => i.State == TestState.Pass) + " of " + selected.Count + " selected tests passed" +
                              (selected.Any(i => i.State == TestState.Fail) ? "  ·  " + selected.Count(i => i.State == TestState.Fail) + " failed" : "");
            var bg = (StateToBrushConverter)FindResource("StateBg");
            var fg = (StateToBrushConverter)FindResource("StateFg");
            OverallBadge.Background = (Brush)bg.Convert(overall, null, null, null);
            var brush = (Brush)fg.Convert(overall, null, null, null);
            OverallText.Foreground = OverallSub.Foreground = OverallCaption.Foreground = brush;
        }

        // ---------------- Test actions ----------------

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            var item = (TestItem)((FrameworkElement)sender).Tag;
            if (!EnsureReady()) return;
            if (_mgr.CanSave && RequireOperator() == null) return;

            var previous = item.State;
            var previousNotes = item.Notes;
            item.State = TestState.Testing;
            AppLog.Info("Test started: " + item.Name);

            TestOutcome outcome = null;
            try
            {
                var panel = TestPanelFactory.Create(item.Kind);
                var host = new TestHostWindow(item, panel) { Owner = Window.GetWindow(this) };
                host.ShowDialog();
                outcome = host.Outcome;
            }
            catch (Exception ex)
            {
                AppLog.Error("Test window failed: " + item.Name, ex);
                Dialogs.Info(Window.GetWindow(this), "BBTech Laptop Tool", "The " + item.Name + " test could not be run on this laptop.");
            }

            if (outcome == null)
            {
                item.State = previous;
                item.Notes = previousNotes;
                return;
            }
            item.Complete(outcome.State, outcome.Notes);
            Persist(item);
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            var item = (TestItem)((FrameworkElement)sender).Tag;
            item.Reset();
            if (_mgr.Current != null) Persist(item);
        }

        private void StartOver_Click(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(this);
            if (_mgr.Current != null)
            {
                if (Dialogs.Choice(owner, "Start over", "Start a new inspection?",
                        "Inspection #" + _mgr.Current.Number + " will be closed as ABANDONED (it stays in this laptop's history) and all tests reset.",
                        "Start over", "Cancel") != 0) return;
                try { _mgr.AbandonCurrent(); }
                catch (Exception ex) { AppLog.Error("Abandon failed", ex); }
            }
            NewSession();
        }

        /// <summary>Fresh set of tests; a new inspection starts in REQUIRED mode unless a plan is given (resume).</summary>
        private void NewSession(TestPlan plan = null)
        {
            foreach (var i in _session.Items) i.PropertyChanged -= Item_PropertyChanged;
            _session = new TestSession(plan ?? TestPlan.Required());
            BindSession();
            SavedText.Text = "";
            UpdateHeader();
        }

        private void Complete_Click(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(this);
            if (!EnsureReady()) return;
            if (!_mgr.CanSave)
            {
                Dialogs.Info(owner, "Complete inspection", "This inspection cannot be saved to the laptop (see the notice at the top). Use Export CSV / JSON to keep a record.");
                return;
            }
            if (_mgr.Current == null || !_mgr.HasResults)
            {
                Dialogs.Info(owner, "Complete inspection", "Run at least one test first.");
                return;
            }

            var overall = OverallResultCalculator.Calculate(_session.Selected);
            var missing = _session.Selected.Where(i => i.State != TestState.Pass && i.State != TestState.Fail).Select(i => i.Name).ToList();
            string msg = "Overall result: " + TestText.Label(overall) + "\nTest mode: " + _session.Plan.ModeLabel + " (" + _session.Selected.Count() + " tests)" +
                         "\nTested by: " + _mgr.Current.OperatorNameSnapshot + " (" + _mgr.Current.OperatorRoleSnapshot + ")" +
                         (missing.Count > 0 ? "\n\nNot completed: " + string.Join(", ", missing) + " → the inspection will be saved as INCOMPLETE." : "") +
                         "\n\nAfter completing, this inspection becomes part of the laptop's permanent QC history and cannot be changed.";
            if (Dialogs.Choice(owner, "Complete inspection", "Complete inspection #" + _mgr.Current.Number + "?", msg, "Complete inspection", "Cancel") != 0) return;

            try
            {
                _lastCompleted = _mgr.Complete(_session.Items, _session.Plan);
            }
            catch (Exception ex)
            {
                AppLog.Error("Complete inspection failed", ex);
                Dialogs.Info(owner, "Complete inspection", "The inspection could not be completed: " + ex.Message);
                return;
            }

            int c = Dialogs.Choice(owner, "Inspection saved", "Inspection #" + _lastCompleted.Number + " saved ✓",
                "Overall: " + _lastCompleted.OverallStatus + "\nTested by: " + _lastCompleted.OperatorNameSnapshot +
                "\n\nThe result is stored encrypted on this laptop. Any BBTech USB can show it later in DETECT.",
                "Close", "Export CSV", "Export JSON");
            var completed = _lastCompleted;
            NewSession();
            SavedText.Text = "✓ Inspection #" + completed.Number + " saved on this laptop (" + completed.OverallStatus + ")";
            if (c == 1) ExportInspection(completed, "csv");
            if (c == 2) ExportInspection(completed, "json");
        }

        // ---------------- Export (explicit, plaintext only on request) ----------------

        private void SaveJson_Click(object sender, RoutedEventArgs e) => ExportCurrent("json");
        private void SaveCsv_Click(object sender, RoutedEventArgs e) => ExportCurrent("csv");

        private void ExportCurrent(string ext)
        {
            if (_mgr.Current != null) ExportInspection(QcStoreService.Current.GetInspection(_mgr.Current.InspectionId), ext);
            else if (_lastCompleted != null) ExportInspection(_lastCompleted, ext);
            else ExportUnsaved(ext);
        }

        private void ExportInspection(InspectionRecord insp, string ext)
        {
            var repo = QcStoreService.Current;
            var view = new InspectionView { Inspection = insp, Results = repo.GetResults(insp.InspectionId) };
            var device = repo.Devices.First(d => d.DeviceId == insp.DeviceId);
            string path = ReportFiles.Save(view, device, ext);
            if (path != null) SavedText.Text = "Exported " + Path.GetFileName(path);
        }

        private async void ExportUnsaved(string ext)
        {
            try
            {
                var device = await DetectionSession.GetOrDetectAsync();
                var now = DateTime.Now;
                var selected = _session.Selected.ToList(); // only the tests selected for this inspection
                var overall = OverallResultCalculator.Calculate(selected);
                string content = ext == "json" ? TestReportExporter.ToJson(selected, device, AppPaths.Version, now) : TestReportExporter.ToCsv(selected, device, now);
                string path = ReportFiles.Write(ReportNaming.BuildFileName(device, overall, now, ext), content,
                    n => ReportNaming.BuildFileName(device.Brand, device.Model, device.ModelNumber, device.System.SerialNumber, overall, now, ext, n));
                if (path != null) SavedText.Text = "Exported " + Path.GetFileName(path);
            }
            catch (Exception ex) { AppLog.Error("Export failed", ex); }
        }
    }

    /// <summary>Writes exported reports to BBTech Data\reports on the USB (only when staff ask for an export).</summary>
    public static class ReportFiles
    {
        public static string Save(InspectionView view, DeviceRecord device, string ext)
        {
            var insp = view.Inspection;
            var items = HistoryReport.Items(view); // exactly the tests selected for that inspection
            var det = HistoryReport.Device(device, insp);
            var meta = HistoryReport.Meta(insp);
            var when = insp.CompletedAt ?? insp.StartedAt;
            string content = ext == "json" ? TestReportExporter.ToJson(items, det, AppPaths.Version, when, meta) : TestReportExporter.ToCsv(items, det, when, meta);
            var overall = insp.OverallStatus == "PASS" ? OverallResult.Pass : insp.OverallStatus == "FAIL" ? OverallResult.Fail
                        : insp.IsOpen ? OverallResultCalculator.Calculate(items) : OverallResult.Incomplete;
            return Write(ReportNaming.BuildFileName(det, overall, when, ext), content,
                n => ReportNaming.BuildFileName(det.Brand, det.Model, det.ModelNumber, det.System.SerialNumber, overall, when, ext, n));
        }

        public static string Write(string name, string content, Func<int, string> duplicateName)
        {
            try
            {
                string dir = AppPaths.Ensure(AppPaths.ReportsDir);
                for (int n = 2; File.Exists(Path.Combine(dir, name)); n++) name = duplicateName(n);
                string path = Path.Combine(dir, name);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                AppLog.Info("Report exported (" + Path.GetExtension(name) + ")");
                return path;
            }
            catch (Exception ex)
            {
                AppLog.Error("Report export failed", ex);
                MessageBox.Show("The report could not be saved. Check that the USB drive is not full or write-protected.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }
    }
}
