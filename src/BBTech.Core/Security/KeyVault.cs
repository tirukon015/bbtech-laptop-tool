using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using BBTech.Core.Identity;
using BBTech.Core.Storage;

namespace BBTech.Core.Security
{
    /// <summary>Machine-bound protection of a small secret (Windows DPAPI in production).</summary>
    public interface IMachineKeyProtector
    {
        byte[] Protect(byte[] data);
        /// <summary>Returns null if the data cannot be unprotected on this machine.</summary>
        byte[] Unprotect(byte[] data);
    }

    /// <summary>Windows DPAPI, LocalMachine scope: any account on THIS Windows installation can unprotect.</summary>
    public sealed class DpapiMachineKeyProtector : IMachineKeyProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BBTech.LaptopTool.QcStore.DPAPI.v1");

        public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine);

        public byte[] Unprotect(byte[] data)
        {
            try { return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine); }
            catch (CryptographicException) { return null; }
        }
    }

    public enum KeyState
    {
        /// <summary>No store existed: a new random data key was created.</summary>
        CreatedNew,
        /// <summary>Unlocked with Windows DPAPI (normal path).</summary>
        UnlockedMachine,
        /// <summary>DPAPI failed (e.g. Windows restored/reinstalled with data kept) but the hardware recovery lock opened it.</summary>
        UnlockedRecovery,
        /// <summary>Neither lock opens the key (different laptop, or both Windows and hardware identity changed).</summary>
        Locked,
        /// <summary>A journal exists but its key file is gone: the data cannot be decrypted.</summary>
        KeyMissing,
    }

    public sealed class KeyUnlockResult
    {
        public KeyState State { get; set; }
        public byte[] Key { get; set; }
        public string Message { get; set; } = "";
        public bool Ok => Key != null;
    }

    [DataContract]
    internal sealed class RecoveryWrap
    {
        [DataMember] public string Kind { get; set; }
        [DataMember] public byte[] Salt { get; set; }
        [DataMember] public byte[] Wrapped { get; set; }
    }

    [DataContract]
    internal sealed class KeyFileModel
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public DateTime CreatedAt { get; set; }
        [DataMember] public byte[] MachineWrap { get; set; }
        [DataMember] public List<RecoveryWrap> RecoveryWraps { get; set; } = new List<RecoveryWrap>();
    }

    /// <summary>
    /// Dual-locked data key for the QC store (qc.key next to the journal).
    ///
    /// The 64-byte random data key is never stored in plaintext. It is wrapped twice:
    ///   1. Machine lock: Windows DPAPI (LocalMachine) – normal unlock path.
    ///   2. Recovery locks: one wrap per stable hardware identifier (BIOS serial, system UUID,
    ///      motherboard serial), each with a key derived (PBKDF2, 60k iterations) from that identifier
    ///      plus the application constant. If DPAPI no longer works on the SAME physical laptop
    ///      (e.g. Windows restored with the data kept), any one unchanged identifier unlocks it, and
    ///      the machine lock is re-created automatically.
    ///
    /// Security level (documented trade-off chosen by BBTech: reliable recovery on the same laptop):
    /// ordinary users and tools see only ciphertext; a determined technical person on that laptop
    /// with the BBTech EXE could decrypt, because the app must be able to read the data without a
    /// password. Copying the files to another computer does not by itself reveal them unless the
    /// original laptop's identifiers and the app are also available.
    /// </summary>
    public static class KeyVault
    {
        public const string KeyFileName = "qc.key";
        private const int RecoveryIterations = 60000;

        public static KeyUnlockResult Open(string folder, IMachineKeyProtector machine, DeviceIdentity identity, bool journalHasData)
        {
            string path = Path.Combine(folder, KeyFileName);
            var model = Load(path) ?? Load(path + ".bak");

            if (model == null)
            {
                if (journalHasData)
                    return new KeyUnlockResult { State = KeyState.KeyMissing, Message = "The QC history key file is missing, so the existing history cannot be decrypted." };

                byte[] fresh = EnvelopeCrypto.NewKey();
                var created = new KeyFileModel { CreatedAt = DateTime.UtcNow, MachineWrap = machine.Protect(fresh) };
                AddRecoveryWraps(created, fresh, identity);
                Save(path, created);
                return new KeyUnlockResult { State = KeyState.CreatedNew, Key = fresh };
            }

            byte[] key = model.MachineWrap != null ? machine.Unprotect(model.MachineWrap) : null;
            if (key != null && key.Length == EnvelopeCrypto.KeySize)
            {
                if (AddRecoveryWraps(model, key, identity)) Save(path, model);
                return new KeyUnlockResult { State = KeyState.UnlockedMachine, Key = key };
            }

            // Machine lock failed: try the hardware recovery locks for this physical laptop.
            key = TryRecover(model, identity);
            if (key != null)
            {
                model.MachineWrap = machine.Protect(key); // heal: re-create the machine lock for this Windows
                AddRecoveryWraps(model, key, identity);
                Save(path, model);
                return new KeyUnlockResult { State = KeyState.UnlockedRecovery, Key = key, Message = "QC history was recovered using this laptop's hardware identity." };
            }

            return new KeyUnlockResult
            {
                State = KeyState.Locked,
                Message = "The QC history on this computer belongs to a different hardware identity and cannot be opened.",
            };
        }

        private static byte[] TryRecover(KeyFileModel model, DeviceIdentity identity)
        {
            if (identity == null) return null;
            foreach (var (kind, value) in identity.StableIdentifiers)
            {
                foreach (var w in model.RecoveryWraps.Where(r => r.Kind == kind))
                {
                    var kek = new EnvelopeCrypto(DeriveRecoveryKey(kind, value, w.Salt));
                    byte[] key = kek.TryDecrypt(w.Wrapped);
                    if (key != null && key.Length == EnvelopeCrypto.KeySize) return key;
                }
            }
            return null;
        }

        /// <summary>Adds a recovery wrap for every current identifier that does not already open the key.</summary>
        private static bool AddRecoveryWraps(KeyFileModel model, byte[] key, DeviceIdentity identity)
        {
            if (identity == null) return false;
            bool changed = false;
            foreach (var (kind, value) in identity.StableIdentifiers)
            {
                bool present = model.RecoveryWraps.Where(r => r.Kind == kind)
                    .Any(w => new EnvelopeCrypto(DeriveRecoveryKey(kind, value, w.Salt)).TryDecrypt(w.Wrapped) != null);
                if (present) continue;
                byte[] salt = EnvelopeCrypto.RandomBytes(16);
                model.RecoveryWraps.Add(new RecoveryWrap { Kind = kind, Salt = salt, Wrapped = new EnvelopeCrypto(DeriveRecoveryKey(kind, value, salt)).Encrypt(key) });
                changed = true;
            }
            return changed;
        }

        private static byte[] DeriveRecoveryKey(string kind, string value, byte[] salt) =>
            EnvelopeCrypto.DeriveKey(AppSecret.Value("qc-recovery") + "|" + kind + "|" + value, salt, RecoveryIterations);

        private static KeyFileModel Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] raw = File.ReadAllBytes(path);
                // The key file itself is obfuscated with the app constant (it contains only wrapped keys).
                byte[] plain = new EnvelopeCrypto(FileKey()).TryDecrypt(raw);
                return plain == null ? null : Json.FromBytes<KeyFileModel>(plain);
            }
            catch { return null; }
        }

        private static void Save(string path, KeyFileModel model) =>
            SafeFile.WriteAllBytesAtomic(path, new EnvelopeCrypto(FileKey()).Encrypt(Json.ToBytes(model)));

        private static byte[] FileKey() =>
            EnvelopeCrypto.DeriveKey(AppSecret.Value("qc-keyfile"), Encoding.UTF8.GetBytes("BBTech.qc.key.v1"), 1000);
    }
}
