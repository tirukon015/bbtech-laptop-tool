using System;
using System.Windows;
using System.Windows.Media;
using BBTech.Core.Testing;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.UI.Tests
{
    public sealed class TestOutcome
    {
        public TestState State { get; set; }
        public string Notes { get; set; } = "";
    }

    public enum SuggestionKind { Info, Good, Bad }

    /// <summary>
    /// Common frame for every hardware test: title, instructions, the test panel, and the
    /// PASS / FAIL / SKIP decision. The operator always makes the final decision; panels can
    /// only show a suggestion based on what they measured.
    /// </summary>
    public partial class TestHostWindow : Window
    {
        private readonly TestPanelBase _panel;

        public TestOutcome Outcome { get; private set; }

        public TestHostWindow(TestItem item, TestPanelBase panel)
        {
            InitializeComponent();
            _panel = panel;
            Title = item.Name + " test - BBTech Laptop Tool";
            TitleText.Text = item.Name + " test";
            InstructionsText.Text = panel.Instructions;
            PanelHost.Content = panel;
            CommentRow.Visibility = panel.AllowComment ? Visibility.Visible : Visibility.Collapsed;
            MainWindow.FitToScreen(this, 1120, 800);

            // Short windows (1280×720 / 1366×768 at 150%+): one-line instructions (full text in the
            // tooltip) so the test area keeps as much height as possible.
            InstructionsText.ToolTip = panel.Instructions;
            SizeChanged += (s, e) =>
            {
                bool shortWindow = e.NewSize.Height < 560;
                InstructionsText.TextWrapping = shortWindow ? TextWrapping.NoWrap : TextWrapping.Wrap;
                InstructionsText.TextTrimming = shortWindow ? TextTrimming.CharacterEllipsis : TextTrimming.None;
            };
            if (panel.PreferMaximized) WindowState = WindowState.Maximized;

            Loaded += (s, e) =>
            {
                try { _panel.Attach(this); }
                catch (Exception ex)
                {
                    AppLog.Error("Test panel failed to start", ex);
                    SetSuggestion("This test could not start on this laptop. You can record FAIL or SKIP.", SuggestionKind.Bad);
                }
            };
            Closed += (s, e) =>
            {
                try { _panel.Detach(); } catch (Exception ex) { AppLog.Warn("Test panel cleanup error", ex); }
            };
        }

        /// <summary>Thread-safe: shows what the automatic checks found.</summary>
        public void SetSuggestion(string text, SuggestionKind kind)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => SetSuggestion(text, kind))); return; }
            if (string.IsNullOrEmpty(text)) { SuggestionBox.Visibility = Visibility.Collapsed; return; }
            string fg = kind == SuggestionKind.Good ? "PassBrush" : kind == SuggestionKind.Bad ? "FailBrush" : "Brand";
            string bg = kind == SuggestionKind.Good ? "PassSoft" : kind == SuggestionKind.Bad ? "FailSoft" : "BrandSoft";
            SuggestionText.Text = text;
            SuggestionText.Foreground = (Brush)FindResource(fg);
            SuggestionBox.Background = (Brush)FindResource(bg);
            SuggestionBox.Visibility = Visibility.Visible;
        }

        private void Finish(TestState state)
        {
            string notes = _panel.Notes ?? "";
            string comment = CommentBox.Text.Trim();
            if (comment.Length > 0) notes = notes.Length > 0 ? notes + "; " + comment : comment;
            Outcome = new TestOutcome { State = state, Notes = notes };
            Close();
        }

        private void Pass_Click(object sender, RoutedEventArgs e) => Finish(TestState.Pass);
        private void Fail_Click(object sender, RoutedEventArgs e) => Finish(TestState.Fail);
        private void Skip_Click(object sender, RoutedEventArgs e) => Finish(TestState.Skipped);
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}

