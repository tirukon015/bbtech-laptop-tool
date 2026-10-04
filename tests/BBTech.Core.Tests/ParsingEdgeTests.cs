using System;
using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Models;
using BBTech.Core.Parsing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Edge cases of the DETECT parsers (capacity, brand, battery, HD Sentinel).</summary>
    public class ParsingEdgeTests
    {
        [Theory]
        [InlineData(1920383410176L, "1.92 TB")]
        [InlineData(4000787030016L, "4 TB")]
        [InlineData(2048408248320L, "2 TB")]
        [InlineData(960197124096L, "960 GB")]
        [InlineData(31268536320L, "32 GB")]     // 32 GB eMMC
        [InlineData(64023257088L, "64 GB")]
        public void Drive_sizes_map_to_the_label_printed_on_the_box(long bytes, string expected)
        {
            Assert.Equal(expected, CapacityFormatter.FormatDrive(bytes));
        }

        [Theory]
        [InlineData(700000000000L, "700 GB")]
        [InlineData(3500000000000L, "3.5 TB")]
        public void Non_standard_drive_sizes_are_shown_as_measured(long bytes, string expected)
        {
            Assert.Equal(expected, CapacityFormatter.FormatDrive(bytes));
        }

        [Fact]
        public void Zero_or_negative_capacities_are_unknown()
        {
            Assert.Equal("Unknown", CapacityFormatter.FormatDrive(0));
            Assert.Equal("Unknown", CapacityFormatter.FormatDrive(-1));
            Assert.Equal("Unknown", CapacityFormatter.FormatRamFromModules(0));
            Assert.Equal("Unknown", CapacityFormatter.FormatRamFromVisible(-5));
        }

    }
}
