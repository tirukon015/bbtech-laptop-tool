using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Settings;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.UI.Views
{
    /// <summary>Admin PIN check with a short lockout after repeated wrong attempts.</summary>
    public static class AdminGate
    {
        private static int _failures;
        private static DateTime _lockedUntil = DateTime.MinValue;

        public static bool Unlock(Window owner)
        {
            var store = SettingsService.Store;
            if (store.LoadFailed)
            {
                Dialogs.Info(owner, "Settings", "The BBTech settings file on this USB is damaged or was modified, so it cannot be used. Copy the \"BBTech Data\\config\" folder from another BBTech USB.");
                return false;
            }
            if (!store.HasPin)
            {
                string p1 = Dialogs.Pin(owner, "Create admin PIN", "Create a BBTech admin PIN (4–12 digits)");
                if (p1 == null) return false;
                if (!Core.Security.PinHasher.IsValidPin(p1)) { Dialogs.Info(owner, "Admin PIN", "The PIN must be 4 to 12 digits."); return false; }
                string p2 = Dialogs.Pin(owner, "Create admin PIN", "Enter the same PIN again");
                if (p2 != p1) { Dialogs.Info(owner, "Admin PIN", "The PINs did not match."); return false; }
                store.SetPin(p1);
                AppLog.Info("Admin PIN created");
                return true;
            }
            if (DateTime.Now < _lockedUntil)
            {
                Dialogs.Info(owner, "Admin PIN", "Too many wrong attempts. Try again in " + Math.Ceiling((_lockedUntil - DateTime.Now).TotalSeconds) + " seconds.");
                return false;
            }
            string error = null;
            while (true)
            {
                string pin = Dialogs.Pin(owner, "Admin PIN", "Enter the BBTech admin PIN", error);
                if (pin == null) return false;
                if (store.VerifyPin(pin)) { _failures = 0; return true; }
                _failures++;
                AppLog.Warn("Wrong admin PIN entered");
                if (_failures >= 5)
                {
                    _failures = 0;
                    _lockedUntil = DateTime.Now.AddSeconds(60);
                    Dialogs.Info(owner, "Admin PIN", "Too many wrong attempts. Settings are locked for 60 seconds.");
                    return false;
                }
                error = "Wrong PIN. Try again.";
            }
        }
    }

    /// <summary>Admin settings (PIN protected): QC operators, managed BBTech Wi-Fi networks, admin PIN.</summary>
    public sealed class SettingsWindow : Window
    {
        private readonly StackPanel _operators = new StackPanel();
        private readonly StackPanel _networks = new StackPanel();
        private SettingsStore S => SettingsService.Store;

        private static Brush Res(string k) => (Brush)Application.Current.FindResource(k);
        private static Style St(string k) => (Style)Application.Current.FindResource(k);

        public SettingsWindow()
        {
            Title = "Settings - BBTech Laptop Tool";
            FontFamily = new FontFamily("Segoe UI");
            Background = Res("AppBackground");
            Foreground = Res("TextPrimary");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            MinWidth = 600; MinHeight = 420;
            MainWindow.FitToScreen(this, 860, 660);

            var tabs = new TabControl { Margin = new Thickness(16), FontSize = 14 };
            tabs.Items.Add(new TabItem { Header = "QC operators", Content = Page(
                "People who can be selected as \"Tested By\". Deactivated operators are hidden for new inspections but stay in history. Renaming never changes past inspections.",
                Btn("Add operator", AddOperator, "PrimaryButton"), _operators) });
            tabs.Items.Add(new TabItem { Header = "BBTech Wi-Fi", Content = Page(
                "Networks installed on laptops by \"Provision BBTech Wi-Fi\" (automatic connection). Passwords are kept only in the encrypted settings on this USB and are never displayed. Removing a network here does not remove it from laptops.",
                Btn("Add network", AddNetwork, "PrimaryButton"), _networks) });
            tabs.Items.Add(new TabItem { Header = "Security", Content = Page(
                "The admin PIN protects these settings. It is stored only as a salted, slow hash in the encrypted settings file.\n\n" +
                "Settings file: " + SettingsService.FilePath + "\nCopy the whole \"BBTech Data\\config\" folder to other BBTech USBs to share operators and networks.\n\n" +
                "QC history of this laptop: stored encrypted on this laptop (" + QcStoreService.Folder + ").",
                Btn("Change admin PIN", ChangePin, "BaseButton"), new Border()) });
            tabs.Items.Add(new TabItem { Header = "ITMS", Content = ItmsPage() });
            Content = tabs;
            Refresh();
        }

        private static UIElement Page(string help, Button action, UIElement list)
        {
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextMuted"), Margin = new Thickness(0, 0, 0, 12) });
            sp.Children.Add(action);
            sp.Children.Add(new Border { Height = 10 });
            sp.Children.Add(list);
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private static Button Btn(string text, Action click, string style)
        {
            var b = new Button { Content = text, Style = St(style), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (s, e) => click();
            return b;
        }

        private void Refresh()
        {
            _operators.Children.Clear();
            foreach (var op in S.Settings.Operators.OrderByDescending(o => o.Active).ThenBy(o => o.Name))
            {
                var row = Row(op.Name + "  ·  " + op.Role + (op.Active ? "" : "  ·  DEACTIVATED") + (S.Settings.CurrentOperatorId == op.OperatorId ? "  ·  selected" : ""),
                    !op.Active);
                var id = op.OperatorId;
                row.Item2.Children.Add(Btn("Edit", () => EditOperator(id), "BaseButton"));
                row.Item2.Children.Add(Btn(op.Active ? "Deactivate" : "Reactivate", () => ToggleOperator(id), "BaseButton"));
                _operators.Children.Add(row.Item1);
            }
            if (S.Settings.Operators.Count == 0) _operators.Children.Add(new TextBlock { Text = "No operators yet.", Foreground = Res("TextMuted") });

            _networks.Children.Clear();
            foreach (var n in S.WifiNetworks)
            {
                string sec = n.Security == WifiSecurity.Open ? "Open" : n.Security == WifiSecurity.Wpa3Personal ? "WPA3" : "WPA2";
                var row = Row(n.Ssid + "  ·  " + sec + (n.Hidden ? "  ·  hidden" : "") + "  ·  password ••••••••", false);
                var id = n.NetworkId;
                row.Item2.Children.Add(Btn("Edit", () => EditNetwork(id), "BaseButton"));
                row.Item2.Children.Add(Btn("Remove", () => RemoveNetwork(id), "BaseButton"));
                _networks.Children.Add(row.Item1);
            }
            if (!S.WifiNetworks.Any()) _networks.Children.Add(new TextBlock { Text = "No BBTech networks yet.", Foreground = Res("TextMuted") });
        }

        private static Tuple<Border, StackPanel> Row(string text, bool muted)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var dock = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            dock.Children.Add(buttons);
            dock.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, Foreground = Res(muted ? "TextMuted" : "TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis });
            var card = new Border { Style = St("Card"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6), Child = dock };
            return Tuple.Create(card, buttons);
        }

        private void Try(Action a)
        {
            try { a(); Refresh(); }
            catch (ArgumentException ex) { Dialogs.Info(this, "Settings", ex.Message); }
            catch (Exception ex)
            {
                AppLog.Error("Settings change failed", ex);
                Dialogs.Info(this, "Settings", "The change could not be saved. Check that the USB drive is not write-protected or full.");
            }
        }

        private void AddOperator()
        {
            var r = Dialogs.Operator(this, "Add operator");
            if (r != null) Try(() => { S.AddOperator(r.Item1, r.Item2); AppLog.Info("Operator added"); });
        }

        private void EditOperator(string id)
        {
            var op = S.FindOperator(id);
            var r = Dialogs.Operator(this, "Edit operator", op.Name, op.Role);
            if (r != null) Try(() => S.UpdateOperator(id, r.Item1, r.Item2));
        }

        private void ToggleOperator(string id)
        {
            var op = S.FindOperator(id);
            Try(() => S.SetOperatorActive(id, !op.Active));
        }

        private void AddNetwork()
        {
            var r = Dialogs.Network(this, "Add BBTech network", null);
            if (r == null) return;
            WifiNetwork saved = null;
            Try(() => { saved = S.AddOrUpdateWifi(null, r.Ssid, r.Security, r.Password, r.Hidden); AppLog.Info("Managed Wi-Fi network added"); });
            if (saved != null) ProvisionHere(saved);
        }

        private void EditNetwork(string id)
        {
            var n = S.WifiNetworks.First(x => x.NetworkId == id);
            var r = Dialogs.Network(this, "Edit BBTech network", n);
            if (r == null) return;
            WifiNetwork saved = null;
            Try(() => { saved = S.AddOrUpdateWifi(id, r.Ssid, r.Security, r.Password, r.Hidden); AppLog.Info("Managed Wi-Fi network updated"); });
            if (saved != null) ProvisionHere(saved);
        }

        /// <summary>
        /// Installs the saved network on THIS laptop as a native Windows profile with "Connect automatically"
        /// and shows the result verified from Windows. Other laptops get it via Home → Wi-Fi → Provision.
        /// </summary>
        private async void ProvisionHere(WifiNetwork net)
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            try
            {
                await WifiView.EnsureStoreAsync();
                var res = await System.Threading.Tasks.Task.Run(() => WifiView.NewProvisioner().ProvisionAll(new[] { net }).Single());
                AppLog.Info("Wi-Fi provisioning after save: " + res.Outcome + (res.AutoConnectVerified ? " (auto-connect verified)" : ""));
                Dialogs.Info(this, "BBTech Wi-Fi",
                    (res.Outcome == Core.Wifi.ProvisionOutcome.Failed ? "Saved, but not installed on this laptop:\n" : "Saved and installed on this laptop.\n") +
                    net.Ssid + ": " + res.Message +
                    "\n\nOther laptops: Home → Wi-Fi → Provision BBTech Wi-Fi (once per laptop; afterwards Windows reconnects automatically).");
            }
            catch (Exception ex)
            {
                AppLog.Error("Provisioning after save failed", ex);
                Dialogs.Info(this, "BBTech Wi-Fi", "The network was saved, but could not be installed on this laptop right now. Use Home → Wi-Fi → Provision.");
            }
            finally { Cursor = null; }
        }

        private void RemoveNetwork(string id)
        {
            var n = S.WifiNetworks.First(x => x.NetworkId == id);
            if (Dialogs.Choice(this, "Remove network", "Remove " + n.Ssid + "?",
                    "It will no longer be installed on new laptops. Laptops that already have it keep their Windows Wi-Fi profile.",
                    "Remove", "Cancel") == 0)
                Try(() => { S.RemoveWifi(id); AppLog.Info("Managed Wi-Fi network removed"); });
        }

        private void ChangePin()
        {
            string p1 = Dialogs.Pin(this, "Change admin PIN", "New admin PIN (4–12 digits)");
            if (p1 == null) return;
            string p2 = Dialogs.Pin(this, "Change admin PIN", "Enter the new PIN again");
            if (p1 != p2) { Dialogs.Info(this, "Admin PIN", "The PINs did not match."); return; }
            Try(() => { S.SetPin(p1); AppLog.Info("Admin PIN changed"); Dialogs.Info(this, "Admin PIN", "The admin PIN was changed."); });
        }

        private UIElement ItmsPage()
        {
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock
            {
                Text = "Configure the Inventory Management System (ITMS) endpoint and API key. " +
                       "These settings are stored encrypted on this USB and enable one-click direct submission from DETECT.\n\n" +
                       "If not set here, the tool falls back to ITMS_URL and ITMS_API_KEY environment variables.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("TextMuted"),
                Margin = new Thickness(0, 0, 0, 16)
            });

            var urlLabel = new TextBlock { Text = "ITMS Server URL (e.g. https://itms.bbtech.com.my or http://localhost:8000)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            var urlBox = new TextBox { Text = S.Settings.ItmsBaseUrl ?? "", Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(6, 4, 6, 4), FontSize = 13 };

            var keyLabel = new TextBlock { Text = "ITMS API Key", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            var keyBox = new PasswordBox { Password = S.Settings.ItmsApiKey ?? "", Margin = new Thickness(0, 0, 0, 16), Padding = new Thickness(6, 4, 6, 4), FontSize = 13 };

            var statusText = new TextBlock
            {
                Text = S.IsItmsConfigured ? "Status: Configured (" + S.EffectiveItmsUrl + ")" : "Status: Not configured",
                Foreground = S.IsItmsConfigured ? Res("PassBrush") : Res("NeutralBrush"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 16)
            };

            var saveBtn = new Button { Content = "Save ITMS Configuration", Style = St("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left };
            saveBtn.Click += (s, e) =>
            {
                S.SetItmsConfig(urlBox.Text, keyBox.Password);
                statusText.Text = S.IsItmsConfigured ? "Status: Configured (" + S.EffectiveItmsUrl + ")" : "Status: Not configured";
                statusText.Foreground = S.IsItmsConfigured ? Res("PassBrush") : Res("NeutralBrush");
                MessageBox.Show("ITMS settings saved successfully to encrypted USB storage.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            };

            sp.Children.Add(urlLabel);
            sp.Children.Add(urlBox);
            sp.Children.Add(keyLabel);
            sp.Children.Add(keyBox);
            sp.Children.Add(statusText);
            sp.Children.Add(saveBtn);

            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
    }
}
