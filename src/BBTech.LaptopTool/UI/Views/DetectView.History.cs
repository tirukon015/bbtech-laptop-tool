using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Identity;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.UI.Views
{
    /// <summary>
    /// DETECT → QC history: identifies the physical laptop and automatically reads the encrypted QC
    /// history stored ON THIS LAPTOP (any BBTech USB works; the original QC USB is not needed).
    /// History is shown only for an unambiguous identity match – never by model, never another laptop's.
    /// </summary>
    public partial class DetectView
    {
        private static Brush B(string k) => (Brush)Application.Current.FindResource(k);

        private async Task LoadHistoryAsync(DetectionResult r)
        {
            HistoryBox.Visibility = Visibility.Visible;
            HistoryPanel.Children.Clear();
            HistoryPanel.Children.Add(Line("QC HISTORY", 12, true, "TextMuted"));
            var step = Line("Device identified ✓   ·   Searching local QC history...", 15, false, "TextPrimary");
            HistoryPanel.Children.Add(step);

            var identity = IdentityFactory.From(r);
            QcOpenResult open;
            MatchResult match = null;
            List<InspectionView> history = null;
            try
            {
                open = await QcStoreService.OpenAsync(identity, readOnly: true);
                if (open.Ok)
                {
                    match = await Task.Run(() => open.Repository.FindDevice(identity));
                    if (match.Status == MatchStatus.Matched)
                        history = await Task.Run(() => open.Repository.GetHistory(match.Device.DeviceId));
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("QC history lookup failed", ex);
                open = new QcOpenResult { Message = "QC history could not be read on this laptop." };
            }

            HistoryPanel.Children.Remove(step);
            if (!open.Ok)
            {
                // No store on this laptop at all = never tested here.
                bool neverTested = open.NoStore;
                Status(neverTested ? "No QC inspection found for this device." : open.Message, neverTested ? "Neutral" : "Warn");
                return;
            }
            if (!string.IsNullOrEmpty(open.Message)) HistoryPanel.Children.Add(Line(open.Message, 12, false, "TextMuted"));

            switch (match.Status)
            {
                case MatchStatus.NotFound:
                    Status("No QC inspection found for this device.", "Neutral");
                    AppLog.Info("DETECT: no QC history for this device");
                    return;
                case MatchStatus.NoIdentifiers:
                    Status(match.Reason, "Warn");
                    return;
                case MatchStatus.Conflict:
                    Status("Identity conflict – QC history not shown", "Fail");
                    HistoryPanel.Children.Add(Line(match.Reason, 13, false, "TextPrimary"));
                    var review = new Button { Content = "Review records (admin)", Style = (Style)FindResource("BaseButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
                    review.Click += (s, e) => ReviewConflict(open.Repository, match);
                    HistoryPanel.Children.Add(review);
                    AppLog.Warn("DETECT: identity conflict, history withheld");
                    return;
            }

            var completed = history.Where(h => h.Inspection.Status == InspectionStatus.Completed).ToList();
            if (history.Count == 0)
            {
                Status("No QC inspection found for this device.", "Neutral");
                return;
            }
            AppLog.Info("DETECT: QC history found (" + history.Count + " inspection(s))");
            RenderHistory(match.Device, history, completed);
        }

        private void RenderHistory(DeviceRecord device, List<InspectionView> history, List<InspectionView> completed)
        {
            Status(completed.Count > 0 ? "QC Inspection Found ✓" : "Unfinished QC inspection found", completed.Count > 0 ? "Pass" : "Warn");
            HistoryPanel.Children.Add(Line(device.DisplayName + (string.IsNullOrEmpty(device.SerialNumber) ? "" : "  ·  Serial " + device.SerialNumber) +
                                           "  ·  " + history.Count + " inspection(s) on this laptop", 13, false, "TextMuted"));

            var latest = completed.FirstOrDefault() ?? history.First();
            var detail = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            ShowInspection(detail, device, latest);
            HistoryPanel.Children.Add(detail);

            if (history.Count > 1)
            {
                HistoryPanel.Children.Add(Line("QC HISTORY (newest first) – click to view", 12, true, "TextMuted"));
                foreach (var h in history)
                {
                    var i = h.Inspection;
                    var row = new Border
                    {
                        Background = Brushes.White, BorderBrush = B("CardBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                        Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(12, 8, 12, 8), Cursor = System.Windows.Input.Cursors.Hand,
                    };
                    row.MouseEnter += (s, e) => row.BorderBrush = B("Brand");
                    row.MouseLeave += (s, e) => row.BorderBrush = B("CardBorder");
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Add(g, 0, "#" + i.Number);
                    Add(g, 1, (i.CompletedAt ?? i.StartedAt).ToString("dd MMM yyyy"));
                    Add(g, 2, i.OperatorNameSnapshot + " (" + i.OperatorRoleSnapshot + ")");
                    var chip = Chip(StatusLabel(i), Kind(i));
                    chip.HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetColumn(chip, 3);
                    g.Children.Add(chip);
                    row.Child = g;
                    var view = h;
                    row.MouseLeftButtonUp += (s, e) => { detail.Children.Clear(); ShowInspection(detail, device, view); };
                    HistoryPanel.Children.Add(row);
                }
            }
        }

        private void ShowInspection(StackPanel target, DeviceRecord device, InspectionView view)
        {
            var i = view.Inspection;
            var head = new WrapPanel();
            head.Children.Add(Field("Inspection", "#" + i.Number));
            head.Children.Add(Field("Tested By", i.OperatorNameSnapshot));
            head.Children.Add(Field("Role", i.OperatorRoleSnapshot));
            head.Children.Add(Field("Date", (i.CompletedAt ?? i.StartedAt).ToString("dd MMM yyyy, HH:mm")));
            head.Children.Add(Field("Overall", StatusLabel(i), Kind(i) + "Brush"));
            string mode = BBTech.Core.Export.HistoryReport.ModeLabel(i);
            if (mode.Length > 0) head.Children.Add(Field("Test mode", mode));
            target.Children.Add(head);

            var grid = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var res in BBTech.Core.Export.HistoryReport.Items(view))
            {
                var cell = new Border { Width = 210, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(6), Background = B("NeutralSoft"),
                                        ToolTip = string.IsNullOrEmpty(res.Notes) ? null : res.Notes };
                var dp = new DockPanel();
                var st = Line(res.StateLabel, 12, true, res.State == Core.Testing.TestState.Pass ? "PassBrush" : res.State == Core.Testing.TestState.Fail ? "FailBrush" : "TextMuted");
                st.Margin = new Thickness(0);
                DockPanel.SetDock(st, Dock.Right);
                dp.Children.Add(st);
                var name = Line(res.Name, 13, false, "TextPrimary"); name.Margin = new Thickness(0);
                dp.Children.Add(name);
                cell.Child = dp;
                grid.Children.Add(cell);
            }
            target.Children.Add(grid);

            var exports = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
            foreach (var ext in new[] { "csv", "json" })
            {
                var b = new Button { Content = "Export " + ext.ToUpperInvariant(), Style = (Style)FindResource("LinkButton") };
                string e2 = ext;
                b.Click += (s, e) =>
                {
                    string path = ReportFiles.Save(view, device, e2);
                    if (path != null) { NoticeText.Text = "Saved: " + path; Notice.Visibility = Visibility.Visible; }
                };
                exports.Children.Add(b);
            }
            target.Children.Add(exports);
        }

        private void ReviewConflict(QcRepository repo, MatchResult match)
        {
            var owner = Window.GetWindow(this);
            if (!AdminGate.Unlock(owner)) return;
            var text = string.Join("\n\n", match.Candidates.Select(d =>
                "Record: " + d.DisplayName + "  ·  Serial " + (d.SerialNumber ?? "-") + "\nCreated " + d.CreatedAt.ToString("dd MMM yyyy") +
                (string.IsNullOrEmpty(d.IdentityNote) ? "" : "\nNote: " + d.IdentityNote) + "\n" +
                string.Join("\n", repo.GetHistory(d.DeviceId).Select(h => "   #" + h.Inspection.Number + "  " + (h.Inspection.CompletedAt ?? h.Inspection.StartedAt).ToString("dd MMM yyyy") +
                    "  " + h.Inspection.OperatorNameSnapshot + "  " + StatusLabel(h.Inspection)))));
            AppLog.Info("Admin reviewed conflicting device records");
            Dialogs.Info(owner, "Conflicting records (admin review)", match.Reason + "\n\n" + text);
        }

        private static string StatusLabel(InspectionRecord i) =>
            i.Status == InspectionStatus.InProgress ? "IN PROGRESS" : i.Status == InspectionStatus.Abandoned ? "ABANDONED" : (i.OverallStatus ?? "INCOMPLETE");

        private static string Kind(InspectionRecord i) =>
            i.Status != InspectionStatus.Completed ? "Neutral" : i.OverallStatus == "PASS" ? "Pass" : i.OverallStatus == "FAIL" ? "Fail" : "Warn";

        private void Status(string text, string kind)
        {
            var b = new Border { Background = B(kind + "Soft"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
            b.Child = new TextBlock { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = B(kind + "Brush"), TextWrapping = TextWrapping.Wrap };
            HistoryPanel.Children.Add(b);
        }

        private static TextBlock Line(string t, double size, bool bold, string brush) =>
            new TextBlock { Text = t, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = B(brush), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

        private static UIElement Field(string label, string value, string brush = "TextPrimary")
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 28, 4) };
            sp.Children.Add(Line(label.ToUpperInvariant(), 11, true, "TextMuted"));
            sp.Children.Add(Line(value, 17, true, brush));
            return sp;
        }

        private static Border Chip(string text, string kind) => new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 2), Background = B(kind + "Soft"),
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = B(kind + "Brush") },
        };

        private static void Add(Grid g, int col, string text)
        {
            var t = Line(text, 13, false, "TextPrimary");
            t.Margin = new Thickness(0);
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }
    }
}
