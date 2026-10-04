using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BBTech.Core.Testing;

namespace BBTech.LaptopTool.Test
{
    /// <summary>
    /// All available hardware tests for the current inspection, built from <see cref="TestCatalog"/> so that
    /// new tests appear automatically. <see cref="Plan"/> decides which of them are selected (REQUIRED /
    /// ALL TESTS); TestItem.Required mirrors "selected", so only selected tests count toward the result.
    /// </summary>
    public sealed class TestSession
    {
        private static readonly Dictionary<HardwareTest, string> Descriptions = new Dictionary<HardwareTest, string>
        {
            { HardwareTest.Display, "Colour patterns for dead pixels, lines and backlight" },
            { HardwareTest.Speaker, "Left / right speaker tones, sweep and BBTech test media" },
            { HardwareTest.Keyboard, "Press every key; tested keys turn green" },
            { HardwareTest.Battery, "Health (BatteryInfoView) and charger unplug / plug-in" },
            { HardwareTest.Touchpad, "Movement coverage, left/right click and scrolling" },
            { HardwareTest.Camera, "Live camera preview" },
            { HardwareTest.Microphone, "Live input level, record and play back" },
            { HardwareTest.Headphone, "Headphone jack detection and left / right audio" },
            { HardwareTest.Usb, "Plug a device into each port; arrivals are detected" },
            { HardwareTest.WiFi, "Adapter, radio state and scan for nearby networks" },
            { HardwareTest.Bluetooth, "Adapter and scan for nearby devices" },
            { HardwareTest.Storage, "Windows drive health (+ optional HD Sentinel)" },
        };

        public ObservableCollection<TestItem> Items { get; }
        public TestPlan Plan { get; private set; }

        public TestSession(TestPlan plan = null)
        {
            Items = new ObservableCollection<TestItem>(TestCatalog.All.Select(t =>
                new TestItem(t, Descriptions.TryGetValue(t, out var d) ? d : "", required: false)));
            ApplyPlan(plan ?? TestPlan.Required());
        }

        public IEnumerable<TestItem> Selected => Items.Where(i => Plan.IsSelected(i.Kind));

        public TestItem Find(HardwareTest kind) => Items.First(i => i.Kind == kind);

        /// <summary>Applies a plan: unselected tests are reset (never counted as tested, passed or failed).</summary>
        public void ApplyPlan(TestPlan plan)
        {
            Plan = plan;
            foreach (var item in Items)
            {
                bool selected = plan.IsSelected(item.Kind);
                if (!selected && item.State != TestState.NotTested) item.Reset();
                item.Required = selected;
            }
        }
    }
}
