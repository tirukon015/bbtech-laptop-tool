using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Export;
using BBTech.Core.Models;
using BBTech.Core.Parsing;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    public class CapacityTests
    {
        [Theory]
        [InlineData(17179869184L * 2, "32 GB")]
        [InlineData(17179869184L, "16 GB")]
        [InlineData(8589934592L, "8 GB")]
        public void Ram_from_modules(long bytes, string expected) => Assert.Equal(expected, CapacityFormatter.FormatRamFromModules(bytes));

        [Theory]
        [InlineData(34048368640L, "32 GB")]   // 31.7 GiB visible on a 32 GB machine
        [InlineData(16954245120L, "16 GB")]
        [InlineData(8458629120L, "8 GB")]
        public void Ram_from_visible_memory_rounds_up(long bytes, string expected) => Assert.Equal(expected, CapacityFormatter.FormatRamFromVisible(bytes));

        [Theory]
        [InlineData(512110190592L, "512 GB")]
        [InlineData(256060514304L, "256 GB")]
        [InlineData(1000204886016L, "1 TB")]
        [InlineData(1024209543168L, "1 TB")]
        [InlineData(240057409536L, "240 GB")]
        [InlineData(500107862016L, "500 GB")]
        [InlineData(128035676160L, "128 GB")]
        [InlineData(2000398934016L, "2 TB")]
        public void Drive_marketing_sizes(long bytes, string expected) => Assert.Equal(expected, CapacityFormatter.FormatDrive(bytes));

        [Fact]
        public void Multiple_drives_are_listed_not_added()
        {
            var devices = new List<StorageDevice>
            {
                new StorageDevice { CapacityLabel = "512 GB", MediaType = "SSD" },
                new StorageDevice { CapacityLabel = "1 TB", MediaType = "HDD" },
                new StorageDevice { CapacityLabel = "32 GB", MediaType = "", IsExternal = true },
            };
            Assert.Equal("512 GB SSD + 1 TB HDD", CapacityFormatter.StorageSummary(devices));
        }

        [Fact]
        public void Drive_without_reliable_type_shows_capacity_only() =>
            Assert.Equal("256 GB", new StorageDevice { CapacityLabel = "256 GB", MediaType = "" }.DisplayValue);
    }

    public class BatteryParserTests
    {
        // Real output captured from BatteryInfoView 1.23 (/sxml) on a ThinkPad T14 Gen 2i.
        private const string SampleXml = @"<?xml version=""1.0"" ?>
<battery_information>
<item><description>Battery Name</description><value>5B10W51829</value><value></value></item>
<item><description>Manufacture Name</description><value>Celxpert</value><value></value></item>
<item><description>Power State</description><value>AC Power</value><value></value></item>
<item><description>Current Capacity (in %)</description><value>99.7%</value><value></value></item>
<item><description>Current Capacity Value</description><value>44,670 mWh</value><value></value></item>
<item><description>Full Charged Capacity</description><value>44,820 mWh</value><value></value></item>
<item><description>Designed Capacity</description><value>51,000 mWh</value><value></value></item>
<item><description>Battery Health</description><value>87.9%</value><value></value></item>
<item><description>Chemistry </description><value>LiP</value><value></value></item>
<item><description>Number of charge/discharge cycles</description><value>202</value><value></value></item>
</battery_information>";

        [Fact]
        public void Parses_batteryinfoview_xml()
        {
            var b = BatteryInfoViewParser.Parse(SampleXml);
            Assert.True(b.Present);
            Assert.Equal(1, b.BatteryCount);
            Assert.Equal(51000, b.DesignCapacityMWh);
            Assert.Equal(44820, b.FullChargeCapacityMWh);
            Assert.Equal(44670, b.CurrentCapacityMWh);
            Assert.Equal(202, b.CycleCount);
            Assert.Equal(87.9, b.HealthPercent);
            Assert.Equal("LiP", b.Chemistry);
            Assert.Equal("87%", BatteryHealth.Format(b.HealthPercent));
        }

        [Fact]
        public void Two_batteries_are_combined()
        {
            string xml = @"<battery_information>
<item><description>Full Charged Capacity</description><value>20,000 mWh</value><value>40,000 mWh</value></item>
<item><description>Designed Capacity</description><value>24,000 mWh</value><value>48,000 mWh</value></item>
</battery_information>";
            var b = BatteryInfoViewParser.Parse(xml);
            Assert.Equal(2, b.BatteryCount);
            Assert.Equal(72000, b.DesignCapacityMWh);
            Assert.Equal(60000, b.FullChargeCapacityMWh);
            Assert.Equal("83%", BatteryHealth.Format(b.HealthPercent));
        }

        [Fact]
        public void No_battery_gives_unknown_health()
        {
            var b = BatteryInfoViewParser.Parse("<battery_information></battery_information>");
            Assert.False(b.Present);
            Assert.Equal("Unknown", BatteryHealth.Format(b.HealthPercent));
        }

        [Fact]
        public void Garbage_input_does_not_throw() => Assert.False(BatteryInfoViewParser.Parse("not xml").Present);

        [Theory]
        [InlineData("44.820 mWh", 44820L)]
        [InlineData("44 820 mWh", 44820L)]
        [InlineData("", null)]
        [InlineData("0 mWh", null)]
        public void Energy_values_are_locale_independent(string s, long? expected) => Assert.Equal(expected, BatteryInfoViewParser.ParseEnergy(s));

        [Fact]
        public void Implausible_capacity_data_is_rejected() => Assert.Null(BatteryHealth.Compute(1000, 5000));
    }

    public class HdSentinelParserTests
    {
        private const string SampleXml = @"<?xml version=""1.0"" encoding=""ISO-8859-1""?>
<Hard_Disk_Sentinel>
  <General_Information><Application_Information><Installed_version>6.30 PRO</Installed_version></Application_Information></General_Information>
  <Physical_Disk_Information_Disk_0>
    <Hard_Disk_Summary>
      <Hard_Disk_Number>0</Hard_Disk_Number>
      <Interface>NVMe</Interface>
      <Hard_Disk_Model_ID>Micron MTFDKBA512TFK</Hard_Disk_Model_ID>
      <Total_Size>488386 MB</Total_Size>
      <Current_Temperature>38 °C</Current_Temperature>
      <Power_On_Time>312 days, 4 hours</Power_On_Time>
      <Health>98 %</Health>
      <Performance>100 %</Performance>
      <Description>The status of the solid state disk is PERFECT.</Description>
    </Hard_Disk_Summary>
  </Physical_Disk_Information_Disk_0>
  <Physical_Disk_Information_Disk_1>
    <Hard_Disk_Summary>
      <Hard_Disk_Model_ID>ST500LM021-1KJ152</Hard_Disk_Model_ID>
      <Health>61 %</Health>
      <Performance>100 %</Performance>
    </Hard_Disk_Summary>
  </Physical_Disk_Information_Disk_1>
</Hard_Disk_Sentinel>";

        [Fact]
        public void Parses_health_per_disk()
        {
            var disks = HdSentinelReportParser.Parse(SampleXml);
            Assert.Equal(2, disks.Count);
            Assert.Equal("Micron MTFDKBA512TFK", disks[0].Model);
            Assert.Equal(98, disks[0].HealthPercent);
            Assert.Equal(100, disks[0].PerformancePercent);
            Assert.Equal(0, disks[0].DiskNumber);
            Assert.Equal(61, disks[1].HealthPercent);
            Assert.Equal(1, disks[1].DiskNumber);
        }

        [Fact]
        public void Invalid_report_returns_empty() => Assert.Empty(HdSentinelReportParser.Parse("<oops"));
    }

    public class OverallResultTests
    {
        private static TestItem T(TestState s, bool required = true)
        {
            var t = new TestItem(HardwareTest.Display, "", required);
            t.State = s;
            return t;
        }

        [Fact]
        public void All_required_pass_is_pass() =>
            Assert.Equal(OverallResult.Pass, OverallResultCalculator.Calculate(new[] { T(TestState.Pass), T(TestState.Pass) }));

        [Fact]
        public void Any_required_fail_is_fail() =>
            Assert.Equal(OverallResult.Fail, OverallResultCalculator.Calculate(new[] { T(TestState.Pass), T(TestState.Fail), T(TestState.NotTested) }));

        [Fact]
        public void Remaining_tests_are_incomplete() =>
            Assert.Equal(OverallResult.Incomplete, OverallResultCalculator.Calculate(new[] { T(TestState.Pass), T(TestState.NotTested) }));

        [Fact]
        public void Skipped_required_test_is_incomplete() =>
            Assert.Equal(OverallResult.Incomplete, OverallResultCalculator.Calculate(new[] { T(TestState.Pass), T(TestState.Skipped) }));

        [Fact]
        public void Optional_tests_are_ignored() =>
            Assert.Equal(OverallResult.Pass, OverallResultCalculator.Calculate(new[] { T(TestState.Pass), T(TestState.Fail, required: false), T(TestState.NotTested, false) }));

        [Fact]
        public void Nothing_required_is_incomplete() =>
            Assert.Equal(OverallResult.Incomplete, OverallResultCalculator.Calculate(new TestItem[0]));

        [Fact]
        public void State_labels_are_explicit_text()
        {
            Assert.Equal("NOT TESTED", TestText.Label(TestState.NotTested));
            Assert.Equal("TESTING", TestText.Label(TestState.Testing));
            Assert.Equal("PASS", TestText.Label(TestState.Pass));
            Assert.Equal("FAIL", TestText.Label(TestState.Fail));
            Assert.Equal("SKIPPED", TestText.Label(TestState.Skipped));
        }
    }

    public class ExportTests
    {
        private static DetectionResult Sample() => new DetectionResult
        {
            Brand = "Dell", Model = "Latitude", ModelNumber = "5420", Cpu = "i5", Generation = "11th Gen",
            Ram = "16 GB", Storage = "512 GB SSD", BatteryHealth = "87%",
        };

        [Fact]
        public void Itms_payload_has_exact_keys_and_values()
        {
            var r = Sample();
            r.System.SerialNumber = "ABC12345";
            var payload = Integration.ItmsDevicePayload.From(r);
            string json = payload.ToJson();
            string expected = "{\n  \"serial_number\": \"ABC12345\",\n  \"system_uuid\": null,\n  \"board_serial\": null,\n" +
                              "  \"hardware_fingerprint\": \"" + payload.HardwareFingerprint + "\",\n" +
                              "  \"brand\": \"Dell\",\n  \"model\": \"Latitude\",\n  \"model_number\": \"5420\",\n  \"cpu\": \"i5\",\n" +
                              "  \"generation\": \"11th Gen\",\n  \"ram\": \"16 GB\",\n  \"storage\": \"512 GB SSD\",\n  \"battery_health\": \"87%\"\n}";
            Assert.Equal(expected, json);
        }

        [Fact]
        public void Full_json_starts_with_itms_fields()
        {
            string json = DetectionExporter.ToJson(Sample());
            Assert.StartsWith("{\n  \"brand\": \"Dell\"", json);
            Assert.Contains("\"details\"", json);
        }

        [Fact]
        public void Csv_has_header_and_quoted_values()
        {
            var r = Sample();
            r.Model = "Latitude, special";
            string csv = DetectionExporter.ToCsv(r);
            var lines = csv.Split(new[] { "\r\n" }, System.StringSplitOptions.RemoveEmptyEntries);
            Assert.StartsWith("brand,model,model_number,cpu,generation,ram,storage,battery_health", lines[0]);
            Assert.Contains("\"Latitude, special\"", lines[1]);
        }

        [Fact]
        public void Json_escapes_special_characters() =>
            Assert.Equal("{\"a\":\"x\\\"y\\\\z\\n\"}", JsonWriter.Serialize(new JsonObject { { "a", "x\"y\\z\n" } }, indent: false));

        [Fact]
        public void Test_report_contains_overall_result()
        {
            var items = new[] { new TestItem(HardwareTest.Display, "") { State = TestState.Pass } };
            var device = Sample();
            device.System.SerialNumber = "ABC12345";
            string json = TestReportExporter.ToJson(items, device, "1.0", new System.DateTime(2026, 10, 4, 17, 56, 25));
            Assert.Contains("\"overall_result\": \"PASS\"", json);
            Assert.Contains("\"inspection_date\": \"2026-10-04\"", json);
            Assert.Contains("\"serial_number\": \"ABC12345\"", json);
            Assert.Contains("\"model_number\": \"5420\"", json);
        }
    }
}
