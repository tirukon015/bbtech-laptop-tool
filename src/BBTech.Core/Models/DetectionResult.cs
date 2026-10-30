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
        public string SsdHealth { get; set; } = Unknown;

        // ---- Supporting detail ----
        public SystemDetails System { get; set; } = new SystemDetails();
        public CpuDetails CpuDetails { get; set; } = new CpuDetails();
        public MemoryDetails Memory { get; set; } = new MemoryDetails();
        public List<StorageDevice> StorageDevices { get; set; } = new List<StorageDevice>();
        public BatteryDetails Battery { get; set; } = new BatteryDetails();
        public List<GpuDetails> Gpus { get; set; } = new List<GpuDetails>();
        public DisplayDetails Display { get; set; } = new DisplayDetails();
        public PeripheralDetails Peripherals { get; set; } = new PeripheralDetails();
        public FirmwareDetails Firmware { get; set; } = new FirmwareDetails();
        public PhysicalConditionDetails Condition { get; set; } = new PhysicalConditionDetails();
        public AccessoryDetails Accessories { get; set; } = new AccessoryDetails();
        public List<FieldQualityInfo> FieldQualities { get; set; } = new List<FieldQualityInfo>();

        private readonly object _warningsLock = new object();

        /// <summary>User-friendly notes about anything that could not be detected.</summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>Thread-safe addition of a user-facing detection note/warning.</summary>
        public void AddWarning(string warning)
        {
            if (string.IsNullOrWhiteSpace(warning)) return;
            lock (_warningsLock)
            {
                Warnings.Add(warning);
            }
        }

        public DateTime DetectedAt { get; set; } = DateTime.Now;
        public string ToolVersion { get; set; } = "";
        /// <summary>Stable UUID v4 generated once per detection session for ITMS idempotency.</summary>
        public string SubmissionId { get; set; } = Guid.NewGuid().ToString("D");
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
        public string BiosReleaseDate { get; set; } = "";
        public string FirmwareAssetTag { get; set; } = "";
        public string ComputerName { get; set; } = "";
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
        public string PartNumber { get; set; } = "";
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
        public string SerialNumber { get; set; } = "";
        public string PartitionsSummary { get; set; } = "";
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
        /// <summary>Assessment: OPTIMAL, GOOD, WARNING, CRITICAL, UNKNOWN.</summary>
        public string HealthAssessment { get; set; } = "";
        /// <summary>Detailed diagnostic or SMART description from the SSD health engine.</summary>
        public string DiagnosticDetails { get; set; } = "";
        /// <summary>Engine provider (HD Sentinel Pro v6.30, Windows NVMe Diagnostics, etc.).</summary>
        public string HealthSource { get; set; } = "";

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
        public bool? AcAdapterConnected { get; set; }
        public string ChargingStatus { get; set; } = "";
    }

    public sealed class GpuDetails
    {
        public string Name { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public long? DedicatedVramBytes { get; set; }
        public string DedicatedVramLabel { get; set; } = "";
        public string DriverVersion { get; set; } = "";
    }

    public sealed class DisplayDetails
    {
        public int DisplayCount { get; set; } = 1;
        public string Resolution { get; set; } = "";
        public int? RefreshRateHz { get; set; }
        public double? PhysicalSizeInches { get; set; }
        public string PanelModel { get; set; } = "";
        public bool? TouchscreenSupported { get; set; }
        public bool? PenSupported { get; set; }
    }

    public sealed class PeripheralDetails
    {
        public string WebcamName { get; set; } = "";
        public string MicrophoneName { get; set; } = "";
        public string SpeakerName { get; set; } = "";
        public string WifiAdapter { get; set; } = "";
        public string BluetoothAdapter { get; set; } = "";
        public string EthernetAdapter { get; set; } = "";
        public string FingerprintReader { get; set; } = "";
        public bool? TpmPresent { get; set; }
        public string TpmVersion { get; set; } = "";
        public bool? SecureBootEnabled { get; set; }
    }

    public sealed class FirmwareDetails
    {
        public string BiosVendor { get; set; } = "";
        public string BiosVersion { get; set; } = "";
        public string BiosReleaseDate { get; set; } = "";
        public bool? SecureBoot { get; set; }
        public bool? TpmReady { get; set; }
        public string TpmVersion { get; set; } = "";
    }

    public sealed class PhysicalConditionDetails
    {
        public string Grade { get; set; } = ""; // e.g. "Grade A", "Grade B", "Grade C"
        public string ScreenCondition { get; set; } = "";
        public string LidCover { get; set; } = "";
        public string PalmRest { get; set; } = "";
        public string KeyboardCondition { get; set; } = "";
        public string BottomCover { get; set; } = "";
        public string Hinges { get; set; } = "";
        public string MissingScrews { get; set; } = "";
        public string RubberFeet { get; set; } = "";
        public string PortDamage { get; set; } = "";
        public string MissingKeys { get; set; } = "";
        public string Notes { get; set; } = "";

        public bool HasValues =>
            !string.IsNullOrWhiteSpace(Grade) ||
            !string.IsNullOrWhiteSpace(ScreenCondition) ||
            !string.IsNullOrWhiteSpace(LidCover) ||
            !string.IsNullOrWhiteSpace(PalmRest) ||
            !string.IsNullOrWhiteSpace(KeyboardCondition) ||
            !string.IsNullOrWhiteSpace(BottomCover) ||
            !string.IsNullOrWhiteSpace(Hinges) ||
            !string.IsNullOrWhiteSpace(MissingScrews) ||
            !string.IsNullOrWhiteSpace(RubberFeet) ||
            !string.IsNullOrWhiteSpace(PortDamage) ||
            !string.IsNullOrWhiteSpace(MissingKeys) ||
            !string.IsNullOrWhiteSpace(Notes);
    }

    public sealed class AccessoryDetails
    {
        public bool? ChargerIncluded { get; set; }
        public string ChargerManufacturer { get; set; } = "";
        public string ChargerModel { get; set; } = "";
        public int? ChargerWattage { get; set; }
        public double? ChargerVoltage { get; set; }
        public double? ChargerAmperage { get; set; }
        public string ConnectorType { get; set; } = ""; // e.g. "USB-C", "Slim Tip", "Barrel"
        public bool? BagIncluded { get; set; }
        public bool? StylusIncluded { get; set; }
        public bool? DongleIncluded { get; set; }
        public string Notes { get; set; } = "";

        public bool HasValues =>
            ChargerIncluded.HasValue ||
            !string.IsNullOrWhiteSpace(ChargerManufacturer) ||
            !string.IsNullOrWhiteSpace(ChargerModel) ||
            ChargerWattage.HasValue ||
            ChargerVoltage.HasValue ||
            ChargerAmperage.HasValue ||
            !string.IsNullOrWhiteSpace(ConnectorType) ||
            BagIncluded.HasValue ||
            StylusIncluded.HasValue ||
            DongleIncluded.HasValue ||
            !string.IsNullOrWhiteSpace(Notes);
    }

    public sealed class FieldQualityInfo
    {
        public string FieldName { get; set; } = "";
        /// <summary>"DETECTED", "UNAVAILABLE", "UNSUPPORTED", "ERROR", "MANUALLY_VERIFIED"</summary>
        public string Status { get; set; } = "DETECTED";
        public string Source { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
