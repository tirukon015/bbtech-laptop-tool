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
    }
}
