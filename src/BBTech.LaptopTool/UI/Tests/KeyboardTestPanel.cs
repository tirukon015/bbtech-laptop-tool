using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace BBTech.LaptopTool.UI.Tests
{
    /// <summary>
    /// Keyboard test. Raw key messages are read from the window procedure so left/right
    /// Shift/Ctrl/Alt, Enter vs keypad Enter, Windows and Print Screen keys are told apart,
    /// and keys cannot trigger window shortcuts while testing.
    /// </summary>
    public sealed class KeyboardTestPanel : TestPanelBase
    {
        private sealed class KeyDef
        {
            public string Id; public string Label; public double Width; public bool Optional;
            public KeyDef(string id, string label, double width = 1, bool optional = false) { Id = id; Label = label; Width = width; Optional = optional; }
        }

        private const double Unit = 54;
        private readonly Dictionary<string, Border> _keys = new Dictionary<string, Border>();
        private readonly HashSet<string> _required = new HashSet<string>();
        private readonly HashSet<string> _tested = new HashSet<string>();
        private readonly HashSet<string> _down = new HashSet<string>();
        private readonly SortedSet<string> _extra = new SortedSet<string>();
        private readonly TextBlock _counter = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _extraText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _lastKey = new TextBlock { FontSize = 13 };
        private readonly StackPanel _numpad;
        private HwndSource _source;

        public override string Instructions =>
            "Press every key once. Tested keys turn green; a key held down shows blue. " +
            "Fn and brightness keys are handled by the laptop firmware and may not appear. Use the mouse for the buttons below.";
        public override bool PreferMaximized => true;
        public override bool AllowComment => false;

        public KeyboardTestPanel()
        {
            var main = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var row in MainRows()) main.Children.Add(BuildRow(row));

            var optional = new StackPanel { Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            optional.Children.Add(T("Extra keys (only if this keyboard has them - not required):", 12, false, "TextMuted"));
            optional.Children.Add(BuildRow(new[]
            {
                new KeyDef("VK2D", "Ins", 1, true), new KeyDef("VK24", "Home", 1, true), new KeyDef("VK23", "End", 1, true),
                new KeyDef("VK21", "PgUp", 1, true), new KeyDef("VK22", "PgDn", 1, true), new KeyDef("VK90", "NumLk", 1, true),
                new KeyDef("VK91", "ScrLk", 1, true), new KeyDef("VK13", "Pause", 1, true), new KeyDef("VK5C", "RWin", 1, true),
            }));

            _numpad = new StackPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
            _numpad.Children.Add(T("Numeric keypad:", 12, false, "TextMuted"));
            _numpad.Children.Add(BuildRow(new[] { O("NP_DIV", "/"), O("NP_MUL", "*"), O("NP_SUB", "-"), O("NP_ADD", "+"), O("NumEnter", "Enter", 1.5), O("NP_DEC", ".") }));
            _numpad.Children.Add(BuildRow(Enumerable.Range(0, 10).Select(i => O("NP" + i, i.ToString())).ToArray()));

            var showNumpad = new CheckBox { Content = "This laptop has a numeric keypad", Margin = new Thickness(0, 0, 16, 0) };
            showNumpad.Checked += (s, e) => { _numpad.Visibility = Visibility.Visible; SetNumpadRequired(true); };
            showNumpad.Unchecked += (s, e) => { _numpad.Visibility = Visibility.Collapsed; SetNumpadRequired(false); };

            var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
            toolbar.Children.Add(_counter);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(showNumpad);
            right.Children.Add(B("Reset keys", (s, e) => ResetKeys()));
            DockPanel.SetDock(right, Dock.Right);
            toolbar.Children.Add(right);

            var info = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            // Empty lines take no space (more room for the keyboard on small screens).
            _lastKey.Visibility = Visibility.Collapsed;
            _extraText.Visibility = Visibility.Collapsed;
            info.Children.Add(_lastKey);
            info.Children.Add(_extraText);

            // The keyboard scales to fit the available width AND height, so the whole layout is
            // always visible without scrolling (small screens, 125-175% scaling, large monitors).
            var layout = new DockPanel();
            DockPanel.SetDock(toolbar, Dock.Top);
            DockPanel.SetDock(info, Dock.Bottom);
            layout.Children.Add(toolbar);
            layout.Children.Add(info);
            // Grayscale text keeps scaled key labels crisp (no ClearType colour fringes).
            TextOptions.SetTextRenderingMode(layout, TextRenderingMode.Grayscale);
            layout.Children.Add(new Border
            {
                Style = (Style)Application.Current.FindResource("Card"),
                Padding = new Thickness(10),
                Child = new Viewbox { Stretch = Stretch.Uniform, MaxWidth = 1500, Child = new StackPanel { Children = { main, optional, _numpad } } },
            });
            Content = layout;
            UpdateCounter();
        }

        private static KeyDef K(string id, string label, double w = 1) => new KeyDef(id, label, w);
        private static KeyDef O(string id, string label, double w = 1) => new KeyDef(id, label, w, true);
        private static string Vk(int vk) => "VK" + vk.ToString("X2");

        private static IEnumerable<KeyDef[]> MainRows()
        {
            var r0 = new List<KeyDef> { K(Vk(0x1B), "Esc", 1.25) };
            for (int i = 1; i <= 12; i++) r0.Add(K(Vk(0x6F + i), "F" + i));
            r0.Add(K(Vk(0x2C), "PrtSc")); r0.Add(K(Vk(0x2E), "Del", 1.25));
            yield return r0.ToArray();

            var r1 = new List<KeyDef> { K(Vk(0xC0), "`") };
            foreach (char c in "1234567890") r1.Add(K(Vk(c), c.ToString()));
            r1.Add(K(Vk(0xBD), "-")); r1.Add(K(Vk(0xBB), "=")); r1.Add(K(Vk(0x08), "Backspace", 2));
            yield return r1.ToArray();

            var r2 = new List<KeyDef> { K(Vk(0x09), "Tab", 1.5) };
            foreach (char c in "QWERTYUIOP") r2.Add(K(Vk(c), c.ToString()));
            r2.Add(K(Vk(0xDB), "[")); r2.Add(K(Vk(0xDD), "]")); r2.Add(K(Vk(0xDC), "\\", 1.5));
            yield return r2.ToArray();

            var r3 = new List<KeyDef> { K(Vk(0x14), "Caps", 1.75) };
            foreach (char c in "ASDFGHJKL") r3.Add(K(Vk(c), c.ToString()));
            r3.Add(K(Vk(0xBA), ";")); r3.Add(K(Vk(0xDE), "'")); r3.Add(K("Enter", "Enter", 2.25));
            yield return r3.ToArray();

            var r4 = new List<KeyDef> { K("LShift", "Shift", 2.25) };
            foreach (char c in "ZXCVBNM") r4.Add(K(Vk(c), c.ToString()));
            r4.Add(K(Vk(0xBC), ",")); r4.Add(K(Vk(0xBE), ".")); r4.Add(K(Vk(0xBF), "/")); r4.Add(K("RShift", "Shift", 1.75)); r4.Add(K(Vk(0x26), "↑"));
            yield return r4.ToArray();

            yield return new[]
            {
                K("LCtrl", "Ctrl", 1.25), K(Vk(0x5B), "Win", 1.25), K("LAlt", "Alt", 1.25), K(Vk(0x20), "Space", 5.75),
                K("RAlt", "Alt"), K(Vk(0x5D), "Menu"), K("RCtrl", "Ctrl"), K(Vk(0x25), "←"), K(Vk(0x28), "↓"), K(Vk(0x27), "→"),
            };
        }

        private StackPanel BuildRow(IEnumerable<KeyDef> defs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            foreach (var d in defs)
            {
                var b = new Border
                {
                    Width = d.Width * Unit - 6,
                    Height = Unit - 6,
                    Margin = new Thickness(3, 0, 3, 0),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = d.Label, FontSize = d.Label.Length > 3 ? 12 : 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                _keys[d.Id] = b;
                if (!d.Optional) _required.Add(d.Id);
                Paint(d.Id);
                row.Children.Add(b);
            }
            return row;
        }

        private void SetNumpadRequired(bool on)
        {
            foreach (var id in _keys.Keys.Where(k => k.StartsWith("NP") || k == "NumEnter").ToList())
            {
                if (on) _required.Add(id); else _required.Remove(id);
            }
            UpdateCounter();
        }

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            _source = HwndSource.FromHwnd(new WindowInteropHelper(host).Handle);
            _source?.AddHook(WndProc);
            host.SetSuggestion("Waiting for key presses...", SuggestionKind.Info);
        }

        public override void Detach() => _source?.RemoveHook(WndProc);

        private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_KEYDOWN && msg != WM_KEYUP && msg != WM_SYSKEYDOWN && msg != WM_SYSKEYUP) return IntPtr.Zero;

            int vk = wParam.ToInt32();
            long lp = lParam.ToInt64();
            int scan = (int)((lp >> 16) & 0xFF);
            bool extended = ((lp >> 24) & 1) != 0;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            string id = KeyId(vk, scan, extended);

            if (down)
            {
                if (_down.Add(id)) OnPressed(id, vk);
            }
            else
            {
                _down.Remove(id);
                if (vk == 0x2C) OnPressed(id, vk); // Print Screen only reports key-up
                Paint(id);
            }
            handled = true; // swallow: no Alt menu, no Alt+F4, no Tab navigation while testing
            return IntPtr.Zero;
        }

        private static readonly Dictionary<int, string> NumpadByScan = new Dictionary<int, string>
        {
            { 0x47, "NP7" }, { 0x48, "NP8" }, { 0x49, "NP9" }, { 0x4B, "NP4" }, { 0x4C, "NP5" }, { 0x4D, "NP6" },
            { 0x4F, "NP1" }, { 0x50, "NP2" }, { 0x51, "NP3" }, { 0x52, "NP0" }, { 0x53, "NP_DEC" },
        };

        private static string KeyId(int vk, int scan, bool extended)
        {
            switch (vk)
            {
                case 0x10: return scan == 0x36 ? "RShift" : "LShift";
                case 0x11: return extended ? "RCtrl" : "LCtrl";
                case 0x12: return extended ? "RAlt" : "LAlt";
                case 0x0D: return extended ? "NumEnter" : "Enter";
                case 0x6A: return "NP_MUL";
                case 0x6B: return "NP_ADD";
                case 0x6D: return "NP_SUB";
                case 0x6E: return "NP_DEC";
                case 0x6F: return "NP_DIV";
            }
            if (vk >= 0x60 && vk <= 0x69) return "NP" + (vk - 0x60);
            // Navigation keys without the extended flag come from the keypad (NumLock off).
            if (!extended && NumpadByScan.TryGetValue(scan, out string np) && (vk == 0x0C || (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E)) return np;
            return Vk(vk);
        }

        private void OnPressed(string id, int vk)
        {
            _tested.Add(id);
            if (!_keys.ContainsKey(id)) _extra.Add(ExtraName(vk));
            Paint(id);
            _lastKey.Text = "Last key: " + (_keys.TryGetValue(id, out var b) ? ((TextBlock)b.Child).Text : ExtraName(vk));
            _extraText.Text = _extra.Count > 0 ? "Other keys detected: " + string.Join(", ", _extra) : "";
            _lastKey.Visibility = Visibility.Visible;
            _extraText.Visibility = _extra.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateCounter();
        }

        private static string ExtraName(int vk)
        {
            switch (vk)
            {
                case 0xAD: return "Mute"; case 0xAE: return "Volume down"; case 0xAF: return "Volume up";
                case 0xB0: return "Next track"; case 0xB1: return "Previous track"; case 0xB2: return "Stop"; case 0xB3: return "Play/Pause";
                case 0xA6: return "Browser back"; case 0xA7: return "Browser forward"; case 0xAC: return "Browser home";
                case 0xB4: return "Mail"; case 0xB7: return "Calculator"; case 0x0C: return "Clear";
                case 0x5F: return "Sleep";
                default: return "Key 0x" + vk.ToString("X2");
            }
        }

        private void Paint(string id)
        {
            if (!_keys.TryGetValue(id, out var b)) return;
            var tb = (TextBlock)b.Child;
            if (_down.Contains(id)) { b.Background = Res("Brand"); b.BorderBrush = Res("BrandDark"); tb.Foreground = Brushes.White; }
            else if (_tested.Contains(id)) { b.Background = Res("PassBrush"); b.BorderBrush = Res("PassBrush"); tb.Foreground = Brushes.White; }
            else { b.Background = Brushes.White; b.BorderBrush = Res("CardBorder"); tb.Foreground = Res("TextPrimary"); }
        }

        private void UpdateCounter()
        {
            int done = _required.Count(k => _tested.Contains(k));
            _counter.Text = "Tested " + done + " / " + _required.Count + " keys";
            if (Host == null) return;
            if (done == _required.Count) Host.SetSuggestion("All keys registered. Choose PASS if every key also felt normal.", SuggestionKind.Good);
            else if (done > 0) Host.SetSuggestion((_required.Count - done) + " key(s) not pressed yet.", SuggestionKind.Info);
        }

        private void ResetKeys()
        {
            _tested.Clear();
            _down.Clear();
            _extra.Clear();
            _extraText.Text = "";
            _lastKey.Text = "";
            _lastKey.Visibility = Visibility.Collapsed;
            _extraText.Visibility = Visibility.Collapsed;
            foreach (var id in _keys.Keys) Paint(id);
            UpdateCounter();
        }

        public override string Notes
        {
            get
            {
                int done = _required.Count(k => _tested.Contains(k));
                var missing = _required.Where(k => !_tested.Contains(k)).Select(k => ((TextBlock)_keys[k].Child).Text).Take(15).ToList();
                return "Keys " + done + "/" + _required.Count + (missing.Count > 0 ? "; not pressed: " + string.Join(" ", missing) : "");
            }
        }
    }
}
