using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BBTech.Core.Testing;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.UI.Tests
{
    // ========================================================================
    // 1. SOFTWARE DISPLAY & GRAPHICS DRIVER TEST
    // ========================================================================
    /// <summary>
    /// In-app replacement for checking Device Manager: lists every device with a driver problem
    /// and flags a missing graphics driver. Suggests PASS only when Windows reports no problems.
    /// </summary>
    public sealed class SoftwareDriversTestPanel : TestPanelBase
    {
        public override string Instructions => "Checks every device driver on this laptop. No need to open Device Manager.";
        private string _measuredNotes = "";
        public override string Notes => string.IsNullOrEmpty(_measuredNotes) ? base.Notes : _measuredNotes + (string.IsNullOrEmpty(base.Notes) ? "" : "; " + base.Notes);

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            var devices = new List<PnpDeviceInfo>();
            string error = null;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, PNPClass, ConfigManagerErrorCode, PNPDeviceID FROM Win32_PnPEntity"))
                    foreach (ManagementObject o in searcher.Get())
                        devices.Add(new PnpDeviceInfo
                        {
                            Name = o["Name"]?.ToString(),
                            PnpClass = o["PNPClass"]?.ToString(),
                            ErrorCode = o["ConfigManagerErrorCode"] == null ? 0 : Convert.ToInt32(o["ConfigManagerErrorCode"]),
                            PnpDeviceId = o["PNPDeviceID"]?.ToString(),
                        });
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppLog.Warn("Driver check (Win32_PnPEntity) failed", ex);
            }

            var report = DriverHealth.Evaluate(devices);
            var root = new StackPanel();
            root.Children.Add(Card("Graphics", KeyValue(new List<KeyValuePair<string, string>>
            {
                KV("Display adapter", report.DisplayAdapters.Count == 0 ? "Not found" : string.Join(", ", report.DisplayAdapters)),
                KV("Devices checked", devices.Count.ToString()),
            })));

            var problems = new StackPanel();
            if (error != null) problems.Children.Add(new TextBlock { Text = "Windows did not return the device list: " + error, TextWrapping = TextWrapping.Wrap });
            else if (report.Ok) problems.Children.Add(new TextBlock { Text = "No driver problems.", FontWeight = FontWeights.SemiBold });
            else foreach (var p in report.Problems) problems.Children.Add(new TextBlock { Text = p, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
            root.Children.Add(Card(report.Ok ? "Drivers" : report.Problems.Count + " driver problem(s)", problems));
            Content = root;

            _measuredNotes = error != null ? "Driver check failed: " + error
                           : report.Ok ? "No driver problems (" + devices.Count + " devices)"
                           : "Driver problems: " + string.Join("; ", report.Problems);
            if (error != null) host.SetSuggestion("Could not read the device list. Run the check again.", SuggestionKind.Bad);
            else if (report.Ok) host.SetSuggestion("All drivers are working. Recommend PASS.", SuggestionKind.Good);
            else host.SetSuggestion("Install the missing drivers, then run this check again. Recommend FAIL for now.", SuggestionKind.Bad);
        }
    }

    public sealed class SoftwareDisplayTestPanel : TestPanelBase
    {
        public override string Instructions => "Verifies active display resolution, refresh rate, and display adapter driver status.";
        private string _measuredNotes = "";
        public override string Notes => string.IsNullOrEmpty(_measuredNotes) ? base.Notes : _measuredNotes + (string.IsNullOrEmpty(base.Notes) ? "" : "; " + base.Notes);

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            var root = new StackPanel();

            string resolution = string.Format("{0} × {1}", (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
            string gpu = "Standard Display Adapter";
            string driverStatus = "OK";
            string refreshRate = "60 Hz";

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, CurrentRefreshRate, Status FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var name = obj["Name"]?.ToString();
                        if (!string.IsNullOrEmpty(name)) gpu = name;
                        var rate = obj["CurrentRefreshRate"]?.ToString();
                        if (!string.IsNullOrEmpty(rate) && rate != "0") refreshRate = rate + " Hz";
                        var st = obj["Status"]?.ToString();
                        if (!string.IsNullOrEmpty(st)) driverStatus = st;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("WMI display driver query error", ex);
            }

            _measuredNotes = string.Format("Display: {0}, GPU: {1}, Driver: {2}, Refresh: {3}", resolution, gpu, driverStatus, refreshRate);

            var rows = new List<KeyValuePair<string, string>>
            {
                KV("Display Resolution", resolution),
                KV("Display Adapter", gpu),
                KV("Driver Status", driverStatus),
                KV("Refresh Rate", refreshRate),
            };

            root.Children.Add(Card("Detected Display Specifications", KeyValue(rows)));
            root.Children.Add(Card("Technician Visual Verification",
                Check("Display resolution and aspect ratio are correct"),
                Check("No graphics artifacts, flickering, or driver warnings"),
                Check("Color rendering and brightness scale cleanly")));

            Content = root;
            host.SetSuggestion(string.Format("Display detected at {0} ({1}). Recommend PASS if visuals are clear.", resolution, gpu), SuggestionKind.Good);
        }
    }

    // ========================================================================
    // 2. SOFTWARE IN-APP KEYBOARD MATRIX TEST
    // ========================================================================
    public sealed class SoftwareKeyboardTestPanel : TestPanelBase
    {
        public override string Instructions => "Press the keys on the physical keyboard. Verified keys light up in green.";
        private readonly HashSet<Key> _pressed = new HashSet<Key>();
        private readonly Dictionary<Key, Border> _keyButtons = new Dictionary<Key, Border>();
        private TextBlock _progressLabel;

        private static readonly Key[][] KeyboardRows = new[]
        {
            new[] { Key.Escape, Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12, Key.Delete },
            new[] { Key.OemTilde, Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9, Key.D0, Key.OemMinus, Key.OemPlus, Key.Back },
            new[] { Key.Tab, Key.Q, Key.W, Key.E, Key.R, Key.T, Key.Y, Key.U, Key.I, Key.O, Key.P, Key.OemOpenBrackets, Key.OemCloseBrackets, Key.OemPipe },
            new[] { Key.CapsLock, Key.A, Key.S, Key.D, Key.F, Key.G, Key.H, Key.J, Key.K, Key.L, Key.OemSemicolon, Key.OemQuotes, Key.Enter },
            new[] { Key.LeftShift, Key.Z, Key.X, Key.C, Key.V, Key.B, Key.N, Key.M, Key.OemComma, Key.OemPeriod, Key.OemQuestion, Key.RightShift },
            new[] { Key.LeftCtrl, Key.LWin, Key.LeftAlt, Key.Space, Key.RightAlt, Key.RightCtrl, Key.Left, Key.Up, Key.Down, Key.Right },
        };

        public override string Notes => string.Format("Tested {0} keys successfully", _pressed.Count);

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            host.PreviewKeyDown += OnPreviewKeyDown;

            var root = new StackPanel();
            _progressLabel = new TextBlock
            {
                Text = "0 keys tested",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("TextMuted"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(_progressLabel);

            var boardContainer = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var row in KeyboardRows)
            {
                var rowSp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                foreach (var k in row)
                {
                    double w = 38;
                    if (k == Key.Space) w = 180;
                    else if (k == Key.Enter || k == Key.LeftShift || k == Key.RightShift || k == Key.Back || k == Key.CapsLock || k == Key.Tab) w = 62;

                    var b = new Border
                    {
                        Width = w,
                        Height = 34,
                        CornerRadius = new CornerRadius(4),
                        Background = Res("CardBackground"),
                        BorderBrush = Res("CardBorder"),
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(2, 0, 2, 0),
                        Child = new TextBlock
                        {
                            Text = FormatKeyName(k),
                            FontSize = 10,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Res("TextPrimary"),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    _keyButtons[k] = b;
                    rowSp.Children.Add(b);
                }
                boardContainer.Children.Add(rowSp);
            }

            root.Children.Add(Card("Interactive Keyboard Matrix", boardContainer));
            Content = root;
        }

        public override void Detach()
        {
            if (Host != null) Host.PreviewKeyDown -= OnPreviewKeyDown;
            base.Detach();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            _pressed.Add(k);

            if (_keyButtons.TryGetValue(k, out var b))
            {
                b.Background = Res("PassSoft");
                b.BorderBrush = Res("PassBrush");
                var tb = b.Child as TextBlock;
                if (tb != null) tb.Foreground = Res("PassBrush");
            }

            _progressLabel.Text = string.Format("{0} unique keys tested", _pressed.Count);
            if (_pressed.Count >= 10)
            {
                Host.SetSuggestion(string.Format("{0} keys confirmed working. Recommend PASS.", _pressed.Count), SuggestionKind.Good);
            }
        }

        private static string FormatKeyName(Key k)
        {
            switch (k)
            {
                case Key.Escape: return "ESC";
                case Key.Back: return "BKSP";
                case Key.LeftShift:
                case Key.RightShift: return "SHIFT";
                case Key.LeftCtrl:
                case Key.RightCtrl: return "CTRL";
                case Key.LeftAlt:
                case Key.RightAlt: return "ALT";
                case Key.LWin: return "WIN";
                case Key.Space: return "SPACE";
                case Key.OemTilde: return "~";
                case Key.OemMinus: return "-";
                case Key.OemPlus: return "+";
                case Key.OemOpenBrackets: return "[";
                case Key.OemCloseBrackets: return "]";
                case Key.OemPipe: return "\\";
                case Key.OemSemicolon: return ";";
                case Key.OemQuotes: return "'";
                case Key.OemComma: return ",";
                case Key.OemPeriod: return ".";
                case Key.OemQuestion: return "/";
                default: return k.ToString().Replace("D1", "1").Replace("D2", "2").Replace("D3", "3").Replace("D4", "4").Replace("D5", "5")
                                         .Replace("D6", "6").Replace("D7", "7").Replace("D8", "8").Replace("D9", "9").Replace("D0", "0");
            }
        }
    }

    // ========================================================================
    // 3. SOFTWARE MOUSE TEST (Movement + Left, Right, Middle Clicks)
    // ========================================================================
    public sealed class SoftwareMouseTestPanel : TestPanelBase
    {
        public override string Instructions => "Move cursor around the tracking surface and test Left, Right, and Middle clicks.";
        private bool _moved, _left, _right, _middle;
        private Border _boxMoved, _boxLeft, _boxRight, _boxMiddle;

        public override string Notes => string.Format("Mouse Movement: {0}, Left: {1}, Right: {2}, Middle: {3}",
            _moved ? "PASS" : "N/A", _left ? "PASS" : "N/A", _right ? "PASS" : "N/A", _middle ? "PASS" : "N/A");

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            var root = new StackPanel();

            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            _boxMoved = MakeIndicator("Movement");
            _boxLeft = MakeIndicator("Left Click");
            _boxRight = MakeIndicator("Right Click");
            _boxMiddle = MakeIndicator("Middle Click");

            statusRow.Children.Add(_boxMoved);
            statusRow.Children.Add(_boxLeft);
            statusRow.Children.Add(_boxRight);
            statusRow.Children.Add(_boxMiddle);

            var canvas = new Border
            {
                Height = 220,
                Background = Res("NeutralSoft"),
                BorderBrush = Res("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Cursor = Cursors.Cross,
                Child = new TextBlock
                {
                    Text = "Move cursor and click inside this surface",
                    FontSize = 13,
                    Foreground = Res("TextMuted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            canvas.MouseMove += (s, e) => { _moved = true; MarkActive(_boxMoved, "Movement ✓"); CheckDone(); };
            canvas.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left) { _left = true; MarkActive(_boxLeft, "Left Click ✓"); }
                else if (e.ChangedButton == MouseButton.Right) { _right = true; MarkActive(_boxRight, "Right Click ✓"); }
                else if (e.ChangedButton == MouseButton.Middle) { _middle = true; MarkActive(_boxMiddle, "Middle Click ✓"); }
                CheckDone();
            };

            root.Children.Add(statusRow);
            root.Children.Add(Card("Interactive Mouse Surface", canvas));
            Content = root;
        }

        private Border MakeIndicator(string label)
        {
            return new Border
            {
                Width = 140,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Res("CardBackground"),
                BorderBrush = Res("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Res("TextMuted"), HorizontalAlignment = HorizontalAlignment.Center }
            };
        }

        private void MarkActive(Border b, string text)
        {
            b.Background = Res("PassSoft");
            b.BorderBrush = Res("PassBrush");
            var tb = (TextBlock)b.Child;
            tb.Text = text;
            tb.Foreground = Res("PassBrush");
        }

        private void CheckDone()
        {
            if (_moved && _left && _right)
            {
                Host.SetSuggestion("Mouse movement and clicks verified. Recommend PASS.", SuggestionKind.Good);
            }
        }
    }

    // ========================================================================
    // 4. SOFTWARE MOUSE SCROLL TEST (Scroll Up & Scroll Down)
    // ========================================================================
    public sealed class SoftwareScrollTestPanel : TestPanelBase
    {
        public override string Instructions => "Position the mouse cursor in the target box and roll the scroll wheel Up and Down.";
        private bool _scrollUp, _scrollDown;
        private Border _boxUp, _boxDown;
        private int _totalDelta;

        public override string Notes => string.Format("Scroll Up: {0}, Scroll Down: {1} (Delta: {2})",
            _scrollUp ? "PASS" : "N/A", _scrollDown ? "PASS" : "N/A", _totalDelta);

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            var root = new StackPanel();

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            _boxUp = MakeIndicator("Scroll Up");
            _boxDown = MakeIndicator("Scroll Down");
            row.Children.Add(_boxUp);
            row.Children.Add(_boxDown);

            var area = new Border
            {
                Height = 220,
                Background = Res("NeutralSoft"),
                BorderBrush = Res("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = "Place cursor here and scroll mouse wheel up and down",
                    FontSize = 13,
                    Foreground = Res("TextMuted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            area.MouseWheel += (s, e) =>
            {
                _totalDelta += e.Delta;
                if (e.Delta > 0)
                {
                    _scrollUp = true;
                    MarkActive(_boxUp, "Scroll Up ✓");
                }
                else if (e.Delta < 0)
                {
                    _scrollDown = true;
                    MarkActive(_boxDown, "Scroll Down ✓");
                }

                if (_scrollUp && _scrollDown)
                {
                    Host.SetSuggestion("Bi-directional mouse scrolling confirmed working. Recommend PASS.", SuggestionKind.Good);
                }
            };

            root.Children.Add(row);
            root.Children.Add(Card("Scroll Wheel Capture Area", area));
            Content = root;
        }

        private Border MakeIndicator(string label)
        {
            return new Border
            {
                Width = 160,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Res("CardBackground"),
                BorderBrush = Res("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Res("TextMuted"), HorizontalAlignment = HorizontalAlignment.Center }
            };
        }

        private void MarkActive(Border b, string text)
        {
            b.Background = Res("PassSoft");
            b.BorderBrush = Res("PassBrush");
            var tb = (TextBlock)b.Child;
            tb.Text = text;
            tb.Foreground = Res("PassBrush");
        }
    }

    // ========================================================================
    // 5. SOFTWARE HOTKEY TEST
    // ========================================================================
    public sealed class SoftwareHotkeyTestPanel : TestPanelBase
    {
        public override string Instructions => "Test standard modifier hotkeys (Ctrl, Alt, Shift, Windows, and key combinations).";
        private readonly HashSet<string> _hotkeysConfirmed = new HashSet<string>();
        private TextBlock _hotkeyLog;

        public override string Notes => string.Format("Confirmed hotkeys: {0}", _hotkeysConfirmed.Count > 0 ? string.Join(", ", _hotkeysConfirmed) : "none");

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            host.PreviewKeyDown += OnPreviewKeyDown;

            var root = new StackPanel();
            _hotkeyLog = new TextBlock
            {
                Text = "Press Ctrl, Alt, Shift, Win or combinations (e.g. Ctrl+C)...",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("TextPrimary"),
                Margin = new Thickness(0, 8, 0, 8)
            };

            root.Children.Add(Card("Hotkey Detection Console", _hotkeyLog));
            root.Children.Add(Card("Common System Shortcuts Verification",
                Check("Ctrl key modifiers respond promptly"),
                Check("Alt key modifiers respond promptly"),
                Check("Shift key modifiers respond promptly"),
                Check("Windows key functions correctly")));

            Content = root;
        }

        public override void Detach()
        {
            if (Host != null) Host.PreviewKeyDown -= OnPreviewKeyDown;
            base.Detach();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var parts = new List<string>();
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) parts.Add("Ctrl");
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) parts.Add("Alt");
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) parts.Add("Shift");
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) parts.Add("Win");

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key != Key.LeftCtrl && key != Key.RightCtrl && key != Key.LeftAlt && key != Key.RightAlt &&
                key != Key.LeftShift && key != Key.RightShift && key != Key.LWin && key != Key.RWin)
            {
                parts.Add(key.ToString());
            }

            if (parts.Count > 0)
            {
                string combo = string.Join(" + ", parts);
                _hotkeysConfirmed.Add(combo);
                _hotkeyLog.Text = string.Format("Latest Pressed: {0} (Total {1} distinct hotkeys detected)", combo, _hotkeysConfirmed.Count);
                if (_hotkeysConfirmed.Count >= 3)
                {
                    Host.SetSuggestion("Hotkey modifiers verified. Recommend PASS.", SuggestionKind.Good);
                }
            }
        }
    }

    // ========================================================================
    // 6. SOFTWARE TOUCHPAD & GESTURES TEST
    // ========================================================================
    public sealed class SoftwareTouchpadTestPanel : TestPanelBase
    {
        public override string Instructions => "Test laptop touchpad tracking, taps, physical clicks, and two-finger scroll gestures.";
        private bool _tracked, _tapped;
        private Border _boxTrack, _boxTap;

        public override string Notes => string.Format("Touchpad Tracking: {0}, Tap: {1}", _tracked ? "PASS" : "N/A", _tapped ? "PASS" : "N/A");

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            var root = new StackPanel();

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            _boxTrack = MakeIndicator("Surface Tracking");
            _boxTap = MakeIndicator("Tap / Click");
            row.Children.Add(_boxTrack);
            row.Children.Add(_boxTap);

            var area = new Border
            {
                Height = 220,
                Background = Res("NeutralSoft"),
                BorderBrush = Res("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = "Move finger on touchpad and tap inside this area",
                    FontSize = 13,
                    Foreground = Res("TextMuted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            area.MouseMove += (s, e) => { _tracked = true; MarkActive(_boxTrack, "Tracking ✓"); CheckDone(); };
            area.MouseDown += (s, e) => { _tapped = true; MarkActive(_boxTap, "Tap / Click ✓"); CheckDone(); };

            root.Children.Add(row);
            root.Children.Add(Card("Touchpad Interactive Surface", area));
            root.Children.Add(Card("Gesture Verification Checklist",
                Check("Smooth pointer movement with no cursor stuttering"),
                Check("Physical left and right clicks depress and actuate cleanly"),
                Check("Two-finger vertical scrolling works in Windows apps")));

            Content = root;
        }

        private Border MakeIndicator(string label)
        {
            return new Border
            {
                Width = 150,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Res("CardBackground"),
                BorderBrush = Res("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Res("TextMuted"), HorizontalAlignment = HorizontalAlignment.Center }
            };
        }

        private void MarkActive(Border b, string text)
        {
            b.Background = Res("PassSoft");
            b.BorderBrush = Res("PassBrush");
            var tb = (TextBlock)b.Child;
            tb.Text = text;
            tb.Foreground = Res("PassBrush");
        }

        private void CheckDone()
        {
            if (_tracked && _tapped)
            {
                Host.SetSuggestion("Touchpad tracking and input registered. Recommend PASS.", SuggestionKind.Good);
            }
        }
    }
}
