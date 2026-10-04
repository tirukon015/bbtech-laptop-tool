using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BBTech.Core.Models;
using BBTech.Core.Testing;

namespace BBTech.Core.Export
{
    public static class DetectionExporter
    {
        /// <summary>The eight ITMS fields, in the agreed order and with the agreed key names.</summary>
        public static JsonObject ToItmsObject(DetectionResult r)
        {
            return new JsonObject
            {
                { "brand", r.Brand },
                { "model", r.Model },
                { "model_number", r.ModelNumber },
                { "cpu", r.Cpu },
                { "generation", r.Generation },
                { "ram", r.Ram },
                { "storage", r.Storage },
                { "battery_health", r.BatteryHealth },
            };
        }

        /// <summary>
        /// JSON export: the ITMS fields at top level (so the file can be consumed as-is),
        /// followed by a "details" block for troubleshooting.
        /// </summary>
        public static string ToJson(DetectionResult r)
        {
            var root = ToItmsObject(r);
            root.Add("storage_devices", r.StorageDevices.Select(d => new JsonObject
            {
                { "display", d.DisplayValue },
                { "capacity", d.CapacityLabel },
                { "type", d.MediaType.Length > 0 ? d.MediaType : DetectionResult.Unknown },
                { "bus", d.BusType },
                { "model", d.Model },
                { "capacity_bytes", d.CapacityBytes },
                { "external", d.IsExternal },
                { "windows_health", d.WindowsHealth },
                { "health_percent", (object)d.HealthPercent },
            }).ToList());

            root.Add("details", new JsonObject
            {
                { "serial_number", r.System.SerialNumber },
                { "product_code", r.System.ProductCode },
                { "raw_manufacturer", r.System.RawManufacturer },
                { "raw_model", r.System.RawModel },
                { "system_family", r.System.SystemFamily },
                { "product_version", r.System.ProductVersion },
                { "chassis", r.System.ChassisType },
                { "bios_version", r.System.BiosVersion },
                { "os", r.System.OperatingSystem },
                { "cpu_full_name", r.CpuDetails.FullName },
                { "cpu_cores", r.CpuDetails.Cores },
                { "cpu_threads", r.CpuDetails.LogicalProcessors },
                { "ram_modules", r.Memory.Modules.Select(m => new JsonObject
                    {
                        { "size", Parsing.CapacityFormatter.FormatRamFromModules(m.CapacityBytes) },
                        { "type", m.Type },
                        { "speed_mhz", m.SpeedMhz },
                        { "slot", m.Slot },
                    }).ToList() },
                { "battery", new JsonObject
                    {
                        { "present", r.Battery.Present },
                        { "count", r.Battery.BatteryCount },
                        { "design_capacity_mwh", (object)r.Battery.DesignCapacityMWh },
                        { "full_charge_capacity_mwh", (object)r.Battery.FullChargeCapacityMWh },
                        { "current_capacity_mwh", (object)r.Battery.CurrentCapacityMWh },
                        { "charge_percent", (object)r.Battery.ChargePercent },
                        { "cycle_count", (object)r.Battery.CycleCount },
                        { "health_percent_exact", (object)r.Battery.HealthPercent },
                        { "chemistry", r.Battery.Chemistry },
                        { "source", r.Battery.Source },
                    } },
                { "warnings", r.Warnings },
                { "detected_at", r.DetectedAt },
                { "tool", "BBTech Laptop Tool " + r.ToolVersion },
            });
            return JsonWriter.Serialize(root);
        }

        public static string ToCsv(DetectionResult r)
        {
            var headers = new[] { "brand", "model", "model_number", "cpu", "generation", "ram", "storage", "battery_health", "serial_number", "detected_at" };
            var values = new[] { r.Brand, r.Model, r.ModelNumber, r.Cpu, r.Generation, r.Ram, r.Storage, r.BatteryHealth,
                                 r.System.SerialNumber, r.DetectedAt.ToString("yyyy-MM-dd HH:mm:ss") };
            return string.Join(",", headers) + "\r\n" + string.Join(",", values.Select(Csv)) + "\r\n";
        }

        public static string Csv(string v)
        {
            v = v ?? "";
            if (v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }
    }

    /// <summary>
    /// TEST inspection report. The laptop identification comes from the same
    /// <see cref="DetectionResult"/> that DETECT produces (no separate detection).
    /// </summary>
    /// <summary>Inspection details added to exported reports (from the laptop's QC history).</summary>
    public sealed class ReportMeta
    {
        public string InspectionId { get; set; } = "";
        public int Number { get; set; }
        public string TestedBy { get; set; } = "";
        public string Role { get; set; } = "";
        public string Status { get; set; } = "";
        /// <summary>Stored overall result of a completed inspection (otherwise calculated).</summary>
        public string OverallStatus { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string AppVersion { get; set; } = "";
        /// <summary>"REQUIRED" / "ALL TESTS" ("" for older inspections).</summary>
        public string TestMode { get; set; } = "";
    }

    public static class TestReportExporter
    {
        private static string Serial(DetectionResult d) =>
            string.IsNullOrWhiteSpace(d.System?.SerialNumber) ? DetectionResult.Unknown : d.System.SerialNumber;

        private static string Overall(List<TestItem> list, ReportMeta meta) =>
            !string.IsNullOrEmpty(meta?.OverallStatus) ? meta.OverallStatus : TestText.Label(OverallResultCalculator.Calculate(list));

        private static string Stamp(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

        public static string ToJson(IEnumerable<TestItem> items, DetectionResult device, string toolVersion, DateTime when, ReportMeta meta = null)
        {
            var list = items.ToList();
            var d = device ?? new DetectionResult();
            var deviceObj = DetectionExporter.ToItmsObject(d);
            deviceObj.Add("serial_number", Serial(d));
            deviceObj.Add("system_uuid", d.System?.SystemUuid ?? "");
            var root = new JsonObject
            {
                { "inspection_date", when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "inspection_time", when.ToString("HH:mm:ss", CultureInfo.InvariantCulture) },
            };
            if (meta != null)
            {
                root.Add("inspection_id", meta.InspectionId);
                root.Add("inspection_number", meta.Number);
                root.Add("inspection_status", meta.Status);
                root.Add("tested_by", meta.TestedBy);
                root.Add("role", meta.Role);
                if (!string.IsNullOrEmpty(meta.TestMode)) root.Add("test_mode", meta.TestMode);
                root.Add("started_at", Stamp(meta.StartedAt));
                root.Add("completed_at", Stamp(meta.CompletedAt));
            }
            root.Add("overall_result", Overall(list, meta));
            root.Add("device", deviceObj);
            root.Add("tests", list.Select(i => new JsonObject
            {
                { "test", i.Name },
                { "status", i.StateLabel },
                { "required", i.Required },
                { "comment", i.Notes },
                { "completed_at", (object)i.CompletedAt },
            }).ToList());
            root.Add("tool", "BBTech Laptop Tool " + (string.IsNullOrEmpty(meta?.AppVersion) ? toolVersion : meta.AppVersion));
            return JsonWriter.Serialize(root);
        }

        /// <summary>
        /// CSV report: a laptop identification block (Field,Value) followed by a blank line and
        /// the individual results (Test,Status,Comment). Opens directly in Excel.
        /// </summary>
        public static string ToCsv(IEnumerable<TestItem> items, DetectionResult device, DateTime when, ReportMeta meta = null)
        {
            var list = items.ToList();
            var d = device ?? new DetectionResult();
            var sb = new StringBuilder();
            void Row(string a, string b) => sb.Append(DetectionExporter.Csv(a)).Append(',').Append(DetectionExporter.Csv(b)).Append("\r\n");

            sb.Append("Field,Value\r\n");
            Row("Inspection Date", when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Row("Inspection Time", when.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            if (meta != null)
            {
                Row("Inspection ID", meta.InspectionId);
                Row("Inspection Number", meta.Number.ToString(CultureInfo.InvariantCulture));
                Row("Tested By", meta.TestedBy);
                Row("Role", meta.Role);
                if (!string.IsNullOrEmpty(meta.TestMode)) Row("Test Mode", meta.TestMode);
                Row("Started", Stamp(meta.StartedAt));
                Row("Completed", Stamp(meta.CompletedAt));
                Row("App Version", meta.AppVersion);
            }
            Row("Brand", d.Brand);
            Row("Model", d.Model);
            Row("Model Number", d.ModelNumber);
            Row("Serial Number", Serial(d));
            if (!string.IsNullOrEmpty(d.System?.SystemUuid)) Row("System UUID", d.System.SystemUuid);
            Row("CPU", d.Cpu);
            Row("Generation", d.Generation);
            Row("RAM", d.Ram);
            Row("Storage", d.Storage);
            Row("Battery Health", d.BatteryHealth);
            Row("Overall Result", Overall(list, meta));
            sb.Append("\r\n");

            sb.Append("Test,Status,Comment\r\n");
            foreach (var i in list)
            {
                string comment = i.Required ? i.Notes : (i.Notes.Length > 0 ? "Not required; " + i.Notes : "Not required");
                sb.Append(DetectionExporter.Csv(i.Name)).Append(',').Append(i.StateLabel).Append(',').Append(DetectionExporter.Csv(comment)).Append("\r\n");
            }
            return sb.ToString();
        }
    }
}
