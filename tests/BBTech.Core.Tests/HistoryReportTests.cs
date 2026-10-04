using System;
using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Export;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Turning stored inspections into report rows.</summary>
    public class HistoryReportTests
    {
        private static TestResultRecord Res(string test, string status, string comment = "") =>
            new TestResultRecord { TestId = test, TestName = test, Status = status, Required = true, Comment = comment };

        [Fact]
        public void Items_tolerates_missing_results_and_repeated_selection_entries()
        {
            Assert.Empty(HistoryReport.Items((IEnumerable<TestResultRecord>)null));
            var view = new InspectionView
            {
                Inspection = new InspectionRecord { SelectedTests = new List<string> { "Display", "Display", "Keyboard" } },
                Results = new List<TestResultRecord> { Res("Display", "PASS"), null },
            };
            var items = HistoryReport.Items(view);
            Assert.Equal(new[] { HardwareTest.Display, HardwareTest.Keyboard }, items.Select(i => i.Kind));
            Assert.Equal(TestState.NotTested, items[1].State);
        }
    }
}
