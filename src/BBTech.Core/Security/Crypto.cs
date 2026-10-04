using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BBTech.Core.Security
{
    /// <summary>
    /// Authenticated encryption: AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC), 64-byte key
    /// (32 bytes encryption + 32 bytes MAC). Any change to the ciphertext is detected.
    /// Format: [0x01][IV 16][ciphertext][HMAC 32].
    /// </summary>
    public sealed class EnvelopeCrypto
    {
        public const int KeySize = 64;
        private const byte Version = 1;
        private readonly byte[] _encKey = new byte[32];
        private readonly byte[] _macKey = new byte[32];

        public EnvelopeCrypto(byte[] key)
        {
            if (key == null || key.Length != KeySize) throw new ArgumentException("Key must be 64 bytes.");
            Buffer.BlockCopy(key, 0, _encKey, 0, 32);
            Buffer.BlockCopy(key, 32, _macKey, 0, 32);
        }

        public static byte[] NewKey() => RandomBytes(KeySize);

        public static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        public byte[] Encrypt(byte[] plain)
        {
            byte[] iv = RandomBytes(16);
            byte[] ct;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encKey; aes.IV = iv;
                using (var enc = aes.CreateEncryptor()) ct = enc.TransformFinalBlock(plain, 0, plain.Length);
            }
            var output = new byte[1 + 16 + ct.Length + 32];
            output[0] = Version;
            Buffer.BlockCopy(iv, 0, output, 1, 16);
            Buffer.BlockCopy(ct, 0, output, 17, ct.Length);
            byte[] mac = Mac(output, 0, 17 + ct.Length);
            Buffer.BlockCopy(mac, 0, output, 17 + ct.Length, 32);
            return output;
        }

        /// <summary>Returns null if the data was altered, truncated, or encrypted with another key.</summary>
        public byte[] TryDecrypt(byte[] data)
        {
            if (data == null || data.Length < 1 + 16 + 16 + 32 || data[0] != Version) return null;
            int ctLen = data.Length - 1 - 16 - 32;
            if (ctLen % 16 != 0) return null;
            byte[] expected = Mac(data, 0, 17 + ctLen);
            if (!FixedTimeEquals(expected, data, 17 + ctLen)) return null;
            try
            {
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    aes.Key = _encKey;
                    var iv = new byte[16];
                    Buffer.BlockCopy(data, 1, iv, 0, 16);
                    aes.IV = iv;
                    using (var dec = aes.CreateDecryptor()) return dec.TransformFinalBlock(data, 17, ctLen);
                }
            }
            catch (CryptographicException) { return null; }
        }

        private byte[] Mac(byte[] data, int offset, int count)
        {
            using (var h = new HMACSHA256(_macKey)) return h.ComputeHash(data, offset, count);
        }

        private static bool FixedTimeEquals(byte[] a, byte[] data, int offset)
        {
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ data[offset + i];
            return diff == 0;
        }

        /// <summary>PBKDF2-HMAC-SHA256 key derivation.</summary>
        public static byte[] DeriveKey(string secret, byte[] salt, int iterations, int length = KeySize)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(length);
        }
    }

    /// <summary>
    /// A constant compiled into the application. It is NOT a real secret (anyone who reverse-engineers
    /// the EXE can find it); it only ensures that files written by the app cannot be read with ordinary
    /// tools. Real protection of QC data comes from Windows DPAPI; see KeyVault.
    /// </summary>
    internal static class AppSecret
    {
        // Stored split so it does not appear as one readable string in the binary.
        private static readonly byte[] A = { 0x5B, 0x1E, 0xC7, 0x42, 0x90, 0x3D, 0x6A, 0xF1, 0x08, 0xB4, 0x77, 0x2C, 0xE9, 0x13, 0x5D, 0xA6, 0x31, 0xCF, 0x84, 0x0B, 0x6E, 0xD2, 0x19, 0x95, 0x4A, 0xF8, 0x23, 0x7C, 0xB0, 0x0E, 0x66, 0xC3 };
        private static readonly byte[] B = { 0x19, 0x5C, 0x93, 0x27, 0xF3, 0x55, 0x2E, 0x9D, 0x67, 0xD3, 0x23, 0x43, 0x86, 0x7F, 0x7D, 0xF2, 0x54, 0xBC, 0xF0, 0x2B, 0x2C, 0x90, 0x4D, 0xD6, 0x02, 0xA8, 0x6B, 0x3E, 0xF9, 0x5A, 0x23, 0x87 };

        public static string Value(string purpose) =>
            purpose + ":" + Convert.ToBase64String(A.Select((x, i) => (byte)(x ^ B[i])).ToArray());
    }

    /// <summary>Salted, slow hash for the admin PIN (PBKDF2-HMAC-SHA256).</summary>
    public static class PinHasher
    {
        public const int Iterations = 120000;

        public static string NewSalt() => Convert.ToBase64String(EnvelopeCrypto.RandomBytes(16));

        public static string Hash(string pin, string saltBase64, int iterations = Iterations) =>
            Convert.ToBase64String(EnvelopeCrypto.DeriveKey("pin|" + (pin ?? ""), Convert.FromBase64String(saltBase64), iterations, 32));

        public static bool Verify(string pin, string saltBase64, string expectedHash, int iterations = Iterations)
        {
            if (string.IsNullOrEmpty(saltBase64) || string.IsNullOrEmpty(expectedHash)) return false;
            byte[] a = Convert.FromBase64String(Hash(pin, saltBase64, iterations));
            byte[] b = Convert.FromBase64String(expectedHash);
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>
        /// PIN rules: 4-12 ASCII digits. char.IsDigit is not used because it also accepts Bengali,
        /// Arabic-Indic and full-width digits, which would make the PIN depend on the keyboard layout.
        /// </summary>
        public static bool IsValidPin(string pin) => pin != null && pin.Length >= 4 && pin.Length <= 12 && pin.All(c => c >= '0' && c <= '9');
    }

    /// <summary>Atomic file replace: write to .tmp, then swap (keeps the old file as .bak).</summary>
    public static class SafeFile
    {
        public static void WriteAllBytesAtomic(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.Write(data, 0, data.Length);
                f.Flush(true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true);
            else File.Move(tmp, path);
        }
    }
}
