using System;
using System.IO;
using BBTech.Core.Identity;
using BBTech.Core.Security;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>The dual-locked QC data key (machine lock + hardware recovery locks).</summary>
    public class KeyVaultTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-keyvault-" + Guid.NewGuid().ToString("N"));
        private string KeyPath => Path.Combine(_dir, KeyVault.KeyFileName);

        public KeyVaultTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly DeviceIdentity Laptop = QcStoreTests.LaptopA;

        [Fact]
        public void The_first_open_creates_a_key_and_later_opens_unlock_the_same_key()
        {
            var first = KeyVault.Open(_dir, new FakeMachine(1), Laptop, false);
            Assert.Equal(KeyState.CreatedNew, first.State);
            Assert.Equal(EnvelopeCrypto.KeySize, first.Key.Length);
            var second = KeyVault.Open(_dir, new FakeMachine(1), Laptop, true);
            Assert.Equal(KeyState.UnlockedMachine, second.State);
            Assert.Equal(first.Key, second.Key);
        }

        [Fact]
        public void After_recovery_the_new_windows_installation_unlocks_with_the_machine_lock()
        {
            byte[] key = KeyVault.Open(_dir, new FakeMachine(1), Laptop, false).Key;
            var recovered = KeyVault.Open(_dir, new FakeMachine(2), Laptop, true);
            Assert.Equal(KeyState.UnlockedRecovery, recovered.State);
            Assert.Equal(key, recovered.Key);
            var next = KeyVault.Open(_dir, new FakeMachine(2), null, true);
            Assert.Equal(KeyState.UnlockedMachine, next.State);
            Assert.Equal(key, next.Key);
        }

    }
}
