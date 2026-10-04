using System;
using System.IO;
using System.Linq;
using BBTech.Core.Identity;
using BBTech.Core.Storage;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Input rules of the laptop's QC repository (invalid calls fail clearly and write nothing).</summary>
    public class RepositoryRulesTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-reporules-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly OperatorSnapshot Op = new OperatorSnapshot { OperatorId = "op-1", Name = "Operator One", Role = "QC" };
        private static readonly DeviceSpecs Specs = new DeviceSpecs { Cpu = "i5", Ram = "16 GB" };

        private QcRepository Open()
        {
            var r = QcRepository.Open(_dir, new FakeMachine(7), QcStoreTests.LaptopA);
            Assert.True(r.Ok, r.Message);
            return r.Repository;
        }

        [Fact]
        public void Updating_an_unknown_device_fails_with_a_clear_message()
        {
            var repo = Open();
            var ex = Assert.Throws<InvalidOperationException>(() => repo.UpdateDevice("missing", QcStoreTests.LaptopA, Specs));
            Assert.Equal("Unknown device.", ex.Message);
            Assert.Throws<InvalidOperationException>(() => repo.UpdateDevice(null, QcStoreTests.LaptopA, Specs));
        }
    }
}
