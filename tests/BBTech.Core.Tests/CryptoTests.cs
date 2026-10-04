using System;
using System.IO;
using System.Linq;
using System.Text;
using BBTech.Core.Security;
using Xunit;

namespace BBTech.Core.Tests
{
    public class CryptoTests
    {
        [Theory]
        [InlineData("1234", true)]
        [InlineData("123456789012", true)]
        [InlineData("123", false)]
        [InlineData("1234567890123", false)]
        [InlineData("12a4", false)]
        [InlineData("১২৩৪", false)]      // Bengali digits
        [InlineData("１２３４", false)]  // full-width digits
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Pin_must_be_4_to_12_ascii_digits(string pin, bool valid)
        {
            Assert.Equal(valid, PinHasher.IsValidPin(pin));
        }

        [Fact]
        public void Pin_verification_with_a_damaged_salt_or_hash_fails_without_throwing()
        {
            string salt = PinHasher.NewSalt();
            string hash = PinHasher.Hash("2468", salt, 1000);
            Assert.True(PinHasher.Verify("2468", salt, hash, 1000));
            Assert.False(PinHasher.Verify("2468", "not base64!", hash, 1000));
            Assert.False(PinHasher.Verify("2468", salt, "%%%", 1000));
            Assert.False(PinHasher.Verify("2468", salt, hash, 0));
        }

        [Fact]
        public void Encrypting_null_is_rejected_but_empty_data_round_trips()
        {
            var c = new EnvelopeCrypto(EnvelopeCrypto.NewKey());
            Assert.Throws<ArgumentNullException>(() => c.Encrypt(null));
            byte[] empty = c.Encrypt(new byte[0]);
            Assert.Equal(1 + 16 + 16 + 32, empty.Length); // one padding block
            Assert.Empty(c.TryDecrypt(empty));
        }

        [Fact]
        public void Encrypted_data_round_trips_and_has_the_documented_layout()
        {
            var c = new EnvelopeCrypto(EnvelopeCrypto.NewKey());
            byte[] plain = Encoding.UTF8.GetBytes("inspection #1 PASS");
            byte[] enc = c.Encrypt(plain);
            Assert.Equal(1, enc[0]);                                  // version
            Assert.Equal(0, (enc.Length - 1 - 16 - 32) % 16);         // IV + AES blocks + HMAC
            Assert.Equal(plain, c.TryDecrypt(enc));
        }

        [Fact]
        public void Encrypting_the_same_data_twice_gives_different_ciphertext()
        {
            var c = new EnvelopeCrypto(EnvelopeCrypto.NewKey());
            byte[] plain = Encoding.UTF8.GetBytes("same text");
            byte[] a = c.Encrypt(plain), b = c.Encrypt(plain);
            Assert.NotEqual(a.Skip(1).Take(16), b.Skip(1).Take(16));
            Assert.NotEqual(a, b);
        }

        [Theory]
        [InlineData(0)]    // version byte
        [InlineData(1)]    // IV
        [InlineData(20)]   // ciphertext
        [InlineData(-1)]   // HMAC (last byte)
        public void Changing_any_byte_of_the_envelope_is_detected(int offset)
        {
            var c = new EnvelopeCrypto(EnvelopeCrypto.NewKey());
            byte[] enc = c.Encrypt(Encoding.UTF8.GetBytes("operator one, overall PASS"));
            int i = offset < 0 ? enc.Length + offset : offset;
            enc[i] ^= 0x01;
            Assert.Null(c.TryDecrypt(enc));
        }

        [Fact]
        public void Data_encrypted_with_another_key_cannot_be_read()
        {
            byte[] enc = new EnvelopeCrypto(EnvelopeCrypto.NewKey()).Encrypt(new byte[] { 1, 2, 3 });
            Assert.Null(new EnvelopeCrypto(EnvelopeCrypto.NewKey()).TryDecrypt(enc));
        }

        [Fact]
        public void Truncated_or_missing_envelopes_return_null()
        {
            var c = new EnvelopeCrypto(EnvelopeCrypto.NewKey());
            byte[] enc = c.Encrypt(new byte[40]);
            Assert.Null(c.TryDecrypt(enc.Take(enc.Length - 1).ToArray()));
            Assert.Null(c.TryDecrypt(enc.Take(1 + 16 + 16).ToArray()));
            Assert.Null(c.TryDecrypt(new byte[0]));
            Assert.Null(c.TryDecrypt(null));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(32)]
        [InlineData(63)]
        [InlineData(65)]
        public void Keys_must_be_exactly_64_bytes(int size)
        {
            Assert.Throws<ArgumentException>(() => new EnvelopeCrypto(new byte[size]));
        }

        [Fact]
        public void Clearing_the_key_buffer_does_not_affect_an_existing_instance()
        {
            byte[] key = EnvelopeCrypto.NewKey();
            var c = new EnvelopeCrypto(key);
            byte[] enc = c.Encrypt(new byte[] { 9 });
            Array.Clear(key, 0, key.Length);
            Assert.Equal(new byte[] { 9 }, c.TryDecrypt(enc));
        }

        [Fact]
        public void Key_derivation_is_deterministic_and_depends_on_secret_salt_and_iterations()
        {
            byte[] salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            byte[] a = EnvelopeCrypto.DeriveKey("secret", salt, 1000);
            Assert.Equal(EnvelopeCrypto.KeySize, a.Length);
            Assert.Equal(a, EnvelopeCrypto.DeriveKey("secret", salt, 1000));
            Assert.NotEqual(a, EnvelopeCrypto.DeriveKey("secret2", salt, 1000));
            Assert.NotEqual(a, EnvelopeCrypto.DeriveKey("secret", salt.Reverse().ToArray(), 1000));
            Assert.NotEqual(a, EnvelopeCrypto.DeriveKey("secret", salt, 1001));
            Assert.Equal(32, EnvelopeCrypto.DeriveKey("secret", salt, 1000, 32).Length);
        }

    }
}
