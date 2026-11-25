using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>
    /// Real-hardware harness (does nothing unless both variables are set):
    ///   BBTECH_REAL_INVENTORY = TechnicalInventory JSON captured by the tool on a real laptop
    ///   BBTECH_ITMS_URL       = an ITMS (or reference ITMS) implementing contract 1.1 §3.4
    /// Runs the real snapshot through the encrypted local store, the full lifecycle up to READY FOR DELIVERY and the real
    /// HTTP connector, and writes the outcome to BBTECH_REAL_OUT (default: next to the inventory file).
    /// </summary>
    public sealed class RealHardwarePipelineTests
    {
        [Fact]
        public async Task RealLaptopSnapshot_ThroughLifecycle_ToItms()
        {
            string invPath = Environment.GetEnvironmentVariable("BBTECH_REAL_INVENTORY");
            string itms = Environment.GetEnvironmentVariable("BBTECH_ITMS_URL");
            if (string.IsNullOrEmpty(invPath) || string.IsNullOrEmpty(itms)) return;
            string outDir = Environment.GetEnvironmentVariable("BBTECH_REAL_OUT") ?? Path.GetDirectoryName(invPath);
            string dir = Path.Combine(Path.GetTempPath(), "bbtech-real-" + Guid.NewGuid().ToString("N"));

            var real = TechnicalInventory.FromJson(File.ReadAllText(invPath));
            Assert.NotNull(real);
            var repo = QcRepository.Open(dir, new FakeMachine(31), QcStoreTests.LaptopA).Repository;
            var d = repo.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs());
            void Rec(string dept, string id, string status, string by, string notes = null, string details = null)
            { Thread.Sleep(12); repo.RecordTest(d.DeviceId, id, id, status, by, dept, notes, details, operatorId: by.ToLowerInvariant()); }
            void Snap(string stage, string dept, string by) { real.Stage = stage; real.CapturedAt = DateTime.UtcNow; Rec(dept, LifecycleEvents.DeviceSnapshot, "PASS", by, real.HardwareSignature(), real.ToJson()); }

            var req = QcRequirements.Default();
            foreach (var t in req.Required(Departments.Hardware)) Rec(Departments.Hardware, t, "PASS", "Kamal");
            Rec(Departments.Hardware, "Repair:Keyboard", "PASS", "Kamal", "Keyboard replaced, retested");
            Snap("HARDWARE_HANDOVER", Departments.Hardware, "Kamal");
            Rec(Departments.Hardware, LifecycleEvents.HardwareHandover, "PASS", "Kamal");
            Rec(Departments.Software, "SoftwareWindowsInstall", "PASS", "Shadir", "Windows 11 Pro build 26200");
            foreach (var t in req.Required(Departments.Software)) Rec(Departments.Software, t, "PASS", "Shadir");
            Snap("SOFTWARE_HANDOVER", Departments.Software, "Shadir");
            Rec(Departments.Software, LifecycleEvents.SoftwareHandover, "PASS", "Shadir");
            Snap("QC_START", Departments.Qc, "Jimmy");
            foreach (var t in req.Required(Departments.Qc)) Rec(Departments.Qc, t, "PASS", "Jimmy");
            Rec(Departments.Qc, LifecycleEvents.QcDecision, "PASS", "Jimmy");
            Snap("QC_DECISION", Departments.Qc, "Jimmy");
            foreach (var t in req.Required(LifecycleEvents.DeliveryGroup)) Rec(Departments.Qc, t, "PASS", "Jimmy");
            Snap("FINAL_CHECK", Departments.Qc, "Jimmy");
            Rec(Departments.Qc, LifecycleEvents.DeliveryDecision, "PASS", "Jimmy", "", "ORD-REALTEST-1");
            var state = LaptopLifecycle.Evaluate(repo.GetDeviceHistory(d.DeviceId));

            var outcome = await new ItmsTransmissionService(repo, new HttpItmsConnector(() => itms, () => "reference-key")).SendOrContinueAsync(d);
            File.WriteAllText(Path.Combine(outDir, "real_pipeline_result.txt"),
                "lifecycle=" + state.Status + "\nverification=" + outcome.TraceabilityVerification + "\nconfirmed=" + outcome.Confirmed +
                "\nreceipt=" + outcome.Record?.RemoteId + "\nerror=" + outcome.Record?.LastError + "\nmessage=" + outcome.Message + "\n");
            Assert.Equal(LifecycleStatus.ReadyForDelivery, state.Status);
            Assert.Equal("VERIFIED", outcome.TraceabilityVerification);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
