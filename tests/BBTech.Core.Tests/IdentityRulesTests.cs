using System;
using System.Linq;
using BBTech.Core.Identity;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Device identity details: identifier cleaning, hashing and fingerprints (example identifiers only).</summary>
    public class IdentityRulesTests
    {
        private const string Uuid = "6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1";

        [Theory]
        [InlineData("Chassis Serial Number")]
        [InlineData("Base Board Serial Number")]
        [InlineData("Not Specified")]
        [InlineData("Not Available")]
        [InlineData("NONE")]
        [InlineData("INVALID")]
        [InlineData("OEM")]
        [InlineData("Default")]
        [InlineData("1234567890")]
        [InlineData("ABCDEFGHIJ")]
        [InlineData("FFFFFFFF")]
        [InlineData("..")]
        [InlineData("AB")]
        public void Placeholder_and_too_short_serials_are_discarded(string raw)
        {
            Assert.Null(DeviceIdentity.CleanSerial(raw));
        }

        [Theory]
        [InlineData("  pf-1a2b \t3c ", "PF-1A2B 3C")]
        [InlineData("serial-example", "SERIAL-EXAMPLE")]
        [InlineData("5CD1234XYZ", "5CD1234XYZ")]
        public void Serials_are_trimmed_upper_cased_and_whitespace_collapsed(string raw, string expected)
        {
            Assert.Equal(expected, DeviceIdentity.CleanSerial(raw));
        }

    }
}
