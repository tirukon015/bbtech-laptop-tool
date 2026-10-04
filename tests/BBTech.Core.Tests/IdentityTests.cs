using System.Collections.Generic;
using BBTech.Core.Identity;
using BBTech.Core.Storage;
using Xunit;

namespace BBTech.Core.Tests
{
    public class DeviceIdentityTests
    {
        [Theory]
        [InlineData("SERIALEX01", "SERIALEX01")]
        [InlineData("  serialex01 ", "SERIALEX01")]
        [InlineData("To be filled by O.E.M.", null)]
        [InlineData("Default string", null)]
        [InlineData("System Serial Number", null)]
        [InlineData("0000000000", null)]
        [InlineData("XXXXXXXX", null)]
        [InlineData("123456789", null)]
        [InlineData("N/A", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Serial_extraction_drops_placeholders(string raw, string expected) => Assert.Equal(expected, DeviceIdentity.CleanSerial(raw));

        [Theory]
        [InlineData("4c4c4544-0042-3510-8051-b8c04f4e3532", "4C4C4544-0042-3510-8051-B8C04F4E3532")]
        [InlineData("{6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1}", "6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1")]
        [InlineData("00000000-0000-0000-0000-000000000000", null)]
        [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", null)]
        [InlineData("03000200-0400-0500-0006-000700080009", null)]
        [InlineData("not-a-uuid", null)]
        [InlineData(null, null)]
        public void Uuid_extraction_drops_placeholders(string raw, string expected) => Assert.Equal(expected, DeviceIdentity.CleanUuid(raw));

        [Fact]
        public void Board_serial_extraction()
        {
            var id = new DeviceIdentity("LENOVO", "ThinkPad", "T14 Gen 2i", "SERIALEX01", null, "L1HF1234567");
            Assert.Equal("L1HF1234567", id.BoardSerial);
            Assert.Null(new DeviceIdentity("Dell", "Latitude", "5420", "ABC123", null, "Base Board Serial Number").BoardSerial);
        }

        [Fact]
        public void Fingerprint_is_stable_and_specific()
        {
            var a1 = new DeviceIdentity("Dell Inc.", "Latitude", "5420", "ABC123", "4C4C4544-0042-3510-8051-B8C04F4E3532", "BRD1");
            var a2 = new DeviceIdentity("DELL INC", "Latitude", "5420", " abc123 ", "4c4c4544-0042-3510-8051-b8c04f4e3532", "brd1");
            var b = new DeviceIdentity("Dell Inc.", "Latitude", "5420", "ABC124", "4C4C4544-0042-3510-8051-B8C04F4E3533", "BRD2");
            Assert.Equal(a1.Fingerprint, a2.Fingerprint);
            Assert.NotEqual(a1.Fingerprint, b.Fingerprint);
            Assert.Equal(64, a1.Fingerprint.Length);
            Assert.DoesNotContain("ABC123", a1.SerialHash); // stored hashed
        }

        [Fact]
        public void No_identifiers_means_no_fingerprint()
        {
            var id = new DeviceIdentity("Dell", "Latitude", "5420", "To be filled by O.E.M.", "00000000-0000-0000-0000-000000000000", "");
            Assert.False(id.HasStrongIdentifier);
            Assert.Null(id.Fingerprint); // never a model-only fingerprint
        }
    }

    public class DeviceMatcherTests
    {
        private static DeviceRecord Rec(DeviceIdentity id) => new DeviceRecord
        {
            DeviceId = id.SerialNumber ?? id.SystemUuid, Manufacturer = id.Manufacturer, Model = id.Model, ModelNumber = id.ModelNumber,
            SerialHash = id.SerialHash, UuidHash = id.UuidHash, BoardSerialHash = id.BoardSerialHash,
        };

        private static readonly DeviceIdentity LaptopA = new DeviceIdentity("Dell", "Latitude", "5420", "ABC123", "11111111-2222-3333-4444-555555555555", "BRD-A");
        private static readonly DeviceIdentity LaptopB = new DeviceIdentity("Dell", "Latitude", "5420", "XYZ999", "66666666-7777-8888-9999-000000000000", "BRD-B");
        private static readonly DeviceIdentity LaptopC = new DeviceIdentity("HP", "EliteBook", "845 G11", "CND111", "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", "BRD-C");

        [Fact]
        public void Exact_match()
        {
            var r = DeviceMatcher.Match(LaptopA, new[] { Rec(LaptopB), Rec(LaptopA), Rec(LaptopC) });
            Assert.Equal(MatchStatus.Matched, r.Status);
            Assert.Equal("ABC123", r.Device.DeviceId);
        }

        [Fact]
        public void No_match() => Assert.Equal(MatchStatus.NotFound, DeviceMatcher.Match(LaptopB, new[] { Rec(LaptopA), Rec(LaptopC) }).Status);

        [Fact]
        public void Same_model_different_serial_is_not_a_match() =>
            Assert.Equal(MatchStatus.NotFound, DeviceMatcher.Match(LaptopB, new[] { Rec(LaptopA) }).Status); // both Dell Latitude 5420

        [Fact]
        public void Same_manufacturer_different_model_is_not_a_match()
        {
            var other = new DeviceIdentity("Dell", "XPS", "13 9310", "QQQ777", null, null);
            Assert.Equal(MatchStatus.NotFound, DeviceMatcher.Match(other, new[] { Rec(LaptopA), Rec(LaptopB) }).Status);
        }

        [Fact]
        public void Conflicting_identifiers_are_flagged_not_matched()
        {
            // Same serial, different UUID: e.g. cloned/mislabelled record.
            var odd = new DeviceIdentity("Dell", "Latitude", "5420", "ABC123", "99999999-9999-9999-9999-999999999990", "BRD-A");
            var r = DeviceMatcher.Match(odd, new[] { Rec(LaptopA) });
            Assert.Equal(MatchStatus.Conflict, r.Status);
            Assert.Null(r.Device);
            Assert.Contains("system UUID", r.Reason);
        }

        [Fact]
        public void Different_manufacturer_with_same_serial_is_conflict()
        {
            var odd = new DeviceIdentity("Lenovo", "ThinkPad", "T14", "ABC123", null, null);
            Assert.Equal(MatchStatus.Conflict, DeviceMatcher.Match(odd, new[] { Rec(LaptopA) }).Status);
        }

        [Fact]
        public void Matching_several_records_is_conflict()
        {
            // Serial matches A, UUID matches B.
            var odd = new DeviceIdentity("Dell", "Latitude", "5420", "ABC123", "66666666-7777-8888-9999-000000000000", null);
            var r = DeviceMatcher.Match(odd, new[] { Rec(LaptopA), Rec(LaptopB) });
            Assert.Equal(MatchStatus.Conflict, r.Status);
            Assert.Equal(2, r.Candidates.Count);
        }

        [Fact]
        public void Missing_identifiers_are_tolerated()
        {
            // Stored record had only a serial; now the UUID is also readable.
            var storedOnlySerial = new DeviceIdentity("Dell", "Latitude", "5420", "ABC123", null, null);
            var r = DeviceMatcher.Match(LaptopA, new[] { Rec(storedOnlySerial) });
            Assert.Equal(MatchStatus.Matched, r.Status);
            Assert.Equal("serial number", r.MatchedOn);
        }

        [Fact]
        public void Laptop_without_identifiers_never_matches()
        {
            var none = new DeviceIdentity("Dell", "Latitude", "5420", "", "", "");
            Assert.Equal(MatchStatus.NoIdentifiers, DeviceMatcher.Match(none, new[] { Rec(LaptopA) }).Status);
        }
    }
}
