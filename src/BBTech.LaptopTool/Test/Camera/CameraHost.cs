using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BBTech.LaptopTool.Test.Camera
{
    /// <summary>Hosts a native child window inside WPF for the DirectShow video renderer.</summary>
    public sealed class CameraHost : HwndHost
    {
        public IntPtr ChildHandle { get; private set; }
        public event Action<int, int> PixelSizeChanged;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            ChildHandle = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, 10, 10,
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return new HandleRef(this, ChildHandle);
        }

        protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            var px = PixelSize;
            PixelSizeChanged?.Invoke(px.Item1, px.Item2);
        }

        public Tuple<int, int> PixelSize
        {
            get
            {
                var src = PresentationSource.FromVisual(this);
                double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
                return Tuple.Create((int)(ActualWidth * sx), (int)(ActualHeight * sy));
            }
        }

        private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h,
                                                    IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);
    }
}
