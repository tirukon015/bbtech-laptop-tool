using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using Microsoft.Win32;

namespace BBTech.LaptopTool.Detect
{
    /// <summary>
    /// Builds the laptop's complete technical record: identity, hardware inventory, measured health,
    /// firmware/security state, Windows and Office state, relevant drivers. It reuses the existing detection
    /// (HardwareDetector, SSD health engine, battery providers) and adds what those do not read.
    ///
    /// Rules: every item names its source; a value the laptop does not expose is stored as UNKNOWN; nothing is
    /// estimated or defaulted to "good". Read-only: no benchmarks, no writes, no personal data.
    /// </summary>
    public static class InventoryCollector
    {
        private const string Wm = "WMI";

        /// <summary>Microsoft 365 / Office subscription licensing (vNext), which Windows Software Protection does not report.</summary>
        private static class OfficeLicensing
        {
            public static bool IsSubscription(string productIds) =>
                !string.IsNullOrEmpty(productIds) && (productIds.IndexOf("O365", StringComparison.OrdinalIgnoreCase) >= 0 || productIds.IndexOf("M365", StringComparison.OrdinalIgnoreCase) >= 0);

            public static bool SubscriptionTokenPresent()
            {
                try
                {
                    string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Office\Licenses");
                    return System.IO.Directory.Exists(dir) && System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories).Any();
                }
                catch { return false; }
            }
        }

        public static TechnicalInventory Collect(DetectionResult d, DeviceRecord device)
        {
            var inv = new TechnicalInventory { CapturedAt = DateTime.UtcNow, ToolVersion = AppPaths.Version, DeviceId = device?.DeviceId };
            Safe("identity", () => Identity(inv, d, device));
            Safe("firmware", () => Firmware(inv));
            Safe("cpu", () => Cpu(inv));
            Safe("memory", () => Memory(inv));
            Safe("storage", () => Storage(inv, d));
            Safe("battery", () => Battery(inv, d));
            Safe("gpu", () => Gpu(inv));
            Safe("display", () => Display(inv, d));
            Safe("network", () => Network(inv));
            Safe("network_virtual", () => VirtualAdapters(inv));
            Safe("usb", () => Usb(inv));
            Safe("devices", () => Devices(inv));
            Safe("tpm", () => Tpm(inv));
            Safe("windows", () => WindowsAndOffice(inv));
            Safe("drivers", () => Drivers(inv));
            return inv;
        }

        private static void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { AppLog.Warn("Inventory section failed: " + what, ex); }
        }

        private static string Date(string wmiDate)
        {
            try { return string.IsNullOrEmpty(wmiDate) ? null : ManagementDateTimeConverter.ToDateTime(wmiDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static string YesNo(object b) => b == null ? null : Convert.ToBoolean(b) ? "true" : "false";

        // ------------------------------------------------------------------ identity
        private static void Identity(TechnicalInventory inv, DetectionResult d, DeviceRecord device)
        {
            var sys = d?.System;
            inv.Add("identity", "Laptop", "SMBIOS (WMI)", DataConfidence.Detected, device?.HardwareFingerprint)
               .Set("bbtech_device_id", device?.DeviceId)
               .Set("hardware_fingerprint", device?.HardwareFingerprint)
               .SetId("manufacturer", sys?.RawManufacturer).SetId("model", sys?.RawModel).SetId("product_name", sys?.ProductCode)
               .SetId("system_family", sys?.SystemFamily).SetId("system_sku", sys?.SystemSku)
               .SetId("serial_number", sys?.SerialNumber).SetId("system_uuid", sys?.SystemUuid).SetId("baseboard_serial", sys?.BoardSerial);

            foreach (var b in Wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber, Version FROM Win32_BIOS"))
                inv.Add("bios", Wmi.Str(b, "SMBIOSBIOSVersion"), "SMBIOS (WMI Win32_BIOS)")
                   .SetId("vendor", Wmi.Str(b, "Manufacturer")).SetId("version", Wmi.Str(b, "SMBIOSBIOSVersion"))
                   .Set("release_date", Date(Wmi.Str(b, "ReleaseDate"))).SetId("bios_serial", Wmi.Str(b, "SerialNumber"));

            foreach (var c in Wmi.Query("SELECT SerialNumber, SMBIOSAssetTag, ChassisTypes, Manufacturer FROM Win32_SystemEnclosure"))
            {
                string type = null;
                if (Wmi.Raw(c, "ChassisTypes") is ushort[] t && t.Length > 0) type = t[0].ToString(CultureInfo.InvariantCulture);
                inv.Add("chassis", "Chassis", "SMBIOS (WMI Win32_SystemEnclosure)")
                   .Set("chassis_type_code", type).Set("chassis_type", sys?.ChassisType)
                   .SetId("chassis_serial", Wmi.Str(c, "SerialNumber")).SetId("asset_tag", Wmi.Str(c, "SMBIOSAssetTag"));
            }

            foreach (var b in Wmi.Query("SELECT Manufacturer, Product, SerialNumber, Version FROM Win32_BaseBoard"))
                inv.Add("baseboard", (Wmi.Str(b, "Manufacturer") + " " + Wmi.Str(b, "Product")).Trim(), "SMBIOS (WMI Win32_BaseBoard)", DataConfidence.Detected, Wmi.Str(b, "SerialNumber"))
                   .SetId("manufacturer", Wmi.Str(b, "Manufacturer")).SetId("product", Wmi.Str(b, "Product"))
                   .SetId("serial_number", Wmi.Str(b, "SerialNumber")).SetId("version", Wmi.Str(b, "Version"));
        }

        // ------------------------------------------------------------------ firmware / security state
        private static void Firmware(TechnicalInventory inv)
        {
            string mode = null, secureBoot = null;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control"))
            {
                var v = k?.GetValue("PEFirmwareType");
                if (v != null) mode = Convert.ToInt32(v) == 2 ? "UEFI" : Convert.ToInt32(v) == 1 ? "Legacy BIOS" : null;
            }
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
            {
                var v = k?.GetValue("UEFISecureBootEnabled");
                if (v != null) secureBoot = Convert.ToInt32(v) == 1 ? "true" : "false";
            }
            if (mode == null)
            {
                try { if (GetFirmwareType(out uint type)) mode = type == 2 ? "UEFI" : type == 1 ? "Legacy BIOS" : null; } catch { }
            }
            inv.Add("firmware", "Boot configuration", "Windows API + Registry").Set("boot_mode", mode).Set("secure_boot_enabled", secureBoot);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GetFirmwareType(out uint firmwareType);

        // ------------------------------------------------------------------ CPU
        private static void Cpu(TechnicalInventory inv)
        {
            // While a hypervisor runs (Hyper-V, WSL2, Docker) Windows hides the CPU's virtualization flags. A running
            // hypervisor itself proves virtualization is supported and enabled.
            bool hypervisor = false;
            foreach (var c in Wmi.Query("SELECT HypervisorPresent FROM Win32_ComputerSystem")) hypervisor = Convert.ToBoolean(Wmi.Raw(c, "HypervisorPresent") ?? false);
            foreach (var p in Wmi.Query("SELECT Name, Manufacturer, Architecture, AddressWidth, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, " +
                                        "MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled, VMMonitorModeExtensions, " +
                                        "SecondLevelAddressTranslationExtensions, ProcessorId, Family, Caption FROM Win32_Processor"))
            {
                int arch = Wmi.Int(p, "Architecture");
                var fms = System.Text.RegularExpressions.Regex.Match(Wmi.Str(p, "Caption"), @"Family (\d+) Model (\d+) Stepping (\d+)");
                var nameClock = System.Text.RegularExpressions.Regex.Match(Wmi.Str(p, "Name"), @"@\s*([\d.]+)\s*GHz", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string archName = arch == 9 ? "x64" : arch == 0 ? "x86" : arch == 12 ? "ARM64" : arch == 5 ? "ARM" : arch.ToString(CultureInfo.InvariantCulture);
                inv.Add("cpu", Wmi.Str(p, "Name"), "WMI Win32_Processor", DataConfidence.Detected, Wmi.Str(p, "Name") + "|" + Wmi.Str(p, "ProcessorId"))
                   .SetId("manufacturer", Wmi.Str(p, "Manufacturer")).SetId("model", Wmi.Str(p, "Name"))
                   .Set("family", fms.Success ? fms.Groups[1].Value : null).Set("model_number", fms.Success ? fms.Groups[2].Value : null).Set("stepping", fms.Success ? fms.Groups[3].Value : null)
                   .Set("architecture", archName).Set("address_width_bits", Wmi.Raw(p, "AddressWidth")).SetId("socket", Wmi.Str(p, "SocketDesignation"))
                   .Set("physical_cores", Wmi.Raw(p, "NumberOfCores")).Set("logical_processors", Wmi.Raw(p, "NumberOfLogicalProcessors"))
                   // WMI "MaxClockSpeed" is the rated (base) frequency, not the turbo maximum: labelled as what it is.
                   .Set("base_clock_mhz", Wmi.Raw(p, "MaxClockSpeed"))
                   .SetFrom("base_clock_mhz_from_name", nameClock.Success ? (object)(int)Math.Round(double.Parse(nameClock.Groups[1].Value, CultureInfo.InvariantCulture) * 1000) : null, "processor name string", DataConfidence.Detected)
                   .SetUnknown("max_turbo_clock_mhz", UnknownReason.NotExposed)
                   .SetFrom("current_clock_mhz", Wmi.Raw(p, "CurrentClockSpeed"), "WMI Win32_Processor", DataConfidence.Measured)
                   .Set("l2_cache_kb", Wmi.Raw(p, "L2CacheSize")).Set("l3_cache_kb", Wmi.Raw(p, "L3CacheSize"))
                   .Set("hypervisor_running", hypervisor ? "true" : "false")
                   .Set("virtualization_supported", hypervisor ? "true" : YesNo(Wmi.Raw(p, "VMMonitorModeExtensions")))
                   .Set("virtualization_enabled_in_firmware", hypervisor ? "true" : YesNo(Wmi.Raw(p, "VirtualizationFirmwareEnabled")))
                   .Set("slat_supported", hypervisor ? "true" : YesNo(Wmi.Raw(p, "SecondLevelAddressTranslationExtensions")))
                   .Set("cpuid_signature", Wmi.Str(p, "ProcessorId"));
            }
        }

        // ------------------------------------------------------------------ RAM (per physical module)
        private static string MemoryType(int smbiosType)
        {
            switch (smbiosType) { case 20: return "DDR"; case 21: return "DDR2"; case 24: return "DDR3"; case 26: return "DDR4"; case 34: return "DDR5"; case 27: return "LPDDR"; case 28: return "LPDDR2"; case 29: return "LPDDR3"; case 30: return "LPDDR4"; case 35: return "LPDDR5"; default: return null; }
        }

        private static string FormFactor(int ff) => ff == 12 ? "SODIMM" : ff == 8 ? "DIMM" : ff == 0 ? null : ff.ToString(CultureInfo.InvariantCulture);

        private static void Memory(TechnicalInventory inv)
        {
            var modules = Wmi.Query("SELECT Manufacturer, PartNumber, SerialNumber, Capacity, SMBIOSMemoryType, Speed, ConfiguredClockSpeed, ConfiguredVoltage, " +
                                    "FormFactor, DeviceLocator, BankLabel FROM Win32_PhysicalMemory");
            long total = 0;
            foreach (var m in modules)
            {
                long cap = Wmi.Long(m, "Capacity"); total += cap;
                string serial = Wmi.Str(m, "SerialNumber");
                string name = (Wmi.Str(m, "Manufacturer") + " " + Wmi.Str(m, "PartNumber")).Trim();
                int volt = Wmi.Int(m, "ConfiguredVoltage");
                inv.Add("ram_module", name.Length == 0 ? "Memory module" : name, "SMBIOS (WMI Win32_PhysicalMemory)", DataConfidence.Detected,
                        string.IsNullOrWhiteSpace(serial) || serial.Trim('0').Length == 0 ? Wmi.Str(m, "DeviceLocator") + "|" + Wmi.Str(m, "PartNumber") + "|" + cap : serial)
                   .SetId("slot", Wmi.Str(m, "DeviceLocator")).SetId("bank", Wmi.Str(m, "BankLabel"))
                   .SetId("manufacturer", Wmi.Str(m, "Manufacturer")).SetId("part_number", Wmi.Str(m, "PartNumber")).SetId("serial_number", serial)
                   .Set("capacity_bytes", cap > 0 ? (object)cap : null).Set("capacity_gb", cap > 0 ? (object)(cap / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) : null)
                   .Set("memory_type", MemoryType(Wmi.Int(m, "SMBIOSMemoryType")))
                   .Set("rated_speed_mts", Wmi.Int(m, "Speed") > 0 ? (object)Wmi.Int(m, "Speed") : null)
                   .Set("configured_speed_mts", Wmi.Int(m, "ConfiguredClockSpeed") > 0 ? (object)Wmi.Int(m, "ConfiguredClockSpeed") : null)
                   .Set("configured_voltage_mv", volt > 0 ? (object)volt : null)
                   .Set("form_factor", FormFactor(Wmi.Int(m, "FormFactor")));
            }

            int slots = 0;
            foreach (var a in Wmi.Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray")) slots += Wmi.Int(a, "MemoryDevices");
            long usable = 0;
            foreach (var c in Wmi.Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem")) usable = Wmi.Long(c, "TotalPhysicalMemory");
            inv.Add("memory", "Memory summary", "SMBIOS + Windows (WMI)")
               .Set("installed_bytes", total > 0 ? (object)total : null).Set("usable_bytes", usable > 0 ? (object)usable : null)
               .Set("populated_slots", modules.Count > 0 ? (object)modules.Count : null).Set("total_slots", slots > 0 ? (object)slots : null)
               .Set("empty_slots", slots > 0 && modules.Count > 0 ? (object)Math.Max(0, slots - modules.Count) : null)
               .Set("configuration", modules.Count == 0 ? null : modules.Count + " x module" + (modules.Count > 1 ? " (multi-channel possible)" : " (single module)"));
        }

        // ------------------------------------------------------------------ storage (identity + REAL health)
        private static void Storage(TechnicalInventory inv, DetectionResult d)
        {
            var byIndex = new Dictionary<string, ManagementBaseObject>(StringComparer.OrdinalIgnoreCase);
            var bySerial = new Dictionary<string, ManagementBaseObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in Wmi.Query("SELECT SerialNumber, FirmwareRevision, InterfaceType, Index, Model, PNPDeviceID FROM Win32_DiskDrive"))
            {
                byIndex[Wmi.Int(w, "Index").ToString(CultureInfo.InvariantCulture)] = w;
                string sn = Wmi.Str(w, "SerialNumber");
                if (sn.Length > 0) bySerial[sn] = w;
            }
            var makers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pd in Wmi.Query("SELECT DeviceId, Manufacturer FROM MSFT_PhysicalDisk", @"root\microsoft\windows\storage"))
                makers[Wmi.Str(pd, "DeviceId")] = Wmi.Str(pd, "Manufacturer");

            foreach (var s in (d?.StorageDevices ?? new List<StorageDevice>()).Where(x => !x.IsExternal))
            {
                // Raw health straight from the drive when not already read during detection.
                bool hasRaw = s.RawHealth != null && s.RawHealth.Keys.Any(k => k != "raw_health_status" && k != "raw_health_reason");
                if (!hasRaw)
                {
                    try
                    {
                        var probe = new StorageDevice { Model = s.Model, DeviceId = s.DeviceId, SerialNumber = s.SerialNumber, BusType = s.BusType, MediaType = s.MediaType, CapacityBytes = s.CapacityBytes };
                        NativeStorageDiagnostics.QueryAll(new[] { probe });
                        if (probe.RawHealth != null && probe.RawHealth.Count > 0) s.RawHealth = probe.RawHealth;
                    }
                    catch (Exception ex) { AppLog.Warn("Raw drive health not readable: " + ex.Message); }
                }
                var raw = s.RawHealth ?? new Dictionary<string, string>();
                hasRaw = raw.Keys.Any(k => k != "raw_health_status" && k != "raw_health_reason");

                string sn = (s.SerialNumber ?? "").Trim();
                ManagementBaseObject w = null;
                if (!bySerial.TryGetValue(sn, out w)) byIndex.TryGetValue(s.DeviceId ?? "", out w);
                makers.TryGetValue(s.DeviceId ?? "", out var maker);
                int diskNum = NativeStorageDiagnostics.ExtractDiskNumber(s.DeviceId);
                bool isNvme = string.Equals(s.BusType, "NVMe", StringComparison.OrdinalIgnoreCase);
                string rawSource = !hasRaw ? null : raw.ContainsKey("critical_warning") ? "NVMe SMART / Health Information log (Windows IOCTL)" : "ATA SMART attributes (Windows IOCTL)";

                // 1) the physical drive (identity)
                var item = inv.Add("storage", s.Model, "WMI Win32_DiskDrive + MSFT_PhysicalDisk", DataConfidence.Detected, sn)
                    .SetId("model", s.Model).SetId("serial_number", sn)
                    .SetId("firmware_version", !string.IsNullOrEmpty(s.FirmwareVersion) ? s.FirmwareVersion : w == null ? null : Wmi.Str(w, "FirmwareRevision"))
                    .Set("interface", w == null ? null : Wmi.Str(w, "InterfaceType")).Set("bus_type", s.BusType).Set("media_type", s.MediaType)
                    .Set("capacity_bytes", s.CapacityBytes > 0 ? (object)s.CapacityBytes : null).Set("capacity_label", s.CapacityLabel)
                    .Set("disk_number", diskNum >= 0 ? (object)diskNum : null)
                    .Set("device_path", diskNum >= 0 ? @"\\.\PhysicalDrive" + diskNum : null)
                    .Set("pnp_device_id", w == null ? null : Wmi.Str(w, "PNPDeviceID"))
                    .Set("partitions", s.PartitionsSummary);
                if (!string.IsNullOrWhiteSpace(maker) && !Core.Parsing.BrandCatalog.IsPlaceholder(maker)) item.SetFrom("manufacturer", maker, "MSFT_PhysicalDisk", DataConfidence.Detected);
                else item.SetUnknown("manufacturer", UnknownReason.NotExposed);

                // 2) raw metrics exactly as the drive reported them (NVMe and SATA keep their own fields)
                item.Set("raw_metrics_available", hasRaw ? "true" : "false");
                if (hasRaw)
                {
                    item.Set("raw_metrics_source", rawSource);
                    foreach (var kv in raw.Where(k => k.Key != "raw_health_status" && k.Key != "raw_health_reason").OrderBy(k => k.Key, StringComparer.Ordinal))
                        item.SetFrom("raw." + kv.Key, kv.Value, rawSource, DataConfidence.Measured);
                }
                else
                {
                    raw.TryGetValue("raw_health_reason", out var why);
                    item.SetUnknown("raw_metrics", string.IsNullOrEmpty(why) ? (isNvme ? UnknownReason.ReadFailed : UnknownReason.NotExposed) : why);
                }

                // 3) values another measuring engine reported (Windows reliability counters / HD Sentinel), labelled as such
                string engine = string.IsNullOrEmpty(s.HealthSource) ? "Windows storage reliability counters" : s.HealthSource;
                if (s.WearPercentUsed.HasValue) item.SetFrom("percentage_used", s.WearPercentUsed, engine, DataConfidence.Measured);
                if (s.PowerOnHours.HasValue) item.SetFrom("power_on_hours", s.PowerOnHours, engine, DataConfidence.Measured);
                if (s.TemperatureC.HasValue) item.SetFrom("temperature_c", s.TemperatureC, engine, DataConfidence.Measured);
                if (s.HealthPercent.HasValue) item.SetFrom("engine_health_percent", s.HealthPercent, engine + " (vendor health formula)", DataConfidence.Calculated);
                item.Set("windows_health_status", s.WindowsHealth);

                // 4) derived values, clearly marked (never replacing the original metric)
                long? used = null;
                if (raw.TryGetValue("percentage_used", out var pu) && long.TryParse(pu, out var pul)) used = pul;
                else if (s.WearPercentUsed.HasValue) used = s.WearPercentUsed;
                if (used.HasValue) item.SetCalculated("derived_remaining_life_percent", Math.Max(0, 100 - used.Value), "100 - percentage_used", "percent");

                // 5) the health word, from the documented central rule only
                var verdict = StorageHealthRules.Evaluate(raw, s.WearPercentUsed, s.TemperatureC);
                item.SetCalculated("health_status", verdict.Status, "StorageHealthRules: " + string.Join("; ", verdict.Reasons));
                if (verdict.Status == "UNKNOWN") item.Values[item.Values.Count - 1].Reason = hasRaw ? null : UnknownReason.NotExposed;
            }
        }

        // ------------------------------------------------------------------ battery (real capacities)
        private static void Battery(TechnicalInventory inv, DetectionResult d)
        {
            var b = d?.Battery;
            if (b == null) { inv.Add("battery", "Battery", "Battery providers", DataConfidence.Unknown).SetUnknown("present", UnknownReason.ReadFailed); return; }
            if (!b.Present) { inv.Add("battery", "Battery", "Battery providers", DataConfidence.Detected).Set("present", "false").SetUnknown("details", UnknownReason.NotPresent); return; }
            string serial = null, maker = null;
            foreach (var s in Wmi.Query("SELECT SerialNumber, ManufactureName, DeviceName FROM BatteryStaticData", @"root\wmi"))
            { serial = Wmi.Str(s, "SerialNumber"); maker = Wmi.Str(s, "ManufactureName"); break; }
            double? health = b.DesignCapacityMWh > 0 && b.FullChargeCapacityMWh > 0 ? Math.Round(b.FullChargeCapacityMWh.Value * 100.0 / b.DesignCapacityMWh.Value, 2) : (double?)null;
            inv.Add("battery", string.IsNullOrWhiteSpace(b.Name) ? "Battery" : b.Name, string.IsNullOrEmpty(b.Source) ? "WMI battery classes" : b.Source, DataConfidence.Measured, serial)
               .SetId("manufacturer", string.IsNullOrWhiteSpace(b.Manufacturer) ? maker : b.Manufacturer).SetId("model", b.Name).SetId("serial_number", serial)
               .Set("chemistry", b.Chemistry)
               .Set("design_capacity_mwh", b.DesignCapacityMWh).Set("full_charge_capacity_mwh", b.FullChargeCapacityMWh).Set("current_capacity_mwh", b.CurrentCapacityMWh)
               .Set("charge_percent", b.ChargePercent).Set("cycle_count", b.CycleCount)
               // Health is calculated from the two measured capacities, never typed in by a person.
               .SetCalculated("health_percent", health, "full_charge_capacity_mwh / design_capacity_mwh x 100", "percent")
               .Set("ac_adapter_connected", YesNo(b.AcAdapterConnected)).Set("charging_status", b.ChargingStatus);
        }

        // ------------------------------------------------------------------ GPU / display
        private static void Gpu(TechnicalInventory inv)
        {
            foreach (var g in Wmi.Query("SELECT Name, AdapterCompatibility, AdapterRAM, DriverVersion, DriverDate, Status, PNPDeviceID, VideoProcessor, " +
                                        "CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
            {
                string name = Wmi.Str(g, "Name"), maker = Wmi.Str(g, "AdapterCompatibility");
                bool integrated = name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("UHD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  name.IndexOf("Iris", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  name.IndexOf("Vega", StringComparison.OrdinalIgnoreCase) >= 0;
                long ram = Wmi.Long(g, "AdapterRAM");
                bool external = Wmi.Str(g, "PNPDeviceID").StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
                inv.Add(external ? "external_display_adapter" : "gpu", name, "WMI Win32_VideoController", DataConfidence.Detected, external ? null : Wmi.Str(g, "PNPDeviceID"))
                   .SetId("manufacturer", maker).SetId("model", name).SetId("video_processor", Wmi.Str(g, "VideoProcessor"))
                   .Set("type", name.Length == 0 ? null : external ? "external (USB dock / adapter)" : integrated ? "integrated" : "discrete")
                   .Set("adapter_ram_bytes_reported", ram > 0 ? (object)ram : null)   // WMI caps this at 4 GB; reported as-is
                   .Set("driver_version", Wmi.Str(g, "DriverVersion")).Set("driver_date", Date(Wmi.Str(g, "DriverDate")))
                   .Set("status", Wmi.Str(g, "Status")).Set("pnp_device_id", Wmi.Str(g, "PNPDeviceID"));
            }
        }

        private static string Ascii(object arr)
        {
            if (!(arr is ushort[] a)) return null;
            string s = new string(a.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
            return s.Length == 0 ? null : s;
        }

        private static void Display(TechnicalInventory inv, DetectionResult d)
        {
            var ids = Wmi.Query("SELECT InstanceName, ManufacturerName, ProductCodeID, SerialNumberID, UserFriendlyName, YearOfManufacture, Active FROM WmiMonitorID", @"root\wmi");
            var disp = d?.Display;
            if (ids.Count == 0)
                inv.Add("display", string.IsNullOrEmpty(disp?.PanelModel) ? "Display" : disp.PanelModel, "Windows API")
                   .Set("resolution", disp?.Resolution).Set("refresh_rate_hz", disp?.RefreshRateHz).Set("display_count", disp?.DisplayCount).Set("edid_available", "false");
            var conn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Wmi.Query("SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams", @"root\wmi"))
            {
                long tech = Wmi.Long(c, "VideoOutputTechnology");
                conn[Wmi.Str(c, "InstanceName")] = tech == 0x80000000L || tech == 11 || tech == 13 ? "internal (laptop panel)" : tech == 5 ? "HDMI" : tech == 10 ? "DisplayPort" : tech == 0 ? "VGA" : tech == 4 ? "DVI" : "other (" + tech + ")";
            }
            bool internalSeen = false;
            foreach (var m in ids)
            {
                string maker = Ascii(Wmi.Raw(m, "ManufacturerName")), code = Ascii(Wmi.Raw(m, "ProductCodeID")), serial = Ascii(Wmi.Raw(m, "SerialNumberID"));
                conn.TryGetValue(Wmi.Str(m, "InstanceName"), out var connType);
                bool external = connType != null && !connType.StartsWith("internal", StringComparison.Ordinal);   // a monitor on a dock is not a laptop part
                internalSeen |= !external;
                inv.Add(external ? "display_external" : "display", (maker + " " + code).Trim(), "EDID (WMI WmiMonitorID)", DataConfidence.Detected, external ? null : maker + code + "|" + serial)
                   .SetId("manufacturer_id", maker).SetId("panel_id", code).SetId("serial_number", serial == "0" ? null : serial)
                   .SetId("friendly_name", Ascii(Wmi.Raw(m, "UserFriendlyName"))).Set("year_of_manufacture", Wmi.Raw(m, "YearOfManufacture"))
                   .Set("active", YesNo(Wmi.Raw(m, "Active")))
                   .Set("connection_type", conn.TryGetValue(Wmi.Str(m, "InstanceName"), out var ct) ? ct : null)
                   .Set("resolution", disp?.Resolution).Set("refresh_rate_hz", disp?.RefreshRateHz)
                   .Set("physical_size_inches", disp?.PhysicalSizeInches).Set("display_count", disp?.DisplayCount).Set("edid_available", "true");
            }
            if (ids.Count > 0 && !internalSeen)
                // Windows only reports active displays: with the lid closed / panel off (docked), the built-in panel is
                // invisible. Say so instead of claiming it is missing.
                inv.Add("display", "Built-in display", "EDID (WMI WmiMonitorID)", DataConfidence.Unknown)
                   .SetUnknown("panel_id", "INTERNAL_PANEL_NOT_ACTIVE (lid closed or panel off while external displays are in use)");
        }

        // ------------------------------------------------------------------ network adapters (with driver)
        private static Dictionary<string, ManagementBaseObject> SignedDrivers(string classes)
        {
            var map = new Dictionary<string, ManagementBaseObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var dr in Wmi.Query("SELECT DeviceID, DeviceName, DeviceClass, DriverVersion, DriverDate, DriverProviderName, Manufacturer FROM Win32_PnPSignedDriver WHERE " + classes, timeoutSeconds: 15))
            {
                string id = Wmi.Str(dr, "DeviceID");
                if (id.Length > 0) map[id] = dr;
            }
            return map;
        }

        private static void Network(TechnicalInventory inv)
        {
            var drivers = SignedDrivers("DeviceClass = 'NET'");
            var ip = new Dictionary<int, string[]>();
            foreach (var c in Wmi.Query("SELECT Index, IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
                if (Wmi.Raw(c, "IPAddress") is string[] addrs) ip[Wmi.Int(c, "Index")] = addrs;

            foreach (var a in Wmi.Query("SELECT Name, Manufacturer, MACAddress, AdapterType, Speed, NetConnectionStatus, PNPDeviceID, NetConnectionID, Index FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
            {
                string name = Wmi.Str(a, "Name"), pnp = Wmi.Str(a, "PNPDeviceID"), mac = Wmi.Str(a, "MACAddress");
                if (pnp.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)) continue; // virtual: listed below
                string lower = name.ToLowerInvariant();
                string kind = lower.Contains("bluetooth") ? "bluetooth" : lower.Contains("wi-fi") || lower.Contains("wireless") || lower.Contains("wlan") || lower.Contains("802.11") ? "wifi" : "ethernet";
                drivers.TryGetValue(pnp, out var dr);
                int status = Wmi.Int(a, "NetConnectionStatus");
                long speed = Wmi.Long(a, "Speed");
                ip.TryGetValue(Wmi.Int(a, "Index"), out var addrs);
                bool usb = pnp.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);   // dock / dongle: not part of the laptop
                inv.Add(usb ? "network_external" : "network", name, "WMI Win32_NetworkAdapter + Win32_PnPSignedDriver", DataConfidence.Detected, mac)
                   .Set("classification", usb ? "EXTERNAL" : "PHYSICAL")
                   .Set("interface_type", kind).SetId("manufacturer", Wmi.Str(a, "Manufacturer")).SetId("model", name).SetId("mac_address", mac)
                   .Set("connection_name", Wmi.Str(a, "NetConnectionID"))
                   .Set("operational_status", status == 2 ? "connected" : status == 7 ? "media disconnected" : status == 0 ? "disconnected" : status.ToString(CultureInfo.InvariantCulture))
                   .Set("link_speed_bps", status == 2 && speed > 0 && speed < long.MaxValue ? (object)speed : null)
                   .Set("ipv4", addrs?.FirstOrDefault(x => x.Contains(".")) ).Set("ipv6", addrs?.FirstOrDefault(x => x.Contains(":")))
                   .Reason(status == 2 ? null : UnknownReason.NotConnected, "link_speed_bps", "ipv4", "ipv6")
                   .Set("driver_version", dr == null ? null : Wmi.Str(dr, "DriverVersion")).Set("driver_date", dr == null ? null : Date(Wmi.Str(dr, "DriverDate")))
                   .Set("driver_provider", dr == null ? null : Wmi.Str(dr, "DriverProviderName")).Set("pnp_device_id", pnp);
            }
        }

        private static void VirtualAdapters(TechnicalInventory inv)
        {
            var names = Wmi.Query("SELECT Name, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = FALSE AND NetEnabled = TRUE")
                           .Select(a => Wmi.Str(a, "Name")).Where(n => n.Length > 0).Distinct().ToList();
            inv.Add("network_virtual", "Virtual network adapters (software, not laptop hardware)", "WMI Win32_NetworkAdapter")
               .Set("classification", "VIRTUAL").Set("count", names.Count).Set("names", names.Count == 0 ? "none" : string.Join("; ", names));
        }

        private static void Usb(TechnicalInventory inv)
        {
            // A controller existing is NOT proof that a physical port works: port tests are separate records.
            foreach (var c in Wmi.Query("SELECT Name, Manufacturer, PNPDeviceID, Status FROM Win32_USBController"))
                inv.Add("usb_controller", Wmi.Str(c, "Name"), "WMI Win32_USBController")
                   .SetId("manufacturer", Wmi.Str(c, "Manufacturer")).Set("status", Wmi.Str(c, "Status")).Set("pnp_device_id", Wmi.Str(c, "PNPDeviceID"))
                   .Set("meaning", "USB controller detected (physical ports are proven only by the USB port test)");
        }

        // ------------------------------------------------------------------ audio / camera / bluetooth
        private static void Devices(TechnicalInventory inv)
        {
            var drivers = SignedDrivers("DeviceClass = 'MEDIA' OR DeviceClass = 'CAMERA' OR DeviceClass = 'IMAGE' OR DeviceClass = 'BLUETOOTH' OR DeviceClass = 'AUDIOENDPOINT'");
            foreach (var e in Wmi.Query("SELECT Name, Manufacturer, PNPClass, PNPDeviceID, Status, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE " +
                                        "PNPClass = 'MEDIA' OR PNPClass = 'Camera' OR PNPClass = 'Image' OR PNPClass = 'Bluetooth' OR PNPClass = 'AudioEndpoint'", timeoutSeconds: 15))
            {
                string cls = Wmi.Str(e, "PNPClass"), pnp = Wmi.Str(e, "PNPDeviceID"), name = Wmi.Str(e, "Name");
                string category = cls.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) ? "audio"
                                : cls.Equals("AudioEndpoint", StringComparison.OrdinalIgnoreCase) ? "audio_endpoint"
                                : cls.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase) ? "bluetooth" : "camera";
                if (category == "bluetooth" && pnp.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue; // paired devices, not the adapter
                drivers.TryGetValue(pnp, out var dr);
                int err = Wmi.Int(e, "ConfigManagerErrorCode");
                string direction = category != "audio_endpoint" ? null : pnp.IndexOf("{0.0.0.", StringComparison.OrdinalIgnoreCase) >= 0 ? "playback"
                                 : pnp.IndexOf("{0.0.1.", StringComparison.OrdinalIgnoreCase) >= 0 ? "recording" : null;
                inv.Add(category, name, "WMI Win32_PnPEntity + Win32_PnPSignedDriver")
                   .Set("direction", category == "audio_endpoint" ? direction : "n/a")
                   .SetId("manufacturer", Wmi.Str(e, "Manufacturer")).SetId("model", name)
                   .Set("device_status", err == 0 ? "working" : BBTech.Core.Testing.DriverHealth.Describe(err)).Set("status", Wmi.Str(e, "Status"))
                   .Set("driver_version", dr == null ? null : Wmi.Str(dr, "DriverVersion")).Set("driver_date", dr == null ? null : Date(Wmi.Str(dr, "DriverDate")))
                   .Set("driver_provider", dr == null ? null : Wmi.Str(dr, "DriverProviderName")).Set("hardware_id", pnp);
            }
        }

        // ------------------------------------------------------------------ TPM (metadata only, never secrets)
        private static void Tpm(TechnicalInventory inv)
        {
            var list = Wmi.Query("SELECT SpecVersion, ManufacturerIdTxt, ManufacturerVersion, IsEnabled_InitialValue, IsActivated_InitialValue FROM Win32_Tpm", @"root\cimv2\Security\MicrosoftTpm");
            if (list.Count == 0)
            {
                // Not readable without administrator rights, or no TPM: say which is unknown, do not guess.
                bool? present = null;
                foreach (var e in Wmi.Query("SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'SecurityDevices'")) if (Wmi.Str(e, "Name").IndexOf("Trusted Platform", StringComparison.OrdinalIgnoreCase) >= 0) present = true;
                var tpm = inv.Add("tpm", "TPM", present == true ? "WMI Win32_PnPEntity" : "WMI", present == true ? DataConfidence.Detected : DataConfidence.Unknown);
                if (present == true) tpm.Set("present", "true");
                else tpm.SetUnknown("present", ElevationHelper.IsAdministrator ? UnknownReason.NotPresent : UnknownReason.AdminRequired);
                foreach (var k in new[] { "spec_version", "manufacturer", "firmware_version", "enabled", "activated" })
                    tpm.SetUnknown(k, ElevationHelper.IsAdministrator ? UnknownReason.ReadFailed : UnknownReason.AdminRequired);
                return;
            }
            foreach (var t in list)
                inv.Add("tpm", "TPM " + Wmi.Str(t, "SpecVersion").Split(',').FirstOrDefault(), "WMI Win32_Tpm")
                   .Set("present", "true").Set("spec_version", Wmi.Str(t, "SpecVersion")).SetId("manufacturer", Wmi.Str(t, "ManufacturerIdTxt"))
                   .Set("firmware_version", Wmi.Str(t, "ManufacturerVersion")).Set("enabled", YesNo(Wmi.Raw(t, "IsEnabled_InitialValue"))).Set("activated", YesNo(Wmi.Raw(t, "IsActivated_InitialValue")));
        }

        // ------------------------------------------------------------------ Windows / Office (real state)
        private static void WindowsAndOffice(TechnicalInventory inv)
        {
            var w = UI.Views.WindowsInstallPanel.ReadWindows();
            string display = null;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) display = k?.GetValue("DisplayVersion")?.ToString();
            var software = new SoftwareExecutionService();
            // Microsoft licensing service (the same source slmgr uses). It can take a while; if it does not answer the
            // status stays UNKNOWN: activation is never assumed.
            string channel = null, activation = null, activationDetail = null;
            foreach (var p in Wmi.Query("SELECT Description, LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationId = '55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL", timeoutSeconds: 90))
            {
                string desc = Wmi.Str(p, "Description").ToUpperInvariant();
                channel = desc.Contains("OEM") ? "OEM" : desc.Contains("RETAIL") ? "Retail" : desc.Contains("VOLUME") ? "Volume" : null;
                int ls = Wmi.Int(p, "LicenseStatus");
                activation = ls == 1 ? "Activated" : "Not Activated";
                activationDetail = ls == 1 ? "Licensed" : ls == 0 ? "Unlicensed" : ls == 2 ? "Out-of-box grace" : ls == 3 ? "Out-of-tolerance grace" : ls == 4 ? "Non-genuine grace" : ls == 5 ? "Notification" : ls == 6 ? "Extended grace" : ls.ToString(CultureInfo.InvariantCulture);
                if (ls == 1) break;
            }
            inv.Add("windows", w.Readable ? w.Edition : "Windows", "WMI Win32_OperatingSystem + SoftwareLicensingProduct", w.Readable ? DataConfidence.Detected : DataConfidence.Unknown)
               .Set("edition", w.Edition).Set("display_version", display).Set("version", w.Version).Set("build", w.Build).Set("architecture", w.Architecture)
               .Set("install_date", w.InstallDate?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
               .Set("activation_status", activation).Set("activation_detail", activationDetail).Set("activation_channel", channel);

            var office = software.CheckOfficeInstallation();
            if (!office.IsInstalled) { inv.Add("office", "Microsoft Office", "Windows Registry").Set("installed", "false"); return; }
            var oAct = software.CheckOfficeActivation();
            string platform = null, channelO = null;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration"))
            { platform = k?.GetValue("Platform")?.ToString(); channelO = k?.GetValue("ProductReleaseIds")?.ToString(); }
            inv.Add("office", office.DisplayName, "Windows Registry + SoftwareLicensingProduct")
               .Set("installed", "true").Set("product", office.DisplayName).Set("version", office.Version).Set("architecture", platform)
               .Set("product_ids", channelO).Set("install_method", platform != null ? "Click-to-Run" : "MSI / other")
               .Set("subscription_product", OfficeLicensing.IsSubscription(channelO) ? "true" : "false");
            var officeItem = inv.Items[inv.Items.Count - 1];
            if (oAct != null && oAct.IsActivated) officeItem.Set("activation_status", "Activated").Set("activation_detail", oAct.LicenseStatus);
            else if (OfficeLicensing.IsSubscription(channelO))
                // Microsoft 365 uses subscription licensing that Windows Software Protection does not report. Never
                // guess: say it cannot be confirmed here, plus the evidence that exists.
                officeItem.SetUnknown("activation_status", "SUBSCRIPTION_LICENSING_NOT_VISIBLE_TO_SOFTWARE_PROTECTION")
                          .Set("subscription_license_token_present", OfficeLicensing.SubscriptionTokenPresent() ? "true" : "false");
            else officeItem.Set("activation_status", oAct == null ? null : "Not Activated").Set("activation_detail", oAct?.LicenseStatus);
        }

        // ------------------------------------------------------------------ relevant drivers only
        private static void Drivers(TechnicalInventory inv)
        {
            foreach (var dr in SignedDrivers("DeviceClass = 'SYSTEM'").Values.Where(x =>
                     { string n = Wmi.Str(x, "DeviceName"); return n.IndexOf("Management Engine", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Chipset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       n.IndexOf("Serial IO", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("SMBus", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Dynamic Tuning", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       n.IndexOf("GPIO", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("PSP", StringComparison.OrdinalIgnoreCase) >= 0; }))
                inv.Add("driver", Wmi.Str(dr, "DeviceName"), "WMI Win32_PnPSignedDriver")
                   .Set("device_class", "SYSTEM (chipset)").Set("driver_version", Wmi.Str(dr, "DriverVersion")).Set("driver_date", Date(Wmi.Str(dr, "DriverDate")))
                   .Set("driver_provider", Wmi.Str(dr, "DriverProviderName"));
            foreach (var dr in SignedDrivers("DeviceClass = 'DISPLAY' OR DeviceClass = 'SCSIADAPTER' OR DeviceClass = 'HDC'").Values.Where(x =>
                     { string n = Wmi.Str(x, "DeviceName"); return n.IndexOf("Storage Spaces", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("VHD", StringComparison.OrdinalIgnoreCase) < 0 &&
                       n.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Remote Display", StringComparison.OrdinalIgnoreCase) < 0; }))
                inv.Add("driver", Wmi.Str(dr, "DeviceName"), "WMI Win32_PnPSignedDriver")
                   .Set("device_class", Wmi.Str(dr, "DeviceClass")).Set("driver_version", Wmi.Str(dr, "DriverVersion")).Set("driver_date", Date(Wmi.Str(dr, "DriverDate")))
                   .Set("driver_provider", Wmi.Str(dr, "DriverProviderName"));

            var problems = new List<BBTech.Core.Testing.PnpDeviceInfo>();
            foreach (var e in Wmi.Query("SELECT Name, PNPClass, ConfigManagerErrorCode, PNPDeviceID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0", timeoutSeconds: 15))
                problems.Add(new BBTech.Core.Testing.PnpDeviceInfo { Name = Wmi.Str(e, "Name"), PnpClass = Wmi.Str(e, "PNPClass"), ErrorCode = Wmi.Int(e, "ConfigManagerErrorCode"), PnpDeviceId = Wmi.Str(e, "PNPDeviceID") });
            var report = BBTech.Core.Testing.DriverHealth.Evaluate(problems);
            inv.Add("driver_health", "Device problems", "WMI Win32_PnPEntity", DataConfidence.Detected)
               .Set("problem_device_count", report.Problems.Count).Set("problems", report.Problems.Count == 0 ? "none" : string.Join("; ", report.Problems));
        }
    }
}
