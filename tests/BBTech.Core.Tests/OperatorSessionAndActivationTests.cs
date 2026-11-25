using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using BBTech.Core.Audit;
using BBTech.Core.Identity;
using BBTech.Core.Models;
using BBTech.Core.Security;
using BBTech.Core.Settings;
using BBTech.Core.Software;
using BBTech.Core.Storage;
using Xunit;

namespace BBTech.Core.Tests
{
    public sealed class OperatorSessionAndActivationTests : IDisposable
    {
        private readonly string _dir;
        private string FilePath => Path.Combine(_dir, "config", "settings.dat");

        public OperatorSessionAndActivationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "bbtech-session-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        // ====================================================================
        // 1. OPERATOR SESSION CREATION & 1-HOUR EXPIRY TESTS
        // ====================================================================

        [Fact]
        public void OperatorSession_Created_HasCorrectDurationAndFields()
        {
            var testTime = new DateTime(2026, 11, 9, 19, 10, 0); // 7:10 PM
            var s = SettingsStore.Load(FilePath, () => testTime);

            var kamal = s.AddOperator("Kamal", "Technician", new[] { "Hardware" }, new[] { AppPermissions.HardwareTest });
            var session = s.CreateSession(kamal, TimeSpan.FromHours(1));

            Assert.NotNull(session);
            Assert.Equal(kamal.OperatorId, session.OperatorId);
            Assert.Equal("Kamal", session.OperatorName);
            Assert.Equal(testTime, session.SelectedAt);
            Assert.Equal(testTime.AddHours(1), session.SessionExpiresAt);
            Assert.False(session.IsExpired(testTime));
            Assert.False(session.IsExpired(testTime.AddMinutes(59)));
            Assert.True(session.IsExpired(testTime.AddMinutes(61)));
        }

        [Fact]
        public void OperatorSession_RestoredWithinOneHour_AndExpiresAtOneHourPlus()
        {
            DateTime clockTime = new DateTime(2026, 11, 9, 19, 10, 0); // 7:10 PM
            var s = SettingsStore.Load(FilePath, () => clockTime);

            var kamal = s.AddOperator("Kamal", "Technician", new[] { "Hardware" }, new[] { AppPermissions.HardwareTest });
            s.SelectProfile(kamal.OperatorId);

            // 7:10 PM: Active operator is Kamal
            Assert.NotNull(s.CurrentOperator);
            Assert.Equal("Kamal", s.CurrentOperator.Name);

            // 7:30 PM: App opened -> automatically Kamal
            clockTime = new DateTime(2026, 11, 9, 19, 30, 0);
            var appOpen730 = SettingsStore.Load(FilePath, () => clockTime);
            Assert.NotNull(appOpen730.CurrentOperator);
            Assert.Equal("Kamal", appOpen730.CurrentOperator.Name);

            // 8:00 PM: App opened -> automatically Kamal
            clockTime = new DateTime(2026, 11, 9, 20, 0, 0);
            var appOpen800 = SettingsStore.Load(FilePath, () => clockTime);
            Assert.NotNull(appOpen800.CurrentOperator);
            Assert.Equal("Kamal", appOpen800.CurrentOperator.Name);

            // 8:11 PM: Session expired (over 1 hour) -> Select Operator
            clockTime = new DateTime(2026, 11, 9, 20, 11, 0);
            var appOpen811 = SettingsStore.Load(FilePath, () => clockTime);
            Assert.Null(appOpen811.CurrentOperator);
            Assert.Null(appOpen811.GetActiveSessionOperator());
        }

        [Fact]
        public void OperatorSession_ExplicitOperatorChange_UpdatesSessionImmediately()
        {
            DateTime clockTime = new DateTime(2026, 11, 9, 19, 10, 0); // 7:10 PM
            var s = SettingsStore.Load(FilePath, () => clockTime);

            var kamal = s.AddOperator("Kamal", "Technician", new[] { "Hardware" }, new[] { AppPermissions.HardwareTest });
            var shadir = s.AddOperator("Shadir", "Technician", new[] { "Hardware", "Software" }, new[] { AppPermissions.HardwareTest, AppPermissions.SoftwareTest });

            s.SelectProfile(kamal.OperatorId);
            Assert.Equal("Kamal", s.CurrentOperator.Name);

            // At 7:40 PM, technician explicitly selects Shadir
            clockTime = new DateTime(2026, 11, 9, 19, 40, 0);
            s.SelectProfile(shadir.OperatorId);

            // Active session must be Shadir immediately
            Assert.Equal("Shadir", s.CurrentOperator.Name);
            var session = s.Settings.ActiveSession;
            Assert.NotNull(session);
            Assert.Equal(shadir.OperatorId, session.OperatorId);
            Assert.Equal(clockTime, session.SelectedAt);
            Assert.Equal(clockTime.AddHours(1), session.SessionExpiresAt); // valid until 8:40 PM

            // At 8:20 PM (Kamal would have expired, but Shadir is active until 8:40 PM)
            clockTime = new DateTime(2026, 11, 9, 20, 20, 0);
            var reloaded = SettingsStore.Load(FilePath, () => clockTime);
            Assert.NotNull(reloaded.CurrentOperator);
            Assert.Equal("Shadir", reloaded.CurrentOperator.Name);
        }

        [Fact]
        public void OperatorSession_DisabledAccount_IsRejectedImmediately()
        {
            DateTime clockTime = new DateTime(2026, 11, 9, 19, 10, 0);
            var s = SettingsStore.Load(FilePath, () => clockTime);

            var op = s.AddOperator("Former Employee", "Technician", new[] { "Hardware" }, new[] { AppPermissions.HardwareTest });
            s.SelectProfile(op.OperatorId);
            Assert.Equal("Former Employee", s.CurrentOperator.Name);

            // Admin disables account
            s.SetOperatorActive(op.OperatorId, false);

            // Cached session must NOT be accepted as authorized identity
            Assert.Null(s.CurrentOperator);
            Assert.Null(s.GetActiveSessionOperator());

            // Reload store
            var reloaded = SettingsStore.Load(FilePath, () => clockTime);
            Assert.Null(reloaded.CurrentOperator);
            Assert.Null(reloaded.GetActiveSessionOperator());
        }

        // ====================================================================
        // 2. ACTIVATION PERMISSION & WORKFLOW TESTS
        // ====================================================================

        [Fact]
        public void Activation_Permissions_EnforcedProperly()
        {
            var tech = new OperatorRecord
            {
                OperatorId = "t1",
                Name = "Kamal",
                Role = "Technician",
                Active = true,
                Permissions = new List<string> { AppPermissions.HardwareTest }
            };

            var softTech = new OperatorRecord
            {
                OperatorId = "t2",
                Name = "Shadir",
                Role = "Technician",
                Active = true,
                Permissions = new List<string> { AppPermissions.HardwareTest, AppPermissions.SoftwareTest, AppPermissions.SoftwareActivationCheck }
            };

            var admin = new OperatorRecord
            {
                OperatorId = "t3",
                Name = "Touhidul",
                Role = "Super Admin",
                Active = true,
                Permissions = new List<string> { AppPermissions.All }
            };

            // Kamal cannot check or execute activation
            Assert.False(tech.CanCheckActivation);
            Assert.False(tech.CanExecuteActivation);

            // Shadir can check but cannot execute
            Assert.True(softTech.CanCheckActivation);
            Assert.False(softTech.CanExecuteActivation);

            // Admin can do both
            Assert.True(admin.CanCheckActivation);
            Assert.True(admin.CanExecuteActivation);
        }

        [Fact]
        public void ActivationResult_SuccessAndFailure_FormulateProperStatus()
        {
            var successResult = new ActivationResult
            {
                Success = true,
                Status = "PASS",
                Target = "Windows",
                ExitCode = 0,
                LicenseStatus = "Licensed",
                Details = "Windows is permanently activated"
            };

            Assert.True(successResult.Success);
            Assert.Equal("PASS", successResult.Status);
            Assert.Equal("Windows", successResult.Target);
            Assert.Equal(0, successResult.ExitCode);

            var failResult = new ActivationResult
            {
                Success = false,
                Status = "FAIL",
                Target = "Microsoft Office",
                ExitCode = 1,
                LicenseStatus = "Unlicensed",
                Details = "License Status: Grace Expired"
            };

            Assert.False(failResult.Success);
            Assert.Equal("FAIL", failResult.Status);
            Assert.Equal("Microsoft Office", failResult.Target);
            Assert.Equal(1, failResult.ExitCode);
        }

        [Fact]
        public void Activation_AuditLogging_RecordsActionWithIntegrity()
        {
            string auditFile = Path.Combine(_dir, "audit_activation.dat");
            var audit = new AuditStore(auditFile);

            var admin = new OperatorRecord
            {
                OperatorId = "admin1",
                Name = "Touhidul",
                Role = "Super Admin",
                Active = true,
                Permissions = new List<string> { AppPermissions.All }
            };

            // Log Windows activation execution
            audit.Log(admin, AuditActions.ActivationExecuted, "Windows", null, "PASS", "Permanently activated via slmgr.vbs /ato");

            // Log Office activation execution
            audit.Log(admin, AuditActions.ActivationExecuted, "Microsoft Office", null, "FAIL", "License grace period expired via ospp.vbs /act");

            var records = audit.GetRecords(admin);
            Assert.Equal(2, records.Count);

            var winRecord = records.FirstOrDefault(r => r.Target == "Windows");
            Assert.NotNull(winRecord);
            Assert.Equal(AuditActions.ActivationExecuted, winRecord.Action);
            Assert.Equal("PASS", winRecord.NewValue);
            Assert.True(winRecord.VerifyIntegrity());

            var officeRecord = records.FirstOrDefault(r => r.Target == "Microsoft Office");
            Assert.NotNull(officeRecord);
            Assert.Equal(AuditActions.ActivationExecuted, officeRecord.Action);
            Assert.Equal("FAIL", officeRecord.NewValue);
            Assert.True(officeRecord.VerifyIntegrity());
        }

        // ====================================================================
        // 3. FOCUSED PROCESS BEHAVIOR & VISIBILITY TESTS
        // ====================================================================

        [Fact]
        public void WindowsActivation_BuildProcess_SelectsCorrectScriptAndVisibleProperties()
        {
            ActivationProcessBuilder.IsElevatedCheck = () => false;

            var psi = ActivationProcessBuilder.BuildWindowsActivationProcess(visible: true);

            // 1. Correct executable and arguments
            Assert.EndsWith("cmd.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("slmgr.vbs", psi.Arguments);
            Assert.Contains("/ato", psi.Arguments);

            // 2. Visible console behavior
            Assert.True(psi.UseShellExecute);
            Assert.False(psi.CreateNoWindow);
            Assert.Equal(ProcessWindowStyle.Normal, psi.WindowStyle);

            // 3. Elevated behavior when not elevated
            Assert.Equal("runas", psi.Verb);

            // 4. When already elevated, verb is not required
            ActivationProcessBuilder.IsElevatedCheck = () => true;
            var elevatedPsi = ActivationProcessBuilder.BuildWindowsActivationProcess(visible: true);
            Assert.NotEqual("runas", elevatedPsi.Verb);
        }

        [Fact]
        public void WindowsActivation_BuildProcess_HiddenMode_UsesCscriptDirectly()
        {
            var psi = ActivationProcessBuilder.BuildWindowsActivationProcess(visible: false);

            Assert.EndsWith("cscript.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("slmgr.vbs", psi.Arguments);
            Assert.Contains("/ato", psi.Arguments);
            Assert.False(psi.UseShellExecute);
            Assert.True(psi.CreateNoWindow);
            Assert.True(psi.RedirectStandardOutput);
        }

        [Fact]
        public void OfficeActivation_BuildProcess_SelectsCorrectScriptAndVisibleProperties()
        {
            string dummyOspp = Path.Combine(_dir, "OSPP.VBS");
            File.WriteAllText(dummyOspp, "' Dummy Microsoft Office OSPP Script");

            ActivationProcessBuilder.IsElevatedCheck = () => false;

            var psi = ActivationProcessBuilder.BuildOfficeActivationProcess(visible: true, customOsppPath: dummyOspp);

            Assert.EndsWith("cmd.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("OSPP.VBS", psi.Arguments);
            Assert.Contains("/act", psi.Arguments);
            Assert.True(psi.UseShellExecute);
            Assert.False(psi.CreateNoWindow);
            Assert.Equal(ProcessWindowStyle.Normal, psi.WindowStyle);
            Assert.Equal("runas", psi.Verb);
        }

        [Fact]
        public void OfficeActivation_MissingScript_ThrowsFileNotFoundException()
        {
            string nonExistentPath = Path.Combine(_dir, "NonExistent_ospp.vbs");
            Assert.Throws<FileNotFoundException>(() =>
                ActivationProcessBuilder.BuildOfficeActivationProcess(customOsppPath: nonExistentPath));
        }

        [Fact]
        public void Process_Execution_StartsSuccessfully_AndHandlesExitCode()
        {
            // Execute real command via cmd.exe and test exit code handling
            var psi = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "/c \"exit /b 0\"")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using (var p = Process.Start(psi))
            {
                Assert.NotNull(p);
                bool finished = p.WaitForExit(5000);
                Assert.True(finished);
                Assert.Equal(0, p.ExitCode);
            }

            var psiFail = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "/c \"exit /b 42\"")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using (var p = Process.Start(psiFail))
            {
                Assert.NotNull(p);
                bool finished = p.WaitForExit(5000);
                Assert.True(finished);
                Assert.Equal(42, p.ExitCode);
            }
        }

        [Fact]
        public void Activation_SoftwareRecord_PersistsWithOperatorAndDeviceId()
        {
            var laptop = new DeviceIdentity("LENOVO", "ThinkPad", "T14 Gen 2", "SERIALEX01", "6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1", "L1HF0001");
            var open = QcRepository.Open(_dir, new FakeMachine(1), laptop);
            Assert.True(open.Ok);
            var repo = open.Repository;

            var specs = new DeviceSpecs { Cpu = "Intel Core i7", Ram = "16GB", Storage = "512GB SSD" };
            var dev = repo.RegisterDevice(laptop, specs);

            var result = repo.RecordTest(
                dev.DeviceId,
                "SoftwareWindowsActivation",
                "Windows Activation",
                "PASS",
                "Kamal",
                "Software",
                "Exit code: 0 • Permanently activated");

            Assert.NotNull(result);
            Assert.Equal("SoftwareWindowsActivation", result.TestId);
            Assert.Equal("Windows Activation", result.TestName);
            Assert.Equal("PASS", result.Status);
            Assert.Equal("Kamal", result.OperatorName);
            Assert.Equal("Software", result.Department);
            Assert.Contains("Exit code: 0", result.Comment);
        }
    }
}
