using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Test.Wireless
{
    public sealed class WifiScanResult
    {
        public bool AdapterFound { get; set; }
        public string AdapterName { get; set; } = "";
        public string State { get; set; } = "";
        public bool Connected { get; set; }
        public bool? RadioOn { get; set; }
        public int NetworksFound { get; set; } = -1;
        public int BestSignalPercent { get; set; }
        /// <summary>Visible network names, shown on screen only (never logged or exported).</summary>
        public List<string> NetworkNames { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    /// <summary>
    /// Wi-Fi check via the Native Wifi API (wlanapi.dll): adapter presence, radio state,
    /// connection state, and an active scan proving the radio can receive nearby networks.
    /// </summary>
    public static class WifiScanner
    {
        public static WifiScanResult Scan()
        {
            var r = new WifiScanResult();
            IntPtr client = IntPtr.Zero, ifList = IntPtr.Zero;
            try
            {
                int rc = WlanOpenHandle(2, IntPtr.Zero, out _, out client);
                if (rc != 0)
                {
                    r.Error = rc == 1062
                        ? "Wi-Fi is not available on this laptop (no Wi-Fi adapter / WLAN service)."
                        : "Wi-Fi service is unavailable on this computer (code " + rc + ").";
                    return r;
                }

                rc = WlanEnumInterfaces(client, IntPtr.Zero, out ifList);
                int count = rc == 0 ? Marshal.ReadInt32(ifList, 0) : 0;
                if (count == 0) { r.Error = "Wi-Fi is not available on this laptop (no adapter found). It may also be disabled in Device Manager or BIOS."; return r; }

                // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] (512) + state (4) = 532 bytes, list header = 8 bytes.
                IntPtr item = ifList + 8;
                Guid ifGuid = (Guid)Marshal.PtrToStructure(item, typeof(Guid));
                r.AdapterFound = true;
                r.AdapterName = Marshal.PtrToStringUni(item + 16);
                int state = Marshal.ReadInt32(item, 16 + 512);
                r.State = StateName(state);
                r.Connected = state == 1;
                r.RadioOn = QueryRadio(client, ifGuid);

                if (r.RadioOn == false)
                {
                    r.Error = "Wi-Fi radio is switched off. Turn on Wi-Fi (or Airplane mode off / hardware switch) and scan again.";
                    return r;
                }

                WlanScan(client, ref ifGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(4500); // scans take ~4 s

                rc = WlanGetAvailableNetworkList(client, ref ifGuid, 0, IntPtr.Zero, out IntPtr netList);
                if (rc == 5)
                {
                    r.Error = "Windows blocked the network scan. Turn on Location services (Settings > Privacy > Location) and scan again.";
                    return r;
                }
                if (rc != 0) { r.Error = "Wi-Fi scan failed (code " + rc + ")."; return r; }

                try
                {
                    int n = Marshal.ReadInt32(netList, 0);
                    int size = Marshal.SizeOf(typeof(WLAN_AVAILABLE_NETWORK));
                    var names = new HashSet<string>();
                    int best = 0;
                    for (int i = 0; i < n; i++)
                    {
                        var net = (WLAN_AVAILABLE_NETWORK)Marshal.PtrToStructure(netList + 8 + i * size, typeof(WLAN_AVAILABLE_NETWORK));
                        int len = (int)Math.Min(net.dot11Ssid.uSSIDLength, 32);
                        string ssid = len > 0 ? Encoding.UTF8.GetString(net.dot11Ssid.ucSSID, 0, len) : "(hidden network)";
                        names.Add(ssid);
                        best = Math.Max(best, (int)net.wlanSignalQuality);
                    }
                    r.NetworksFound = names.Count;
                    r.BestSignalPercent = best;
                    r.NetworkNames = names.OrderBy(s => s).ToList();
                }
                finally { WlanFreeMemory(netList); }

                AppLog.Info("Wi-Fi scan: adapter ok, state=" + r.State + ", networks=" + r.NetworksFound + ", best signal=" + r.BestSignalPercent + "%");
                return r;
            }
            catch (DllNotFoundException)
            {
                r.Error = "Wi-Fi support (wlanapi.dll) is not available on this Windows installation.";
                return r;
            }
            catch (Exception ex)
            {
                AppLog.Error("Wi-Fi scan failed", ex);
                r.Error = "Wi-Fi check failed.";
                return r;
            }
            finally
            {
                if (ifList != IntPtr.Zero) WlanFreeMemory(ifList);
                if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
            }
        }

        private static bool? QueryRadio(IntPtr client, Guid ifGuid)
        {
            try
            {
                if (WlanQueryInterface(client, ref ifGuid, 4 /* radio_state */, IntPtr.Zero, out _, out IntPtr data, out _) != 0) return null;
                try
                {
                    int phys = Marshal.ReadInt32(data, 0);
                    if (phys == 0) return null;
                    bool anyOn = false;
                    for (int i = 0; i < phys; i++)
                    {
                        int sw = Marshal.ReadInt32(data, 4 + i * 12 + 4);
                        int hw = Marshal.ReadInt32(data, 4 + i * 12 + 8);
                        if (sw == 1 && hw == 1) anyOn = true;
                    }
                    return anyOn;
                }
                finally { WlanFreeMemory(data); }
            }
            catch { return null; }
        }

        private static string StateName(int s)
        {
            switch (s)
            {
                case 0: return "Not ready"; case 1: return "Connected"; case 2: return "Ad-hoc"; case 3: return "Disconnecting";
                case 4: return "Disconnected"; case 5: return "Associating"; case 6: return "Discovering"; case 7: return "Authenticating";
                default: return "Unknown";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_AVAILABLE_NETWORK
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
            public DOT11_SSID dot11Ssid;
            public int dot11BssType;
            public uint uNumberOfBssids;
            public int bNetworkConnectable;
            public uint wlanNotConnectableReason;
            public uint uNumberOfPhyTypes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] dot11PhyTypes;
            public int bMorePhyTypes;
            public uint wlanSignalQuality;
            public int bSecurityEnabled;
            public int dot11DefaultAuthAlgorithm;
            public int dot11DefaultCipherAlgorithm;
            public uint dwFlags;
            public uint dwReserved;
        }

        [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
        [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
        [DllImport("wlanapi.dll")] private static extern int WlanScan(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr ssid, IntPtr ieData, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern int WlanGetAvailableNetworkList(IntPtr clientHandle, ref Guid interfaceGuid, uint flags, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved, out int dataSize, out IntPtr data, out int opcodeValueType);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    }
}

