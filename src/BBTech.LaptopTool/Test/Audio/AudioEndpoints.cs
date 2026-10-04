using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Test.Audio
{
    public sealed class AudioEndpoint
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int FormFactor { get; set; } = 10;
        public bool IsDefault { get; set; }

        public bool IsHeadphoneLike => FormFactor == 3 || FormFactor == 5; // Headphones, Headset

        public string FormFactorName
        {
            get
            {
                switch (FormFactor)
                {
                    case 1: return "Speakers"; case 2: return "Line level"; case 3: return "Headphones";
                    case 4: return "Microphone"; case 5: return "Headset"; case 6: return "Handset";
                    case 8: return "S/PDIF"; case 9: return "HDMI / DisplayPort";
                    default: return "Audio device";
                }
            }
        }
    }

    /// <summary>
    /// Lists active audio endpoints via the Windows Core Audio API (MMDevice), including the
    /// endpoint form factor (speakers / headphones), which is used to detect headphone plug-in.
    /// </summary>
    public static class AudioEndpoints
    {
        public static List<AudioEndpoint> List(bool capture)
        {
            var result = new List<AudioEndpoint>();
            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                int flow = capture ? 1 : 0;
                string defaultId = "";
                if (enumerator.GetDefaultAudioEndpoint(flow, 1 /* eMultimedia */, out IMMDevice def) == 0 && def != null)
                {
                    def.GetId(out defaultId);
                    Marshal.ReleaseComObject(def);
                }

                if (enumerator.EnumAudioEndpoints(flow, 1 /* DEVICE_STATE_ACTIVE */, out IMMDeviceCollection col) != 0 || col == null) return result;
                col.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (col.Item(i, out IMMDevice dev) != 0 || dev == null) continue;
                    try
                    {
                        dev.GetId(out string id);
                        var ep = new AudioEndpoint { Id = id ?? "", IsDefault = id == defaultId };
                        if (dev.OpenPropertyStore(0 /* STGM_READ */, out IPropertyStore store) == 0 && store != null)
                        {
                            ep.Name = ReadString(store, PKEY_Device_FriendlyName) ?? "";
                            ep.FormFactor = ReadUInt(store, PKEY_AudioEndpoint_FormFactor) ?? 10;
                            Marshal.ReleaseComObject(store);
                        }
                        result.Add(ep);
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
                Marshal.ReleaseComObject(col);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Audio endpoint enumeration failed", ex);
            }
            finally
            {
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
            return result;
        }

        private static string ReadString(IPropertyStore store, PROPERTYKEY key)
        {
            var pv = new PROPVARIANT();
            try
            {
                if (store.GetValue(ref key, out pv) != 0) return null;
                return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.pointerValue) : null;
            }
            finally { PropVariantClear(ref pv); }
        }

        private static int? ReadUInt(IPropertyStore store, PROPERTYKEY key)
        {
            var pv = new PROPVARIANT();
            try
            {
                if (store.GetValue(ref key, out pv) != 0) return null;
                return pv.vt == 19 /* VT_UI4 */ ? (int?)pv.intValue : null;
            }
            finally { PropVariantClear(ref pv); }
        }

        private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        private static readonly PROPERTYKEY PKEY_AudioEndpoint_FormFactor = new PROPERTYKEY(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid; public int pid;
            public PROPERTYKEY(Guid g, int p) { fmtid = g; pid = p; }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pointerValue;
            [FieldOffset(8)] public int intValue;
            [FieldOffset(16)] private IntPtr padding; // PROPVARIANT is 16 (x86) / 24 (x64) bytes
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pvar);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }
    }
}
