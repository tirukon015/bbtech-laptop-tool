using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using BBTech.Core.Storage;

namespace BBTech.Core.Testing
{
    public static class Departments
    {
        public const string Hardware = "Hardware";
        public const string Software = "Software";
        public const string Qc = "QC";
        public const string Ecommerce = "E-Commerce";
    }

    /// <summary>One test definition: which department runs it, whether it is required, and whether SKIPPED is acceptable.</summary>
    [DataContract]
    public sealed class TestRequirement
    {
        [DataMember] public string TestId { get; set; }
        [DataMember] public string Department { get; set; }
        [DataMember] public string Label { get; set; }
        [DataMember] public bool Required { get; set; }
        /// <summary>
        /// SKIPPED satisfies the requirement only when this is true (hardware the laptop genuinely may not
        /// have, e.g. no headphone jack). Otherwise SKIPPED counts as "not done", so it cannot be used to
        /// get around a required test.
        /// </summary>
        [DataMember] public bool SkipAllowed { get; set; }
    }

    /// <summary>
    /// The test definitions QC uses. Built-in defaults below; the server's master configuration replaces
    /// them (cached locally for offline use) so new requirements do not need a new release.
    /// </summary>
    [DataContract]
    public sealed class QcRequirements
    {
        [DataMember] public int Version { get; set; }
        [DataMember] public List<TestRequirement> Tests { get; set; } = new List<TestRequirement>();
        /// <summary>
        /// Normal lifecycle is Hardware → Software → QC: Software work needs the Hardware handover and QC work
        /// needs both. Set true only if BBTech decides departments may work out of order.
        /// </summary>
        [DataMember] public bool AllowOutOfOrderWork { get; set; }

        public IReadOnlyList<string> Required(string department) =>
            Tests.Where(t => t.Required && string.Equals(t.Department, department, StringComparison.OrdinalIgnoreCase)).Select(t => t.TestId).ToList();

        public IReadOnlyList<string> All(string department) =>
            Tests.Where(t => string.Equals(t.Department, department, StringComparison.OrdinalIgnoreCase)).Select(t => t.TestId).ToList();

        public bool SkipAllowed(string department, string testId) =>
            Tests.Any(t => t.SkipAllowed && t.TestId == testId && string.Equals(t.Department, department, StringComparison.OrdinalIgnoreCase));

        /// <summary>True when the definitions are usable (each department has at least one required test).</summary>
        public bool IsValid =>
            Tests != null && Tests.All(t => !string.IsNullOrWhiteSpace(t.TestId) && !string.IsNullOrWhiteSpace(t.Department)) &&
            new[] { Departments.Hardware, Departments.Software, Departments.Qc }.All(d => Required(d).Count > 0);

        private static TestRequirement T(string dept, HardwareTest t, bool required = true, bool skipAllowed = false) =>
            new TestRequirement { TestId = t.ToString(), Department = dept, Label = TestText.Name(t), Required = required, SkipAllowed = skipAllowed };

        public static QcRequirements Default()
        {
            const string H = Departments.Hardware, S = Departments.Software, Q = Departments.Qc;
            return new QcRequirements
            {
                Version = 0,
                Tests = new List<TestRequirement>
                {
                    T(H, HardwareTest.Battery), T(H, HardwareTest.Storage), T(H, HardwareTest.Ram), T(H, HardwareTest.Display),
                    T(H, HardwareTest.Keyboard), T(H, HardwareTest.Touchpad), T(H, HardwareTest.Speaker),
                    T(H, HardwareTest.Headphone, skipAllowed: true),     // some models have no headphone jack
                    T(H, HardwareTest.Microphone), T(H, HardwareTest.Camera), T(H, HardwareTest.Usb), T(H, HardwareTest.WiFi),
                    T(H, HardwareTest.Bluetooth, skipAllowed: true),     // some models have no Bluetooth radio
                    T(H, HardwareTest.Mouse, required: false, skipAllowed: true),
                    T(H, HardwareTest.PhysicalInspection), T(H, HardwareTest.OtherInspection),

                    T(S, HardwareTest.SoftwareDrivers), T(S, HardwareTest.SoftwareDisplay), T(S, HardwareTest.SoftwareKeyboard),
                    T(S, HardwareTest.SoftwareMouse, skipAllowed: true), T(S, HardwareTest.SoftwareScroll, skipAllowed: true),
                    T(S, HardwareTest.SoftwareHotkeys), T(S, HardwareTest.SoftwareTouchpad),
                    T(S, HardwareTest.SoftwareAudio), T(S, HardwareTest.SoftwareMicrophone), T(S, HardwareTest.SoftwareCamera),
                    T(S, HardwareTest.SoftwareWindowsActivation),
                    T(S, HardwareTest.SoftwareOfficeActivation, required: false),   // not every laptop ships with Office
                    new TestRequirement { TestId = SoftwareOfficeInstallId, Department = S, Label = "Microsoft Office Installation", Required = false },

                    T(Q, HardwareTest.Display), T(Q, HardwareTest.Keyboard), T(Q, HardwareTest.Touchpad), T(Q, HardwareTest.Battery),
                    T(Q, HardwareTest.Camera), T(Q, HardwareTest.Speaker), T(Q, HardwareTest.WiFi), T(Q, HardwareTest.Usb),
                    T(Q, HardwareTest.PhysicalInspection), T(Q, HardwareTest.OtherInspection),
                    T(Q, HardwareTest.Mouse, skipAllowed: true), T(Q, HardwareTest.Microphone), T(Q, HardwareTest.Bluetooth, skipAllowed: true),
                    T(Q, HardwareTest.Storage), T(Q, HardwareTest.QcWindows), T(Q, HardwareTest.QcSoftware), T(Q, HardwareTest.QcActivation),
                    T(Q, HardwareTest.QcAccessories), T(Q, HardwareTest.QcOverall),

                    // Software: Windows preparation is tracked and verified
                    new TestRequirement { TestId = LifecycleEvents.WindowsInstall, Department = S, Label = "Windows Installation", Required = true },

                    // Final check before delivery (separate from the preparation QC)
                    T(LifecycleEvents.DeliveryGroup, HardwareTest.DeliveryPhysical), T(LifecycleEvents.DeliveryGroup, HardwareTest.DeliveryDisplay),
                    T(LifecycleEvents.DeliveryGroup, HardwareTest.DeliveryKeyboard), T(LifecycleEvents.DeliveryGroup, HardwareTest.DeliveryCharger),
                    T(LifecycleEvents.DeliveryGroup, HardwareTest.DeliveryAccessories), T(LifecycleEvents.DeliveryGroup, HardwareTest.DeliveryWindows),
                    T(LifecycleEvents.DeliveryGroup, HardwareTest.DeliverySoftware),
                    T(LifecycleEvents.DeliveryGroup, HardwareTest.DeliveryCustomerConfig, skipAllowed: true),
                },
            };
        }

        public const string SoftwareOfficeInstallId = "SoftwareOfficeInstall";
    }

    /// <summary>Built-in default lists (kept for callers that only need the defaults).</summary>
    public static class DepartmentRequirements
    {
        private static readonly QcRequirements D = QcRequirements.Default();
        public static IReadOnlyList<string> Hardware => D.Required(Departments.Hardware);
        public static IReadOnlyList<string> Software => D.Required(Departments.Software);
        public static IReadOnlyList<string> QcChecklist => D.Required(Departments.Qc);
    }

    /// <summary>The state of one department for the laptop, as QC sees it.</summary>
    public sealed class DepartmentStatus
    {
        public string Department { get; set; }
        public IReadOnlyList<string> Required { get; set; } = new List<string>();
        public List<TestResultRecord> Latest { get; set; } = new List<TestResultRecord>();
        public List<string> Missing { get; set; } = new List<string>();
        public List<TestResultRecord> Failed { get; set; } = new List<TestResultRecord>();
        /// <summary>Required tests marked SKIPPED although skipping is not allowed for them.</summary>
        public List<string> SkippedNotAllowed { get; set; } = new List<string>();
        public List<TestResultRecord> Skipped { get; set; } = new List<TestResultRecord>();
        public List<TestResultRecord> Passed { get; set; } = new List<TestResultRecord>();
        /// <summary>Distinct operators of the latest results (who did the work).</summary>
        public List<string> Operators { get; set; } = new List<string>();
        public DateTime? LastAt { get; set; }

        public bool Complete => Missing.Count == 0 && Failed.Count == 0;
        public bool Started => Latest.Count > 0;

        public string Summary
        {
            get
            {
                if (!Started) return "Not started";
                if (Failed.Count > 0) return Failed.Count + " failed";
                if (Missing.Count > 0) return Missing.Count + " not done";
                return "All passed";
            }
        }
    }

    public sealed class QcGateReport
    {
        public DepartmentStatus Hardware { get; set; }
        public DepartmentStatus Software { get; set; }
        public DepartmentStatus QcChecklist { get; set; }
        public List<string> BlockingReasons { get; } = new List<string>();
        /// <summary>QC may record PASS only when nothing blocks it. FAIL can always be recorded.</summary>
        public bool CanPass => BlockingReasons.Count == 0;
    }

    /// <summary>
    /// QC is the final gate. Rules:
    ///  * the LATEST result of each test counts (history is kept, but an older PASS does not hide a newer FAIL);
    ///  * PASS satisfies a requirement; SKIPPED only where the definition allows skipping; FAIL, NOT TESTED,
    ///    TESTING or no record do not;
    ///  * final QC PASS needs Hardware, Software (including Windows activation) and the QC checklist complete;
    ///  * final QC FAIL can always be recorded.
    /// </summary>
    public static class QcGate
    {
        /// <summary>Test id under which the final QC decision is stored.</summary>
        public const string QcDecisionTestId = "QC_FINAL";

        public static DepartmentStatus Evaluate(string department, IEnumerable<TestResultRecord> latestResults, IEnumerable<string> required, Func<string, bool> skipAllowed = null)
        {
            skipAllowed = skipAllowed ?? (_ => true);
            var requiredList = (required ?? Enumerable.Empty<string>()).ToList();
            var latest = (latestResults ?? Enumerable.Empty<TestResultRecord>()).Where(r => r != null && !r.Deleted).ToList();
            var byTest = latest.GroupBy(r => r.TestId)
                               .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CompletedAt ?? r.StartedAt ?? DateTime.MinValue).First());
            var st = new DepartmentStatus { Department = department, Latest = byTest.Values.ToList(), Required = requiredList };
            foreach (var id in requiredList)
            {
                if (!byTest.TryGetValue(id, out var r)) st.Missing.Add(id);
                else if (r.Status == "FAIL") st.Failed.Add(r);
                else if (r.Status == "SKIPPED" && !skipAllowed(id)) { st.Missing.Add(id); st.SkippedNotAllowed.Add(id); }
                else if (r.Status != "PASS" && r.Status != "SKIPPED") st.Missing.Add(id);
            }
            // A failed optional test is still shown to QC as a failure.
            st.Failed.AddRange(st.Latest.Where(r => r.Status == "FAIL" && !st.Failed.Contains(r)));
            st.Passed = st.Latest.Where(r => r.Status == "PASS").ToList();
            st.Skipped = st.Latest.Where(r => r.Status == "SKIPPED").ToList();
            st.Operators = st.Latest.Select(r => r.OperatorName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            st.LastAt = st.Latest.Select(r => r.CompletedAt ?? r.StartedAt).Where(d => d.HasValue).DefaultIfEmpty().Max();
            return st;
        }

        public static QcGateReport Evaluate(IEnumerable<TestResultRecord> hardware, IEnumerable<TestResultRecord> software, IEnumerable<TestResultRecord> qcChecklist, QcRequirements requirements = null)
        {
            var req = requirements != null && requirements.IsValid ? requirements : QcRequirements.Default();
            DepartmentStatus Dept(string d, IEnumerable<TestResultRecord> rs) => Evaluate(d, rs, req.Required(d), id => req.SkipAllowed(d, id));
            var report = new QcGateReport
            {
                Hardware = Dept(Departments.Hardware, hardware),
                Software = Dept(Departments.Software, software),
                QcChecklist = Dept(Departments.Qc, (qcChecklist ?? Enumerable.Empty<TestResultRecord>()).Where(r => r.TestId != QcDecisionTestId)),
            };
            Explain(report.Hardware, report.BlockingReasons);
            Explain(report.Software, report.BlockingReasons);
            Explain(report.QcChecklist, report.BlockingReasons);
            return report;
        }

        private static void Explain(DepartmentStatus d, List<string> reasons)
        {
            string name = d.Department == Departments.Qc ? "QC checklist" : d.Department;
            var requiredFailed = d.Failed.Where(f => d.Required.Contains(f.TestId)).ToList();
            if (requiredFailed.Count > 0) reasons.Add(name + ": " + requiredFailed.Count + " failed (" + string.Join(", ", requiredFailed.Select(f => f.TestName ?? f.TestId)) + ")");
            var plainMissing = d.Missing.Except(d.SkippedNotAllowed).ToList();
            if (plainMissing.Count > 0) reasons.Add(name + ": " + plainMissing.Count + " not done (" + string.Join(", ", plainMissing.Select(Label)) + ")");
            if (d.SkippedNotAllowed.Count > 0) reasons.Add(name + ": skipped but required (" + string.Join(", ", d.SkippedNotAllowed.Select(Label)) + ")");
        }

        public static string Label(string testId) =>
            Enum.TryParse(testId, out HardwareTest t) ? TestText.Name(t) : testId;
    }
}
