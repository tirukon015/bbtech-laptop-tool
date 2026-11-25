using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using BBTech.Core.Audit;
using BBTech.Core.Security;
using BBTech.Core.Settings;
using BBTech.Core.Software;
using BBTech.Core.Sync;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    public sealed class ArchitectureAndSecurityTests : IDisposable
    {
        private readonly string _tempDir;

        public ArchitectureAndSecurityTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "BBTech_ArchTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        // ====================================================================
        // 1. ACCOUNT SYSTEM TESTS
        // ====================================================================

        [Fact]
        public void ActiveUser_HasPermissions_AllowsAction()
        {
            var op = new OperatorRecord
            {
                OperatorId = "op1",
                Name = "Kamal",
                Role = "Technician",
                Active = true,
                Departments = new List<string> { "Hardware" },
                Permissions = new List<string> { AppPermissions.HardwareTest }
            };

            Assert.True(op.CanAccessHardware);
            Assert.True(op.CanPerform(AppPermissions.HardwareTest));
            Assert.False(op.CanAccessQc);
            Assert.False(op.CanAccessSoftware);
            Assert.False(op.CanInstallSoftware);
            Assert.False(op.CanManageUsers);
        }

        [Fact]
        public void DisabledUser_CannotPerformAnyAction()
        {
            var op = new OperatorRecord
            {
                OperatorId = "op2",
                Name = "Former Employee",
                Role = "Technician",
                Active = false,
                Departments = new List<string> { "Hardware", "Software", "QC" },
                Permissions = new List<string> { AppPermissions.HardwareTest, AppPermissions.SoftwareTest, AppPermissions.QcInspect }
            };

            Assert.False(op.CanAccessHardware);
            Assert.False(op.CanAccessQc);
            Assert.False(op.CanAccessSoftware);
            Assert.False(op.CanPerform(AppPermissions.HardwareTest));
            Assert.False(op.CanInstallSoftware);
            Assert.False(op.CanCheckActivation);
        }

        [Fact]
        public void MultipleDepartments_OperatorHasAllAssignedDepartments()
        {
            var op = new OperatorRecord
            {
                OperatorId = "op3",
                Name = "Shadir",
                Role = "Technician",
                Active = true,
                Departments = new List<string> { "Hardware", "Software" },
                Permissions = new List<string> { AppPermissions.HardwareTest, AppPermissions.SoftwareTest, AppPermissions.SoftwareInstall }
            };

            Assert.True(op.CanAccessHardware);
            Assert.True(op.CanAccessSoftware);
            Assert.True(op.CanInstallSoftware);
            Assert.False(op.CanAccessQc);
            Assert.False(op.CanAccessEcommerce);
        }

        [Fact]
        public void AdminAccount_HasAllPermissionsImplicitly()
        {
            var admin = new OperatorRecord
            {
                OperatorId = "opAdmin",
                Name = "Touhidul",
                Role = "Super Admin",
                Active = true,
                Departments = new List<string> { "Hardware", "Software", "QC", "E-Commerce", "General" },
                Permissions = new List<string> { AppPermissions.All }
            };

            Assert.True(admin.IsAdmin);
            Assert.True(admin.CanAccessHardware);
            Assert.True(admin.CanAccessQc);
            Assert.True(admin.CanAccessSoftware);
            Assert.True(admin.CanAccessEcommerce);
            Assert.True(admin.CanInstallSoftware);
            Assert.True(admin.CanCheckActivation);
            Assert.True(admin.CanExecuteActivation);
            Assert.True(admin.CanManageUsers);
            Assert.True(admin.CanDeleteRecords);
            Assert.True(admin.CanViewAudit);
        }

        // ====================================================================
        // 2. SECURITY & RECORD INTEGRITY TESTS
        // ====================================================================

        [Fact]
        public void NormalTechnician_CannotDeleteRecords()
        {
            var tech = new OperatorRecord
            {
                OperatorId = "t1",
                Name = "Kamal",
                Role = "Technician",
                Active = true,
                Permissions = new List<string> { AppPermissions.HardwareTest }
            };

            Assert.False(tech.CanDeleteRecords);
        }

        [Fact]
        public void NormalTechnician_CannotModifyPermissionsOrUsers()
        {
            var tech = new OperatorRecord
            {
                OperatorId = "t2",
                Name = "Jimmy",
                Role = "QC Inspector",
                Active = true,
                Permissions = new List<string> { AppPermissions.QcInspect }
            };

            Assert.False(tech.CanManageUsers);
            Assert.False(tech.CanPerform(AppPermissions.PermissionsManage));
            Assert.False(tech.CanPerform(AppPermissions.UsersManage));
        }

        [Fact]
        public void AuditStore_AppendsAndVerifiesIntegrity()
        {
            string auditFile = Path.Combine(_tempDir, "audit.dat");
            var store = new AuditStore(auditFile);

            var actor = new OperatorRecord { OperatorId = "adm", Name = "Touhidul", Role = "Super Admin", Active = true, Permissions = new List<string> { AppPermissions.All } };
            store.Log(actor, AuditActions.PermissionChanged, "Kamal", "Hardware", "Hardware + Software", "Department added");

            var records = store.GetRecords(actor);
            Assert.Equal(1, records.Count);
            Assert.Equal(AuditActions.PermissionChanged, records[0].Action);
            Assert.Equal("Kamal", records[0].Target);
            Assert.Equal("Hardware + Software", records[0].NewValue);
            Assert.True(records[0].VerifyIntegrity());
        }

        [Fact]
        public void AuditStore_UnauthorizedTechnicianCannotViewAuditLogs()
        {
            string auditFile = Path.Combine(_tempDir, "audit.dat");
            var store = new AuditStore(auditFile);

            var tech = new OperatorRecord { OperatorId = "tech", Name = "Kamal", Role = "Technician", Active = true, Permissions = new List<string> { AppPermissions.HardwareTest } };
            Assert.Throws<UnauthorizedAccessException>(() => store.GetRecords(tech));
        }

        // ====================================================================
        // 3. USB SYNC QUEUE & IDEMPOTENCY TESTS
        // ====================================================================

        [Fact]
        public void SyncQueue_EnqueuesItems_IdempotentOnDuplicateRecordId()
        {
            string queueFile = Path.Combine(_tempDir, "sync_queue.dat");
            var queue = new UsbSyncQueue(queueFile);

            var item1 = SyncQueueItem.FromTestResult("dev1", "op1", "Kamal", "Hardware", "Battery", "Battery", "PASS", "Healthy", "1.1.0", "REC-001");
            var itemDuplicate = SyncQueueItem.FromTestResult("dev1", "op1", "Kamal", "Hardware", "Battery", "Battery", "PASS", "Healthy", "1.1.0", "REC-001");

            queue.Enqueue(item1);
            queue.Enqueue(itemDuplicate); // duplicate RecordId

            var all = queue.GetAllItems();
            Assert.Equal(1, all.Count);
            Assert.Equal("REC-001", all[0].RecordId);
            Assert.True(all[0].VerifyIntegrity());
        }

        [Fact]
        public void SyncQueue_UploadSuccess_TransitionsToVerifiedAndPurgesSafely()
        {
            string queueFile = Path.Combine(_tempDir, "sync_queue.dat");
            var queue = new UsbSyncQueue(queueFile);

            var item1 = SyncQueueItem.FromTestResult("dev1", "op1", "Kamal", "Hardware", "Battery", "Battery", "PASS", "Healthy", "1.1.0", "REC-001");
            var item2 = SyncQueueItem.FromTestResult("dev1", "op1", "Kamal", "Hardware", "Storage", "Storage", "PASS", "SMART OK", "1.1.0", "REC-002");

            queue.Enqueue(item1);
            queue.Enqueue(item2);

            // Step 1: Uploading
            queue.TransitionState(new[] { "REC-001", "REC-002" }, SyncState.Uploading);
            var pendingDuringUpload = queue.GetPendingItems();
            Assert.Equal(0, pendingDuringUpload.Count); // Not pending while uploading

            // Step 2: Server Received & Verified REC-001 ONLY
            queue.TransitionState(new[] { "REC-001" }, SyncState.Verified, "SERVER_RECEIPT_999");
            queue.TransitionState(new[] { "REC-001" }, SyncState.DeleteAllowed);

            // Step 3: Purge
            int purged = queue.PurgeVerifiedItems();
            Assert.Equal(1, purged);

            // REC-002 MUST be preserved on the USB!
            var remaining = queue.GetAllItems();
            Assert.Equal(1, remaining.Count);
            Assert.Equal("REC-002", remaining[0].RecordId);
        }

        [Fact]
        public void SyncQueue_OnUploadFailure_RetainsAllDataOnUsb()
        {
            string queueFile = Path.Combine(_tempDir, "sync_queue.dat");
            var queue = new UsbSyncQueue(queueFile);

            var item = SyncQueueItem.FromTestResult("dev1", "op1", "Kamal", "Hardware", "Battery", "Battery", "PASS", "Healthy", "1.1.0", "REC-FAIL");
            queue.Enqueue(item);

            // Simulate failure
            queue.TransitionState(new[] { "REC-FAIL" }, SyncState.Failed, null, "500 Internal Server Error");

            // Purge attempt
            int purged = queue.PurgeVerifiedItems();
            Assert.Equal(0, purged);

            // Record is retained on USB
            var items = queue.GetAllItems();
            Assert.Equal(1, items.Count);
            Assert.Equal(SyncState.Failed, items[0].State);
            Assert.Equal("500 Internal Server Error", items[0].LastError);
        }

        // ====================================================================
        // 4. SOFTWARE TEST ENUM & RESULT CREATION
        // ====================================================================

        [Fact]
        public void SoftwareTests_AreDefinedInHardwareTestEnum_AndHaveLabels()
        {
            Assert.Equal("Display & Driver", TestText.Name(HardwareTest.SoftwareDisplay));
            Assert.Equal("Keyboard Matrix", TestText.Name(HardwareTest.SoftwareKeyboard));
            Assert.Equal("Mouse Tracking", TestText.Name(HardwareTest.SoftwareMouse));
            Assert.Equal("Mouse Scroll Wheel", TestText.Name(HardwareTest.SoftwareScroll));
            Assert.Equal("System Hotkeys", TestText.Name(HardwareTest.SoftwareHotkeys));
            Assert.Equal("Touchpad & Gestures", TestText.Name(HardwareTest.SoftwareTouchpad));
        }

        // ====================================================================
        // 5. SOFTWARE INSTALLATION & ACTIVATION SECURITY
        // ====================================================================

        [Fact]
        public void SoftwarePackage_ValidChecksum_PassesVerification()
        {
            string dummyInstaller = Path.Combine(_tempDir, "TestSetup.exe");
            File.WriteAllText(dummyInstaller, "Microsoft Office Setup Payload");

            var pkg = new SoftwarePackage
            {
                PackageId = "office-test",
                Name = "Office 2024"
            };

            // Calculate actual checksum of dummyInstaller
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(dummyInstaller))
            {
                byte[] hash = sha.ComputeHash(stream);
                pkg.Sha256Checksum = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }

            Assert.True(pkg.VerifyFileIntegrity(dummyInstaller));
        }

        [Fact]
        public void SoftwarePackage_ChecksumMismatch_FailsVerification()
        {
            string dummyInstaller = Path.Combine(_tempDir, "TamperedSetup.exe");
            File.WriteAllText(dummyInstaller, "Tampered Payload");

            var pkg = new SoftwarePackage
            {
                PackageId = "office-tampered",
                Name = "Office 2024",
                Sha256Checksum = "0000000000000000000000000000000000000000000000000000000000000000"
            };

            Assert.False(pkg.VerifyFileIntegrity(dummyInstaller));
        }

        [Fact]
        public void UnauthorizedUser_InstallAndActivation_SecurityChecks()
        {
            var unauthorizedOp = new OperatorRecord
            {
                OperatorId = "unauth",
                Name = "Kamal",
                Role = "Technician",
                Active = true,
                Departments = new List<string> { "Hardware" },
                Permissions = new List<string> { AppPermissions.HardwareTest }
            };

            Assert.False(unauthorizedOp.CanInstallSoftware);
            Assert.False(unauthorizedOp.CanCheckActivation);
            Assert.False(unauthorizedOp.CanExecuteActivation);
        }
    }
}
