using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.UI.Views
{
    /// <summary>About box: logo, version, environment and third-party credits.</summary>
    public sealed class AboutWindow : Window
    {
        public AboutWindow()
        {
            Title = "About BBTech Laptop Tool";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            Background = (Brush)Application.Current.FindResource("AppBackground");
            Foreground = (Brush)Application.Current.FindResource("TextPrimary");

            var sp = new StackPanel { Margin = new Thickness(24) };
            sp.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Branding/logo-tile-256.png")),
                Width = 128, Height = 128, HorizontalAlignment = HorizontalAlignment.Center,
            });
            sp.Children.Add(Text("BBTech Laptop Tool", 22, FontWeights.SemiBold, 12));
            sp.Children.Add(Text("Version " + AppPaths.Version, 13, FontWeights.Normal, 2));
            sp.Children.Add(Text(App.OsName() + (Environment.Is64BitProcess ? " · 64-bit" : " · 32-bit"), 12, FontWeights.Normal, 2));
            sp.Children.Add(Text("Laptop inspection and hardware testing for BBTech. Runs from USB, works offline, installs nothing.", 13, FontWeights.Normal, 14));
            sp.Children.Add(Text("Includes BatteryInfoView (NirSoft), Hard Disk Sentinel (H.D.S. Hungary, licensed separately) and Free PC Audit (MIS Utilities).", 12, FontWeights.Normal, 10));
            var ok = new Button { Content = "Close", Style = (Style)Application.Current.FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0), MinWidth = 110 };
            ok.Click += (s, e) => Close();
            sp.Children.Add(ok);
            Content = sp;
        }

        private static TextBlock Text(string t, double size, FontWeight weight, double top) => new TextBlock
        {
            Text = t, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, top, 0, 0),
        };
    }
}
