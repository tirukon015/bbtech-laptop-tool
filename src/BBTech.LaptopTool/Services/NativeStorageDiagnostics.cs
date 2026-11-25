using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BBTech.Core.Models;
using BBTech.LaptopTool.Logging;
using Microsoft.Win32.SafeHandles;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// Direct, low-level native storage protocol diagnostics via Win32 IOCTLs.
    /// Provides hardware-level NVMe SMART log page 0x02 and ATA SMART telemetry
    /// in sub-millisecond execution time without external processes.
    /// </summary>
    public static class NativeStorageDiagnostics
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;

        private const uint StorageDeviceProtocolSpecificProperty = 50;
        private const uint StorageAdapterProtocolSpecificProperty = 49;
        private const uint PropertyStandardQuery = 0;
        private const uint ProtocolTypeNvme = 3;            // STORAGE_PROTOCOL_TYPE: Scsi = 1, Ata = 2, Nvme = 3 (was 1: Windows rejected it as ERROR_INVALID_FUNCTION)
        private const uint STORAGE_PROTOCOL_NVME_DATA_TYPE_LOG_PAGE = 2;
        private const uint NVME_LOG_PAGE_HEALTH_INFO = 2;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct IDEREGS
        {
            public byte bFeaturesReg;
            public byte bSectorCountReg;
            public byte bSectorNumberReg;
            public byte bCylLowReg;
            public byte bCylHighReg;
            public byte bDriveHeadReg;
            public byte bCommandReg;
            public byte bReserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct SENDCMDINPARAMS
        {
            public uint cBufferSize;
            public IDEREGS irDriveRegs;
            public byte bDriveNumber;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public byte[] bReserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] dwReserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] bBuffer;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct DRIVERSTATUS
        {
            public byte bDriverError;
            public byte bIDEError;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public byte[] bReserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public uint[] dwReserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct SENDCMDOUTPARAMS
        {
            public uint cBufferSize;
            public DRIVERSTATUS DriverStatus;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
            public byte[] bBuffer;
        }

        /// <summary>
        /// Queries low-level NVMe / ATA SMART telemetry for all discovered storage drives.
        /// </summary>
        public static void QueryAll(IEnumerable<StorageDevice> devices)
        {
            if (devices == null) return;

            foreach (var dev in devices)
            {
                if (dev == null) continue;
                int diskNum = ExtractDiskNumber(dev.DeviceId);
                if (diskNum < 0) continue;

                try
                {
                    // 1. Try Native NVMe Query if BusType is NVMe or drive appears to be solid-state
                    bool isNvme = dev.BusType.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ||
                                  (dev.Model != null && dev.Model.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (isNvme || string.IsNullOrEmpty(dev.BusType))
                    {
                        if (TryQueryNvme(diskNum, dev))
                        {
                            AppLog.Info(string.Format("NativeStorage: Drive #{0} ({1}) successfully read via Native NVMe IOCTL: Health={2}%, Temp={3}, Hours={4}",
                                diskNum, dev.Model, dev.HealthPercent, dev.Temperature, dev.PowerOnTime));
                            continue;
                        }
                    }

                    // 2. Try Direct ATA SMART Pass-Through
                    if (TryQueryAtaSmart(diskNum, dev))
                    {
                        AppLog.Info(string.Format("NativeStorage: Drive #{0} ({1}) successfully read via Direct ATA SMART: Health={2}%, Temp={3}, Hours={4}",
                            diskNum, dev.Model, dev.HealthPercent, dev.Temperature, dev.PowerOnTime));
                        continue;
                    }

                    // 3. If standard ATA failed and drive is not NVMe, retry NVMe query once in case bus was misidentified
                    if (!isNvme && TryQueryNvme(diskNum, dev))
                    {
                        AppLog.Info(string.Format("NativeStorage: Drive #{0} ({1}) successfully read via Native NVMe IOCTL (fallback): Health={2}%",
                            diskNum, dev.Model, dev.HealthPercent));
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn(string.Format("NativeStorage query failed for drive #{0} ({1})", diskNum, dev.Model), ex);
                }
            }
        }

        private static bool TryQueryNvme(int diskNum, StorageDevice dev)
        {
            string diskPath = string.Format(CultureInfo.InvariantCulture, @"\\.\PhysicalDrive{0}", diskNum);

            // Attempt with GENERIC_READ | GENERIC_WRITE; fall back to GENERIC_READ if write is restricted
            SafeFileHandle hDisk = CreateFile(
                diskPath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (hDisk.IsInvalid)
            {
                hDisk = CreateFile(
                    diskPath,
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
            }

            if (hDisk.IsInvalid)
            {
                // IOCTL_STORAGE_QUERY_PROPERTY is FILE_ANY_ACCESS: a handle with no read/write rights is enough,
                // and standard users may open one. This is how the raw NVMe health log is read without admin rights.
                hDisk = CreateFile(diskPath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            }

            if (hDisk.IsInvalid)
            {
                dev.RawHealth["raw_health_status"] = "UNKNOWN";
                dev.RawHealth["raw_health_reason"] = "DEVICE_NOT_OPENABLE (Win32 error " + Marshal.GetLastWin32Error() + ")";
                return false;
            }

            using (hDisk)
            {
                // Layout per the Windows SDK / Microsoft's NVMe sample: STORAGE_PROPERTY_QUERY.AdditionalParameters is at
                // offset 8, followed by STORAGE_PROTOCOL_SPECIFIC_DATA (40 bytes); the log page follows at 8 + 40 = 48.
                // (Earlier code used offset 12, which Windows rejects with ERROR_INVALID_PARAMETER.)
                const int AdditionalParameters = 8;
                int queryHeaderSize = AdditionalParameters + 40;
                int bufferSize = queryHeaderSize + 512;
                byte[] buffer = new byte[bufferSize];

                // STORAGE_PROPERTY_QUERY
                BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty).CopyTo(buffer, 0); // 50
                BitConverter.GetBytes(PropertyStandardQuery).CopyTo(buffer, 4); // 0

                // STORAGE_PROTOCOL_SPECIFIC_DATA (starts at offset 8)
                BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buffer, AdditionalParameters + 0);                       // ProtocolType = NVMe
                BitConverter.GetBytes(STORAGE_PROTOCOL_NVME_DATA_TYPE_LOG_PAGE).CopyTo(buffer, AdditionalParameters + 4); // DataType = log page
                BitConverter.GetBytes(NVME_LOG_PAGE_HEALTH_INFO).CopyTo(buffer, AdditionalParameters + 8);              // log page 02h
                BitConverter.GetBytes(0).CopyTo(buffer, AdditionalParameters + 12);                                      // sub value
                BitConverter.GetBytes(40).CopyTo(buffer, AdditionalParameters + 16);                                     // ProtocolDataOffset
                BitConverter.GetBytes(512).CopyTo(buffer, AdditionalParameters + 20);                                    // ProtocolDataLength

                GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                uint returned = 0;
                bool ok = false;
                int lastError = 0;
                try
                {
                    IntPtr pBuf = handle.AddrOfPinnedObject();
                    ok = DeviceIoControl(hDisk, IOCTL_STORAGE_QUERY_PROPERTY, pBuf, (uint)bufferSize, pBuf, (uint)bufferSize, out returned, IntPtr.Zero);
                    if (!ok) lastError = Marshal.GetLastWin32Error();

                    if (!ok)
                    {
                        // Fall back to StorageAdapterProtocolSpecificProperty (49)
                        BitConverter.GetBytes(StorageAdapterProtocolSpecificProperty).CopyTo(buffer, 0);
                        ok = DeviceIoControl(hDisk, IOCTL_STORAGE_QUERY_PROPERTY, pBuf, (uint)bufferSize, pBuf, (uint)bufferSize, out returned, IntPtr.Zero);
                    }
                }
                finally
                {
                    handle.Free();
                }

                if (!ok || returned < (queryHeaderSize + 64))
                {
                    // Say why the raw values are missing instead of leaving them silently absent.
                    dev.RawHealth["raw_health_status"] = "UNKNOWN";
                    dev.RawHealth["raw_health_reason"] = !ElevationHelper.IsAdministrator && lastError == 5
                        ? "ADMIN_REQUIRED (Windows denied access to the drive for this user)"
                        : lastError == 1 || lastError == 50
                        ? "NOT_SUPPORTED_BY_DRIVER (the storage driver does not pass the NVMe health log through; Win32 error " + lastError + ")"
                        : "NVME_HEALTH_LOG_NOT_RETURNED (Win32 error " + lastError + ")";
                    return false;
                }

                int offset = queryHeaderSize; // log page data
                byte crit = buffer[offset];
                ushort tempK = BitConverter.ToUInt16(buffer, offset + 1);
                byte spare = buffer[offset + 3];
                byte percentageUsed = buffer[offset + 5];

                ulong dataUnitsRead = BitConverter.ToUInt64(buffer, offset + 32);
                ulong dataUnitsWritten = BitConverter.ToUInt64(buffer, offset + 48);
                ulong powerCycles = BitConverter.ToUInt64(buffer, offset + 112);
                ulong powerOnHours = BitConverter.ToUInt64(buffer, offset + 128);
                ulong mediaErrors = BitConverter.ToUInt64(buffer, offset + 160);

                // Raw NVMe SMART / Health Information log (NVMe spec, log page 02h), kept exactly as reported.
                Func<int, string> u64 = o => returned >= offset + o + 8 ? BitConverter.ToUInt64(buffer, offset + o).ToString(CultureInfo.InvariantCulture) : "UNKNOWN";
                Func<int, string> u32 = o => returned >= offset + o + 4 ? BitConverter.ToUInt32(buffer, offset + o).ToString(CultureInfo.InvariantCulture) : "UNKNOWN";
                dev.RawHealth = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["critical_warning"] = crit.ToString(CultureInfo.InvariantCulture),
                    ["composite_temperature_k"] = tempK.ToString(CultureInfo.InvariantCulture),
                    ["available_spare_percent"] = spare.ToString(CultureInfo.InvariantCulture),
                    ["available_spare_threshold_percent"] = buffer[offset + 4].ToString(CultureInfo.InvariantCulture),
                    ["percentage_used"] = percentageUsed.ToString(CultureInfo.InvariantCulture),
                    ["data_units_read"] = dataUnitsRead.ToString(CultureInfo.InvariantCulture),
                    ["data_units_written"] = dataUnitsWritten.ToString(CultureInfo.InvariantCulture),
                    ["host_read_commands"] = u64(64),
                    ["host_write_commands"] = u64(80),
                    ["controller_busy_time_minutes"] = u64(96),
                    ["power_cycles"] = powerCycles.ToString(CultureInfo.InvariantCulture),
                    ["power_on_hours"] = powerOnHours.ToString(CultureInfo.InvariantCulture),
                    ["unsafe_shutdowns"] = u64(144),
                    ["media_and_data_integrity_errors"] = mediaErrors.ToString(CultureInfo.InvariantCulture),
                    ["error_information_log_entries"] = u64(176),
                    ["warning_temperature_time_minutes"] = u32(192),
                    ["critical_temperature_time_minutes"] = u32(196),
                };

                // Endurance calculation: 100 - % wear
                int healthPct = Math.Max(0, Math.Min(100, 100 - percentageUsed));

                dev.HealthPercent = healthPct;
                dev.WearPercentUsed = percentageUsed;
                dev.BusType = "NVMe";
                dev.MediaType = "SSD";

                if (tempK > 200 && tempK < 400)
                {
                    int tempC = tempK - 273;
                    dev.TemperatureC = tempC;
                    dev.Temperature = tempC + " °C";
                }

                if (powerOnHours > 0)
                {
                    dev.PowerOnHours = (long)powerOnHours;
                    dev.PowerOnTime = string.Format(CultureInfo.InvariantCulture, "{0:N0} hours", powerOnHours);
                }

                // Format host writes/reads (1 unit = 1000 sectors of 512 bytes = 512,000 bytes)
                string hostWrites = "";
                if (dataUnitsWritten > 0)
                {
                    double tb = (dataUnitsWritten * 512000.0) / 1e12;
                    hostWrites = tb >= 1.0 ? string.Format(CultureInfo.InvariantCulture, "{0:F1} TB written", tb) :
                                             string.Format(CultureInfo.InvariantCulture, "{0:F0} GB written", tb * 1000);
                }

                dev.HealthAssessment = EvaluateAssessment(healthPct, crit, mediaErrors);
                dev.HealthSource = "Windows Native NVMe Protocol IOCTL";
                dev.DiagnosticDetails = string.Format(
                    CultureInfo.InvariantCulture,
                    "NVMe SMART: {0}% health ({1}% endurance used). Temp: {2}. Power-on: {3}. {4}",
                    healthPct, percentageUsed, dev.Temperature, dev.PowerOnTime, hostWrites).Trim();

                return true;
            }
        }

        private static bool TryQueryAtaSmart(int diskNum, StorageDevice dev)
        {
            string diskPath = string.Format(CultureInfo.InvariantCulture, @"\\.\PhysicalDrive{0}", diskNum);

            SafeFileHandle hDisk = CreateFile(
                diskPath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (hDisk.IsInvalid)
            {
                hDisk = CreateFile(
                    diskPath,
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
            }

            if (hDisk.IsInvalid) return false;

            using (hDisk)
            {
                var sendCmd = new SENDCMDINPARAMS
                {
                    cBufferSize = 512,
                    bDriveNumber = (byte)diskNum,
                    irDriveRegs = new IDEREGS
                    {
                        bFeaturesReg = 0xD0, // SMART READ DATA
                        bSectorCountReg = 1,
                        bSectorNumberReg = 1,
                        bCylLowReg = 0x4F,
                        bCylHighReg = 0xC2,
                        bDriveHeadReg = 0xA0,
                        bCommandReg = 0xB0 // SMART
                    }
                };

                int inSize = Marshal.SizeOf(sendCmd);
                int outSize = Marshal.SizeOf(typeof(SENDCMDOUTPARAMS));

                IntPtr pIn = Marshal.AllocHGlobal(inSize);
                IntPtr pOut = Marshal.AllocHGlobal(outSize);
                try
                {
                    Marshal.StructureToPtr(sendCmd, pIn, false);
                    uint returned = 0;
                    bool ok = DeviceIoControl(hDisk, SMART_RCV_DRIVE_DATA, pIn, (uint)inSize, pOut, (uint)outSize, out returned, IntPtr.Zero);

                    if (ok && returned >= 512)
                    {
                        byte[] rawBuf = new byte[512];
                        Marshal.Copy(new IntPtr(pOut.ToInt64() + 16), rawBuf, 0, 512);
                        return ParseAtaSmart(dev, rawBuf);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pIn);
                    Marshal.FreeHGlobal(pOut);
                }
            }

            return false;
        }

        private static bool ParseAtaSmart(StorageDevice dev, byte[] data)
        {
            if (data == null || data.Length < 362) return false;

            int? remainingLife = null;
            int? temp = null;
            ulong? pOH = null;
            ulong? reallocated = null;      // null = the drive did not report it (never stored as 0)
            var named = new Dictionary<byte, string>
            {
                [0x05] = "reallocated_sector_count", [0x09] = "power_on_hours", [0x0C] = "power_cycle_count",
                [0xBB] = "reported_uncorrectable_errors", [0xC5] = "current_pending_sector_count", [0xC6] = "offline_uncorrectable_sector_count",
                [0xC7] = "udma_crc_error_count", [0xF1] = "total_lbas_written", [0xF2] = "total_lbas_read",
            };

            // 30 attributes of 12 bytes each, starting at offset 2
            for (int i = 0; i < 30; i++)
            {
                int offset = 2 + (i * 12);
                if (offset + 12 > data.Length) break;

                byte attrId = data[offset];
                if (attrId == 0) continue;

                int current = data[offset + 3];
                ulong raw = 0;
                for (int b = 0; b < 6; b++)
                {
                    raw |= ((ulong)data[offset + 5 + b]) << (b * 8);
                }

                // Every reported attribute is kept raw (the drive's own numbers are the source of truth).
                dev.RawHealth[string.Format(CultureInfo.InvariantCulture, "smart_{0:X2}_raw", attrId)] = raw.ToString(CultureInfo.InvariantCulture);
                dev.RawHealth[string.Format(CultureInfo.InvariantCulture, "smart_{0:X2}_normalized", attrId)] = current.ToString(CultureInfo.InvariantCulture);
                if (named.TryGetValue(attrId, out var label)) dev.RawHealth[label] = raw.ToString(CultureInfo.InvariantCulture);

                switch (attrId)
                {
                    case 0x05: // Reallocated Sectors
                        reallocated = raw;
                        break;
                    case 0x09: // Power-On Hours
                        pOH = raw;
                        break;
                    case 0xC2: // Temperature
                    case 0xBE:
                        byte t = data[offset + 5];
                        if (t > 0 && t < 100) temp = t;
                        break;
                    case 0xE7: // SSD Life Remaining
                    case 0xA9: // Lifetime Remaining
                    case 0xB4: // Remaining Spare
                    case 0xE9: // Media Wearout Indicator
                        if (current > 0 && current <= 100)
                            remainingLife = current;
                        break;
                }
            }

            if (temp.HasValue)
            {
                dev.TemperatureC = temp.Value;
                dev.Temperature = temp.Value + " °C";
            }

            if (pOH.HasValue && pOH.Value > 0)
            {
                dev.PowerOnHours = (long)pOH.Value;
                dev.PowerOnTime = string.Format(CultureInfo.InvariantCulture, "{0:N0} hours", pOH.Value);
            }

            // Raw ATA SMART values that were readable (others stay absent = UNKNOWN).
            dev.RawHealth["smart_readable"] = "true";
            if (pOH.HasValue) dev.RawHealth["power_on_hours"] = pOH.Value.ToString(CultureInfo.InvariantCulture);
            if (temp.HasValue) dev.RawHealth["temperature_c"] = temp.Value.ToString(CultureInfo.InvariantCulture);
            if (remainingLife.HasValue) dev.RawHealth["life_remaining_percent"] = remainingLife.Value.ToString(CultureInfo.InvariantCulture);

            if (remainingLife.HasValue)
            {
                int hp = remainingLife.Value;
                dev.HealthPercent = hp;
                dev.WearPercentUsed = 100 - hp;
                dev.HealthAssessment = EvaluateAssessment(hp, 0, reallocated ?? 0);
                dev.HealthSource = "Direct ATA SMART IOCTL";
                dev.DiagnosticDetails = string.Format(
                    CultureInfo.InvariantCulture,
                    "ATA SMART: {0}% health remaining. Temp: {1}. Power-on: {2}.",
                    hp, dev.Temperature, dev.PowerOnTime);
                return true;
            }

            return false;
        }

        private static string EvaluateAssessment(int healthPercent, byte criticalWarning, ulong mediaErrors)
        {
            if (criticalWarning != 0 || mediaErrors > 50 || healthPercent < 20) return "CRITICAL";
            if (healthPercent < 80 || mediaErrors > 0) return "WARNING";
            if (healthPercent >= 90) return "OPTIMAL";
            return "GOOD";
        }

        public static int ExtractDiskNumber(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return -1;
            var m = Regex.Match(deviceId, @"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int num)) return num;
            if (int.TryParse(deviceId.Trim(), out int directNum)) return directNum;
            return -1;
        }
    }
}
