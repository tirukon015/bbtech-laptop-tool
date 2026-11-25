using System;
using System.Windows;
using System.Windows.Controls;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.UI.Views;

namespace BBTech.LaptopTool.UI
{
    /// <summary>Shell window: header + one of Home / Detect / Test (views are created only when opened).</summary>
    public partial class MainWindow : Window
    {
        private DetectView _detect;

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

            var store = SettingsService.Store;
            store.EnsureDefaultTeamProfiles();

            // Validate and restore persistent operator session (1 hour duration)
            var activeOp = store.GetActiveSessionOperator();
            if (activeOp != null)
            {
                if (string.IsNullOrEmpty(store.CurrentDepartment) || !activeOp.HasDepartment(store.CurrentDepartment))
                {
                    store.SelectDepartment(activeOp.Departments != null && activeOp.Departments.Count > 0 ? activeOp.Departments[0] : "Hardware");
                }
            }
            else
            {
                store.ResetSession();
            }

            UpdateUserDeptChip();

            // Open to the simple, clean start screen
            ShowHome();
        }

        public void StartTestingWorkflow()
        {
            var store = SettingsService.Store;
            var activeOp = store.GetActiveSessionOperator();
            if (activeOp != null)
            {
                // Active session restored: route directly to assigned dashboard without prompting
                RouteSession(activeOp, store.CurrentDepartment);
            }
            else
            {
                // Session expired or none: open operator selection ("Select your name")
                ShowUserProfileSetup(allowCancel: true);
            }
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

        public void UpdateUserDeptChip()
        {
            var store = SettingsService.Store;
            string op = store.CurrentOperator?.Name;
            string dept = store.CurrentDepartment;

            if (!string.IsNullOrEmpty(op) && !string.IsNullOrEmpty(dept))
            {
                UserDeptText.Text = op + " • " + dept;
                UserDeptChip.ToolTip = "Active Operator Session: " + op + " (" + dept + ")";
                UserDeptChip.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrEmpty(op))
            {
                UserDeptText.Text = op;
                UserDeptChip.ToolTip = "Active Operator: " + op;
                UserDeptChip.Visibility = Visibility.Visible;
            }
            else
            {
                UserDeptChip.Visibility = Visibility.Collapsed;
            }
        }

        public void UpdateSidebar()
        {
            if (Host.Content is HomeView || Host.Content is UserProfileSelectView || Host.Content == null)
            {
                SidebarBorder.Visibility = Visibility.Collapsed;
                SidebarCol.Width = new GridLength(0);
                return;
            }

            SidebarBorder.Visibility = Visibility.Visible;
            SidebarCol.Width = new GridLength(200);

            var op = SettingsService.Store.CurrentOperator;
            NavHardwareBtn.Visibility = (op != null && op.CanAccessHardware) ? Visibility.Visible : Visibility.Collapsed;
            NavQcBtn.Visibility = (op != null && op.CanAccessQc) ? Visibility.Visible : Visibility.Collapsed;
            NavSoftwareBtn.Visibility = (op != null && op.CanAccessSoftware) ? Visibility.Visible : Visibility.Collapsed;
            NavEcommerceBtn.Visibility = (op != null && op.CanAccessEcommerce) ? Visibility.Visible : Visibility.Collapsed;

            // Highlight active navigation tab
            HighlightNavButton(NavHardwareBtn, Host.Content is HardwareDashboardView);
            HighlightNavButton(NavQcBtn, Host.Content is QcDashboardView);
            HighlightNavButton(NavSoftwareBtn, Host.Content is SoftwareDashboardView);
            HighlightNavButton(NavEcommerceBtn, Host.Content is EcommerceDashboardView);
        }

        private void HighlightNavButton(Button btn, bool isActive)
        {
            if (isActive)
            {
                btn.Background = (System.Windows.Media.Brush)FindResource("NeutralSoft");
                btn.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary");
                btn.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                btn.Background = System.Windows.Media.Brushes.Transparent;
                btn.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
                btn.FontWeight = FontWeights.Normal;
            }
        }

        private void NavHardware_Click(object sender, RoutedEventArgs e) => ShowHardwareDashboard();
        private void NavQc_Click(object sender, RoutedEventArgs e) => ShowQcDashboard();
        private void NavSoftware_Click(object sender, RoutedEventArgs e) => ShowSoftwareDashboard();
        private void NavEcommerce_Click(object sender, RoutedEventArgs e) => ShowEcommerceDashboard();
        private void NavHome_Click(object sender, RoutedEventArgs e) => ShowHome();

        private void NavSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow.ShowAuthorized(this);
            UpdateUserDeptChip();
            UpdateSidebar();
        }

        public void RouteSession(BBTech.Core.Settings.OperatorRecord op, string preferredDept)
        {
            if (op == null)
            {
                ShowUserProfileSetup(allowCancel: false);
                return;
            }

            if (!string.IsNullOrEmpty(preferredDept) && op.HasDepartment(preferredDept))
            {
                ApplyDepartment(preferredDept);
                return;
            }

            var depts = op.Departments != null && op.Departments.Count > 0
                ? op.Departments.FindAll(d => !string.Equals(d, "General", StringComparison.OrdinalIgnoreCase))
                : new System.Collections.Generic.List<string> { "Hardware" };

            if (depts.Count == 1)
            {
                SettingsService.Store.SelectDepartment(depts[0]);
                ApplyDepartment(depts[0]);
            }
            else if (depts.Count > 1)
            {
                ShowUserProfileSetup(allowCancel: false);
            }
            else
            {
                ShowHardwareDashboard();
            }
        }

        public void ApplyDepartment(string dept)
        {
            UpdateUserDeptChip();
            if (string.Equals(dept, "QC", StringComparison.OrdinalIgnoreCase))
            {
                ShowQcDashboard();
            }
            else if (string.Equals(dept, "E-Commerce", StringComparison.OrdinalIgnoreCase))
            {
                ShowEcommerceDashboard();
            }
            else if (string.Equals(dept, "Software", StringComparison.OrdinalIgnoreCase))
            {
                ShowSoftwareDashboard();
            }
            else
            {
                ShowHardwareDashboard();
            }
        }

        public void ShowHardwareDashboard()
        {
            var op = SettingsService.Store.CurrentOperator;
            if (op == null) { ShowUserProfileSetup(allowCancel: false); return; }
            if (!op.CanAccessHardware)
            {
                MessageBox.Show("Access Denied: Your profile (" + op.Name + ") is not authorized for the Hardware department.",
                                "BBTech Laptop Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                RouteSession(op, null);
                return;
            }

            Host.Content = new HardwareDashboardView(this);
            BackButton.Visibility = Visibility.Visible;
            SectionTitle.Text = "·  Hardware";
            UpdateUserDeptChip();
            UpdateSidebar();
        }

        public void ShowQcDashboard()
        {
            var op = SettingsService.Store.CurrentOperator;
            if (op == null) { ShowUserProfileSetup(allowCancel: false); return; }
            if (!op.CanAccessQc)
            {
                MessageBox.Show("Access Denied: Your profile (" + op.Name + ") is not authorized for the QC department.",
                                "BBTech Laptop Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                RouteSession(op, null);
                return;
            }

            Host.Content = new QcDashboardView(this);
            BackButton.Visibility = Visibility.Visible;
            SectionTitle.Text = "·  QC";
            UpdateUserDeptChip();
            UpdateSidebar();
        }

        public void ShowEcommerceDashboard()
        {
            var op = SettingsService.Store.CurrentOperator;
            if (op == null) { ShowUserProfileSetup(allowCancel: false); return; }
            if (!op.CanAccessEcommerce)
            {
                MessageBox.Show("Access Denied: Your profile (" + op.Name + ") is not authorized for E-Commerce intake.",
                                "BBTech Laptop Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                RouteSession(op, null);
                return;
            }

            Host.Content = new EcommerceDashboardView(this);
            BackButton.Visibility = Visibility.Visible;
            SectionTitle.Text = "·  E-Commerce";
            UpdateUserDeptChip();
            UpdateSidebar();
        }

        public void ShowSoftwareDashboard()
        {
            var op = SettingsService.Store.CurrentOperator;
            if (op == null) { ShowUserProfileSetup(allowCancel: false); return; }
            if (!op.CanAccessSoftware)
            {
                MessageBox.Show("Access Denied: Your profile (" + op.Name + ") is not authorized for Software tools.",
                                "BBTech Laptop Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                RouteSession(op, null);
                return;
            }

            Host.Content = new SoftwareDashboardView(this);
            BackButton.Visibility = Visibility.Visible;
            SectionTitle.Text = "·  Software";
            UpdateUserDeptChip();
            UpdateSidebar();
        }

        public void ShowUserProfileSetup(bool allowCancel = false)
        {
            Host.Content = new UserProfileSelectView(this);
            BackButton.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
            SectionTitle.Text = "·  Select Operator";
            UpdateUserDeptChip();
            UpdateSidebar();
        }

        public void ShowDepartmentSetup(bool allowCancel = false)
        {
            ShowUserProfileSetup(allowCancel);
        }

        public void RestorePreviousView()
        {
            var store = SettingsService.Store;
            if (store.CurrentOperator != null)
            {
                RouteSession(store.CurrentOperator, store.CurrentDepartment);
            }
            else
            {
                ShowHome();
            }
        }

        public void ShowHome()
        {
            Host.Content = new HomeView(this);
            BackButton.Visibility = Visibility.Collapsed;
            SectionTitle.Text = "";
            UpdateUserDeptChip();
            UpdateSidebar();
        }

        /// <summary>DETECT is independent of TEST; it only runs when chosen.</summary>
        public void ShowDetect()
        {
            bool first = _detect == null;
            if (first) _detect = new DetectView();
            Host.Content = _detect;
            BackButton.Visibility = Visibility.Visible;
            SectionTitle.Text = "·  Detect Specs";
            if (first) _detect.StartDetection();
            UpdateSidebar();
        }

        public void ShowTest()
        {
            StartTestingWorkflow();
        }

        public void ShowWifi()
        {
            Host.Content = new WifiView();
            BackButton.Visibility = Visibility.Visible;
            SectionTitle.Text = "·  Wi-Fi";
            UpdateSidebar();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (Host.Content is UserProfileSelectView)
            {
                ShowHome();
                return;
            }

            var store = SettingsService.Store;
            var op = store.CurrentOperator;
            if (op == null)
            {
                ShowHome();
                return;
            }

            string dept = store.CurrentDepartment;
            if (string.Equals(dept, "QC", StringComparison.OrdinalIgnoreCase) && !(Host.Content is QcDashboardView))
            {
                ShowQcDashboard();
            }
            else if (string.Equals(dept, "E-Commerce", StringComparison.OrdinalIgnoreCase) && !(Host.Content is EcommerceDashboardView))
            {
                ShowEcommerceDashboard();
            }
            else if (string.Equals(dept, "Software", StringComparison.OrdinalIgnoreCase) && !(Host.Content is SoftwareDashboardView))
            {
                ShowSoftwareDashboard();
            }
            else if (string.Equals(dept, "Hardware", StringComparison.OrdinalIgnoreCase) && !(Host.Content is HardwareDashboardView))
            {
                ShowHardwareDashboard();
            }
            else
            {
                ShowHome();
            }
        }
    }
}
