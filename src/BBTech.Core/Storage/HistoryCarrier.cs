using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using BBTech.Core.Identity;
using BBTech.Core.Security;

namespace BBTech.Core.Storage
{
    [DataContract]
    public sealed class CarriedHistory
    {
        [DataMember] public string Fingerprint { get; set; }
        [DataMember] public string SerialHash { get; set; }
        [DataMember] public string UuidHash { get; set; }
        [DataMember] public string DeviceId { get; set; }
        [DataMember] public DateTime SavedAt { get; set; }
        [DataMember] public List<TestResultRecord> Records { get; set; } = new List<TestResultRecord>();
    }

    /// <summary>
    /// The laptop keeps its own history on its disk, and a clean Windows install (a normal Software step)
    /// wipes that disk. So the USB carries an encrypted copy of each laptop's history
    /// (BBTech Data\carry\&lt;fingerprint&gt;.dat), updated on every save. When the tool meets a laptop whose
    /// local history is missing or shorter, the copy is restored, so Hardware's work is still there after
    /// Windows was installed. The file is matched to the physical laptop by its identity, never by name.
    /// </summary>
    public static class HistoryCarrier
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BBTH0001");
        private const int KdfIterations = 20000;

        public static string FileFor(string carryFolder, DeviceIdentity id)
        {
            string fp = id?.Fingerprint ?? "";
            return Path.Combine(carryFolder, (fp.Length > 24 ? fp.Substring(0, 24) : fp) + ".dat");
        }

        public static void Save(string carryFolder, DeviceIdentity id, string deviceId, IEnumerable<TestResultRecord> records)
        {
            if (id == null || !id.HasStrongIdentifier) return;
            var data = new CarriedHistory
            {
                Fingerprint = id.Fingerprint, SerialHash = id.SerialHash, UuidHash = id.UuidHash, DeviceId = deviceId,
                SavedAt = DateTime.UtcNow, Records = (records ?? Enumerable.Empty<TestResultRecord>()).ToList(),
            };
            Directory.CreateDirectory(carryFolder);
            byte[] salt = EnvelopeCrypto.RandomBytes(16);
            byte[] body = new EnvelopeCrypto(Key(salt)).Encrypt(Json.ToBytes(data));
            var file = new byte[Magic.Length + 16 + body.Length];
            Buffer.BlockCopy(Magic, 0, file, 0, Magic.Length);
            Buffer.BlockCopy(salt, 0, file, Magic.Length, 16);
            Buffer.BlockCopy(body, 0, file, Magic.Length + 16, body.Length);
            SafeFile.WriteAllBytesAtomic(FileFor(carryFolder, id), file);
        }

        /// <summary>The carried history for THIS physical laptop, or null (missing, damaged, or another laptop's file).</summary>
        public static CarriedHistory Load(string carryFolder, DeviceIdentity id)
        {
            try
            {
                if (id == null || !id.HasStrongIdentifier) return null;
                string path = FileFor(carryFolder, id);
                if (!File.Exists(path)) return null;
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < Magic.Length + 16 || !Magic.SequenceEqual(data.Take(Magic.Length))) return null;
                byte[] salt = data.Skip(Magic.Length).Take(16).ToArray();
                byte[] plain = new EnvelopeCrypto(Key(salt)).TryDecrypt(data.Skip(Magic.Length + 16).ToArray());
                if (plain == null) return null;
                var h = Json.FromBytes<CarriedHistory>(plain);
                // Must be this laptop: the full fingerprint has to match (not just the file name).
                return h != null && h.Fingerprint == id.Fingerprint && h.Records != null ? h : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Removes carry files not touched for <paramref name="days"/> days (the USB is temporary storage).</summary>
        public static int Cleanup(string carryFolder, int days = 90)
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(carryFolder)) return 0;
                foreach (var f in Directory.GetFiles(carryFolder, "*.dat"))
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-days)) { File.Delete(f); n++; }
            }
            catch { }
            return n;
        }

        private static byte[] Key(byte[] salt) => EnvelopeCrypto.DeriveKey(AppSecret.Value("history-carry"), salt, KdfIterations);
    }
}
