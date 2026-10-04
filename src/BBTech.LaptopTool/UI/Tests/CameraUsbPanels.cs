using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.Test.Camera;
using BBTech.LaptopTool.Test.Usb;

namespace BBTech.LaptopTool.UI.Tests
{
    /// <summary>Live camera preview (DirectShow) with an operator PASS/FAIL decision.</summary>
    public sealed class CameraTestPanel : TestPanelBase
    {
        private readonly DirectShowCamera _camera = new DirectShowCamera();
        private readonly CameraHost _host = new CameraHost();
        private readonly ComboBox _devices = new ComboBox { MinWidth = 280, FontSize = 14, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(8, 6, 8, 6), Focusable = false };
        private readonly TextBlock _status = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        private bool _previewShown;
        private string _deviceName = "";

        public override string Instructions => "The camera preview starts automatically. Check that the image is clear, in focus and in colour, and that the camera light turns on.";
        public override bool PreferMaximized => true;

        public CameraTestPanel()
        {
            var bar = Row(_devices, B("Start preview", (s, e) => StartPreview(), "PrimaryButton"), B("Stop", (s, e) => { _camera.Stop(); _status.Text = "Preview stopped."; }),
                          B("Open Windows Camera app", (s, e) => OpenCameraApp()));
            var right = new StackPanel { Width = 250, Margin = new Thickness(12, 0, 0, 0) };
            right.Children.Add(Card("Confirm", Check("Live image visible"), Check("Image is sharp and correctly coloured"), Check("Camera indicator light works (if present)")));

            var preview = new Border { Background = System.Windows.Media.Brushes.Black, CornerRadius = new CornerRadius(8), Child = _host, MinHeight = 140 };
            var dock = new DockPanel();
            var top = new StackPanel();
            top.Children.Add(bar);
            top.Children.Add(_status);
            DockPanel.SetDock(top, Dock.Top);
            var rightScroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            DockPanel.SetDock(rightScroll, Dock.Right);
            dock.Children.Add(top);
            dock.Children.Add(rightScroll);
            dock.Children.Add(preview);
            Content = dock;

            _host.PixelSizeChanged += (w, h) => _camera.Resize(w, h);
        }

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            var names = DirectShowCamera.ListDevices();
            _devices.ItemsSource = names;
            if (names.Count == 0)
            {
                _status.Text = "Camera not available on this laptop (none detected). It may also be disabled in Device Manager/BIOS.";
                _status.Foreground = Res("FailBrush");
                host.SetSuggestion("Camera not available on this laptop.", SuggestionKind.Bad);
                return;
            }
            // Prefer an integrated camera over IR cameras ("IR" in the name) when there are several.
            int idx = names.FindIndex(n => n.IndexOf("IR", StringComparison.Ordinal) < 0);
            _devices.SelectedIndex = idx >= 0 ? idx : 0;
            _status.Text = "Starting camera...";
            Dispatcher.BeginInvoke(new Action(StartPreview), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void StartPreview()
        {
            if (_devices.SelectedIndex < 0 || _host.ChildHandle == IntPtr.Zero) return;
            var size = _host.PixelSize;
            string err = _camera.Start(_devices.SelectedIndex, _host.ChildHandle, size.Item1, size.Item2);
            if (err != null)
            {
                _status.Text = err + " You can also try the Windows Camera app.";
                _status.Foreground = Res("FailBrush");
                Host?.SetSuggestion(err, SuggestionKind.Bad);
                return;
            }
            _previewShown = true;
            _deviceName = _devices.SelectedItem as string ?? "";
            _status.Text = "Preview running: " + _deviceName;
            _status.Foreground = Res("PassBrush");
            Host?.SetSuggestion("Camera opened. Confirm the picture, then choose PASS or FAIL.", SuggestionKind.Good);
        }

        private void OpenCameraApp()
        {
            _camera.Stop(); // release the camera for the other app
            if (!ProcessRunner.OpenShell("microsoft.windows.camera:"))
                _status.Text = "The Windows Camera app is not available on this PC.";
        }

        public override void Detach() => _camera.Dispose();

        public override string Notes => Join(_previewShown ? "Preview opened: " + _deviceName : "Preview not opened", ChecklistNotes());
    }

    /// <summary>
    /// USB port test: the operator plugs a device into each port in turn; every arrival is
    /// detected from Windows Plug and Play, so a port is only marked working on real evidence.
    /// </summary>
    public sealed class UsbTestPanel : TestPanelBase
    {
        private readonly UsbMonitor _monitor = new UsbMonitor();
        private readonly StackPanel _portList = new StackPanel();
        private readonly TextBlock _portCountText = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        private readonly List<string> _ports = new List<string>(); // detected device per port ("" = waiting)
        private readonly TextBlock _events = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        private bool _usbStickPortCounted;

        public override string Instructions =>
            "Set the number of USB ports on this laptop. Plug a USB device (pendrive, mouse...) into each port one by one; each arrival marks the next port as working. " +
            "Remove it before moving to the next port. USB-C ports need a USB-C device or adapter.";

        public UsbTestPanel()
        {
            for (int i = 0; i < 3; i++) _ports.Add("");
            if (AppPaths.IsRunningFromRemovableDrive)
            {
                _ports[0] = "BBTech USB drive (this tool is running from it)";
                _usbStickPortCounted = true;
            }

            var counter = Row(B("−", (s, e) => SetPorts(_ports.Count - 1)), _portCountText, B("+", (s, e) => SetPorts(_ports.Count + 1)), B("Clear results", (s, e) => ClearPorts()));
            var controllersText = T("Loading...", 13);
            var devicesText = T("Loading...", 13);
            // WMI queries off the UI thread so the test opens instantly.
            System.Threading.Tasks.Task.Run(() => Tuple.Create(string.Join("\n", UsbMonitor.Controllers()), string.Join("\n", UsbMonitor.ConnectedDevices())))
                .ContinueWith(t => Ui(() =>
                {
                    controllersText.Text = t.Result.Item1.Length > 0 ? t.Result.Item1 : "None reported";
                    devicesText.Text = t.Result.Item2.Length > 0 ? t.Result.Item2 : "None";
                }));

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Children =
                    {
                        Card("USB ports on this laptop", counter, new Border { Height = 6 }, _portList),
                        Card("Activity", _events),
                        Card("USB controllers (Windows)", controllersText),
                        Card("Currently connected USB devices", devicesText),
                    }
                }
            };
            RenderPorts();
        }

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            _monitor.DeviceArrived += name => Ui(() => OnArrived(name));
            _monitor.DeviceRemoved += () => Ui(() => _events.Text = DateTime.Now.ToString("HH:mm:ss") + "  Device removed. Plug it into the next port.\n" + _events.Text);
            _monitor.Start();
            _events.Text = "Watching for USB devices...";
            UpdateSuggestion();
        }

        private void OnArrived(string name)
        {
            int idx = _ports.FindIndex(p => p.Length == 0);
            _events.Text = DateTime.Now.ToString("HH:mm:ss") + "  Detected: " + name + (idx >= 0 ? "  →  Port " + (idx + 1) + " OK" : "") + "\n" + _events.Text;
            if (idx >= 0) _ports[idx] = name;
            AppLog.Info("USB arrival detected" + (idx >= 0 ? " (port " + (idx + 1) + ")" : ""));
            RenderPorts();
        }

        private void SetPorts(int n)
        {
            n = Math.Max(1, Math.Min(8, n));
            while (_ports.Count < n) _ports.Add("");
            while (_ports.Count > n) _ports.RemoveAt(_ports.Count - 1);
            RenderPorts();
        }

        private void ClearPorts()
        {
            for (int i = 0; i < _ports.Count; i++) _ports[i] = "";
            if (_usbStickPortCounted) _ports[0] = "BBTech USB drive (this tool is running from it)";
            RenderPorts();
        }

        private void RenderPorts()
        {
            _portCountText.Text = _ports.Count + " port" + (_ports.Count == 1 ? "" : "s");
            _portList.Children.Clear();
            for (int i = 0; i < _ports.Count; i++)
            {
                bool ok = _ports[i].Length > 0;
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 4), Width = 120, Background = Res(ok ? "PassSoft" : "NeutralSoft"),
                                         Child = T(ok ? "WORKING" : "WAITING", 13, true, ok ? "PassBrush" : "NeutralBrush") };
                ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
                DockPanel.SetDock(badge, Dock.Right);
                row.Children.Add(badge);
                row.Children.Add(T("Port " + (i + 1) + ":  " + (ok ? _ports[i] : "plug a device in..."), 15));
                _portList.Children.Add(row);
            }
            UpdateSuggestion();
        }

        private void UpdateSuggestion()
        {
            int ok = _ports.Count(p => p.Length > 0);
            if (Host == null) return;
            if (ok == _ports.Count) Host.SetSuggestion("A device was detected on every port.", SuggestionKind.Good);
            else Host.SetSuggestion(ok + " of " + _ports.Count + " ports verified.", SuggestionKind.Info);
        }

        public override void Detach() => _monitor.Dispose();

        public override string Notes => _ports.Count(p => p.Length > 0) + "/" + _ports.Count + " ports detected a device" + (_usbStickPortCounted ? " (incl. BBTech USB port)" : "");
    }
}


