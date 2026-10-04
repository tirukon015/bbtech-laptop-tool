using System;
using System.Linq;
using BBTech.Core.Export;
using BBTech.Core.Models;
using BBTech.Core.Parsing;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    public class ReportNamingTests
    {
        private static readonly DateTime When = new DateTime(2026, 10, 4, 17, 56, 25);

        /// <summary>Builds the detection result the same way DETECT does (ModelParser), plus a serial.</summary>
        private static DetectionResult Detected(string fullName, string serial)
        {
            var p = ModelParser.ParseFullName(fullName);
            var d = new DetectionResult { Brand = p.Brand, Model = p.Model, ModelNumber = p.ModelNumber };
            d.System.SerialNumber = serial;
            return d;
        }

        [Fact]
        public void Lenovo_ThinkPad_T14_Gen_2() =>
            Assert.Equal("Lenovo_ThinkPad_T14-Gen-2_SPF12345_20261004_INCOMPLETE.csv",
                ReportNaming.BuildFileName(Detected("Lenovo ThinkPad T14 Gen 2", "SPF12345"), OverallResult.Incomplete, When, "csv"));

        [Fact]
        public void Dell_Latitude_5420() =>
            Assert.Equal("Dell_Latitude_5420_ABC12345_20261004_PASS.csv",
                ReportNaming.BuildFileName(Detected("Dell Latitude 5420", "ABC12345"), OverallResult.Pass, When, "csv"));

        [Fact]
        public void HP_EliteBook_845_G11() =>
            Assert.Equal("HP_EliteBook_845-G11_XYZ98765_20261004_FAIL.csv",
                ReportNaming.BuildFileName(Detected("HP EliteBook 845 G11", "XYZ98765"), OverallResult.Fail, When, "csv"));

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("   ")]
        [InlineData("Unknown")]
        public void Missing_serial_uses_NOSERIAL(string serial) =>
            Assert.Equal("Dell_Latitude_5420_NOSERIAL_20261004_PASS.csv",
                ReportNaming.BuildFileName(Detected("Dell Latitude 5420", serial), OverallResult.Pass, When, "csv"));

        [Fact]
        public void Missing_model_number_uses_NOMODEL()
        {
            // "Acer Aspire" has no model number -> parser gives "Unknown".
            Assert.Equal("Acer_Aspire_NOMODEL_NXABC123_20261004_INCOMPLETE.csv",
                ReportNaming.BuildFileName(Detected("Acer Aspire", "NXABC123"), OverallResult.Incomplete, When, "csv"));
        }

        [Fact]
        public void Missing_model_number_and_serial() =>
            Assert.Equal("Acer_Aspire_NOMODEL_NOSERIAL_20261004_FAIL.json",
                ReportNaming.BuildFileName(Detected("Acer Aspire", ""), OverallResult.Fail, When, "json"));

        [Fact]
        public void Nothing_detected_still_gives_valid_name() =>
            Assert.Equal("UNKNOWN_UNKNOWN_NOMODEL_NOSERIAL_20261004_INCOMPLETE.csv",
                ReportNaming.BuildFileName(new DetectionResult(), OverallResult.Incomplete, When, "csv"));

        [Fact]
        public void Invalid_characters_and_underscores_are_sanitized()
        {
            string name = ReportNaming.BuildFileName("HP/Compaq", "Elite:Book*", "845 G11 \"x\"", "AB_12?34|5", OverallResult.Pass, When, "csv");
            Assert.Equal("HPCompaq_EliteBook_845-G11-x_AB-12345_20261004_PASS.csv", name);
            Assert.True(name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0);
            Assert.Equal(6, System.IO.Path.GetFileNameWithoutExtension(name).Split('_').Length); // separators intact
        }

        [Fact]
        public void Long_values_are_shortened()
        {
            string name = ReportNaming.BuildFileName("Brand", new string('M', 80), new string('N', 80), new string('S', 80), OverallResult.Pass, When, "csv");
            Assert.True(name.Length <= 140, name);
            Assert.EndsWith("_20261004_PASS.csv", name);
        }

        [Fact]
        public void Duplicate_report_keeps_status_last() =>
            Assert.Equal("Dell_Latitude_5420_ABC12345_20261004-2_PASS.csv",
                ReportNaming.BuildFileName("Dell", "Latitude", "5420", "ABC12345", OverallResult.Pass, When, "csv", duplicate: 2));

        [Theory]
        [InlineData(OverallResult.Pass, "PASS")]
        [InlineData(OverallResult.Fail, "FAIL")]
        [InlineData(OverallResult.Incomplete, "INCOMPLETE")]
        public void Status_is_last(OverallResult r, string label) =>
            Assert.EndsWith("_" + label + ".csv", ReportNaming.BuildFileName(Detected("Dell Latitude 5420", "X1"), r, When, "csv"));
    }

    public class TestReportCsvTests
    {
        [Fact]
        public void Csv_contains_identification_block_and_results()
        {
            var d = new DetectionResult
            {
                Brand = "Lenovo", Model = "ThinkPad", ModelNumber = "T14 Gen 2", Cpu = "i5", Generation = "11th Gen",
                Ram = "16 GB", Storage = "512 GB SSD", BatteryHealth = "87%",
            };
            d.System.SerialNumber = "SPF12345";
            var items = new[]
            {
                new TestItem(HardwareTest.Display, "") { State = TestState.Pass },
                new TestItem(HardwareTest.Camera, "") { State = TestState.NotTested },
                new TestItem(HardwareTest.Keyboard, "") { State = TestState.Fail, Notes = "Keys 78/79; not pressed: Q" },
            };

            string csv = TestReportExporter.ToCsv(items, d, new DateTime(2026, 10, 4, 17, 56, 25));
            var lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.None);

            Assert.Equal("Field,Value", lines[0]);
            Assert.Contains("Inspection Date,2026-10-04", lines);
            Assert.Contains("Inspection Time,17:56:25", lines);
            Assert.Contains("Brand,Lenovo", lines);
            Assert.Contains("Model,ThinkPad", lines);
            Assert.Contains("Model Number,T14 Gen 2", lines);
            Assert.Contains("Serial Number,SPF12345", lines);
            Assert.Contains("CPU,i5", lines);
            Assert.Contains("Generation,11th Gen", lines);
            Assert.Contains("RAM,16 GB", lines);
            Assert.Contains("Storage,512 GB SSD", lines);
            Assert.Contains("Battery Health,87%", lines);
            Assert.Contains("Overall Result,FAIL", lines);
            Assert.Contains("Test,Status,Comment", lines);
            Assert.Contains("Display,PASS,", lines);
            Assert.Contains("Camera,NOT TESTED,", lines);
            Assert.Contains("Keyboard,FAIL,Keys 78/79; not pressed: Q", lines);

            // Identification block comes before the test table.
            Assert.True(Array.IndexOf(lines, "Overall Result,FAIL") < Array.IndexOf(lines, "Test,Status,Comment"));
        }

        [Fact]
        public void Report_from_history_contains_inspection_operator_and_stored_result()
        {
            var device = new BBTech.Core.Storage.DeviceRecord
            {
                DeviceId = "d1", Manufacturer = "Dell", Model = "Latitude", ModelNumber = "5420", SerialNumber = "ABC12345",
                SystemUuid = "4C4C4544-0042-3510-8051-B8C04F4E3532",
            };
            var insp = new BBTech.Core.Storage.InspectionRecord
            {
                InspectionId = "insp-42", DeviceId = "d1", Number = 2, OperatorNameSnapshot = "Rahim", OperatorRoleSnapshot = "QC",
                Status = BBTech.Core.Storage.InspectionStatus.Completed, OverallStatus = "FAIL", AppVersion = "1.0.0",
                StartedAt = new DateTime(2026, 10, 18, 9, 0, 0), CompletedAt = new DateTime(2026, 10, 18, 9, 40, 0),
                Specs = new BBTech.Core.Storage.DeviceSpecs { Cpu = "i5", Generation = "11th Gen", Ram = "16 GB", Storage = "512 GB SSD", BatteryHealth = "81%" },
            };
            var results = new[]
            {
                new BBTech.Core.Storage.TestResultRecord { TestId = "Battery", Status = "FAIL", Required = true, Comment = "Health 41%" },
                new BBTech.Core.Storage.TestResultRecord { TestId = "Display", Status = "PASS", Required = true },
            };
            var items = HistoryReport.Items(results);
            var det = HistoryReport.Device(device, insp);
            string csv = TestReportExporter.ToCsv(items, det, insp.CompletedAt.Value, HistoryReport.Meta(insp));
            var lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.None);
            Assert.Contains("Inspection ID,insp-42", lines);
            Assert.Contains("Inspection Number,2", lines);
            Assert.Contains("Tested By,Rahim", lines);
            Assert.Contains("Role,QC", lines);
            Assert.Contains("App Version,1.0.0", lines);
            Assert.Contains("System UUID,4C4C4544-0042-3510-8051-B8C04F4E3532", lines);
            Assert.Contains("Battery Health,81%", lines);
            Assert.Contains("Overall Result,FAIL", lines);
            Assert.Contains("Battery,FAIL,Health 41%", lines);
            Assert.Equal("Dell_Latitude_5420_ABC12345_20261018_FAIL.csv", ReportNaming.BuildFileName(det, OverallResult.Fail, insp.CompletedAt.Value, "csv"));
        }

        [Fact]
        public void Csv_without_detection_reports_unknown_not_blank()
        {
            string csv = TestReportExporter.ToCsv(new TestItem[0], new DetectionResult(), DateTime.Now);
            Assert.Contains("Serial Number,Unknown", csv);
            Assert.Contains("Overall Result,INCOMPLETE", csv);
        }
    }
}
