using System;
using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using BBTech.Core.Testing;

namespace BBTech.Core.Export
{
    /// <summary>Turns a stored inspection (from the laptop's QC history) into report inputs.</summary>
    public static class HistoryReport
    {
        public static ReportMeta Meta(InspectionRecord i) => new ReportMeta
        {
            InspectionId = i.InspectionId,
            Number = i.Number,
            TestedBy = i.OperatorNameSnapshot,
            Role = i.OperatorRoleSnapshot,
            Status = i.Status,
            OverallStatus = i.Status == InspectionStatus.InProgress ? null : i.OverallStatus,
            StartedAt = i.StartedAt,
            CompletedAt = i.CompletedAt,
            AppVersion = i.AppVersion,
            TestMode = ModeLabel(i),
        };

        /// <summary>Device identification + the hardware summary recorded with the inspection.</summary>
        public static DetectionResult Device(DeviceRecord d, InspectionRecord i)
        {
            var s = i?.Specs ?? d.Specs ?? new DeviceSpecs();
            var r = new DetectionResult
            {
                Brand = d.Manufacturer, Model = d.Model, ModelNumber = d.ModelNumber,
                Cpu = s.Cpu ?? DetectionResult.Unknown, Generation = s.Generation ?? DetectionResult.Unknown,
                Ram = s.Ram ?? DetectionResult.Unknown, Storage = s.Storage ?? DetectionResult.Unknown,
                BatteryHealth = s.BatteryHealth ?? DetectionResult.Unknown,
            };
            r.System.SerialNumber = d.SerialNumber ?? "";
            r.System.SystemUuid = d.SystemUuid ?? "";
            return r;
        }

        /// <summary>Stored results as TestItems in the standard test order.</summary>
        public static List<TestItem> Items(IEnumerable<TestResultRecord> results)
        {
            var list = new List<TestItem>();
            foreach (var r in results)
            {
                if (!Enum.TryParse(r.TestId, out HardwareTest kind)) continue;
                var item = new TestItem(kind, "", r.Required) { State = Parse(r.Status), Notes = r.Comment ?? "" };
                item.CompletedAt = r.CompletedAt;
                list.Add(item);
            }
            return list.OrderBy(i => TestCatalog.Order(i.Kind)).ToList();
        }

        /// <summary>
        /// The tests of an inspection as recorded: exactly the selected tests (a selected test without a
        /// result is shown as NOT TESTED). Older inspections without a stored selection show their results.
        /// </summary>
        public static List<TestItem> Items(InspectionView view)
        {
            var results = view.Results ?? new List<TestResultRecord>();
            var selected = view.Inspection?.SelectedTests;
            if (selected == null) return Items(results);

            var list = new List<TestItem>();
            foreach (var name in selected)
            {
                if (!Enum.TryParse(name, out HardwareTest kind)) continue;
                var r = results.FirstOrDefault(x => x.TestId == name);
                var item = new TestItem(kind, "", true) { State = r == null ? TestState.NotTested : Parse(r.Status), Notes = r?.Comment ?? "" };
                item.CompletedAt = r?.CompletedAt;
                list.Add(item);
            }
            return list.OrderBy(i => TestCatalog.Order(i.Kind)).ToList();
        }

        /// <summary>"REQUIRED" / "ALL TESTS" / "" (older inspections).</summary>
        public static string ModeLabel(InspectionRecord i) =>
            i?.TestMode == TestMode.AllTests.ToString() ? "ALL TESTS" : i?.TestMode == TestMode.Required.ToString() ? "REQUIRED" : "";

        public static TestState Parse(string label)
        {
            switch (label)
            {
                case "PASS": return TestState.Pass;
                case "FAIL": return TestState.Fail;
                case "SKIPPED": return TestState.Skipped;
                case "TESTING": return TestState.Testing;
                default: return TestState.NotTested;
            }
        }
    }
}
