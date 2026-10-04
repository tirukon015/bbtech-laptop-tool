using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.Test.Wireless
{
    public sealed class BluetoothScanResult
    {
        public bool RadioFound { get; set; }
        public string RadioName { get; set; } = "";
        public bool Connectable { get; set; }
        public bool Discoverable { get; set; }
        /// <summary>Devices seen during this scan (inquiry responses or currently connected).</summary>
        public int DevicesFound { get; set; }
        /// <summary>Names shown on screen only (never logged or exported).</summary>
        public List<string> DeviceNames { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    /// <summary>
    /// Bluetooth check via the Windows Bluetooth API (BluetoothApis.dll): finds the local
    /// radio, then performs a classic device inquiry (~10 s) to prove the radio works.
    /// </summary>
    public static class BluetoothScanner
    {
        public static BluetoothScanResult Scan()
        {
            var r = new BluetoothScanResult();
            IntPtr hFind = IntPtr.Zero, hRadio = IntPtr.Zero;
            try
            {
                var fp = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS)) };
                hFind = BluetoothFindFirstRadio(ref fp, out hRadio);
                if (hFind == IntPtr.Zero)
                {
                    bool adapterPresent = Wmi.Query("SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'").Any();
                    r.Error = adapterPresent
                        ? "Bluetooth adapter is present but turned off. Turn Bluetooth on in Windows Settings and scan again."
                        : "Bluetooth is not available on this laptop (no adapter found). It may also be disabled in Device Manager or BIOS.";
                    return r;
                }
                BluetoothFindRadioClose(hFind);
                hFind = IntPtr.Zero;

                r.RadioFound = true;
                var info = new BLUETOOTH_RADIO_INFO { dwSize = Marshal.SizeOf(typeof(BLUETOOTH_RADIO_INFO)) };
                if (BluetoothGetRadioInfo(hRadio, ref info) == 0) r.RadioName = info.szName;
                r.Connectable = BluetoothIsConnectable(hRadio);
                r.Discoverable = BluetoothIsDiscoverable(hRadio);

                var sp = new BLUETOOTH_DEVICE_SEARCH_PARAMS
                {
                    dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS)),
                    fReturnAuthenticated = 1, fReturnRemembered = 1, fReturnUnknown = 1, fReturnConnected = 1,
                    fIssueInquiry = 1, cTimeoutMultiplier = 8, // 8 x 1.28 s
                    hRadio = hRadio,
                };
                var di = new BLUETOOTH_DEVICE_INFO { dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO)) };
                DateTime scanStartUtc = DateTime.UtcNow.AddMinutes(-2), scanStartLocal = DateTime.Now.AddMinutes(-2);

                IntPtr hDev = BluetoothFindFirstDevice(ref sp, ref di);
                if (hDev != IntPtr.Zero)
                {
                    try
                    {
                        do
                        {
                            DateTime seen = di.stLastSeen.ToDateTime();
                            bool seenNow = di.fConnected != 0 || di.fRemembered == 0 || seen >= scanStartUtc || seen >= scanStartLocal;
                            if (seenNow)
                            {
                                r.DevicesFound++;
                                r.DeviceNames.Add(string.IsNullOrWhiteSpace(di.szName) ? "(unnamed device)" : di.szName);
                            }
                            di = new BLUETOOTH_DEVICE_INFO { dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO)) };
                        } while (BluetoothFindNextDevice(hDev, ref di));
                    }
                    finally { BluetoothFindDeviceClose(hDev); }
                }

                AppLog.Info("Bluetooth scan: radio ok, devices seen=" + r.DevicesFound);
                return r;
            }
            catch (DllNotFoundException)
            {
                r.Error = "Bluetooth support is not available on this Windows installation.";
                return r;
            }
            catch (EntryPointNotFoundException)
            {
                r.Error = "Bluetooth support is not available on this Windows installation.";
                return r;
            }
            catch (Exception ex)
            {
                AppLog.Error("Bluetooth scan failed", ex);
                r.Error = "Bluetooth check failed.";
                return r;
            }
            finally
            {
                if (hFind != IntPtr.Zero) BluetoothFindRadioClose(hFind);
                if (hRadio != IntPtr.Zero) CloseHandle(hRadio);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS { public int dwSize; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
            public DateTime ToDateTime()
            {
                try { return wYear < 1601 ? DateTime.MinValue : new DateTime(wYear, wMonth, wDay, wHour, wMinute, wSecond); }
                catch { return DateTime.MinValue; }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_RADIO_INFO
        {
            public int dwSize;
            public ulong address;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
            public uint ulClassofDevice;
            public ushort lmpSubversion;
            public ushort manufacturer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public int dwSize;
            public int fReturnAuthenticated;
            public int fReturnRemembered;
            public int fReturnUnknown;
            public int fReturnConnected;
            public int fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_DEVICE_INFO
        {
            public int dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public int fConnected;
            public int fRemembered;
            public int fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [DllImport("BluetoothApis.dll", SetLastError = true)] private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr phRadio);
        [DllImport("BluetoothApis.dll", SetLastError = true)] private static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport("BluetoothApis.dll", SetLastError = true)] private static extern int BluetoothGetRadioInfo(IntPtr hRadio, ref BLUETOOTH_RADIO_INFO info);
        [DllImport("BluetoothApis.dll", SetLastError = true)] private static extern bool BluetoothIsConnectable(IntPtr hRadio);
        [DllImport("BluetoothApis.dll", SetLastError = true)] private static extern bool BluetoothIsDiscoverable(IntPtr hRadio);
        [DllImport("BluetoothApis.dll", SetLastError = true)] private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS p, ref BLUETOOTH_DEVICE_INFO info);
        [DllImport("BluetoothApis.dll", SetLastError = true)] private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO info);
        [DllImport("BluetoothApis.dll", SetLastError = true)] private static extern bool BluetoothFindDeviceClose(IntPtr hFind);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    }
}

