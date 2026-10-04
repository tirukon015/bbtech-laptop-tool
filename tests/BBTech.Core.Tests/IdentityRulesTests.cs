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

        [Fact]
        public void Serial_hash_ignores_case_and_surrounding_whitespace()
        {
            var a = new DeviceIdentity("Dell", "Latitude", "5420", "abc12345", null, null);
            var b = new DeviceIdentity("Dell", "Latitude", "5420", "  ABC12345 ", null, null);
            Assert.Equal(a.SerialHash, b.SerialHash);
            Assert.Equal(a.Fingerprint, b.Fingerprint);
        }

        [Theory]
        [InlineData("ABC12345", null, null)]
        [InlineData(null, Uuid, null)]
        [InlineData(null, null, "BRD-0001")]
        public void Any_one_stable_identifier_is_enough_for_a_fingerprint(string serial, string uuid, string board)
        {
            var id = new DeviceIdentity("HP", "EliteBook", "840 G8", serial, uuid, board);
            Assert.True(id.HasStrongIdentifier);
            Assert.Matches("^[0-9a-f]{64}$", id.Fingerprint);
        }

        [Fact]
        public void Fingerprint_ignores_model_text_but_includes_the_manufacturer()
        {
            var a = new DeviceIdentity("Lenovo", "ThinkPad", "T14 Gen 2", "ABC12345", Uuid, null);
            var renamed = new DeviceIdentity("LENOVO", "Unknown", "20W0", "ABC12345", Uuid, null);
            var otherVendor = new DeviceIdentity("Dell", "ThinkPad", "T14 Gen 2", "ABC12345", Uuid, null);
            Assert.Equal(a.Fingerprint, renamed.Fingerprint);
            Assert.NotEqual(a.Fingerprint, otherVendor.Fingerprint);
        }

        [Fact]
        public void Stable_identifiers_skip_missing_values_and_keep_their_order()
        {
            var id = new DeviceIdentity("Acer", "Aspire", "5", "To be filled by O.E.M.", Uuid, "BRD-0001");
            Assert.Equal(new[] { "uuid", "board" }, id.StableIdentifiers.Select(p => p.Kind));
            Assert.Equal(Uuid, id.StableIdentifiers[0].Value);
        }

        [Theory]
        [InlineData("Dell Inc.", "DELLINC")]
        [InlineData("  hewlett-packard ", "HEWLETTPACKARD")]
        [InlineData(null, "")]
        public void Names_are_normalised_to_upper_case_letters_and_digits(string raw, string expected)
        {
            Assert.Equal(expected, DeviceIdentity.NormalizeName(raw));
        }

        [Fact]
        public void ToString_reports_uuid_presence_without_the_value()
        {
            var id = new DeviceIdentity("Dell", "Latitude", "5420", "ABC12345", Uuid, "BRD-0001");
            string s = id.ToString();
            Assert.DoesNotContain(Uuid, s);
            Assert.DoesNotContain("BRD-0001", s);
            Assert.Contains("uuid=yes", s);
        }

        [Fact]
        public void Identity_factory_uses_the_detection_result()
        {
            var r = new DetectionResult { Brand = "Dell", Model = "Latitude", ModelNumber = "5420", Cpu = "i5", Generation = "11th Gen", Ram = "16 GB", Storage = "256 GB SSD", BatteryHealth = "91%" };
            r.System.SerialNumber = "abc12345";
            r.System.SystemUuid = Uuid.ToLowerInvariant();
            r.System.BoardSerial = "Default string";
            var id = IdentityFactory.From(r);
            Assert.Equal("ABC12345", id.SerialNumber);
            Assert.Equal(Uuid, id.SystemUuid);
            Assert.Null(id.BoardSerial);
            var specs = IdentityFactory.SpecsFrom(r);
            Assert.Equal("i5", specs.Cpu);
            Assert.Equal("91%", specs.BatteryHealth);
        }

        [Fact]
        public void A_laptop_can_be_matched_by_motherboard_serial_only()
        {
            var stored = new DeviceIdentity("ASUS", "VivoBook", "X515EA", null, null, "BRD-0001");
            var now = new DeviceIdentity("ASUS", "VivoBook", "X515EA", null, null, "brd-0001");
            var m = DeviceMatcher.Match(now, new[] { Rec(stored) });
            Assert.Equal(MatchStatus.Matched, m.Status);
            Assert.Equal("motherboard serial", m.MatchedOn);
        }

        private static DeviceRecord Rec(DeviceIdentity id) => new DeviceRecord
        {
            DeviceId = Guid.NewGuid().ToString("N"), Manufacturer = id.Manufacturer,
            SerialHash = id.SerialHash, UuidHash = id.UuidHash, BoardSerialHash = id.BoardSerialHash,
        };

        [Fact]
        public void Matching_against_no_records_is_not_found()
        {
            var id = new DeviceIdentity("Dell", "Latitude", "5420", "ABC12345", null, null);
            Assert.Equal(MatchStatus.NotFound, DeviceMatcher.Match(id, null).Status);
            Assert.Equal(MatchStatus.NotFound, DeviceMatcher.Match(id, new DeviceRecord[0]).Status);
        }

        [Fact]
        public void MatchedOn_lists_all_agreeing_identifiers()
        {
            var id = new DeviceIdentity("Dell", "Latitude", "5420", "ABC12345", Uuid, "BRD-0001");
            var m = DeviceMatcher.Match(id, new[] { Rec(id) });
            Assert.Equal("serial number, system UUID, motherboard serial", m.MatchedOn);
            Assert.Empty(m.Candidates);
        }

        [Fact]
        public void A_stored_record_without_manufacturer_still_matches()
        {
            var id = new DeviceIdentity("Dell", "Latitude", "5420", "ABC12345", null, null);
            var rec = Rec(id);
            rec.Manufacturer = "";
            Assert.Equal(MatchStatus.Matched, DeviceMatcher.Match(id, new[] { rec }).Status);
        }

        [Fact]
        public void A_null_identity_is_reported_as_no_identifiers()
        {
            var m = DeviceMatcher.Match(null, new DeviceRecord[0]);
            Assert.Equal(MatchStatus.NoIdentifiers, m.Status);
            Assert.Contains("cannot be matched safely", m.Reason);
        }
    }
}
