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

    }
}
