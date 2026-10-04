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
    }
}
