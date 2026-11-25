using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using BBTech.Core.Export;
using BBTech.Core.Storage;

namespace BBTech.Core.Models
{
    /// <summary>How a value was obtained (ITMS/admins can tell machine data from human judgement).</summary>
    public static class DataConfidence
    {
        public const string Detected = "DETECTED";   // read from the hardware/firmware/OS
        public const string Measured = "MEASURED";   // a live measurement (temperature, wear, capacity now)
        public const string Calculated = "CALCULATED"; // computed from other measured values (formula recorded)
        public const string Verified = "VERIFIED";   // checked against a second source
        public const string Manual = "MANUAL";       // a person looked and decided
        public const string Unknown = "UNKNOWN";
    }

    /// <summary>Why a value is missing (stored next to UNKNOWN so the gap is explained, not hidden).</summary>
    public static class UnknownReason
    {
        public const string AdminRequired = "ADMIN_REQUIRED";
        public const string NotExposed = "NOT_EXPOSED_BY_HARDWARE";
        public const string NotPresent = "NOT_PRESENT";
        public const string ReadFailed = "READ_FAILED";
        /// <summary>The source (Windows / WMI / firmware) returned no value for this field.</summary>
        public const string NotReported = "NOT_REPORTED (the source returned no value)";
        public const string NotConnected = "NOT_CONNECTED (adapter not connected when captured)";
        public const string NoUniqueId = "NO_UNIQUE_ID (Windows reports no serial or unique id for this part)";
    }

    [DataContract]
    public sealed class InventoryValue
    {
        [DataMember] public string Key { get; set; }
        [DataMember] public string Value { get; set; }
        /// <summary>percent, hours, mWh, MHz, MT/s, bytes, °C... (null for text values).</summary>
        [DataMember] public string Unit { get; set; }
        /// <summary>Source of THIS value when it differs from the item's source (e.g. a calculated value).</summary>
        [DataMember] public string Source { get; set; }
        /// <summary>Confidence of THIS value when it differs from the item's (e.g. CALCULATED).</summary>
        [DataMember] public string Confidence { get; set; }
        /// <summary>For CALCULATED values: the formula, e.g. "100 - percentage_used".</summary>
        [DataMember] public string Calculation { get; set; }
        /// <summary>For UNKNOWN values: why (ADMIN_REQUIRED, NOT_EXPOSED_BY_HARDWARE, NOT_PRESENT, READ_FAILED).</summary>
        [DataMember] public string Reason { get; set; }
    }

    /// <summary>Units of known technical keys (one place, so every snapshot and ITMS use the same units).</summary>
    public static class InventoryUnits
    {
        public static string For(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string k = key.ToLowerInvariant();
            if (k.StartsWith("raw.")) k = k.Substring(4);                 // raw drive values use the same units
            if (k.StartsWith("data_units_")) return "NVMe data unit (1 = 512,000 bytes)";
            if (k.StartsWith("total_lbas_")) return "LBA (512 bytes)";
            if (k.EndsWith("_bytes") || k.EndsWith("_bytes_reported")) return "bytes";
            if (k.EndsWith("_mwh")) return "mWh";
            if (k.EndsWith("_mhz")) return "MHz";
            if (k.EndsWith("_mts")) return "MT/s";
            if (k.EndsWith("_mv")) return "mV";
            if (k.EndsWith("_kb")) return "KB";
            if (k.EndsWith("_gb")) return "GB";
            if (k.EndsWith("_temperature_k") || k.EndsWith("temperature_k")) return "K";
            if (k.EndsWith("_c") || k == "temperature") return "°C";
            if (k.EndsWith("_hz")) return "Hz";
            if (k.EndsWith("_bps")) return "bit/s";
            if (k.EndsWith("_inches")) return "inch";
            if (k.EndsWith("_minutes")) return "minutes";
            if (k.EndsWith("_hours") || k == "power_on_hours") return "hours";
            if (k.Contains("percent") || k == "percentage_used") return "percent";
            if (k.EndsWith("_count") || k.EndsWith("_cycles") || k == "power_cycles" || k == "unsafe_shutdowns" || k.EndsWith("_errors") ||
                k.EndsWith("_entries") || k.EndsWith("_commands") || k == "physical_cores" || k == "logical_processors" || k.EndsWith("_slots")) return "count";
            return null;
        }
    }

    /// <summary>One component or fact group (a RAM module, a drive, the BIOS, Windows...), with where it came from.</summary>
    [DataContract]
    public sealed class InventoryItem
    {
        /// <summary>identity, bios, chassis, baseboard, cpu, memory, ram_module, storage, battery, gpu, display, network, audio, camera, bluetooth, tpm, firmware, windows, office, driver</summary>
        [DataMember] public string Category { get; set; }
        [DataMember] public string Name { get; set; }
        /// <summary>
        /// The value that identifies THIS physical part (a serial number, a MAC...). Used to notice that a
        /// part was replaced. Empty when the hardware exposes nothing unique.
        /// </summary>
        [DataMember] public string IdentityKey { get; set; }
        /// <summary>WMI, SMBIOS, NVMe Health Log, SMART, Windows API, Registry, Battery report, Manual Inspection...</summary>
        [DataMember] public string Source { get; set; }
        [DataMember] public string Confidence { get; set; }
        [DataMember] public List<InventoryValue> Values { get; set; } = new List<InventoryValue>();

        public const string UnknownValue = "UNKNOWN";

        /// <summary>Adds a measured/technical value. Missing or empty is stored as UNKNOWN (never invented); "0" stays "0".</summary>
        public InventoryItem Set(string key, object value)
        {
            string v = value == null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim();
            bool unknown = string.IsNullOrEmpty(v);
            Values.Add(new InventoryValue { Key = key, Value = unknown ? UnknownValue : v, Unit = unknown ? null : InventoryUnits.For(key), Reason = unknown ? UnknownReason.NotReported : null });
            return this;
        }

        /// <summary>A value calculated from other values; the formula is stored with it.</summary>
        public InventoryItem SetCalculated(string key, object value, string calculation, string unit = null)
        {
            Set(key, value);
            var last = Values[Values.Count - 1];
            if (last.Value != UnknownValue) { last.Confidence = DataConfidence.Calculated; last.Source = "calculated"; last.Calculation = calculation; last.Unit = unit ?? last.Unit; }
            return this;
        }

        /// <summary>A value with its own source/confidence (e.g. measured by a different engine than the item's identity).</summary>
        public InventoryItem SetFrom(string key, object value, string source, string confidence)
        {
            Set(key, value);
            var last = Values[Values.Count - 1];
            if (last.Value != UnknownValue) { last.Source = source; last.Confidence = confidence; }
            return this;
        }

        /// <summary>An explicitly unknown value with the reason (never a guessed value).</summary>
        public InventoryItem SetUnknown(string key, string reason)
        {
            Values.Add(new InventoryValue { Key = key, Value = UnknownValue, Reason = reason });
            return this;
        }

        /// <summary>Adds an identifying text (serial, asset tag, part number). Firmware placeholders ("To be filled by O.E.M.", "Default string") become UNKNOWN.</summary>
        public InventoryItem SetId(string key, string value)
        {
            string v = value?.Trim();
            bool unknown = string.IsNullOrEmpty(v) || Parsing.BrandCatalog.IsPlaceholder(v);
            Values.Add(new InventoryValue { Key = key, Value = unknown ? UnknownValue : v, Reason = unknown ? (string.IsNullOrEmpty(v) ? UnknownReason.NotReported : "FIRMWARE_PLACEHOLDER (\"" + v + "\" is not a real value)") : null });
            return this;
        }

        public string Get(string key) => Values.FirstOrDefault(x => x.Key == key)?.Value;

        /// <summary>Gives UNKNOWN values of these keys a more specific reason (no effect on known values or a null reason).</summary>
        public InventoryItem Reason(string reason, params string[] keys)
        {
            if (reason == null) return this;
            foreach (var v in Values.Where(x => x.Value == UnknownValue && keys.Contains(x.Key))) v.Reason = reason;
            return this;
        }
    }

    /// <summary>One changed part between two snapshots (stored as the details of a DEVICE_HARDWARE_CHANGED event).</summary>
    [DataContract]
    public sealed class HardwareChange
    {
        [DataMember] public string Component { get; set; }
        /// <summary>replaced, added, removed</summary>
        [DataMember] public string Change { get; set; }
        [DataMember] public string Old { get; set; }
        [DataMember] public string New { get; set; }
        /// <summary>Why (the repair note recorded between the two snapshots), or "detected automatically".</summary>
        [DataMember] public string Reason { get; set; }

        public string Describe() =>
            Change == "replaced" ? Component + " replaced: " + Old + " → " + New
          : Change == "removed" ? Component + " removed: " + Old : Component + " added: " + New;
    }

    /// <summary>
    /// The technical identity + hardware inventory + measured health of one laptop at one moment.
    /// Stored as a history record (never overwritten), sent to the BBTech server and to ITMS.
    /// Only technical, traceability-relevant data: no user files, passwords, keys or browsing data.
    /// </summary>
    [DataContract]
    public sealed class TechnicalInventory
    {
        [DataMember] public DateTime CapturedAt { get; set; }
        [DataMember] public string ToolVersion { get; set; }
        [DataMember] public string DeviceId { get; set; }
        /// <summary>Lifecycle moment of the snapshot: HARDWARE_HANDOVER, SOFTWARE_HANDOVER, QC_START, QC_DECISION, FINAL_CHECK, PERIODIC, CHANGE.</summary>
        [DataMember] public string Stage { get; set; }
        [DataMember] public List<InventoryItem> Items { get; set; } = new List<InventoryItem>();

        public InventoryItem Add(string category, string name, string source, string confidence = DataConfidence.Detected, string identityKey = null)
        {
            var item = new InventoryItem
            {
                Category = category, Name = string.IsNullOrWhiteSpace(name) ? InventoryItem.UnknownValue : name.Trim(), Source = source, Confidence = confidence,
                IdentityKey = string.IsNullOrWhiteSpace(identityKey) || Parsing.BrandCatalog.IsPlaceholder(identityKey) ? "" : identityKey.Trim(),
            };
            Items.Add(item);
            return item;
        }

        public IEnumerable<InventoryItem> Of(string category) => Items.Where(i => i.Category == category);

        /// <summary>Categories whose parts can be swapped; their identity keys make up the hardware signature.</summary>
        public static readonly string[] ReplaceableCategories = { "baseboard", "cpu", "ram_module", "storage", "battery", "gpu", "network", "display" };

        /// <summary>
        /// Stable signature of the installed parts (serials / identities only, no volatile measurements such as
        /// temperature or charge). Two snapshots with the same signature have the same hardware.
        /// </summary>
        public string HardwareSignature()
        {
            // The display is left out: whether the built-in panel is visible depends on the lid/dock, not on the
            // hardware. Panel replacements are still detected by StructuredChanges (known panel → different panel).
            var parts = Items.Where(i => ReplaceableCategories.Contains(i.Category) && i.Category != "display")
                             .Select(i => i.Category + "=" + (string.IsNullOrEmpty(i.IdentityKey) ? i.Name : i.IdentityKey))
                             .OrderBy(x => x, StringComparer.Ordinal);
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", parts))).Select(b => b.ToString("x2")));
        }

        /// <summary>What changed between two snapshots, in plain words ("Storage replaced: A (S/N 1) → B (S/N 2)").</summary>
        public static List<string> DescribeChanges(TechnicalInventory before, TechnicalInventory after) =>
            StructuredChanges(before, after).Select(c => c.Describe()).ToList();

        /// <summary>Each changed part: component, old part (or none), new part (or none).</summary>
        public static List<HardwareChange> StructuredChanges(TechnicalInventory before, TechnicalInventory after)
        {
            var changes = new List<HardwareChange>();
            if (before == null || after == null) return changes;
            foreach (var cat in ReplaceableCategories)
            {
                string Key(InventoryItem i) => string.IsNullOrEmpty(i.IdentityKey) ? i.Name : i.IdentityKey;
                string Show(InventoryItem i) => i.Name + (string.IsNullOrEmpty(i.IdentityKey) ? "" : " (" + i.IdentityKey + ")");
                var old = before.Of(cat).Where(i => cat != "display" || !string.IsNullOrEmpty(i.IdentityKey)).ToList();
                var now = after.Of(cat).Where(i => cat != "display" || !string.IsNullOrEmpty(i.IdentityKey)).ToList();
                // A built-in panel that is merely not visible (lid closed, docked) is not "removed" or "added":
                // only a known panel replaced by a different known panel is a change.
                if (cat == "display" && (old.Count == 0 || now.Count == 0)) continue;
                var removed = old.Where(o => !now.Any(n => Key(n) == Key(o))).ToList();
                var added = now.Where(n => !old.Any(o => Key(o) == Key(n))).ToList();
                string label = cat == "ram_module" ? "RAM module" : cat == "gpu" ? "GPU" : cat == "cpu" ? "CPU" : char.ToUpperInvariant(cat[0]) + cat.Substring(1);
                if (removed.Count == 1 && added.Count == 1) changes.Add(new HardwareChange { Component = label, Change = "replaced", Old = Show(removed[0]), New = Show(added[0]) });
                else
                {
                    changes.AddRange(removed.Select(r => new HardwareChange { Component = label, Change = "removed", Old = Show(r), New = "NOT_PRESENT" }));
                    changes.AddRange(added.Select(a => new HardwareChange { Component = label, Change = "added", Old = "NOT_PRESENT", New = Show(a) }));
                }
            }
            return changes;
        }

        public string ToJson() => Encoding.UTF8.GetString(Json.ToBytes(this));

        public static TechnicalInventory FromJson(string json)
        {
            try { return string.IsNullOrWhiteSpace(json) ? null : Json.FromBytes<TechnicalInventory>(Encoding.UTF8.GetBytes(json)); }
            catch { return null; }
        }

        /// <summary>Machine-readable form for ITMS: category → list of { name, identity, source, confidence, values{...} }.</summary>
        public JsonObject ToItmsObject()
        {
            var root = new JsonObject();
            root.Add("captured_at", CapturedAt.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            root.Add("tool_version", ToolVersion ?? "");
            root.Add("hardware_signature", HardwareSignature());
            foreach (var group in Items.GroupBy(i => i.Category))
            {
                root.Add(group.Key, group.Select(i =>
                {
                    var o = new JsonObject();
                    o.Add("name", i.Name);
                    o.Add("identity", string.IsNullOrEmpty(i.IdentityKey) ? InventoryItem.UnknownValue : i.IdentityKey);
                    o.Add("source", i.Source ?? "");
                    o.Add("confidence", i.Confidence ?? DataConfidence.Unknown);
                    var values = new JsonObject();
                    foreach (var v in i.Values) values.Add(v.Key, v.Value);
                    o.Add("values", values);
                    return (object)o;
                }).ToList());
            }
            return root;
        }
    }
}
