using System;
using System.IO;
using System.Linq;
using System.Text;
using BBTech.Core.Security;
using BBTech.Core.Storage;

namespace BBTech.Core.Sync
{
    /// <summary>
    /// Offline copy of the server's master configuration (BBTech Data\config\master.dat), encrypted and
    /// authenticated like the settings file. Used when the server is unreachable; replaced whenever a newer,
    /// valid configuration arrives. An older configuration never replaces a newer one (no rollback).
    /// </summary>
    public static class MasterConfigCache
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BBTM0001");
        private const int KdfIterations = 20000;

        public static MasterConfigResponse Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < Magic.Length + 16 || !Magic.SequenceEqual(data.Take(Magic.Length))) return null;
                byte[] salt = data.Skip(Magic.Length).Take(16).ToArray();
                byte[] plain = new EnvelopeCrypto(Key(salt)).TryDecrypt(data.Skip(Magic.Length + 16).ToArray());
                if (plain == null) return null;
                var cfg = Json.FromBytes<MasterConfigResponse>(plain);
                return cfg != null && cfg.Validate() == null ? cfg : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Stores <paramref name="config"/> if it is valid and not older than the cached one. Returns why not, or null.</summary>
        public static string TrySave(string path, MasterConfigResponse config)
        {
            if (config == null) return "No configuration received.";
            string invalid = config.Validate();
            if (invalid != null) return invalid;
            var current = Load(path);
            if (current != null && config.ConfigVersion < current.ConfigVersion)
                return "The server sent an older configuration (version " + config.ConfigVersion + " < " + current.ConfigVersion + "); it was ignored.";

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] salt = EnvelopeCrypto.RandomBytes(16);
            byte[] body = new EnvelopeCrypto(Key(salt)).Encrypt(Json.ToBytes(config));
            var file = new byte[Magic.Length + 16 + body.Length];
            Buffer.BlockCopy(Magic, 0, file, 0, Magic.Length);
            Buffer.BlockCopy(salt, 0, file, Magic.Length, 16);
            Buffer.BlockCopy(body, 0, file, Magic.Length + 16, body.Length);
            SafeFile.WriteAllBytesAtomic(path, file);
            return null;
        }

        private static byte[] Key(byte[] salt) => EnvelopeCrypto.DeriveKey(AppSecret.Value("master-config"), salt, KdfIterations);
    }
}
