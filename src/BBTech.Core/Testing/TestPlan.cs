using System;
using System.Collections.Generic;
using System.Linq;

namespace BBTech.Core.Testing
{
    public enum TestMode { Required, AllTests }

    /// <summary>The tests the application offers, in display order.</summary>
    public static class TestCatalog
    {
        /// <summary>The minimum QC tests. Always selected and mandatory.</summary>
        public static readonly HardwareTest[] RequiredTests =
            { HardwareTest.Display, HardwareTest.Speaker, HardwareTest.Keyboard, HardwareTest.Battery };

        // Preferred order for the remaining tests; any test added to HardwareTest later is appended automatically.
        private static readonly HardwareTest[] PreferredOrder =
        {
            HardwareTest.Display, HardwareTest.Speaker, HardwareTest.Keyboard, HardwareTest.Battery,
            HardwareTest.Touchpad, HardwareTest.Camera, HardwareTest.Microphone, HardwareTest.Headphone,
            HardwareTest.Usb, HardwareTest.WiFi, HardwareTest.Bluetooth, HardwareTest.Storage,
        };

        private static readonly IReadOnlyList<HardwareTest> AllTests = BuildAll();

        /// <summary>Every available test (built from the HardwareTest enum, so new tests are included automatically).</summary>
        public static IReadOnlyList<HardwareTest> All => AllTests;

        private static IReadOnlyList<HardwareTest> BuildAll()
        {
            var all = Enum.GetValues(typeof(HardwareTest)).Cast<HardwareTest>().ToList();
            return PreferredOrder.Where(all.Contains).Concat(all.Except(PreferredOrder)).ToList().AsReadOnly();
        }

        public static bool IsRequired(HardwareTest t) => Array.IndexOf(RequiredTests, t) >= 0;

        /// <summary>Display position of a test (used to sort report and history rows).</summary>
        public static int Order(HardwareTest t)
        {
            for (int i = 0; i < AllTests.Count; i++) if (AllTests[i] == t) return i;
            return int.MaxValue;
        }
    }

    /// <summary>
    /// Which tests belong to an inspection.
    ///  • REQUIRED: Display, Sound, Keyboard, Battery (mandatory) + any tests the operator adds.
    ///  • ALL TESTS: every available test.
    /// The four required tests can never be removed. Removing any other test while in ALL TESTS mode
    /// turns the plan into REQUIRED mode with the remaining selection (it is no longer "all").
    /// Only selected tests count toward the overall result.
    /// </summary>
    public sealed class TestPlan
    {
        private readonly HashSet<HardwareTest> _selected = new HashSet<HardwareTest>();

        public TestMode Mode { get; private set; }

        private TestPlan(TestMode mode) => SetMode(mode);

        public static TestPlan Required() => new TestPlan(TestMode.Required);
        public static TestPlan AllTests() => new TestPlan(TestMode.AllTests);

        public string ModeLabel => Mode == TestMode.AllTests ? "ALL TESTS" : "REQUIRED";

        /// <summary>Selected tests in display order.</summary>
        public IReadOnlyList<HardwareTest> Selected => TestCatalog.All.Where(_selected.Contains).ToList();

        /// <summary>Selected tests that are not one of the four required tests.</summary>
        public IReadOnlyList<HardwareTest> Additional => Selected.Where(t => !TestCatalog.IsRequired(t)).ToList();

        /// <summary>Tests that can still be added.</summary>
        public IReadOnlyList<HardwareTest> Available => TestCatalog.All.Where(t => !_selected.Contains(t)).ToList();

        public bool IsSelected(HardwareTest t) => _selected.Contains(t);

        public bool IsMandatory(HardwareTest t) => TestCatalog.IsRequired(t);

        /// <summary>REQUIRED resets to the four required tests; ALL TESTS selects everything.</summary>
        public void SetMode(TestMode mode)
        {
            Mode = mode;
            _selected.Clear();
            foreach (var t in mode == TestMode.AllTests ? TestCatalog.All : (IEnumerable<HardwareTest>)TestCatalog.RequiredTests) _selected.Add(t);
        }

        public void Add(HardwareTest t)
        {
            _selected.Add(t);
            if (TestCatalog.All.All(_selected.Contains)) Mode = TestMode.AllTests;
        }

        /// <summary>Removes an additional test. Returns false for the mandatory required tests.</summary>
        public bool Remove(HardwareTest t)
        {
            if (IsMandatory(t)) return false;
            if (_selected.Remove(t) && Mode == TestMode.AllTests) Mode = TestMode.Required;
            return true;
        }

        // ---- persistence (stored on the inspection record) ----

        public string ModeName => Mode.ToString();
        public List<string> SelectedNames => Selected.Select(t => t.ToString()).ToList();

        /// <summary>Restores a stored plan. Unknown names are ignored; the required tests are always included.</summary>
        public static TestPlan FromStored(string mode, IEnumerable<string> selected)
        {
            var plan = Required();
            if (selected != null)
                foreach (var name in selected)
                    if (Enum.TryParse(name, out HardwareTest t)) plan._selected.Add(t);
            plan.Mode = mode == TestMode.AllTests.ToString() && TestCatalog.All.All(plan._selected.Contains) ? TestMode.AllTests : TestMode.Required;
            return plan;
        }
    }
}
