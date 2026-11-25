using System.Windows;
using System.Windows.Controls;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.UI.Views
{
    public partial class HomeView : UserControl
    {
        private readonly MainWindow _shell;

        public HomeView(MainWindow shell)
        {
            InitializeComponent();
            _shell = shell;
            VersionText.Text = "Version " + AppPaths.Version + "  ·  Works offline  ·  Nothing is installed" +
                               (AppPaths.UsingFallbackStorage ? "  ·  USB is read-only: files are saved on this PC" : "");
            SizeChanged += (s, e) =>
            {
                double size = e.NewSize.Height < 560 ? 56 : 68;
                HomeLogo.Width = HomeLogo.Height = size;
            };
        }

        private void StartTesting_Click(object sender, RoutedEventArgs e) => _shell.StartTestingWorkflow();
        private void Detect_Click(object sender, RoutedEventArgs e) => _shell.ShowDetect();
        private void Test_Click(object sender, RoutedEventArgs e) => _shell.ShowTest();
        private void Wifi_Click(object sender, RoutedEventArgs e) => _shell.ShowWifi();
        private void DeptSetup_Click(object sender, RoutedEventArgs e) => _shell.ShowUserProfileSetup(allowCancel: true);
        private void Data_Click(object sender, RoutedEventArgs e) => ProcessRunner.OpenShell(AppPaths.Ensure(AppPaths.DataDir));

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(this);
            SettingsWindow.ShowAuthorized(owner);
        }
        private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
