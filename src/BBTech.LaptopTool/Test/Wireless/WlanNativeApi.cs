using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BBTech.Core.Wifi;

namespace BBTech.LaptopTool.Test.Wireless
{
    /// <summary>
    /// IWlanApi over the Windows Native Wifi API (wlanapi.dll, Windows 10/11). Profiles are installed with
    /// WlanSetProfile, so Windows stores the key in its own protected profile store (DPAPI, SYSTEM-only).
    /// </summary>
    public sealed class WlanNativeApi : IWlanApi
    {
        private const int WLAN_PROFILE_USER = 2;

        private static T WithHandle<T>(Func<IntPtr, T> f, T onFail)
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr h) != 0) return onFail;
            try { return f(h); }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }

        public WlanInterface GetInterface() => WithHandle(h =>
        {
            if (WlanEnumInterfaces(h, IntPtr.Zero, out IntPtr list) != 0) return null;
            try
            {
                if (Marshal.ReadInt32(list, 0) == 0) return null;
                IntPtr item = list + 8;
                var guid = (Guid)Marshal.PtrToStructure(item, typeof(Guid));
                var wi = new WlanInterface { Id = guid, Description = Marshal.PtrToStringUni(item + 16) };
                wi.RadioOn = QueryRadio(h, guid) != false;
                return wi;
            }
            finally { WlanFreeMemory(list); }
        }, null);

        public string GetProfileXml(Guid iface, string name) => WithHandle(h =>
        {
            int flags = 0;
            int rc = WlanGetProfile(h, ref iface, name, IntPtr.Zero, out IntPtr xml, ref flags, out _);
            if (rc != 0) return null;
            try { return Marshal.PtrToStringUni(xml); }
            finally { WlanFreeMemory(xml); }
        }, null);

        public int SetProfile(Guid iface, string xml, bool allUsers, out string reason)
        {
            string r = "";
            int rc = WithHandle(h =>
            {
                int code = WlanSetProfile(h, ref iface, allUsers ? 0 : WLAN_PROFILE_USER, xml, null, true, IntPtr.Zero, out int reasonCode);
                if (reasonCode != 0)
                {
                    var sb = new StringBuilder(256);
                    if (WlanReasonCodeToString(reasonCode, sb.Capacity, sb, IntPtr.Zero) == 0) r = sb.ToString();
                }
                return code;
            }, 1062);
            reason = r;
            return rc;
        }

        public int Connect(Guid iface, string profileName) => WithHandle(h =>
        {
            var p = new WLAN_CONNECTION_PARAMETERS { wlanConnectionMode = 0, strProfile = profileName, dot11BssType = 1 };
            return WlanConnect(h, ref iface, ref p, IntPtr.Zero);
        }, 1062);

        public HashSet<string> VisibleSsids(Guid iface) => WithHandle(h =>
        {
            WlanScan(h, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(3500);
            if (WlanGetAvailableNetworkList(h, ref iface, 0, IntPtr.Zero, out IntPtr list) != 0) return null; // e.g. Location off on 24H2
            try
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                int n = Marshal.ReadInt32(list, 0);
                const int size = 628; // WLAN_AVAILABLE_NETWORK
                for (int i = 0; i < n; i++)
                {
                    IntPtr item = list + 8 + i * size;
                    int len = Math.Min(Marshal.ReadInt32(item, 512), 32);
                    var bytes = new byte[len];
                    Marshal.Copy(item + 516, bytes, 0, len);
                    if (len > 0) set.Add(Encoding.UTF8.GetString(bytes));
                }
                return set;
            }
            finally { WlanFreeMemory(list); }
        }, null);

        public string ConnectedProfile(Guid iface) => WithHandle(h =>
        {
            if (WlanQueryInterface(h, ref iface, 7 /* current_connection */, IntPtr.Zero, out _, out IntPtr data, out _) != 0) return null;
            try
            {
                int state = Marshal.ReadInt32(data, 0);
                return state == 1 ? Marshal.PtrToStringUni(data + 8) : null;
            }
            finally { WlanFreeMemory(data); }
        }, null);

        private static bool? QueryRadio(IntPtr h, Guid iface)
        {
            if (WlanQueryInterface(h, ref iface, 4, IntPtr.Zero, out _, out IntPtr data, out _) != 0) return null;
            try
            {
                int phys = Marshal.ReadInt32(data, 0);
                bool any = false;
                for (int i = 0; i < phys; i++)
                    if (Marshal.ReadInt32(data, 4 + i * 12 + 4) == 1 && Marshal.ReadInt32(data, 4 + i * 12 + 8) == 1) any = true;
                return phys == 0 ? (bool?)null : any;
            }
            finally { WlanFreeMemory(data); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_CONNECTION_PARAMETERS
        {
            public int wlanConnectionMode;
            [MarshalAs(UnmanagedType.LPWStr)] public string strProfile;
            public IntPtr pDot11Ssid;
            public IntPtr pDesiredBssidList;
            public int dot11BssType;
            public int dwFlags;
        }

        [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern int WlanGetProfile(IntPtr handle, ref Guid iface, string name, IntPtr reserved, out IntPtr xml, ref int flags, out int access);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern int WlanSetProfile(IntPtr handle, ref Guid iface, int flags, string xml, string allUserSecurity, bool overwrite, IntPtr reserved, out int reasonCode);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern int WlanReasonCodeToString(int reason, int bufSize, StringBuilder buf, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern int WlanConnect(IntPtr handle, ref Guid iface, ref WLAN_CONNECTION_PARAMETERS p, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern int WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern int WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode, IntPtr reserved, out int size, out IntPtr data, out int type);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    }
}
