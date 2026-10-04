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

        [Theory]
        [InlineData(536870912L, "512 MB")]
        [InlineData(12884901888L, "12 GB")]
        [InlineData(25769803776L, "24 GB")]
        [InlineData(68719476736L, "64 GB")]
        public void Ram_module_totals_are_shown_in_binary_units(long bytes, string expected)
        {
            Assert.Equal(expected, CapacityFormatter.FormatRamFromModules(bytes));
        }

        [Theory]
        [InlineData(4080218931L, "4 GB")]    // 3.8 GiB visible
        [InlineData(12562779750L, "12 GB")]  // 11.7 GiB visible
        [InlineData(6335076761L, "6 GB")]    // 5.9 GiB visible
        public void Visible_memory_is_rounded_up_to_a_common_size(long bytes, string expected)
        {
            Assert.Equal(expected, CapacityFormatter.FormatRamFromVisible(bytes));
        }

        [Fact]
        public void Storage_summary_is_unknown_when_only_external_drives_exist()
        {
            Assert.Equal("Unknown", CapacityFormatter.StorageSummary(null));
            Assert.Equal("Unknown", CapacityFormatter.StorageSummary(new[]
            {
                new StorageDevice { CapacityLabel = "64 GB", BusType = "USB", IsExternal = true },
            }));
        }

        [Theory]
        [InlineData("Dell Inc.", "Dell")]
        [InlineData("Hewlett-Packard", "HP")]
        [InlineData("HP", "HP")]
        [InlineData("LENOVO", "Lenovo")]
        [InlineData("ASUSTeK COMPUTER INC.", "ASUS")]
        [InlineData("Micro-Star International Co., Ltd.", "MSI")]
        [InlineData("Microsoft Corporation", "Microsoft")]
        [InlineData("SAMSUNG ELECTRONICS CO., LTD.", "Samsung")]
        [InlineData("FUJITSU CLIENT COMPUTING LIMITED", "Fujitsu")]
        [InlineData("Dynabook Inc.", "Dynabook")]
        [InlineData("TIMI", "Xiaomi")]
        [InlineData("LG Electronics", "LG")]
        [InlineData("VAIO Corporation", "VAIO")]
        [InlineData("HONOR", "Honor")]
        [InlineData("Alienware", "Alienware")]
        public void Manufacturer_strings_become_clean_brands(string raw, string expected)
        {
            Assert.Equal(expected, BrandCatalog.Normalize(raw));
        }

        [Theory]
        [InlineData("To Be Filled By O.E.M.")]
        [InlineData("System manufacturer")]
        [InlineData("Default string")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("Inc.")]
        public void Placeholder_manufacturers_are_unknown(string raw)
        {
            Assert.Equal("Unknown", BrandCatalog.Normalize(raw));
        }

        [Theory]
        [InlineData("Tongfang Hongkong Limited", "Tongfang Hongkong")]
        [InlineData("XYZ Corp.", "XYZ")]
        [InlineData("THUNDEROBOT TECHNOLOGY CO., LTD.", "Thunderobot")]
        [InlineData("ACME CO., LTD.", "ACME")]      // short all-caps names are kept as acronyms
        public void Unknown_vendors_are_title_cased_without_corporate_suffixes(string raw, string expected)
        {
            Assert.Equal(expected, BrandCatalog.Normalize(raw));
        }

        [Fact]
        public void Brand_prefixes_include_the_long_vendor_names()
        {
            Assert.Equal(new[] { "HP", "Hewlett-Packard", "Hewlett Packard" }, BrandCatalog.BrandPrefixes("HP").ToArray());
            Assert.Equal(new[] { "Acer" }, BrandCatalog.BrandPrefixes("Acer").ToArray());
        }

        [Theory]
        [InlineData("87,9 %", 87.9)]
        [InlineData("100%", 100.0)]
        [InlineData("99.7%", 99.7)]
        [InlineData("abc", null)]
        [InlineData("", null)]
        [InlineData("87.9", null)]
        public void Battery_percentages_are_parsed_locale_independently(string s, double? expected)
        {
            Assert.Equal(expected, BatteryInfoViewParser.ParsePercent(s));
        }

        [Fact]
        public void Cycle_counts_and_grouped_energy_values_are_parsed()
        {
            Assert.Equal(123, BatteryInfoViewParser.ParseInt("123 cycles"));
            Assert.Null(BatteryInfoViewParser.ParseInt("none"));
            Assert.Null(BatteryInfoViewParser.ParseInt(""));
            Assert.Equal(57000L, BatteryInfoViewParser.ParseEnergy("57,000 mWh"));
            Assert.Equal(57000L, BatteryInfoViewParser.ParseEnergy("57'000 mWh"));
            Assert.Null(BatteryInfoViewParser.ParseEnergy("n/a"));
        }

        [Fact]
        public void Battery_health_requires_plausible_design_and_full_capacities()
        {
            Assert.Equal(87.9, BatteryHealth.Compute(50000, 43950));
            Assert.Equal(150.0, BatteryHealth.Compute(50000, 75000));
            Assert.Null(BatteryHealth.Compute(50000, 400));     // below 1 %
            Assert.Null(BatteryHealth.Compute(0, 40000));
            Assert.Null(BatteryHealth.Compute(50000, 0));
            Assert.Null(BatteryHealth.Compute(null, 40000));
        }

    }
}
