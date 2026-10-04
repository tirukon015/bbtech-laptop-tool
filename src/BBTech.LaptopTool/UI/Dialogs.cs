using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BBTech.Core.Settings;

namespace BBTech.LaptopTool.UI
{
    /// <summary>Small, consistently styled dialogs (code-built; mouse/touch friendly).</summary>
    public static class Dialogs
    {
        private static Brush Res(string k) => (Brush)Application.Current.FindResource(k);
        private static Style St(string k) => (Style)Application.Current.FindResource(k);

        private static Window Frame(Window owner, string title, double width)
        {
            var w = new Window
            {
                Title = title,
                Width = Math.Min(width, SystemParameters.WorkArea.Width * 0.95),
                SizeToContent = SizeToContent.Height,
                MaxHeight = SystemParameters.WorkArea.Height * 0.95,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI"),
                Background = Res("AppBackground"),
                Foreground = Res("TextPrimary"),
                Owner = owner,
            };
            return w;
        }

        private static TextBlock Text(string t, double size = 14, bool bold = false) =>
            new TextBlock { Text = t, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

        /// <summary>Message with up to three buttons; returns the index of the clicked button, or -1 if closed.</summary>
        public static int Choice(Window owner, string title, string heading, string message, params string[] buttons)
        {
            var w = Frame(owner, title, 520);
            int result = -1;
            var sp = new StackPanel { Margin = new Thickness(22) };
            if (!string.IsNullOrEmpty(heading)) sp.Children.Add(Text(heading, 18, true));
            sp.Children.Add(Text(message));
            var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = new Button { Content = buttons[i], Style = St(i == 0 ? "PrimaryButton" : "BaseButton"), Margin = new Thickness(8, 0, 0, 0), MinWidth = 100 };
                b.Click += (s, e) => { result = idx; w.Close(); };
                row.Children.Add(b);
            }
            sp.Children.Add(row);
            w.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            w.ShowDialog();
            return result;
        }

        public static void Info(Window owner, string title, string message) => Choice(owner, title, null, message, "OK");

        /// <summary>Asks for a numeric PIN. Returns null if cancelled.</summary>
        public static string Pin(Window owner, string title, string prompt, string error = null)
        {
            var w = Frame(owner, title, 380);
            string result = null;
            var sp = new StackPanel { Margin = new Thickness(22) };
            sp.Children.Add(Text(prompt, 15, true));
            if (!string.IsNullOrEmpty(error)) { var e = Text(error, 13); e.Foreground = Res("FailBrush"); sp.Children.Add(e); }
            var box = new PasswordBox { FontSize = 20, Padding = new Thickness(8, 6, 8, 6), MaxLength = 12 };
            box.PreviewTextInput += (s, e) => e.Handled = !int.TryParse(e.Text, out _);
            sp.Children.Add(box);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var ok = new Button { Content = "OK", Style = St("PrimaryButton"), MinWidth = 90, IsDefault = true };
            var cancel = new Button { Content = "Cancel", Style = St("BaseButton"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            ok.Click += (s, e) => { result = box.Password; w.Close(); };
            cancel.Click += (s, e) => w.Close();
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { result = box.Password; w.Close(); } };
            row.Children.Add(ok);
            row.Children.Add(cancel);
            sp.Children.Add(row);
            w.Content = sp;
            w.Loaded += (s, e) => box.Focus();
            w.ShowDialog();
            return result;
        }

        /// <summary>Add/edit operator. Returns (name, role) or null.</summary>
        public static Tuple<string, string> Operator(Window owner, string title, string name = "", string role = "QC")
        {
            var w = Frame(owner, title, 420);
            Tuple<string, string> result = null;
            var sp = new StackPanel { Margin = new Thickness(22) };
            sp.Children.Add(Text("Name", 13, true));
            var nameBox = new TextBox { Text = name, FontSize = 15, Padding = new Thickness(6, 4, 6, 4), MaxLength = 60 };
            sp.Children.Add(nameBox);
            sp.Children.Add(new Border { Height = 10 });
            sp.Children.Add(Text("Role", 13, true));
            var roleBox = new ComboBox { IsEditable = true, Text = role, FontSize = 15 };
            foreach (var r in new[] { "QC", "Senior QC", "E-commerce", "Technician" }) roleBox.Items.Add(r);
            sp.Children.Add(roleBox);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var ok = new Button { Content = "Save", Style = St("PrimaryButton"), MinWidth = 90 };
            var cancel = new Button { Content = "Cancel", Style = St("BaseButton"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            ok.Click += (s, e) => { result = Tuple.Create(nameBox.Text.Trim(), roleBox.Text.Trim()); w.Close(); };
            cancel.Click += (s, e) => w.Close();
            row.Children.Add(ok); row.Children.Add(cancel);
            sp.Children.Add(row);
            w.Content = sp;
            w.Loaded += (s, e) => nameBox.Focus();
            w.ShowDialog();
            return result;
        }

        /// <summary>
        /// "+ Add Tests": choose additional tests (the four required tests are always included and not listed).
        /// choices: (test, name, description, currently selected). Returns the chosen tests, or null if cancelled.
        /// </summary>
        public static System.Collections.Generic.HashSet<Core.Testing.HardwareTest> SelectTests(Window owner,
            System.Collections.Generic.IList<Tuple<Core.Testing.HardwareTest, string, string, bool>> choices)
        {
            var w = Frame(owner, "Add tests", 520);
            System.Collections.Generic.HashSet<Core.Testing.HardwareTest> result = null;
            var sp = new StackPanel { Margin = new Thickness(22) };
            sp.Children.Add(Text("Additional tests", 18, true));
            sp.Children.Add(Text("Display, Sound, Keyboard and Battery are always required. Tick the extra tests to run on this laptop."));

            var boxes = new System.Collections.Generic.List<Tuple<CheckBox, Core.Testing.HardwareTest>>();
            var list = new StackPanel();
            foreach (var c in choices)
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = c.Item2, FontSize = 15, FontWeight = FontWeights.SemiBold });
                if (!string.IsNullOrEmpty(c.Item3)) content.Children.Add(new TextBlock { Text = c.Item3, FontSize = 12, Foreground = Res("TextMuted"), TextWrapping = TextWrapping.Wrap });
                var cb = new CheckBox { Content = content, IsChecked = c.Item4, Margin = new Thickness(0, 4, 0, 6), VerticalContentAlignment = VerticalAlignment.Top };
                boxes.Add(Tuple.Create(cb, c.Item1));
                list.Children.Add(cb);
            }
            sp.Children.Add(new ScrollViewer { Content = list, MaxHeight = Math.Max(160, SystemParameters.WorkArea.Height * 0.55), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var quick = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var allBtn = new Button { Content = "Select all", Style = St("LinkButton") };
            var noneBtn = new Button { Content = "Clear", Style = St("LinkButton") };
            allBtn.Click += (s, e) => boxes.ForEach(b => b.Item1.IsChecked = true);
            noneBtn.Click += (s, e) => boxes.ForEach(b => b.Item1.IsChecked = false);
            quick.Children.Add(allBtn); quick.Children.Add(noneBtn);
            sp.Children.Add(quick);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = "Apply", Style = St("PrimaryButton"), MinWidth = 100 };
            var cancel = new Button { Content = "Cancel", Style = St("BaseButton"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            ok.Click += (s, e) =>
            {
                result = new System.Collections.Generic.HashSet<Core.Testing.HardwareTest>(boxes.Where(b => b.Item1.IsChecked == true).Select(b => b.Item2));
                w.Close();
            };
            cancel.Click += (s, e) => w.Close();
            row.Children.Add(ok); row.Children.Add(cancel);
            sp.Children.Add(row);
            w.Content = sp;
            w.ShowDialog();
            return result;
        }

        public sealed class NetworkInput
        {
            public string Ssid; public WifiSecurity Security; public string Password; public bool Hidden;
        }

        /// <summary>Add/edit a managed Wi-Fi network. The password is never shown unless the admin ticks "Show".</summary>
        public static NetworkInput Network(Window owner, string title, WifiNetwork existing)
        {
            var w = Frame(owner, title, 460);
            NetworkInput result = null;
            var sp = new StackPanel { Margin = new Thickness(22) };
            sp.Children.Add(Text("Network name (SSID)", 13, true));
            var ssid = new TextBox { Text = existing?.Ssid ?? "", FontSize = 15, Padding = new Thickness(6, 4, 6, 4), MaxLength = 32 };
            sp.Children.Add(ssid);
            sp.Children.Add(new Border { Height = 10 });
            sp.Children.Add(Text("Security", 13, true));
            var sec = new ComboBox { FontSize = 15 };
            sec.Items.Add("WPA2-Personal"); sec.Items.Add("WPA3-Personal"); sec.Items.Add("Open (no password)");
            sec.SelectedIndex = existing == null ? 0 : (int)existing.Security;
            sp.Children.Add(sec);
            sp.Children.Add(new Border { Height = 10 });
            sp.Children.Add(Text(existing == null ? "Password" : "Password (leave empty to keep the current one)", 13, true));
            var pw = new PasswordBox { FontSize = 15, Padding = new Thickness(6, 4, 6, 4), MaxLength = 64 };
            var pwPlain = new TextBox { FontSize = 15, Padding = new Thickness(6, 4, 6, 4), MaxLength = 64, Visibility = Visibility.Collapsed };
            sp.Children.Add(pw); sp.Children.Add(pwPlain);
            var show = new CheckBox { Content = "Show password while typing" };
            show.Checked += (s, e) => { pwPlain.Text = pw.Password; pw.Visibility = Visibility.Collapsed; pwPlain.Visibility = Visibility.Visible; };
            show.Unchecked += (s, e) => { pw.Password = pwPlain.Text; pwPlain.Visibility = Visibility.Collapsed; pw.Visibility = Visibility.Visible; pwPlain.Text = ""; };
            sp.Children.Add(show);
            var hidden = new CheckBox { Content = "Hidden network (does not broadcast its name)", IsChecked = existing?.Hidden == true };
            sp.Children.Add(hidden);
            var err = Text("", 13); err.Foreground = Res("FailBrush");
            sp.Children.Add(err);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "Save", Style = St("PrimaryButton"), MinWidth = 90 };
            var cancel = new Button { Content = "Cancel", Style = St("BaseButton"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            ok.Click += (s, e) =>
            {
                string password = show.IsChecked == true ? pwPlain.Text : pw.Password;
                var security = (WifiSecurity)Math.Max(0, sec.SelectedIndex);
                if (password.Length == 0 && existing != null && existing.Security == security) password = existing.Password; // keep
                string v = Core.Wifi.WlanProfileXml.Validate(ssid.Text, security, password);
                if (v != null) { err.Text = v; return; }
                result = new NetworkInput { Ssid = ssid.Text, Security = security, Password = password, Hidden = hidden.IsChecked == true };
                w.Close();
            };
            cancel.Click += (s, e) => w.Close();
            row.Children.Add(ok); row.Children.Add(cancel);
            sp.Children.Add(row);
            w.Content = sp;
            w.Loaded += (s, e) => ssid.Focus();
            w.ShowDialog();
            return result;
        }
    }
}
