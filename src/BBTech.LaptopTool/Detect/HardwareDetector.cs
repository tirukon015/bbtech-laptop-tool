using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBTech.Core.Models;
using BBTech.Core.Parsing;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.Detect
{
    /// <summary>
    /// DETECT orchestration. Each step is isolated: a failure in one step is logged,
    /// turned into a friendly warning, and the field is left as "Unknown".
    /// </summary>
    public static class HardwareDetector
    {
        public static Task<DetectionResult> DetectAsync(IProgress<string> progress)
        {
            return Task.Run(() =>
            {
                var r = new DetectionResult { ToolVersion = AppPaths.Version, DetectedAt = DateTime.Now };
                AppLog.Info("Detection started");

                Step(r, progress, "Reading system information...", () => SystemInfoProvider.Fill(r), "System model information unavailable.");
                Step(r, progress, "Reading processor...", () => CpuProvider.Fill(r), "Processor information unavailable.");
                Step(r, progress, "Reading memory...", () => MemoryProvider.Fill(r), "Memory information unavailable.");
                Step(r, progress, "Reading storage drives...", () => StorageProvider.Fill(r), "Storage information unavailable.");
                Step(r, progress, "Reading battery...", () => BatteryProvider.Fill(r), "Battery information is currently unavailable.");

                AppLog.Info("Detection finished: " + r.Brand + " | " + r.Model + " | " + r.ModelNumber + " | " + r.Cpu + " | " +
                            r.Generation + " | " + r.Ram + " | " + r.Storage + " | battery " + r.BatteryHealth +
                            (r.Warnings.Count > 0 ? " | warnings: " + r.Warnings.Count : ""));
                return r;
            });
        }

        private static void Step(DetectionResult r, IProgress<string> progress, string label, Action action, string failureMessage)
        {
            progress?.Report(label);
            try { action(); }
            catch (Exception ex)
            {
                AppLog.Error("Detection step failed: " + label, ex);
                r.Warnings.Add(failureMessage);
            }
        }
    }

    internal static class SystemInfoProvider
    {
        // SMBIOS chassis types that indicate a portable computer.
        private static readonly int[] PortableChassis = { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };

        public static void Fill(DetectionResult r)
        {
            var s = r.System;
            var cs = Wmi.Query("SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault()
                     ?? Wmi.Query("SELECT * FROM Win32_ComputerSystem").FirstOrDefault();
            var prod = Wmi.Query("SELECT Vendor, Name, Version, UUID FROM Win32_ComputerSystemProduct").FirstOrDefault();
            var bios = Wmi.Query("SELECT SerialNumber, SMBIOSBIOSVersion FROM Win32_BIOS").FirstOrDefault();
            var board = Wmi.Query("SELECT SerialNumber FROM Win32_BaseBoard").FirstOrDefault();
            var enc = Wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure").FirstOrDefault();
            var os = Wmi.Query("SELECT Caption, Version FROM Win32_OperatingSystem").FirstOrDefault();

            if (cs != null)
            {
                s.RawManufacturer = Wmi.Str(cs, "Manufacturer");
                s.RawModel = Wmi.Str(cs, "Model");
                s.SystemFamily = Wmi.Str(cs, "SystemFamily");
                s.SystemSku = Wmi.Str(cs, "SystemSKUNumber");
            }
            if (prod != null)
            {
                s.ProductVersion = Wmi.Str(prod, "Version");
                if (s.RawManufacturer.Length == 0) s.RawManufacturer = Wmi.Str(prod, "Vendor");
                if (s.RawModel.Length == 0) s.RawModel = Wmi.Str(prod, "Name");
            }
            if (bios != null)
            {
                string serial = Wmi.Str(bios, "SerialNumber");
                s.SerialNumber = BrandCatalog.IsPlaceholder(serial) ? "" : serial;
                s.BiosVersion = Wmi.Str(bios, "SMBIOSBIOSVersion");
            }
            // Stable identifiers for physical-device identity (placeholders are discarded).
            if (prod != null) s.SystemUuid = BBTech.Core.Identity.DeviceIdentity.CleanUuid(Wmi.Str(prod, "UUID")) ?? "";
            if (board != null) s.BoardSerial = BBTech.Core.Identity.DeviceIdentity.CleanSerial(Wmi.Str(board, "SerialNumber")) ?? "";
            if (enc != null && Wmi.Raw(enc, "ChassisTypes") is ushort[] types && types.Length > 0)
            {
                int t = types[0];
                s.ChassisType = ChassisName(t);
                s.IsPortable = PortableChassis.Contains(t);
            }
            if (os != null) s.OperatingSystem = (Wmi.Str(os, "Caption") + " " + Wmi.Str(os, "Version")).Trim();

            var parsed = ModelParser.Parse(s.RawManufacturer, s.RawModel, s.SystemFamily, s.ProductVersion);
            r.Brand = parsed.Brand;
            r.Model = parsed.Model;
            r.ModelNumber = parsed.ModelNumber;
            s.ProductCode = parsed.ProductCode;

            if (r.Brand == DetectionResult.Unknown) r.Warnings.Add("Laptop manufacturer could not be identified.");
            if (r.Model == DetectionResult.Unknown) r.Warnings.Add("Laptop model name could not be identified from firmware.");
            if (s.IsPortable == false) r.Warnings.Add("This computer reports a desktop chassis (" + s.ChassisType + ").");
        }

        private static string ChassisName(int t)
        {
            switch (t)
            {
                case 3: return "Desktop"; case 4: return "Low-profile desktop"; case 6: return "Mini tower"; case 7: return "Tower";
                case 8: return "Portable"; case 9: return "Laptop"; case 10: return "Notebook"; case 11: return "Handheld";
                case 13: return "All-in-one"; case 14: return "Sub-notebook"; case 30: return "Tablet"; case 31: return "Convertible";
                case 32: return "Detachable"; case 35: return "Mini PC"; case 36: return "Stick PC";
                default: return "Type " + t;
            }
        }
    }

    internal static class CpuProvider
    {
        public static void Fill(DetectionResult r)
        {
            var cpu = Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor").FirstOrDefault();
            if (cpu == null) { r.Warnings.Add("Processor information unavailable."); return; }

            string name = Wmi.Str(cpu, "Name");
            var p = CpuParser.Parse(name);
            r.Cpu = p.Family;
            r.Generation = p.Generation;
            r.CpuDetails.FullName = p.CleanName.Length > 0 ? p.CleanName : name;
            r.CpuDetails.Vendor = p.Vendor;
            r.CpuDetails.ModelCode = p.ModelCode;
            r.CpuDetails.Cores = Wmi.Int(cpu, "NumberOfCores");
            r.CpuDetails.LogicalProcessors = Wmi.Int(cpu, "NumberOfLogicalProcessors");
            r.CpuDetails.MaxClockMhz = Wmi.Int(cpu, "MaxClockSpeed");

            if (r.Cpu == DetectionResult.Unknown) r.Warnings.Add("Processor family not recognised (see details).");
            AppLog.Info("CPU raw: " + name);
        }
    }

    internal static class MemoryProvider
    {
        public static void Fill(DetectionResult r)
        {
            var modules = Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType, DeviceLocator, Manufacturer FROM Win32_PhysicalMemory");
            if (modules.Count == 0) modules = Wmi.Query("SELECT * FROM Win32_PhysicalMemory");

            foreach (var m in modules)
            {
                long cap = Wmi.Long(m, "Capacity");
                if (cap <= 0) continue;
                int speed = Wmi.Int(m, "ConfiguredClockSpeed");
                if (speed <= 0) speed = Wmi.Int(m, "Speed");
                int type = Wmi.Int(m, "SMBIOSMemoryType");
                if (type == 0) type = Wmi.Int(m, "MemoryType");
                r.Memory.Modules.Add(new MemoryModule
                {
                    CapacityBytes = cap,
                    SpeedMhz = speed,
                    Type = MemoryTypeName(type),
                    Slot = Wmi.Str(m, "DeviceLocator"),
                    Manufacturer = Wmi.Str(m, "Manufacturer"),
                });
            }

            long sum = r.Memory.Modules.Sum(m => m.CapacityBytes);
            if (sum > 0)
            {
                r.Memory.TotalBytes = sum;
                r.Memory.FromModules = true;
                r.Ram = CapacityFormatter.FormatRamFromModules(sum);
                return;
            }

            // Fallback: OS-visible memory (virtual machines / unusual firmware).
            var cs = Wmi.Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
            long visible = cs == null ? 0 : Wmi.Long(cs, "TotalPhysicalMemory");
            r.Memory.TotalBytes = visible;
            r.Ram = CapacityFormatter.FormatRamFromVisible(visible);
            if (visible <= 0) r.Warnings.Add("Memory size could not be read.");
        }

        private static string MemoryTypeName(int t)
        {
            switch (t)
            {
                case 20: return "DDR"; case 21: return "DDR2"; case 24: return "DDR3"; case 26: return "DDR4";
                case 27: return "LPDDR"; case 28: return "LPDDR2"; case 29: return "LPDDR3"; case 30: return "LPDDR4";
                case 34: return "DDR5"; case 35: return "LPDDR5";
                default: return "";
            }
        }
    }

    internal static class StorageProvider
    {
        private const string StorageNs = @"root\Microsoft\Windows\Storage";

        public static void Fill(DetectionResult r)
        {
            var disks = Wmi.Query("SELECT FriendlyName, MediaType, BusType, Size, HealthStatus, SpindleSpeed, DeviceId FROM MSFT_PhysicalDisk", StorageNs);
            foreach (var d in disks)
            {
                int bus = Wmi.Int(d, "BusType");
                // Skip SD/MMC cards and virtual disks entirely; mark USB drives (incl. this pendrive) as external.
                if (bus == 12 || bus == 13 || bus == 14 || bus == 15) continue;

                long size = Wmi.Long(d, "Size");
                if (size <= 0) continue;
                string model = Wmi.Str(d, "FriendlyName");

                r.StorageDevices.Add(new StorageDevice
                {
                    Model = model,
                    CapacityBytes = size,
                    CapacityLabel = CapacityFormatter.FormatDrive(size),
                    BusType = BusName(bus),
                    MediaType = MediaTypeName(Wmi.Int(d, "MediaType"), bus, Wmi.Long(d, "SpindleSpeed"), model),
                    WindowsHealth = HealthName(Wmi.Int(d, "HealthStatus")),
                    IsExternal = bus == 7, // USB (includes the BBTech pendrive itself)
                    DeviceId = Wmi.Str(d, "DeviceId"),
                });
            }

            if (r.StorageDevices.Count == 0) FillFromDiskDrive(r);

            // Order: internal first, then by size.
            r.StorageDevices = r.StorageDevices.OrderBy(d => d.IsExternal).ThenByDescending(d => d.CapacityBytes).ToList();
            r.Storage = CapacityFormatter.StorageSummary(r.StorageDevices);
            if (r.Storage == DetectionResult.Unknown) r.Warnings.Add("No internal storage drive was detected.");
            foreach (var d in r.StorageDevices) AppLog.Info("Drive: " + d.DisplayValue + " bus=" + d.BusType + " external=" + d.IsExternal + " health=" + d.WindowsHealth);
        }

        private static void FillFromDiskDrive(DetectionResult r)
        {
            foreach (var d in Wmi.Query("SELECT Model, Size, InterfaceType, MediaType, PNPDeviceID FROM Win32_DiskDrive"))
            {
                long size = Wmi.Long(d, "Size");
                if (size <= 0) continue;
                string iface = Wmi.Str(d, "InterfaceType");
                string pnp = Wmi.Str(d, "PNPDeviceID");
                string model = Wmi.Str(d, "Model");
                bool usb = iface.Equals("USB", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase);
                bool nvme = pnp.IndexOf("NVME", StringComparison.OrdinalIgnoreCase) >= 0;
                r.StorageDevices.Add(new StorageDevice
                {
                    Model = model,
                    CapacityBytes = size,
                    CapacityLabel = CapacityFormatter.FormatDrive(size),
                    BusType = usb ? "USB" : nvme ? "NVMe" : iface,
                    MediaType = nvme || model.IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0 ? "SSD" : "",
                    IsExternal = usb || Wmi.Str(d, "MediaType").IndexOf("External", StringComparison.OrdinalIgnoreCase) >= 0,
                });
            }
        }

        /// <summary>
        /// Windows-native drive health counters (no third-party tool). Returns false when Windows does not
        /// provide them (typically without administrator rights, or unsupported drives). Values are only
        /// filled when actually reported; nothing is estimated.
        /// </summary>
        public static bool ReadReliability(List<StorageDevice> devices)
        {
            var rows = Wmi.Query("SELECT DeviceId, Wear, PowerOnHours, Temperature FROM MSFT_StorageReliabilityCounter", StorageNs);
            if (rows.Count == 0) return false;
            foreach (var row in rows)
            {
                var dev = devices.FirstOrDefault(d => d.DeviceId == Wmi.Str(row, "DeviceId"));
                if (dev == null) continue;
                object wear = Wmi.Raw(row, "Wear"), hours = Wmi.Raw(row, "PowerOnHours"), temp = Wmi.Raw(row, "Temperature");
                // NVMe "percentage used" is reliable; SATA drives often report 0 = not supported.
                if (wear != null && (dev.BusType == "NVMe" || Convert.ToInt32(wear) > 0)) dev.WearPercentUsed = Convert.ToInt32(wear);
                if (hours != null && Convert.ToInt64(hours) > 0) dev.PowerOnHours = Convert.ToInt64(hours);
                if (temp != null && Convert.ToInt32(temp) > 0) dev.TemperatureC = Convert.ToInt32(temp);
            }
            return true;
        }

        public static string MediaTypeName(int mediaType, int bus, long spindle, string model)
        {
            if (mediaType == 4) return "SSD";
            if (mediaType == 3) return "HDD";
            if (mediaType == 5) return "SSD"; // storage-class memory
            if (bus == 17) return "SSD";      // NVMe is always solid state
            string m = model ?? "";
            if (m.IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.IndexOf("Solid State", StringComparison.OrdinalIgnoreCase) >= 0) return "SSD";
            if (spindle > 0 && spindle < uint.MaxValue) return "HDD";
            return ""; // not reliably known
        }

        private static string BusName(int bus)
        {
            switch (bus)
            {
                case 1: return "SCSI"; case 2: return "ATAPI"; case 3: return "ATA"; case 6: return "Fibre Channel";
                case 7: return "USB"; case 8: return "RAID"; case 9: return "iSCSI"; case 10: return "SAS";
                case 11: return "SATA"; case 12: return "SD"; case 13: return "MMC"; case 17: return "NVMe"; case 18: return "SCM";
                case 19: return "UFS";
                default: return "";
            }
        }

        private static string HealthName(int h)
        {
            switch (h)
            {
                case 0: return "Healthy"; case 1: return "Warning"; case 2: return "Unhealthy";
                default: return "Unknown";
            }
        }
    }

    internal static class BatteryProvider
    {
        public static void Fill(DetectionResult r)
        {
            var power = PowerStatus.Read();
            BatteryDetails b = null;
            var notes = new List<string>();

            // 1. BatteryInfoView (reused tool) - most complete, no admin required.
            var biv = Tools.BatteryInfoViewTool.Read();
            if (biv.Ok && biv.Value.Present) b = biv.Value;
            else if (biv.Message != null) notes.Add(biv.Message);

            // 2. Windows battery WMI classes (fallback if the tool is missing or failed).
            if (b == null || b.HealthPercent == null) b = Merge(b, BatteryFallbacks.FromWmi());

            // 3. powercfg battery report (fallback; built into Windows).
            if (b == null || b.HealthPercent == null) b = Merge(b, BatteryFallbacks.FromPowercfg());

            if (b == null || !b.Present)
            {
                r.Battery = b ?? new BatteryDetails();
                r.BatteryHealth = DetectionResult.Unknown;
                r.Warnings.Add(power.NoSystemBattery
                    ? "No battery detected (desktop PC, or battery removed/disconnected)."
                    : "Battery information is currently unavailable.");
                return;
            }

            r.Battery = b;
            r.BatteryHealth = BatteryHealth.Format(b.HealthPercent);
            if (b.HealthPercent == null) r.Warnings.Add("Battery found, but its health could not be determined.");
        }

        private static BatteryDetails Merge(BatteryDetails primary, BatteryDetails fallback)
        {
            if (fallback == null || !fallback.Present) return primary;
            if (primary == null || !primary.Present) return fallback;
            primary.DesignCapacityMWh = primary.DesignCapacityMWh ?? fallback.DesignCapacityMWh;
            primary.FullChargeCapacityMWh = primary.FullChargeCapacityMWh ?? fallback.FullChargeCapacityMWh;
            primary.CycleCount = primary.CycleCount ?? fallback.CycleCount;
            primary.HealthPercent = BatteryHealth.Compute(primary.DesignCapacityMWh, primary.FullChargeCapacityMWh) ?? fallback.HealthPercent;
            primary.Source += " + " + fallback.Source;
            return primary;
        }
    }
}

