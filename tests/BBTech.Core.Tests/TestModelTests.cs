using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using BBTech.Core.Export;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Test items, plans, labels and the ITMS extension points.</summary>
    public class TestModelTests
    {
        [Fact]
        public void Test_items_notify_the_ui_about_state_and_notes_changes()
        {
            var item = new TestItem(HardwareTest.Display, "Colours and patterns");
            var changed = new List<string>();
            item.PropertyChanged += (s, e) => changed.Add(e.PropertyName);
            item.State = TestState.Pass;
            item.Notes = "no dead pixels";
            Assert.Contains("State", changed);
            Assert.Contains("StateLabel", changed);
            Assert.Contains("Notes", changed);
        }

        [Fact]
        public void Setting_the_same_state_again_does_not_notify()
        {
            var item = new TestItem(HardwareTest.Keyboard, "");
            int count = 0;
            item.PropertyChanged += (s, e) => count++;
            item.State = TestState.NotTested;
            item.Notes = "";
            item.Required = true;
            Assert.Equal(0, count);
        }

        [Fact]
        public void Complete_records_state_notes_and_time_and_reset_clears_them()
        {
            var item = new TestItem(HardwareTest.Battery, "");
            item.Complete(TestState.Fail, null);
            Assert.Equal(TestState.Fail, item.State);
            Assert.Equal("", item.Notes);
            Assert.NotNull(item.CompletedAt);
            item.Reset();
            Assert.Equal(TestState.NotTested, item.State);
            Assert.Null(item.CompletedAt);
        }

        [Fact]
        public void Only_display_sound_keyboard_and_battery_are_mandatory()
        {
            var mandatory = TestCatalog.All.Where(t => new TestItem(t, "").Mandatory).ToList();
            Assert.Equal(new[] { HardwareTest.Display, HardwareTest.Speaker, HardwareTest.Keyboard, HardwareTest.Battery }, mandatory);
        }

        [Theory]
        [InlineData(HardwareTest.Speaker, "Sound")]
        [InlineData(HardwareTest.Storage, "Storage / SSD")]
        [InlineData(HardwareTest.Usb, "USB")]
        [InlineData(HardwareTest.WiFi, "Wi-Fi")]
        [InlineData(HardwareTest.Bluetooth, "Bluetooth")]
        public void Tests_have_friendly_names(HardwareTest t, string name)
        {
            Assert.Equal(name, TestText.Name(t));
        }

        [Fact]
        public void Overall_results_have_explicit_labels()
        {
            Assert.Equal("PASS", TestText.Label(OverallResult.Pass));
            Assert.Equal("FAIL", TestText.Label(OverallResult.Fail));
            Assert.Equal("INCOMPLETE", TestText.Label(OverallResult.Incomplete));
        }

        [Fact]
        public void No_items_is_incomplete()
        {
            Assert.Equal(OverallResult.Incomplete, OverallResultCalculator.Calculate(null));
        }

        [Fact]
        public void A_failure_wins_even_while_other_tests_are_running()
        {
            var items = new[]
            {
                new TestItem(HardwareTest.Display, "") { State = TestState.Testing },
                new TestItem(HardwareTest.Keyboard, "") { State = TestState.Fail },
            };
            Assert.Equal(OverallResult.Fail, OverallResultCalculator.Calculate(items));
        }

        [Fact]
        public void Selected_and_available_tests_follow_the_catalog_order()
        {
            var plan = TestPlan.Required();
            plan.Add(HardwareTest.Storage);
            plan.Add(HardwareTest.Touchpad);
            Assert.Equal(new[] { HardwareTest.Display, HardwareTest.Speaker, HardwareTest.Keyboard, HardwareTest.Battery, HardwareTest.Touchpad, HardwareTest.Storage }, plan.Selected);
            Assert.Equal(new[] { HardwareTest.Touchpad, HardwareTest.Storage }, plan.Additional);
            Assert.DoesNotContain(HardwareTest.Touchpad, plan.Available);
            Assert.Equal(TestCatalog.All.Count, plan.Selected.Count + plan.Available.Count);
        }

        [Fact]
        public void Adding_a_selected_test_again_changes_nothing()
        {
            var plan = TestPlan.Required();
            plan.Add(HardwareTest.Display);
            Assert.Equal(4, plan.Selected.Count);
            Assert.Equal(TestMode.Required, plan.Mode);
        }

        [Fact]
        public void Removing_a_test_that_is_not_selected_returns_true_and_keeps_the_mode()
        {
            var plan = TestPlan.Required();
            Assert.True(plan.Remove(HardwareTest.Camera));
            Assert.Equal(TestMode.Required, plan.Mode);
            Assert.Equal(4, plan.Selected.Count);
        }

        [Fact]
        public void Stored_plans_ignore_unknown_names_and_survive_null()
        {
            var plan = TestPlan.FromStored("Required", new[] { "Camera", "Hologram", "" });
            Assert.True(plan.IsSelected(HardwareTest.Camera));
            Assert.Equal(5, plan.Selected.Count);
            var empty = TestPlan.FromStored(null, null);
            Assert.Equal(TestMode.Required, empty.Mode);
            Assert.Equal(4, empty.Selected.Count);
        }

        [Fact]
        public void A_stored_all_tests_plan_missing_a_test_is_restored_as_required()
        {
            var names = TestCatalog.All.Where(t => t != HardwareTest.Bluetooth).Select(t => t.ToString());
            var plan = TestPlan.FromStored("AllTests", names);
            Assert.Equal(TestMode.Required, plan.Mode);
            Assert.False(plan.IsSelected(HardwareTest.Bluetooth));
        }

        [Theory]
        [InlineData("PASS", TestState.Pass)]
        [InlineData("FAIL", TestState.Fail)]
        [InlineData("SKIPPED", TestState.Skipped)]
        [InlineData("TESTING", TestState.Testing)]
        [InlineData("NOT TESTED", TestState.NotTested)]
        [InlineData("garbage", TestState.NotTested)]
        [InlineData(null, TestState.NotTested)]
        public void Stored_status_labels_parse_back_to_states(string label, TestState expected)
        {
            Assert.Equal(expected, HistoryReport.Parse(label));
        }

        [Fact]
        public void Report_meta_copies_the_operator_snapshot_and_hides_overall_while_open()
        {
            var open = new InspectionRecord { InspectionId = "i1", Number = 3, OperatorNameSnapshot = "Operator One", OperatorRoleSnapshot = "QC", Status = InspectionStatus.InProgress, OverallStatus = "PASS", TestMode = "AllTests", AppVersion = "1.0.0" };
            var meta = HistoryReport.Meta(open);
            Assert.Equal("Operator One", meta.TestedBy);
            Assert.Equal(3, meta.Number);
            Assert.Null(meta.OverallStatus);
            Assert.Equal("ALL TESTS", meta.TestMode);
            open.Status = InspectionStatus.Completed;
            Assert.Equal("PASS", HistoryReport.Meta(open).OverallStatus);
        }

        [Fact]
        public void Report_device_uses_inspection_specs_then_device_specs()
        {
            var device = new DeviceRecord { Manufacturer = "Dell", Model = "Latitude", ModelNumber = "5420", SerialNumber = "SERIAL-EXAMPLE", Specs = new DeviceSpecs { Cpu = "i5", Ram = "8 GB" } };
            var withSpecs = new InspectionRecord { Specs = new DeviceSpecs { Cpu = "i7", Ram = "16 GB" } };
            Assert.Equal("i7", HistoryReport.Device(device, withSpecs).Cpu);
            var r = HistoryReport.Device(device, new InspectionRecord());
            Assert.Equal("i5", r.Cpu);
            Assert.Equal("Unknown", r.Storage);
            Assert.Equal("SERIAL-EXAMPLE", r.System.SerialNumber);
            Assert.Equal("", r.System.SystemUuid);
        }

        [Fact]
        public async Task The_shipped_itms_client_is_not_configured_and_sends_nothing()
        {
            IItmsClient client = new NotConfiguredItmsClient();
            Assert.False(client.IsConfigured);
            var r = await client.SubmitNewDeviceAsync(new DetectionResult());
            Assert.False(r.Success);
            Assert.Equal("ITMS integration is not configured yet.", r.Message);
        }

        [Fact]
        public async Task The_shipped_sync_service_leaves_records_local_only()
        {
            ISyncService sync = new LocalOnlySyncService();
            var record = new SyncRecord { SyncId = "s1", SyncStatus = SyncStatus.LocalOnly };
            Assert.False(sync.IsEnabled);
            Assert.Same(record, await sync.SyncInspectionAsync(new DeviceRecord(), new InspectionView(), record));
            Assert.Equal("LOCAL_ONLY", record.SyncStatus);
        }

        [Fact]
        public void Itms_payload_contains_only_the_eight_fields_in_order()
        {
            var r = new DetectionResult { Brand = "Dell", Model = "Latitude", ModelNumber = "5420", Cpu = "i5", Generation = "11th Gen", Ram = "16 GB", Storage = "256 GB SSD", BatteryHealth = "91%" };
            r.System.SerialNumber = "SERIAL-EXAMPLE";
            string json = ItmsPayload.Build(r);
            var keys = System.Text.RegularExpressions.Regex.Matches(json, "\"([a-z_]+)\":").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value);
            Assert.Equal(new[] { "brand", "model", "model_number", "cpu", "generation", "ram", "storage", "battery_health" }, keys);
            Assert.DoesNotContain("SERIAL-EXAMPLE", json);
        }
    }
}
