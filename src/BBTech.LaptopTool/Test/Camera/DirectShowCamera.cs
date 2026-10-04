using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Test.Camera
{
    /// <summary>
    /// Live webcam preview using DirectShow (built into Windows, works with all UVC webcams).
    /// The video renderer is parented into a child window supplied by <see cref="CameraHost"/>.
    /// Must be used from the UI (STA) thread.
    /// </summary>
    public sealed class DirectShowCamera : IDisposable
    {
        private object _graph;
        private ICaptureGraphBuilder2 _builder;
        private IBaseFilter _source;
        private IVideoWindow _videoWindow;
        private IMediaControl _control;

        public bool IsRunning { get; private set; }

        public static List<string> ListDevices()
        {
            var names = new List<string>();
            foreach (var m in EnumerateMonikers())
            {
                names.Add(FriendlyName(m) ?? "Camera");
                Marshal.ReleaseComObject(m);
            }
            return names;
        }

        /// <summary>Returns null on success, otherwise a friendly error message.</summary>
        public string Start(int deviceIndex, IntPtr ownerHwnd, int width, int height)
        {
            Stop();
            try
            {
                IMoniker moniker = null;
                int i = 0;
                foreach (var m in EnumerateMonikers())
                {
                    if (i++ == deviceIndex && moniker == null) moniker = m;
                    else Marshal.ReleaseComObject(m);
                }
                if (moniker == null) return "No camera was found.";

                Guid iidBaseFilter = typeof(IBaseFilter).GUID;
                moniker.BindToObject(null, null, ref iidBaseFilter, out object srcObj);
                Marshal.ReleaseComObject(moniker);
                _source = (IBaseFilter)srcObj;

                _graph = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FilterGraph));
                _builder = (ICaptureGraphBuilder2)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_CaptureGraphBuilder2));
                var gb = (IGraphBuilder)_graph;

                Check(_builder.SetFiltergraph(gb), "SetFiltergraph");
                Check(gb.AddFilter(_source, "Camera"), "AddFilter");

                Guid preview = PIN_CATEGORY_PREVIEW, capture = PIN_CATEGORY_CAPTURE, video = MEDIATYPE_Video;
                int hr = _builder.RenderStream(ref preview, ref video, _source, null, null);
                if (hr < 0) hr = _builder.RenderStream(ref capture, ref video, _source, null, null);
                if (hr < 0) return "The camera could not be opened. It may be in use by another app, or blocked in Windows camera privacy settings.";

                _videoWindow = (IVideoWindow)_graph;
                _videoWindow.put_Owner(ownerHwnd);
                _videoWindow.put_WindowStyle(WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN);
                _videoWindow.SetWindowPosition(0, 0, Math.Max(1, width), Math.Max(1, height));
                _videoWindow.put_Visible(-1);

                _control = (IMediaControl)_graph;
                hr = _control.Run();
                if (hr < 0)
                {
                    Stop();
                    return "The camera could not be started. Close other apps using the camera and check Windows privacy settings.";
                }
                IsRunning = true;
                AppLog.Info("Camera preview started (device " + deviceIndex + ")");
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Error("Camera start failed", ex);
                Stop();
                return "The camera could not be started.";
            }
        }

        public void Resize(int width, int height)
        {
            try { _videoWindow?.SetWindowPosition(0, 0, Math.Max(1, width), Math.Max(1, height)); } catch { }
        }

        public void Stop()
        {
            IsRunning = false;
            try { _control?.Stop(); } catch { }
            try
            {
                if (_videoWindow != null)
                {
                    _videoWindow.put_Visible(0);
                    _videoWindow.put_Owner(IntPtr.Zero);
                }
            }
            catch { }
            Release(ref _source);
            Release(ref _builder);
            _videoWindow = null;
            _control = null;
            Release(ref _graph);
        }

        public void Dispose() => Stop();

        private static void Release<T>(ref T o) where T : class
        {
            if (o != null && Marshal.IsComObject(o)) { try { Marshal.ReleaseComObject(o); } catch { } }
            o = null;
        }

        private static void Check(int hr, string step)
        {
            if (hr < 0) throw new COMException("DirectShow " + step + " failed", hr);
        }

        private static IEnumerable<IMoniker> EnumerateMonikers()
        {
            var list = new List<IMoniker>();
            ICreateDevEnum devEnum = null;
            IEnumMoniker enumMon = null;
            try
            {
                devEnum = (ICreateDevEnum)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum));
                Guid cat = CLSID_VideoInputDeviceCategory;
                if (devEnum.CreateClassEnumerator(ref cat, out enumMon, 0) != 0 || enumMon == null) return list;
                var arr = new IMoniker[1];
                while (enumMon.Next(1, arr, IntPtr.Zero) == 0) list.Add(arr[0]);
            }
            catch (Exception ex) { AppLog.Warn("Camera enumeration failed", ex); }
            finally
            {
                if (enumMon != null) Marshal.ReleaseComObject(enumMon);
                if (devEnum != null) Marshal.ReleaseComObject(devEnum);
            }
            return list;
        }

        private static string FriendlyName(IMoniker m)
        {
            try
            {
                Guid iid = typeof(IPropertyBag).GUID;
                m.BindToStorage(null, null, ref iid, out object bagObj);
                var bag = (IPropertyBag)bagObj;
                try
                {
                    return bag.Read("FriendlyName", out object v, IntPtr.Zero) == 0 ? v as string : null;
                }
                finally { Marshal.ReleaseComObject(bag); }
            }
            catch { return null; }
        }

        // ---- DirectShow interop ----
        private static readonly Guid CLSID_SystemDeviceEnum = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        private static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        private static readonly Guid CLSID_FilterGraph = new Guid("E436EBB3-524F-11CE-9F53-0020AF0BA770");
        private static readonly Guid CLSID_CaptureGraphBuilder2 = new Guid("BF87B6E1-8C27-11d0-B3F0-00AA003761C5");
        private static readonly Guid PIN_CATEGORY_PREVIEW = new Guid("fb6c4282-0353-11d1-905f-0000c0cc16ba");
        private static readonly Guid PIN_CATEGORY_CAPTURE = new Guid("fb6c4281-0353-11d1-905f-0000c0cc16ba");
        private static readonly Guid MEDIATYPE_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        private const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;

        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum
        {
            [PreserveSig] int CreateClassEnumerator([In] ref Guid clsidDeviceClass, out IEnumMoniker enumMoniker, int flags);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag
        {
            [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string propName, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
            [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string propName, [MarshalAs(UnmanagedType.Struct)] ref object value);
        }

        // Only used as a typed pointer; methods are never called directly.
        [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IBaseFilter { }

        [ComImport, Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphBuilder
        {
            [PreserveSig] int AddFilter([In] IBaseFilter filter, [In, MarshalAs(UnmanagedType.LPWStr)] string name);
        }

        [ComImport, Guid("93E5A4E0-2D50-11d2-ABFA-00A0C9C6E38D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICaptureGraphBuilder2
        {
            [PreserveSig] int SetFiltergraph([In] IGraphBuilder graph);
            [PreserveSig] int GetFiltergraph(out IntPtr graph);
            [PreserveSig] int SetOutputFileName(IntPtr type, IntPtr file, out IntPtr filter, out IntPtr sink);
            [PreserveSig] int FindInterface(IntPtr category, IntPtr type, IntPtr filter, IntPtr riid, out IntPtr iface);
            [PreserveSig] int RenderStream([In] ref Guid category, [In] ref Guid type, [In, MarshalAs(UnmanagedType.IUnknown)] object source,
                                           [In] IBaseFilter compressor, [In] IBaseFilter renderer);
        }

        [ComImport, Guid("56a868b1-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IMediaControl
        {
            [PreserveSig] int Run();
            [PreserveSig] int Pause();
            [PreserveSig] int Stop();
        }

        [ComImport, Guid("56a868b4-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IVideoWindow
        {
            [PreserveSig] int put_Caption([MarshalAs(UnmanagedType.BStr)] string caption);
            [PreserveSig] int get_Caption([MarshalAs(UnmanagedType.BStr)] out string caption);
            [PreserveSig] int put_WindowStyle(int style);
            [PreserveSig] int get_WindowStyle(out int style);
            [PreserveSig] int put_WindowStyleEx(int style);
            [PreserveSig] int get_WindowStyleEx(out int style);
            [PreserveSig] int put_AutoShow(int show);
            [PreserveSig] int get_AutoShow(out int show);
            [PreserveSig] int put_WindowState(int state);
            [PreserveSig] int get_WindowState(out int state);
            [PreserveSig] int put_BackgroundPalette(int palette);
            [PreserveSig] int get_BackgroundPalette(out int palette);
            [PreserveSig] int put_Visible(int visible);
            [PreserveSig] int get_Visible(out int visible);
            [PreserveSig] int put_Left(int left);
            [PreserveSig] int get_Left(out int left);
            [PreserveSig] int put_Width(int width);
            [PreserveSig] int get_Width(out int width);
            [PreserveSig] int put_Top(int top);
            [PreserveSig] int get_Top(out int top);
            [PreserveSig] int put_Height(int height);
            [PreserveSig] int get_Height(out int height);
            [PreserveSig] int put_Owner(IntPtr owner);
            [PreserveSig] int get_Owner(out IntPtr owner);
            [PreserveSig] int put_MessageDrain(IntPtr drain);
            [PreserveSig] int get_MessageDrain(out IntPtr drain);
            [PreserveSig] int get_BorderColor(out int color);
            [PreserveSig] int put_BorderColor(int color);
            [PreserveSig] int get_FullScreenMode(out int mode);
            [PreserveSig] int put_FullScreenMode(int mode);
            [PreserveSig] int SetWindowForeground(int focus);
            [PreserveSig] int NotifyOwnerMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
            [PreserveSig] int SetWindowPosition(int left, int top, int width, int height);
        }
    }
}
