using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBTech.Core.Export;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    public class TestPlanTests
    {
        private static readonly HardwareTest[] Required = { HardwareTest.Display, HardwareTest.Speaker, HardwareTest.Keyboard, HardwareTest.Battery };

        [Fact]
        public void Required_mode_selects_exactly_the_four_required_tests()
        {
            var p = TestPlan.Required();
            Assert.Equal(TestMode.Required, p.Mode);
            Assert.Equal(Required.OrderBy(x => x), p.Selected.OrderBy(x => x));
            Assert.All(Required, t => Assert.True(p.IsMandatory(t)));
            Assert.Equal("Sound", TestText.Name(HardwareTest.Speaker));
        }

        [Fact]
        public void All_tests_mode_selects_every_available_test_including_future_ones()
        {
            var p = TestPlan.AllTests();
            var everyTest = Enum.GetValues(typeof(HardwareTest)).Cast<HardwareTest>().ToList(); // includes any test added later
            Assert.Equal(TestMode.AllTests, p.Mode);
            Assert.Equal(everyTest.Count, p.Selected.Count);
            Assert.All(everyTest, t => Assert.True(p.IsSelected(t)));
            Assert.Equal(everyTest.Count, TestCatalog.All.Count);
            Assert.Empty(p.Available);
        }

        [Fact]
        public void Additional_tests_can_be_added_in_required_mode()
        {
            var p = TestPlan.Required();
            p.Add(HardwareTest.Camera);
            p.Add(HardwareTest.WiFi);
            Assert.Equal(TestMode.Required, p.Mode);
            Assert.Equal(6, p.Selected.Count);
            Assert.Equal(new[] { HardwareTest.Camera, HardwareTest.WiFi }.OrderBy(x => x), p.Additional.OrderBy(x => x));
        }

        [Fact]
        public void Required_tests_can_never_be_removed()
        {
            foreach (var plan in new[] { TestPlan.Required(), TestPlan.AllTests() })
                foreach (var t in Required)
                {
                    Assert.False(plan.Remove(t));
                    Assert.True(plan.IsSelected(t));
                }
        }

        [Fact]
        public void Removing_a_test_from_all_tests_turns_it_into_required_mode_with_the_rest()
        {
            var p = TestPlan.AllTests();
            Assert.True(p.Remove(HardwareTest.Bluetooth));
            Assert.Equal(TestMode.Required, p.Mode);
            Assert.False(p.IsSelected(HardwareTest.Bluetooth));
            Assert.Equal(TestCatalog.All.Count - 1, p.Selected.Count);
        }

        [Fact]
        public void Adding_every_test_becomes_all_tests_and_switching_back_resets_to_four()
        {
            var p = TestPlan.Required();
            foreach (var t in p.Available.ToList()) p.Add(t);
            Assert.Equal(TestMode.AllTests, p.Mode);
            p.SetMode(TestMode.Required);
            Assert.Equal(4, p.Selected.Count);
        }

        [Fact]
        public void Stored_plan_round_trips_and_always_keeps_required_tests()
        {
            var p = TestPlan.Required();
            p.Add(HardwareTest.Usb);
            var restored = TestPlan.FromStored(p.ModeName, p.SelectedNames);
            Assert.Equal(p.Mode, restored.Mode);
            Assert.Equal(p.Selected, restored.Selected);

            var odd = TestPlan.FromStored("Required", new[] { "Camera", "NotARealTest" });
            Assert.True(odd.IsSelected(HardwareTest.Camera));
            Assert.All(Required, t => Assert.True(odd.IsSelected(t)));

            Assert.Equal(TestMode.AllTests, TestPlan.FromStored("AllTests", TestPlan.AllTests().SelectedNames).Mode);
            Assert.Equal(TestMode.Required, TestPlan.FromStored("AllTests", new[] { "Display" }).Mode); // not actually all
        }

        // ---- overall result is calculated from the selected tests only ----

        private static List<TestItem> Items(TestPlan plan, Dictionary<HardwareTest, TestState> states)
        {
            // Same rule as the app's TestSession: TestItem.Required mirrors "selected".
            return TestCatalog.All.Select(t => new TestItem(t, "", plan.IsSelected(t))
            {
                State = states.TryGetValue(t, out var s) ? s : TestState.NotTested,
            }).ToList();
        }

        private static Dictionary<HardwareTest, TestState> AllRequired(TestState s) => Required.ToDictionary(t => t, t => s);

        [Fact]
        public void Required_mode_passes_when_the_four_required_tests_pass()
        {
            var states = AllRequired(TestState.Pass);
            states[HardwareTest.Camera] = TestState.Fail; // NOT selected -> must not count
            Assert.Equal(OverallResult.Pass, OverallResultCalculator.Calculate(Items(TestPlan.Required(), states)));
        }

        [Fact]
        public void Required_test_incomplete_or_failed_blocks_pass()
        {
            var incomplete = AllRequired(TestState.Pass);
            incomplete[HardwareTest.Keyboard] = TestState.NotTested;
            Assert.Equal(OverallResult.Incomplete, OverallResultCalculator.Calculate(Items(TestPlan.Required(), incomplete)));

            var skipped = AllRequired(TestState.Pass);
            skipped[HardwareTest.Battery] = TestState.Skipped;
            Assert.Equal(OverallResult.Incomplete, OverallResultCalculator.Calculate(Items(TestPlan.Required(), skipped)));

            var failed = AllRequired(TestState.Pass);
            failed[HardwareTest.Speaker] = TestState.Fail;
            Assert.Equal(OverallResult.Fail, OverallResultCalculator.Calculate(Items(TestPlan.Required(), failed)));
        }

        [Fact]
        public void Added_tests_count_toward_the_result()
        {
            var plan = TestPlan.Required();
            plan.Add(HardwareTest.Camera);
            var states = AllRequired(TestState.Pass);
            Assert.Equal(OverallResult.Incomplete, OverallResultCalculator.Calculate(Items(plan, states))); // camera not run yet
            states[HardwareTest.Camera] = TestState.Fail;
            Assert.Equal(OverallResult.Fail, OverallResultCalculator.Calculate(Items(plan, states)));
            states[HardwareTest.Camera] = TestState.Pass;
            Assert.Equal(OverallResult.Pass, OverallResultCalculator.Calculate(Items(plan, states)));
        }

        [Fact]
        public void All_tests_mode_needs_every_test_to_pass()
        {
            var plan = TestPlan.AllTests();
            var states = TestCatalog.All.ToDictionary(t => t, t => TestState.Pass);
            Assert.Equal(OverallResult.Pass, OverallResultCalculator.Calculate(Items(plan, states)));
            states[HardwareTest.Storage] = TestState.NotTested;
            Assert.Equal(OverallResult.Incomplete, OverallResultCalculator.Calculate(Items(plan, states)));
        }
    }

    public class TestSelectionStorageTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-plan-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly OperatorSnapshot Jimmy = new OperatorSnapshot { OperatorId = "j", Name = "Jimmy", Role = "QC" };

        private QcRepository Open() => QcRepository.Open(_dir, new FakeMachine(1), QcStoreTests.LaptopA).Repository;

        [Fact]
        public void Inspection_records_exactly_which_tests_were_selected()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs());
            var plan = TestPlan.Required();
            var insp = repo.StartInspection(dev.DeviceId, Jimmy, "1.0", null, plan.ModeName, plan.SelectedNames);

            plan.Add(HardwareTest.Camera);
            repo.UpdateTestSelection(insp.InspectionId, plan.ModeName, plan.SelectedNames);
            repo.SaveResult(insp.InspectionId, "Display", "Display", "PASS", true, "");
            repo.CompleteInspection(insp.InspectionId, "INCOMPLETE");

            var stored = Open().GetInspection(insp.InspectionId); // after reopen (another USB / restart)
            Assert.Equal("Required", stored.TestMode);
            Assert.Equal(new[] { "Display", "Speaker", "Keyboard", "Battery", "Camera" }.OrderBy(x => x), stored.SelectedTests.OrderBy(x => x));
            Assert.Throws<InvalidOperationException>(() => Open().UpdateTestSelection(insp.InspectionId, "AllTests", TestPlan.AllTests().SelectedNames));
        }

        [Fact]
        public void Unfinished_inspection_keeps_its_mode_and_selection_for_recovery()
        {
            var repo = Open();
            var dev = repo.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs());
            var plan = TestPlan.AllTests();
            repo.StartInspection(dev.DeviceId, Jimmy, "1.0", null, plan.ModeName, plan.SelectedNames);
            var open = Open().GetOpenInspection(dev.DeviceId);
            var restored = TestPlan.FromStored(open.TestMode, open.SelectedTests);
            Assert.Equal(TestMode.AllTests, restored.Mode);
            Assert.Equal(TestCatalog.All.Count, restored.Selected.Count);
        }

        [Fact]
        public void History_and_reports_show_only_selected_tests()
        {
            var view = new InspectionView
            {
                Inspection = new InspectionRecord { TestMode = "Required", SelectedTests = new List<string> { "Display", "Speaker", "Keyboard", "Battery", "Usb" } },
                Results = new List<TestResultRecord>
                {
                    new TestResultRecord { TestId = "Display", Status = "PASS", Required = true },
                    new TestResultRecord { TestId = "Speaker", Status = "PASS", Required = true },
                    new TestResultRecord { TestId = "Camera", Status = "NOT TESTED", Required = false, Comment = "Removed from this inspection" },
                },
            };
            var items = HistoryReport.Items(view);
            Assert.Equal(new[] { "Display", "Sound", "Keyboard", "Battery", "USB" }, items.Select(i => i.Name).ToArray());
            Assert.Equal(TestState.NotTested, items.Single(i => i.Kind == HardwareTest.Usb).State); // selected, not run
            Assert.DoesNotContain(items, i => i.Kind == HardwareTest.Camera);                       // unselected -> not shown
            Assert.Equal("REQUIRED", HistoryReport.ModeLabel(view.Inspection));

            string csv = TestReportExporter.ToCsv(items, new Models.DetectionResult(), DateTime.Now, HistoryReport.Meta(view.Inspection));
            Assert.Contains("Test Mode,REQUIRED", csv);
            Assert.DoesNotContain("Camera", csv);
        }

        [Fact]
        public void Older_inspections_without_a_selection_still_display()
        {
            var view = new InspectionView
            {
                Inspection = new InspectionRecord(), // recorded before test modes existed
                Results = new List<TestResultRecord> { new TestResultRecord { TestId = "Keyboard", Status = "PASS", Required = true } },
            };
            Assert.Single(HistoryReport.Items(view));
            Assert.Equal("", HistoryReport.ModeLabel(view.Inspection));
        }
    }
}
