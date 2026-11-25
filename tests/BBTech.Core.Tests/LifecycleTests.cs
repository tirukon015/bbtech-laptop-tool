using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBTech.Core.Audit;
using BBTech.Core.Security;
using BBTech.Core.Settings;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>
    /// The real BBTech laptop lifecycle: Hardware (test / fail / fix / retest) → handover → Software → handover →
    /// QC → return loop → QC PASS → ready for E-Commerce / ITMS → final delivery check.
    /// </summary>
    public sealed class LifecycleTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-lifecycle-" + Guid.NewGuid().ToString("N"));
        private readonly QcRepository _repo;
        private readonly DeviceRecord _device;
        private readonly QcRequirements _req = QcRequirements.Default();
        private DateTime _now = new DateTime(2026, 11, 25, 9, 0, 0);

        public LifecycleTests()
        {
            Directory.CreateDirectory(_dir);
            var open = QcRepository.Open(_dir, new FakeMachine(3), QcStoreTests.LaptopA, () => _now);
            Assert.True(open.Ok, open.Message);
            _repo = open.Repository;
            _device = _repo.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs());
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private LifecycleState State() => LaptopLifecycle.Evaluate(_repo.GetDeviceHistory(_device.DeviceId), _req);

        /// <summary>Records like the service layer does: the lifecycle rule is checked first, time moves forward.</summary>
        private string Try(string dept, string testId, string status, string by, string notes = null, string details = null)
        {
            string why = LaptopLifecycle.CheckEvent(State(), dept, testId, status, _req);
            if (why != null) return why;
            _now = _now.AddMinutes(1);
            _repo.RecordTest(_device.DeviceId, testId, QcGate.Label(testId), status, by, dept, notes, details, operatorId: by.ToLowerInvariant());
            return null;
        }

        private void Ok(string dept, string testId, string status, string by, string notes = null, string details = null) =>
            Assert.Null(Try(dept, testId, status, by, notes, details));

        private void PassAll(string dept, string by, params string[] except)
        {
            foreach (var t in _req.Required(dept).Where(t => !except.Contains(t))) Ok(dept, t, "PASS", by);
        }

        [Fact]
        public void Acceptance_FullLifecycle_WithReworkLoops_AndDeliveryCheck()
        {
            const string H = Departments.Hardware, S = Departments.Software, Q = Departments.Qc;
            Assert.Equal(LifecycleStatus.Received, State().Status);

            // 1-3. Laptop enters Hardware; Hardware finds a failure; it is recorded.
            PassAll(H, "Kamal", "Battery");
            Ok(H, "Battery", "FAIL", "Kamal", "Health 54%");
            Assert.Equal(LifecycleStatus.HardwareInProgress, State().Status);
            Assert.Contains("Battery", Try(H, LifecycleEvents.HardwareHandover, "PASS", "Kamal"));          // cannot hand over with a failure

            // The normal flow cannot jump ahead.
            Assert.Contains("Hardware has not handed", Try(S, "SoftwareDrivers", "PASS", "Shadir"));
            Assert.Contains("still with Hardware", Try(Q, "Display", "PASS", "Jimmy"));

            // 4-6. Fixed, retested, handed over.
            Ok(H, LifecycleEvents.RepairPrefix + "Battery", "PASS", "Kamal", "Battery replaced");
            Ok(H, "Battery", "PASS", "Kamal", "Health 96%");
            Ok(H, LifecycleEvents.HardwareHandover, "PASS", "Kamal");
            Assert.Equal(LifecycleStatus.ReadyForSoftware, State().Status);
            Assert.Equal(3, _repo.GetDeviceHistory(_device.DeviceId).Count(r => r.TestId == "Battery" || r.TestId == "Repair:Battery")); // FAIL, repair, PASS all kept

            // 7-10. Software: Windows, checks, activation, handover.
            Ok(S, LifecycleEvents.WindowsInstall, "TESTING", "Shadir", "Started");
            Assert.Equal(LifecycleStatus.SoftwareInProgress, State().Status);
            Assert.Contains("Software is not finished", Try(S, LifecycleEvents.SoftwareHandover, "PASS", "Shadir"));
            Ok(S, LifecycleEvents.WindowsInstall, "PASS", "Shadir", "Windows 11 Pro 26200");
            PassAll(S, "Shadir", LifecycleEvents.WindowsInstall);
            Ok(S, LifecycleEvents.SoftwareHandover, "PASS", "Shadir");
            Assert.Equal(LifecycleStatus.ReadyForQc, State().Status);

            // 11. QC sees Hardware + Software and the earlier failure.
            var atQc = State();
            Assert.Equal(new[] { "Kamal" }, atQc.Gate.Hardware.Operators);
            Assert.Equal(new[] { "Shadir" }, atQc.Gate.Software.Operators);
            var issue = Assert.Single(atQc.PreviousIssues);
            Assert.Equal("Battery", issue.TestId);
            Assert.Equal("Health 54%", issue.FailureNote);
            Assert.Equal("Battery replaced", issue.RepairNote);

            // 12-13. QC finds a problem and returns the laptop to the right department.
            PassAll(Q, "Jimmy", "Speaker");
            Ok(Q, "Speaker", "FAIL", "Jimmy", "Left speaker crackles");
            Assert.Equal(LifecycleStatus.QcInProgress, State().Status);
            Assert.Contains("QC PASS is not possible", Try(Q, LifecycleEvents.QcDecision, "PASS", "Jimmy"));
            Ok(Q, LifecycleEvents.QcDecision, "FAIL", "Jimmy", "Left speaker crackles");
            Ok(Q, LifecycleEvents.QcReturn, "FAIL", "Jimmy", "Left speaker crackles", H);
            var returned = State();
            Assert.Equal(LifecycleStatus.HardwareRework, returned.Status);
            Assert.Equal(H, returned.Owner);
            Assert.Equal("Left speaker crackles", returned.PendingReturn.Comment);
            Assert.Contains("still with Hardware", Try(Q, "Speaker", "PASS", "Jimmy"));                     // QC cannot fix it by itself

            // 14-15. Hardware fixes it and hands over again: straight back to QC (Software stays valid).
            Ok(H, "Speaker", "FAIL", "Kamal", "Confirmed");
            Ok(H, LifecycleEvents.RepairPrefix + "Speaker", "PASS", "Kamal", "Speaker replaced");
            Ok(H, "Speaker", "PASS", "Kamal");
            Ok(H, LifecycleEvents.HardwareHandover, "PASS", "Kamal");
            Assert.Equal(LifecycleStatus.ReadyForQc, State().Status);

            // A second loop: QC finds an Office problem → Software → fix → QC.
            Ok(Q, "QcSoftware", "FAIL", "Jimmy", "Office not installed");
            Ok(Q, LifecycleEvents.QcDecision, "FAIL", "Jimmy", "Office not installed");
            Ok(Q, LifecycleEvents.QcReturn, "FAIL", "Jimmy", "Office not installed", S);
            Assert.Equal(LifecycleStatus.SoftwareRework, State().Status);
            Ok(S, QcRequirements.SoftwareOfficeInstallId, "PASS", "Shadir", "Office 2024 installed");
            Ok(S, LifecycleEvents.SoftwareHandover, "PASS", "Shadir");
            Assert.Equal(LifecycleStatus.ReadyForQc, State().Status);

            // 16-17. QC rechecks the failed items and passes.
            Ok(Q, "Speaker", "PASS", "Jimmy");
            Ok(Q, "QcSoftware", "PASS", "Jimmy");
            Ok(Q, LifecycleEvents.QcDecision, "PASS", "Jimmy");
            var passed = State();
            Assert.Equal(LifecycleStatus.ReadyForEcommerce, passed.Status);
            Assert.True(passed.ReadyForItms);                                                                // 18. ITMS can use the ready state
            Assert.Equal(2, passed.ReturnCount);

            // 19-21. Later: final delivery check, linked to the ITMS order, recorded separately from the QC.
            Assert.Contains("hardware must be checked again", Try(Q, LifecycleEvents.DeliveryDecision, "PASS", "Jimmy", null, "ORD-123456"));
            Ok(Q, LifecycleEvents.DeviceSnapshot, "PASS", "Jimmy", "sig-1", null);                            // hardware re-checked after QC PASS
            Assert.Contains("not finished", Try(Q, LifecycleEvents.DeliveryDecision, "PASS", "Jimmy", null, "ORD-123456"));
            foreach (var t in _req.Required(LifecycleEvents.DeliveryGroup)) Ok(Q, t, t == "DeliveryCustomerConfig" ? "SKIPPED" : "PASS", "Jimmy");
            Assert.Equal(LifecycleStatus.FinalCheck, State().Status);
            Ok(Q, LifecycleEvents.DeliveryDecision, "PASS", "Jimmy", null, "ORD-123456");
            var done = State();
            Assert.Equal(LifecycleStatus.ReadyForDelivery, done.Status);
            Assert.Equal("ORD-123456", done.DeliveryDecision.Details);
            Assert.Equal("PASS", done.QcDecision.Status);                                                    // the preparation QC stays a separate record

            // Traceability: the whole story is in one ordered timeline, nothing overwritten.
            var timeline = LaptopLifecycle.Timeline(_repo.GetDeviceHistory(_device.DeviceId));
            Assert.Equal(2, timeline.Count(e => e.Kind == "RETURN"));
            Assert.Equal(2, timeline.Count(e => e.Kind == "REPAIR"));                                        // battery, speaker
            Assert.Equal(3, timeline.Count(e => e.Kind == "QC_DECISION"));                                   // FAIL, FAIL, PASS
            Assert.True(timeline.Zip(timeline.Skip(1), (a, b) => a.At <= b.At).All(x => x));
            Assert.Contains(timeline, e => e.Title.Contains("ORD-123456") && e.Result == "READY FOR DELIVERY");

            // And it survives a restart.
            var reopened = QcRepository.Open(_dir, new FakeMachine(3), QcStoreTests.LaptopA).Repository;
            Assert.Equal(LifecycleStatus.ReadyForDelivery, LaptopLifecycle.Evaluate(reopened.GetDeviceHistory(_device.DeviceId), _req).Status);
        }

        [Fact]
        public void QcPass_IsSuperseded_WhenTheLaptopIsReturnedLater()
        {
            PassAll(Departments.Hardware, "Kamal"); Ok(Departments.Hardware, LifecycleEvents.HardwareHandover, "PASS", "Kamal");
            PassAll(Departments.Software, "Shadir"); Ok(Departments.Software, LifecycleEvents.SoftwareHandover, "PASS", "Shadir");
            PassAll(Departments.Qc, "Jimmy"); Ok(Departments.Qc, LifecycleEvents.QcDecision, "PASS", "Jimmy");
            Assert.True(State().ReadyForItms);

            Ok(Departments.Qc, LifecycleEvents.QcDecision, "FAIL", "Jimmy", "Screen scratched in storage");
            Ok(Departments.Qc, LifecycleEvents.QcReturn, "FAIL", "Jimmy", "Screen scratched in storage", Departments.Hardware);
            var s = State();
            Assert.False(s.ReadyForItms);
            Assert.Equal(LifecycleStatus.HardwareRework, s.Status);
            Assert.Contains("needs a QC PASS", Try(Departments.Qc, LifecycleEvents.DeliveryDecision, "FAIL", "Jimmy", "x"));
        }

        [Fact]
        public void DeliveryHold_IsRecorded_WithReason_AndCanBeReleasedLater()
        {
            PassAll(Departments.Hardware, "Kamal"); Ok(Departments.Hardware, LifecycleEvents.HardwareHandover, "PASS", "Kamal");
            PassAll(Departments.Software, "Shadir"); Ok(Departments.Software, LifecycleEvents.SoftwareHandover, "PASS", "Shadir");
            PassAll(Departments.Qc, "Jimmy"); Ok(Departments.Qc, LifecycleEvents.QcDecision, "PASS", "Jimmy");

            Ok(Departments.Qc, "DeliveryCharger", "FAIL", "Jimmy", "Charger missing");
            Ok(Departments.Qc, LifecycleEvents.DeliveryDecision, "FAIL", "Jimmy", "Charger missing", "ORD-9");
            Assert.Equal(LifecycleStatus.DeliveryHold, State().Status);
            Assert.True(State().ReadyForItms);                                   // the preparation QC is still PASS

            foreach (var t in _req.Required(LifecycleEvents.DeliveryGroup)) Ok(Departments.Qc, t, "PASS", "Jimmy");
            Ok(Departments.Qc, LifecycleEvents.DeviceSnapshot, "PASS", "Jimmy", "sig-1", null);
            Ok(Departments.Qc, LifecycleEvents.DeliveryDecision, "PASS", "Jimmy", null, "ORD-9");
            Assert.Equal(LifecycleStatus.ReadyForDelivery, State().Status);
        }

        [Fact]
        public void HardwareFailureAfterHandover_PullsTheLaptopBack()
        {
            PassAll(Departments.Hardware, "Kamal"); Ok(Departments.Hardware, LifecycleEvents.HardwareHandover, "PASS", "Kamal");
            Assert.Equal(LifecycleStatus.ReadyForSoftware, State().Status);
            Ok(Departments.Hardware, "Display", "FAIL", "Kamal", "Backlight died");
            Assert.Equal(LifecycleStatus.HardwareRework, State().Status);        // a handover never hides a newer failure
            Assert.NotNull(Try(Departments.Software, "SoftwareDrivers", "PASS", "Shadir"));
        }

        [Fact]
        public void OutOfOrderWork_IsOnlyPossible_WhenConfigured()
        {
            Assert.NotNull(Try(Departments.Software, "SoftwareDrivers", "PASS", "Shadir"));
            _req.AllowOutOfOrderWork = true;
            Assert.Null(Try(Departments.Software, "SoftwareDrivers", "PASS", "Shadir"));
            Assert.NotNull(Try(Departments.Software, LifecycleEvents.SoftwareHandover, "PASS", "Shadir")); // handovers still need the real state
        }

        [Fact]
        public void Authorization_QcReturnAndDeliveryNeedApprover_LifecycleEventsCannotBeDeleted()
        {
            var helper = new OperatorRecord { OperatorId = "h", Name = "Helper", Active = true, Departments = new List<string> { "QC" }, Permissions = new List<string> { AppPermissions.QcInspect } };
            var admin = new OperatorRecord { OperatorId = "a", Name = "Admin", Role = "Super Admin", Active = true };
            Assert.NotNull(ResultAuthorization.CheckRecord(helper, Departments.Qc, LifecycleEvents.QcReturn));
            Assert.NotNull(ResultAuthorization.CheckRecord(helper, Departments.Qc, LifecycleEvents.DeliveryDecision));
            Assert.Null(ResultAuthorization.CheckRecord(helper, Departments.Qc, "DeliveryCharger"));
            foreach (var id in new[] { LifecycleEvents.HardwareHandover, LifecycleEvents.SoftwareHandover, LifecycleEvents.QcReturn, LifecycleEvents.QcDecision, LifecycleEvents.DeliveryDecision })
                Assert.NotNull(ResultAuthorization.CheckDelete(admin, id));

            PassAll(Departments.Hardware, "Kamal"); Ok(Departments.Hardware, LifecycleEvents.HardwareHandover, "PASS", "Kamal");
            var handover = _repo.GetLatestTestResult(_device.DeviceId, LifecycleEvents.HardwareHandover, Departments.Hardware);
            Assert.False(_repo.DeleteTestResult(handover.TestResultId));
        }

        [Theory]
        [InlineData("Hardware", "Battery", "FAIL", false, AuditActions.HardwareFail)]
        [InlineData("Hardware", "Battery", "PASS", true, AuditActions.HardwareRetest)]
        [InlineData("Hardware", "Battery", "PASS", false, AuditActions.HardwarePass)]
        [InlineData("Hardware", "Repair:Battery", "PASS", false, AuditActions.HardwareRepair)]
        [InlineData("Hardware", LifecycleEvents.HardwareHandover, "PASS", false, AuditActions.HardwareHandover)]
        [InlineData("Software", LifecycleEvents.WindowsInstall, "PASS", false, AuditActions.WindowsInstalled)]
        [InlineData("Software", "SoftwareWindowsActivation", "PASS", false, AuditActions.ActivationCheck)]
        [InlineData("Software", LifecycleEvents.SoftwareHandover, "PASS", false, AuditActions.SoftwarePass)]
        [InlineData("QC", "Display", "PASS", false, AuditActions.QcItemChecked)]
        [InlineData("QC", LifecycleEvents.QcReturn, "FAIL", false, AuditActions.QcRework)]
        [InlineData("QC", LifecycleEvents.QcDecision, "PASS", false, AuditActions.QcPass)]
        [InlineData("QC", LifecycleEvents.QcDecision, "FAIL", false, AuditActions.QcFail)]
        [InlineData("QC", LifecycleEvents.DeliveryDecision, "PASS", false, AuditActions.FinalQcPass)]
        [InlineData("QC", LifecycleEvents.DeliveryDecision, "FAIL", false, AuditActions.FinalQcFail)]
        public void AuditAction_MatchesTheLifecycleEvent(string dept, string testId, string status, bool retest, string expected) =>
            Assert.Equal(expected, AuditActions.ForRecord(dept, testId, status, retest));
    }
}
