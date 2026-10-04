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

    }
}
