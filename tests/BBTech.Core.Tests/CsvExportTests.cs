using System;
using System.Globalization;
using System.Threading;
using BBTech.Core.Export;
using BBTech.Core.Models;
using Xunit;

namespace BBTech.Core.Tests
{
    public class CsvExportTests
    {
        private static T WithCulture<T>(string name, Func<T> f)
        {
            var old = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo(name);
            try { return f(); } finally { Thread.CurrentThread.CurrentCulture = old; }
        }

        [Fact]
        public void Detection_csv_timestamp_does_not_depend_on_the_windows_region()
        {
            var r = new DetectionResult { DetectedAt = new DateTime(2026, 10, 5, 9, 7, 3) };
            // Thai uses the Buddhist calendar (year 2569) and Arabic locales use other digits/calendars.
            string th = WithCulture("th-TH", () => DetectionExporter.ToCsv(r));
            string ar = WithCulture("ar-SA", () => DetectionExporter.ToCsv(r));
            Assert.Contains("2026-10-05 09:07:03", th);
            Assert.Contains("2026-10-05 09:07:03", ar);
        }

        [Theory]
        [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
        [InlineData("+1+2", "'+1+2")]
        [InlineData("-2+3", "'-2+3")]
        [InlineData("@SUM(A1)", "'@SUM(A1)")]
        [InlineData("Keys 79/79", "Keys 79/79")]
        [InlineData("87%", "87%")]
        public void Csv_cells_that_excel_would_run_as_formulas_are_neutralised(string value, string expected)
        {
            Assert.Equal(expected, DetectionExporter.Csv(value));
        }

        [Theory]
        [InlineData("line1\nline2", "\"line1\nline2\"")]
        [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
        [InlineData("a,b", "\"a,b\"")]
        [InlineData(null, "")]
        public void Csv_cells_with_separators_are_quoted(string value, string expected)
        {
            Assert.Equal(expected, DetectionExporter.Csv(value));
        }

        [Fact]
        public void Csv_reports_use_windows_line_endings()
        {
            string csv = TestReportExporter.ToCsv(new[] { new Testing.TestItem(Testing.HardwareTest.Display, "") }, new DetectionResult(), new DateTime(2026, 10, 5));
            Assert.DoesNotContain("\n", csv.Replace("\r\n", ""));
            Assert.EndsWith("\r\n", csv);
        }

    }
}
