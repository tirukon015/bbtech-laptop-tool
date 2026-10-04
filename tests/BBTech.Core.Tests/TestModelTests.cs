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

    }
}
