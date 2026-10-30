using System;
using System.Windows;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.UI.Views;

namespace BBTech.LaptopTool.UI
{
    /// <summary>Shell window: header + one of Home / Detect / Test (views are created only when opened).</summary>
    public partial class MainWindow : Window
    {
        private DetectView _detect;
        private TestView _test;

        public MainWindow()
        {
            InitializeComponent();
            FitToScreen(this, 1180, 800);
            UsbText.Text = AppPaths.IsRunningFromRemovableDrive
                ? "Running from USB " + AppPaths.DriveLetter.TrimEnd('\\')
                : "Running from " + AppPaths.DriveLetter.TrimEnd('\\');

            if (ElevationHelper.IsAdministrator)
            {
                AdminBadge.Visibility = Visibility.Visible;
                RunAdminButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                AdminBadge.Visibility = Visibility.Collapsed;
                RunAdminButton.Visibility = Visibility.Visible;
            }

            SizeChanged += (s, e) => UsbChip.Visibility = ActualWidth < 960 ? Visibility.Collapsed : Visibility.Visible;
            ShowHome();
        }

        private void RunAdmin_Click(object sender, RoutedEventArgs e)
        {
            ElevationHelper.RestartAsAdministrator();
        }

        /// <summary>
        /// Sizes a window to the preferred size, but never larger than ~94% of the usable screen
        /// area (in DPI-independent units, so 125% / 150% / 175% scaling is handled). Small or
        /// highly scaled screens (e.g. 1280×720 at 150%) start maximised.
        /// </summary>
        public static void FitToScreen(Window w, double preferredWidth, double preferredHeight)
        {
            var area = SystemParameters.WorkArea;
            w.Width = Math.Max(w.MinWidth, Math.Min(preferredWidth, area.Width * 0.94));
            w.Height = Math.Max(w.MinHeight, Math.Min(preferredHeight, area.Height * 0.94));
            if (area.Width < preferredWidth * 0.85 || area.Height < preferredHeight * 0.75) w.WindowState = WindowState.Maximized;
        }

        public void ShowHome()
        {
            Host.Content = new HomeView(this);
            BackButton.Visibility = Visibility.Collapsed;
            SectionTitle.Text = "";
        }

        /// <summary>DETECT is independent of TEST; it only runs when chosen.</summary>
        public void ShowDetect()
        {
            bool first = _detect == null;
            if (first) _detect = new DetectView();
            Host.Content = _detect;
            BackButton.Visibility = Visibility.Visible;
            SectionTitle.Text = "·  DETECT";
            if (first) _detect.StartDetection();
        }

        public void ShowTest()
        {
            if (_test == null) _test = new TestView();
            Host.Content = _test;
            BackButton.Visibility = Visibility.Visible;
            SectionTitle.Text = "·  TEST";
        }

        public void ShowWifi()
        {
            Host.Content = new WifiView();
            BackButton.Visibility = Visibility.Visible;
            SectionTitle.Text = "·  Wi-Fi";
        }

        private void Back_Click(object sender, RoutedEventArgs e) => ShowHome();
    }
}
