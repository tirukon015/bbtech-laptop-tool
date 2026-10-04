using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Testing;

namespace BBTech.LaptopTool.UI.Tests
{
    /// <summary>Base class for the test panels shown inside <see cref="TestHostWindow"/>.</summary>
    public abstract class TestPanelBase : UserControl
    {
        protected TestHostWindow Host { get; private set; }
        private readonly List<CheckBox> _checklist = new List<CheckBox>();

        public abstract string Instructions { get; }
        public virtual bool PreferMaximized => false;
        public virtual bool AllowComment => true;

        /// <summary>Evidence recorded with the result (e.g. "84/84 keys").</summary>
        public virtual string Notes => ChecklistNotes();

        public virtual void Attach(TestHostWindow host) { Host = host; }
        public virtual void Detach() { }

        protected void Ui(Action a)
        {
            if (Dispatcher.CheckAccess()) a();
            else Dispatcher.BeginInvoke(a);
        }

        // ---- small UI builders (keeps panels compact and consistent) ----

        protected static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        protected static TextBlock T(string text, double size = 14, bool bold = false, string brush = "TextPrimary")
            => new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Res(brush), TextWrapping = TextWrapping.Wrap };

        protected static Button B(string text, RoutedEventHandler click, string style = "BaseButton")
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.FindResource(style), Margin = new Thickness(0, 0, 8, 8) };
            if (click != null) b.Click += click;
            return b;
        }

        protected static Border Card(string title, params UIElement[] children)
        {
            var sp = new StackPanel();
            if (!string.IsNullOrEmpty(title)) sp.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), Foreground = Res("TextPrimary") });
            foreach (var c in children) if (c != null) sp.Children.Add(c);
            return new Border { Style = (Style)Application.Current.FindResource("Card"), Child = sp, Margin = new Thickness(0, 0, 0, 12) };
        }

        protected static WrapPanel Row(params UIElement[] children)
        {
            var w = new WrapPanel();
            foreach (var c in children) w.Children.Add(c);
            return w;
        }

        protected static Grid KeyValue(IEnumerable<KeyValuePair<string, string>> rows)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int i = 0;
            foreach (var kv in rows.Where(r => !string.IsNullOrEmpty(r.Value)))
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var k = T(kv.Key, 13, false, "TextMuted"); k.Margin = new Thickness(0, 3, 0, 3);
                var v = T(kv.Value, 13); v.Margin = new Thickness(0, 3, 0, 3);
                Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
                g.Children.Add(k); g.Children.Add(v);
                i++;
            }
            return g;
        }

        protected static KeyValuePair<string, string> KV(string k, string v) => new KeyValuePair<string, string>(k, v);

        /// <summary>Operator checklist item; ticked items are included in the notes.</summary>
        protected CheckBox Check(string text)
        {
            var c = new CheckBox { Content = text, FontSize = 15, Margin = new Thickness(0, 4, 0, 4) };
            _checklist.Add(c);
            return c;
        }

        protected string ChecklistNotes()
        {
            if (_checklist.Count == 0) return "";
            var ok = _checklist.Where(c => c.IsChecked == true).Select(c => c.Content.ToString()).ToList();
            var missing = _checklist.Where(c => c.IsChecked != true).Select(c => c.Content.ToString()).ToList();
            string s = "Confirmed: " + (ok.Count > 0 ? string.Join(", ", ok) : "none");
            if (missing.Count > 0) s += "; not confirmed: " + string.Join(", ", missing);
            return s;
        }

        protected static string Join(params string[] parts) => string.Join("; ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    public static class TestPanelFactory
    {
        public static TestPanelBase Create(HardwareTest kind)
        {
            switch (kind)
            {
                case HardwareTest.Display: return new DisplayTestPanel();
                case HardwareTest.Keyboard: return new KeyboardTestPanel();
                case HardwareTest.Touchpad: return new TouchpadTestPanel();
                case HardwareTest.Speaker: return new AudioOutputTestPanel(headphones: false);
                case HardwareTest.Headphone: return new AudioOutputTestPanel(headphones: true);
                case HardwareTest.Microphone: return new MicrophoneTestPanel();
                case HardwareTest.Camera: return new CameraTestPanel();
                case HardwareTest.Usb: return new UsbTestPanel();
                case HardwareTest.WiFi: return new WifiTestPanel();
                case HardwareTest.Bluetooth: return new BluetoothTestPanel();
                case HardwareTest.Battery: return new BatteryTestPanel();
                case HardwareTest.Storage: return new StorageTestPanel();
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }
}
