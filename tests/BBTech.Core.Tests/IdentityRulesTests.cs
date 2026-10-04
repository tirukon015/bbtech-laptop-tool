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

        [Theory]
        [InlineData("6a3f2c10-1b2d-11b2-a85c-d5a1a3b0c9e1")]
        [InlineData("6A3F2C101B2D11B2A85CD5A1A3B0C9E1")]
        [InlineData("(6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1)")]
        [InlineData("  {6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1}  ")]
        public void Uuid_formats_are_canonicalised(string raw)
        {
            Assert.Equal(Uuid, DeviceIdentity.CleanUuid(raw));
        }

        [Theory]
        [InlineData("00020003-0004-0005-0006-000700080009")]
        [InlineData("12345678-1234-5678-90AB-CDDEEFAABBCC")]
        [InlineData("11111111-1111-1111-1111-111111111111")]
        [InlineData("")]
        [InlineData("   ")]
        public void Placeholder_uuids_are_discarded(string raw)
        {
            Assert.Null(DeviceIdentity.CleanUuid(raw));
        }

        [Fact]
        public void The_same_value_hashes_differently_as_serial_and_board_serial()
        {
            Assert.NotEqual(DeviceIdentity.Hash("serial", "ABC12345"), DeviceIdentity.Hash("board", "ABC12345"));
            Assert.Equal(DeviceIdentity.Hash("serial", "ABC12345"), DeviceIdentity.Hash("serial", "ABC12345"));
            Assert.Null(DeviceIdentity.Hash("serial", null));
            Assert.Matches("^[0-9a-f]{64}$", DeviceIdentity.Hash("uuid", Uuid));
        }

    }
}
