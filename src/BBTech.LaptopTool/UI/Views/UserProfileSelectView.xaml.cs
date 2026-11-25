using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BBTech.Core.Settings;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.UI.Views
{
    public sealed class OperatorComboItem
    {
        public OperatorRecord Operator { get; set; }
        public string Name => Operator.Name;
        public string Role => Operator.Role;
        public string DepartmentSummary
        {
            get
            {
                if (Operator.Departments == null || Operator.Departments.Count == 0) return "General Access";
                return string.Join(" • ", Operator.Departments);
            }
        }
    }

    public partial class UserProfileSelectView : UserControl
    {
        /// <summary>Workflow departments this operator may open, in workflow order (Hardware → Software → QC → E-Commerce).</summary>
        public static List<string> AuthorizedDepartments(OperatorRecord op)
        {
            var list = new List<string>();
            if (op == null || !op.Active) return list;
            if (op.CanAccessHardware) list.Add("Hardware");
            if (op.CanAccessSoftware) list.Add("Software");
            if (op.CanAccessQc) list.Add("QC");
            if (op.CanAccessEcommerce) list.Add("E-Commerce");
            return list;
        }

        private readonly MainWindow _shell;
        private List<OperatorComboItem> _items = new List<OperatorComboItem>();

        public UserProfileSelectView(MainWindow shell)
        {
            InitializeComponent();
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            Loaded += (s, e) =>
            {
                LoadOperators();
                OperatorComboBox.Focus();
            };
        }

        private void LoadOperators()
        {
            var store = SettingsService.Store;
            if (!store.ActiveOperators.Any())
            {
                store.EnsureDefaultTeamProfiles();
            }

            _items = store.ActiveOperators
                .Where(o => o.Active)
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .Select(o => new OperatorComboItem { Operator = o })
                .ToList();

            OperatorComboBox.ItemsSource = _items;

            // Pre-select current operator if active
            var current = store.CurrentOperator;
            if (current != null)
            {
                var match = _items.FirstOrDefault(i => i.Operator.OperatorId == current.OperatorId);
                if (match != null) OperatorComboBox.SelectedItem = match;
            }
            else if (_items.Count > 0)
            {
                OperatorComboBox.SelectedIndex = 0;
            }
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            ApplySelection();
        }

        private void OperatorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            NoticeText.Visibility = Visibility.Collapsed;
        }

        private void OperatorComboBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplySelection();
            }
        }

        private void ApplySelection()
        {
            var selected = OperatorComboBox.SelectedItem as OperatorComboItem;
            if (selected == null && !string.IsNullOrWhiteSpace(OperatorComboBox.Text))
            {
                string text = OperatorComboBox.Text.Trim();
                selected = _items.FirstOrDefault(i => i.Name.Equals(text, StringComparison.OrdinalIgnoreCase))
                           ?? _items.FirstOrDefault(i => i.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (selected == null || selected.Operator == null)
            {
                NoticeText.Text = "Please select your operator account from the dropdown list.";
                NoticeText.Visibility = Visibility.Visible;
                return;
            }

            var op = selected.Operator;
            if (!op.Active)
            {
                NoticeText.Text = "This account is disabled. Contact your system administrator.";
                NoticeText.Visibility = Visibility.Visible;
                return;
            }

            // Admin profiles carry full rights: picking the name is not enough, the admin PIN is required.
            var owner = Window.GetWindow(this);
            if (op.IsAdmin && !AdminGate.Unlock(owner))
            {
                NoticeText.Text = "Administrator profiles need the admin PIN.";
                NoticeText.Visibility = Visibility.Visible;
                return;
            }

            // Establish the 1-hour session
            var store = SettingsService.Store;
            store.SelectProfile(op.OperatorId);
            try
            {
                var audit = new BBTech.Core.Audit.AuditStore(AppPaths.AuditLogPath);
                audit.Log(op, BBTech.Core.Audit.AuditActions.OperatorSelected, "Workstation:" + Environment.MachineName, null, op.Name);
                audit.Log(op, BBTech.Core.Audit.AuditActions.SessionStarted, "Workstation:" + Environment.MachineName, null,
                    store.Settings.ActiveSession?.SessionExpiresAt.ToString("yyyy-MM-dd HH:mm"), "1-hour operator session");
            }
            catch (Exception ex) { BBTech.LaptopTool.Logging.AppLog.Warn("Operator audit failed: " + ex.Message); }

            // One department: go straight there. Several: ask once, showing only the operator's own departments.
            var depts = AuthorizedDepartments(op);
            if (depts.Count == 0)
            {
                NoticeText.Text = "Your profile has no department assigned. Contact your administrator.";
                NoticeText.Visibility = Visibility.Visible;
                return;
            }
            string targetDept = depts[0];
            if (depts.Count > 1)
            {
                int pick = Dialogs.Choice(owner, "Department", "Hello " + op.Name, "Which work are you doing now?", depts.ToArray());
                if (pick < 0 || pick >= depts.Count) return;
                targetDept = depts[pick];
            }

            store.SelectDepartment(targetDept);
            _shell.ApplyDepartment(targetDept);
        }
    }
}
