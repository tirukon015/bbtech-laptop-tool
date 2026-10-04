using System;
using System.Collections.Generic;

namespace BBTech.Core.Models
{
    /// <summary>
    /// Structured result of the DETECT section. The eight primary properties are
    /// the ITMS values; everything else is supporting detail.
    /// </summary>
    public sealed class DetectionResult
    {
        public const string Unknown = "Unknown";

        // ---- Primary ITMS fields ----
        public string Brand { get; set; } = Unknown;
        public string Model { get; set; } = Unknown;
        public string ModelNumber { get; set; } = Unknown;
        public string Cpu { get; set; } = Unknown;
        public string Generation { get; set; } = Unknown;
        public string Ram { get; set; } = Unknown;
        public string Storage { get; set; } = Unknown;
        public string BatteryHealth { get; set; } = Unknown;

        // ---- Supporting detail ----
        public SystemDetails System { get; set; } = new SystemDetails();
        public CpuDetails CpuDetails { get; set; } = new CpuDetails();
        public MemoryDetails Memory { get; set; } = new MemoryDetails();
        public List<StorageDevice> StorageDevices { get; set; } = new List<StorageDevice>();
        public BatteryDetails Battery { get; set; } = new BatteryDetails();

        /// <summary>User-friendly notes about anything that could not be detected.</summary>
        public List<string> Warnings { get; set; } = new List<string>();

        public DateTime DetectedAt { get; set; } = DateTime.Now;
        public string ToolVersion { get; set; } = "";
    }

    public sealed class SystemDetails
    {
        public string RawManufacturer { get; set; } = "";
        public string RawModel { get; set; } = "";
        public string SystemFamily { get; set; } = "";
        public string ProductVersion { get; set; } = "";
        public string SystemSku { get; set; } = "";
        /// <summary>Machine type / product code reported by firmware (e.g. Lenovo "20W0S22200").</summary>
        public string ProductCode { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        /// <summary>SMBIOS system UUID (Win32_ComputerSystemProduct.UUID); "" when missing/placeholder.</summary>
        public string SystemUuid { get; set; } = "";
        /// <summary>Motherboard serial (Win32_BaseBoard.SerialNumber); "" when missing/placeholder.</summary>
        public string BoardSerial { get; set; } = "";
        public string BiosVersion { get; set; } = "";
        public string ChassisType { get; set; } = "";
        public bool? IsPortable { get; set; }
        public string OperatingSystem { get; set; } = "";
    }

    public sealed class CpuDetails
    {
        public string FullName { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string ModelCode { get; set; } = "";
        public int Cores { get; set; }
        public int LogicalProcessors { get; set; }
        public int MaxClockMhz { get; set; }
    }

    public sealed class MemoryDetails
    {
        public long TotalBytes { get; set; }
        /// <summary>True when the total is the sum of physical modules (most accurate).</summary>
        public bool FromModules { get; set; }
        public List<MemoryModule> Modules { get; set; } = new List<MemoryModule>();
    }

    public sealed class MemoryModule
    {
        public long CapacityBytes { get; set; }
        public int SpeedMhz { get; set; }
        public string Type { get; set; } = "";
        public string Slot { get; set; } = "";
        public string Manufacturer { get; set; } = "";
    }

    public sealed class StorageDevice
    {
        public string Model { get; set; } = "";
        public long CapacityBytes { get; set; }
        /// <summary>Marketing capacity, e.g. "512 GB" or "1 TB".</summary>
        public string CapacityLabel { get; set; } = DetectionResult.Unknown;
        /// <summary>"SSD", "HDD" or "" when not reliably known.</summary>
        public string MediaType { get; set; } = "";
        /// <summary>"NVMe", "SATA", "USB", ...</summary>
        public string BusType { get; set; } = "";
        public string WindowsHealth { get; set; } = "";
        public bool IsExternal { get; set; }

        /// <summary>Windows storage device id (MSFT_PhysicalDisk.DeviceId), to join reliability counters.</summary>
        public string DeviceId { get; set; } = "";
        // Windows-native reliability counters (MSFT_StorageReliabilityCounter; usually needs administrator rights).
        public int? WearPercentUsed { get; set; }
        public long? PowerOnHours { get; set; }
        public int? TemperatureC { get; set; }

        // Filled by HD Sentinel when its report is available.
        public int? HealthPercent { get; set; }
        public int? PerformancePercent { get; set; }
        public string PowerOnTime { get; set; } = "";
        public string Temperature { get; set; } = "";

        /// <summary>ITMS-style value, e.g. "512 GB SSD".</summary>
        public string DisplayValue
        {
            get
            {
                if (string.IsNullOrEmpty(MediaType)) return CapacityLabel;
                return CapacityLabel + " " + MediaType;
            }
        }
    }

    public sealed class BatteryDetails
    {
        public bool Present { get; set; }
        public int BatteryCount { get; set; }
        public string Name { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Chemistry { get; set; } = "";
        public string PowerState { get; set; } = "";
        public long? DesignCapacityMWh { get; set; }
        public long? FullChargeCapacityMWh { get; set; }
        public long? CurrentCapacityMWh { get; set; }
        public double? ChargePercent { get; set; }
        public int? CycleCount { get; set; }
        /// <summary>Full charge / design capacity, in percent. Null when not determinable.</summary>
        public double? HealthPercent { get; set; }
        /// <summary>Where the data came from (BatteryInfoView, WMI, ...).</summary>
        public string Source { get; set; } = "";
    }
}
