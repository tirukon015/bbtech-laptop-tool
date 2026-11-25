using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace BBTech.Core.Testing
{
    public enum TestState { NotTested, Testing, Pass, Fail, Skipped }

    public enum OverallResult { Incomplete, Pass, Fail }

    public enum HardwareTest
    {
        Battery, Storage, Display, Keyboard, Touchpad, Speaker, Headphone, Microphone, Camera, Usb, WiFi, Bluetooth, Ram, PhysicalInspection, OtherInspection,
        SoftwareDisplay, SoftwareKeyboard, SoftwareMouse, SoftwareScroll, SoftwareHotkeys, SoftwareTouchpad,
        SoftwareOfficeActivation, SoftwareWindowsActivation,
        // Added in 1.3/1.4 (appended: stored results use the names, existing ids never change)
        Mouse, SoftwareDrivers, SoftwareAudio, SoftwareMicrophone, SoftwareCamera,
        // 1.5: QC checklist confirmations and the final delivery checklist (visual PASS/FAIL items)
        QcWindows, QcSoftware, QcActivation, QcAccessories, QcOverall,
        DeliveryPhysical, DeliveryDisplay, DeliveryKeyboard, DeliveryCharger, DeliveryAccessories, DeliveryWindows, DeliverySoftware, DeliveryCustomerConfig
    }

    public static class TestText
    {
        public static string Label(TestState s)
        {
            switch (s)
            {
                case TestState.Testing: return "TESTING";
                case TestState.Pass: return "PASS";
                case TestState.Fail: return "FAIL";
                case TestState.Skipped: return "SKIPPED";
                default: return "NOT TESTED";
            }
        }

        public static string Label(OverallResult r)
        {
            switch (r)
            {
                case OverallResult.Pass: return "PASS";
                case OverallResult.Fail: return "FAIL";
                default: return "INCOMPLETE";
            }
        }

        public static string Name(HardwareTest t)
        {
            switch (t)
            {
                case HardwareTest.Speaker: return "Sound"; // the speaker test (tones, sweep, BBTech test media)
                case HardwareTest.Storage: return "Storage / SSD";
                case HardwareTest.Usb: return "USB";
                case HardwareTest.WiFi: return "Wi-Fi";
                case HardwareTest.Ram: return "RAM";
                case HardwareTest.PhysicalInspection: return "Physical Inspection";
                case HardwareTest.OtherInspection: return "Other Inspection";
                case HardwareTest.SoftwareDisplay: return "Display & Driver";
                case HardwareTest.SoftwareKeyboard: return "Keyboard Matrix";
                case HardwareTest.SoftwareMouse: return "Mouse Tracking";
                case HardwareTest.SoftwareScroll: return "Mouse Scroll Wheel";
                case HardwareTest.SoftwareHotkeys: return "System Hotkeys";
                case HardwareTest.SoftwareTouchpad: return "Touchpad & Gestures";
                case HardwareTest.SoftwareOfficeActivation: return "Microsoft Office Activation";
                case HardwareTest.SoftwareWindowsActivation: return "Windows Activation";
                case HardwareTest.SoftwareDrivers: return "Drivers";
                case HardwareTest.SoftwareAudio: return "Audio Output";
                case HardwareTest.SoftwareMicrophone: return "Microphone Input";
                case HardwareTest.SoftwareCamera: return "Camera";
                case HardwareTest.QcWindows: return "Windows";
                case HardwareTest.QcSoftware: return "Software";
                case HardwareTest.QcActivation: return "Activation";
                case HardwareTest.QcAccessories: return "Accessories";
                case HardwareTest.QcOverall: return "Overall";
                case HardwareTest.DeliveryPhysical: return "Physical";
                case HardwareTest.DeliveryDisplay: return "Display";
                case HardwareTest.DeliveryKeyboard: return "Keyboard";
                case HardwareTest.DeliveryCharger: return "Charger";
                case HardwareTest.DeliveryAccessories: return "Accessories";
                case HardwareTest.DeliveryWindows: return "Windows";
                case HardwareTest.DeliverySoftware: return "Software";
                case HardwareTest.DeliveryCustomerConfig: return "Customer setup";
                default: return t.ToString();
            }
        }
    }

    /// <summary>One row in the TEST section. Observable so the UI updates automatically.</summary>
    public sealed class TestItem : INotifyPropertyChanged
    {
        private TestState _state = TestState.NotTested;
        private bool _required = true;
        private string _notes = "";
        private DateTime? _completedAt;

        public TestItem(HardwareTest kind, string description, bool required = true)
        {
            Kind = kind;
            Description = description;
            _required = required;
        }

        public HardwareTest Kind { get; }
        public string Name => TestText.Name(Kind);
        /// <summary>One of the four REQUIRED tests (Display, Sound, Keyboard, Battery): cannot be removed.</summary>
        public bool Mandatory => TestCatalog.IsRequired(Kind);
        public string Description { get; }

        public TestState State
        {
            get => _state;
            set { if (_state != value) { _state = value; Raise(); Raise(nameof(StateLabel)); } }
        }

        public string StateLabel => TestText.Label(State);

        /// <summary>True when the test is selected for the inspection; only selected tests affect the overall result.</summary>
        public bool Required
        {
            get => _required;
            set { if (_required != value) { _required = value; Raise(); } }
        }

        /// <summary>Short evidence/notes recorded by the test (e.g. "84/84 keys", "3 networks found").</summary>
        public string Notes
        {
            get => _notes;
            set { if (_notes != value) { _notes = value ?? ""; Raise(); } }
        }

        public DateTime? CompletedAt
        {
            get => _completedAt;
            set { if (_completedAt != value) { _completedAt = value; Raise(); } }
        }

        public void Complete(TestState state, string notes)
        {
            State = state;
            Notes = notes ?? "";
            CompletedAt = DateTime.Now;
        }

        public void Reset()
        {
            State = TestState.NotTested;
            Notes = "";
            CompletedAt = null;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static class OverallResultCalculator
    {
        /// <summary>
        /// Rules (tests selected for the inspection only; <see cref="TestItem.Required"/> = selected):
        ///   any FAIL                    -> FAIL
        ///   all PASS                    -> PASS
        ///   otherwise (NOT TESTED, TESTING or SKIPPED remaining) -> INCOMPLETE
        /// A selected test that was skipped is not a pass. Tests that are not selected (see TestPlan)
        /// never affect the result.
        /// </summary>
        public static OverallResult Calculate(IEnumerable<TestItem> items)
        {
            var required = (items ?? Enumerable.Empty<TestItem>()).Where(i => i.Required).ToList();
            if (required.Any(i => i.State == TestState.Fail)) return OverallResult.Fail;
            if (required.Count > 0 && required.All(i => i.State == TestState.Pass)) return OverallResult.Pass;
            return OverallResult.Incomplete;
        }
    }
}
