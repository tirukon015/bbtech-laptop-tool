using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using BBTech.Core.Export;
using BBTech.Core.Models;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>JSON writer, report file names and exporter output details.</summary>
    public class ReportFormatTests
    {
        private static readonly DateTime When = new DateTime(2026, 10, 5, 14, 30, 0);

        [Fact]
        public void Json_primitives_are_written_as_json_literals()
        {
            Assert.Equal("null", JsonWriter.Serialize(null));
            Assert.Equal("true", JsonWriter.Serialize(true));
            Assert.Equal("42", JsonWriter.Serialize(42));
            Assert.Equal("9000000000", JsonWriter.Serialize(9000000000L));
            Assert.Equal("\"Pass\"", JsonWriter.Serialize(TestState.Pass));
            Assert.Equal("\"2026-10-05T14:30:00\"", JsonWriter.Serialize(When));
        }

        [Fact]
        public void Json_numbers_use_a_dot_in_every_windows_region()
        {
            var old = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                Assert.Equal("2.5", JsonWriter.Serialize(2.5));
                Assert.Equal("1.235", JsonWriter.Serialize(1.23456));
                Assert.Equal("1.5", JsonWriter.Serialize(1.5f));
                Assert.Equal("1.10", JsonWriter.Serialize(1.10m));
            }
            finally { Thread.CurrentThread.CurrentCulture = old; }
        }

        [Fact]
        public void Non_finite_numbers_become_null()
        {
            Assert.Equal("null", JsonWriter.Serialize(double.NaN));
            Assert.Equal("null", JsonWriter.Serialize(double.PositiveInfinity));
        }

        [Fact]
        public void Json_strings_escape_special_and_control_characters()
        {
            Assert.Equal("\"a\\\"b\\\\c\\n\\t\\u0001\"", JsonWriter.Serialize("a\"b\\c\n\t\u0001"));
            Assert.Equal("\"Sound – left/right ✓\"", JsonWriter.Serialize("Sound – left/right ✓"));
        }

        [Fact]
        public void Compact_json_keeps_insertion_order()
        {
            var o = new JsonObject { { "z", 1 }, { "a", new[] { 1, 2 } }, { "m", new JsonObject() }, { "e", new int[0] } };
            Assert.Equal("{\"z\":1,\"a\":[1,2],\"m\":{},\"e\":[]}", JsonWriter.Serialize(o, false));
        }

        [Fact]
        public void Indented_json_uses_two_spaces_per_level()
        {
            var o = new JsonObject { { "a", new JsonObject { { "b", 1 } } } };
            Assert.Equal("{\n  \"a\": {\n    \"b\": 1\n  }\n}", JsonWriter.Serialize(o));
        }

        [Fact]
        public void Dictionaries_are_written_as_objects()
        {
            var d = new Dictionary<string, object> { { "brand", "Dell" }, { "count", 2 } };
            Assert.Equal("{\"brand\":\"Dell\",\"count\":2}", JsonWriter.Serialize(d, false));
        }

        [Theory]
        [InlineData("CON", "CON-")]
        [InlineData("nul", "nul-")]
        [InlineData("COM1", "COM1-")]
        [InlineData("CONSOLE", "CONSOLE")]
        public void Reserved_device_names_are_made_safe(string value, string expected)
        {
            Assert.Equal(expected, ReportNaming.Part(value, "X", 30));
        }

        [Theory]
        [InlineData("A/B:C*?\"<>|D", "ABCD")]
        [InlineData("ThinkPad X1 Carbon Gen 9", "ThinkPad-X1-Carbon-Gen-9")]
        [InlineData("845_G11", "845-G11")]
        [InlineData("  ___  ", "FALLBACK")]
        [InlineData("Unknown", "FALLBACK")]
        [InlineData("Model (2021)", "Model-2021")]
        public void File_name_parts_are_sanitised(string value, string expected)
        {
            Assert.Equal(expected, ReportNaming.Part(value, "FALLBACK", 30));
        }

        [Fact]
        public void Compatibility_characters_are_normalised()
        {
            Assert.Equal("Book-XII", ReportNaming.Part("Book Ⅻ", "X", 30));
            Assert.Equal("AB12", ReportNaming.Part("ＡＢ１２", "X", 30));
        }

        [Fact]
        public void Long_parts_are_capped_without_a_trailing_hyphen()
        {
            Assert.Equal(new string('A', 30), ReportNaming.Part(new string('A', 45), "X", 30));
            Assert.Equal(new string('A', 9), ReportNaming.Part(new string('A', 9) + " BBBB", "X", 10));
        }

        [Fact]
        public void Extension_and_duplicate_suffix_are_applied()
        {
            string b = ReportNaming.BuildFileName("Dell", "Latitude", "5420", "SERIAL-EXAMPLE", OverallResult.Fail, When, ".csv");
            Assert.Equal("Dell_Latitude_5420_SERIAL-EXAMPLE_20261005_FAIL.csv", b);
            Assert.EndsWith("_PASS", ReportNaming.BuildFileName("Dell", "Latitude", "5420", "S1", OverallResult.Pass, When, null));
            Assert.Contains("_20261005_", ReportNaming.BuildFileName("Dell", "Latitude", "5420", "S1", OverallResult.Pass, When, "json", 1));
            Assert.Contains("_20261005-3_", ReportNaming.BuildFileName("Dell", "Latitude", "5420", "S1", OverallResult.Pass, When, "json", 3));
        }

        [Fact]
        public void Missing_detection_gives_placeholder_parts()
        {
            Assert.Equal("UNKNOWN_UNKNOWN_NOMODEL_NOSERIAL_20261005_INCOMPLETE.json",
                ReportNaming.BuildFileName((DetectionResult)null, OverallResult.Incomplete, When, "json"));
        }

        private static List<TestItem> Items(params (HardwareTest Kind, TestState State, bool Required, string Notes)[] rows) =>
            rows.Select(r => new TestItem(r.Kind, "", r.Required) { State = r.State, Notes = r.Notes ?? "" }).ToList();

        [Fact]
        public void Csv_marks_tests_that_are_not_required()
        {
            var items = Items((HardwareTest.Display, TestState.Pass, true, ""), (HardwareTest.Camera, TestState.Skipped, false, "shutter closed"), (HardwareTest.Usb, TestState.NotTested, false, ""));
            string csv = TestReportExporter.ToCsv(items, new DetectionResult(), When);
            Assert.Contains("Camera,SKIPPED,Not required; shutter closed", csv);
            Assert.Contains("USB,NOT TESTED,Not required", csv);
            Assert.Contains("Overall Result,PASS", csv);
        }

        [Fact]
        public void Stored_overall_result_wins_over_recalculation()
        {
            var items = Items((HardwareTest.Display, TestState.Pass, true, ""));
            var meta = new ReportMeta { OverallStatus = "INCOMPLETE", AppVersion = "1.0.0" };
            Assert.Contains("Overall Result,INCOMPLETE", TestReportExporter.ToCsv(items, null, When, meta));
            Assert.Contains("\"overall_result\": \"INCOMPLETE\"", TestReportExporter.ToJson(items, null, "9.9.9", When, meta));
        }

        [Fact]
        public void Json_tool_version_comes_from_the_inspection_when_known()
        {
            var items = Items((HardwareTest.Display, TestState.Pass, true, ""));
            Assert.Contains("\"tool\": \"BBTech Laptop Tool 1.0.0\"", TestReportExporter.ToJson(items, null, "1.0.1", When, new ReportMeta { AppVersion = "1.0.0" }));
            Assert.Contains("\"tool\": \"BBTech Laptop Tool 1.0.1\"", TestReportExporter.ToJson(items, null, "1.0.1", When));
        }

        [Fact]
        public void Test_mode_is_omitted_when_not_recorded()
        {
            var items = Items((HardwareTest.Display, TestState.Pass, true, ""));
            var meta = new ReportMeta { InspectionId = "id-1", Number = 1 };
            Assert.DoesNotContain("Test Mode", TestReportExporter.ToCsv(items, null, When, meta));
            Assert.DoesNotContain("test_mode", TestReportExporter.ToJson(items, null, "1.0.0", When, meta));
            meta.TestMode = "ALL TESTS";
            Assert.Contains("Test Mode,ALL TESTS", TestReportExporter.ToCsv(items, null, When, meta));
        }

        [Fact]
        public void Missing_serial_is_unknown_and_missing_uuid_row_is_omitted()
        {
            string csv = TestReportExporter.ToCsv(new TestItem[0], new DetectionResult(), When);
            Assert.Contains("Serial Number,Unknown", csv);
            Assert.DoesNotContain("System UUID", csv);
            string json = TestReportExporter.ToJson(new TestItem[0], new DetectionResult(), "1.0.0", When);
            Assert.Contains("\"serial_number\": \"Unknown\"", json);
            Assert.Contains("\"system_uuid\": \"\"", json);
        }

        [Fact]
        public void Detection_json_lists_storage_devices_with_unknown_type_when_unsure()
        {
            var r = new DetectionResult();
            r.StorageDevices.Add(new StorageDevice { CapacityLabel = "256 GB", CapacityBytes = 256060514304L, BusType = "SATA", Model = "Example SSD" });
            string json = DetectionExporter.ToJson(r);
            Assert.Contains("\"type\": \"Unknown\"", json);
            Assert.Contains("\"capacity_bytes\": 256060514304", json);
            Assert.Contains("\"health_percent\": null", json);
        }

        [Fact]
        public void Date_time_offsets_are_written_in_iso_format_with_the_offset()
        {
            var t = new DateTimeOffset(2026, 10, 5, 14, 30, 0, TimeSpan.FromHours(6));
            Assert.Equal("\"2026-10-05T14:30:00+06:00\"", JsonWriter.Serialize(t));
        }

        private static List<TestItem> SelectedAndUnselected() => new List<TestItem>
        {
            new TestItem(HardwareTest.Display, "") { State = TestState.Pass },
            new TestItem(HardwareTest.Speaker, "") { State = TestState.Pass },
            new TestItem(HardwareTest.Camera, "", required: false),
        };

        [Fact]
        public void Csv_report_lists_exactly_the_selected_tests()
        {
            string csv = TestReportExporter.ToCsv(SelectedAndUnselected(), new DetectionResult(), When);
            Assert.Contains("\r\nSelected Tests,Display; Sound\r\n", csv);
        }

        [Fact]
        public void Json_report_lists_exactly_the_selected_tests()
        {
            string json = TestReportExporter.ToJson(SelectedAndUnselected(), new DetectionResult(), "1.0", When);
            Assert.Contains("\"selected_tests\": [\n    \"Display\",\n    \"Sound\"\n  ]", json);
        }
    }
}
