using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.UI.Tests
{
    /// <summary>
    /// Touchpad test: move the pointer over every cell of the grid (proves tracking over the
    /// whole pad), then left-click, right-click, double-tap and scroll in the targets.
    /// </summary>
    public sealed class TouchpadTestPanel : TestPanelBase
    {
        private const int Cols = 12, Rows = 6;
        private readonly Rectangle[,] _cells = new Rectangle[Cols, Rows];
        private readonly bool[,] _hit = new bool[Cols, Rows];
        private readonly UniformGrid _grid = new UniformGrid { Columns = Cols, Rows = Rows };
        private readonly TextBlock _coverage = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold };
        private bool _left, _right, _double, _scrollUp, _scrollDown;
        private Border _leftT, _rightT, _doubleT, _scrollT;

        public override string Instructions =>
            "Disconnect any external mouse. Using only the touchpad, move the pointer across every grey cell, then use each target below " +
            "(left click, right click, double tap, two-finger scroll up and down).";
        public override bool PreferMaximized => true;

        public TouchpadTestPanel()
        {
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Cols; x++)
                {
                    var r = new Rectangle { Fill = Res("NeutralSoft"), Stroke = Brushes.White, StrokeThickness = 2 };
                    _cells[x, y] = r;
                    _grid.Children.Add(r);
                }
            _grid.MouseMove += Grid_MouseMove;
            _grid.Background = Brushes.Transparent;
            _grid.MinHeight = 120;

            _leftT = Target("Left click here", (s, e) => { if (e.ChangedButton == MouseButton.Left) { _left = true; Done(_leftT); } });
            _rightT = Target("Right click here", (s, e) => { if (e.ChangedButton == MouseButton.Right) { _right = true; Done(_rightT); } });
            _doubleT = Target("Double-tap here", null);
            _doubleT.MouseLeftButtonDown += (s, e) => { if (e.ClickCount >= 2) { _double = true; Done(_doubleT); } };
            _scrollT = Target("Scroll up and down here", null);
            _scrollT.MouseWheel += (s, e) =>
            {
                if (e.Delta > 0) _scrollUp = true; else _scrollDown = true;
                ((TextBlock)_scrollT.Child).Text = "Scroll: up " + (_scrollUp ? "✓" : "…") + "   down " + (_scrollDown ? "✓" : "…");
                if (_scrollUp && _scrollDown) Done(_scrollT);
                e.Handled = true;
            };

            var targets = new UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 0) };
            targets.Children.Add(_leftT); targets.Children.Add(_rightT); targets.Children.Add(_doubleT); targets.Children.Add(_scrollT);


            var layout = new DockPanel();
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(_coverage);
            var reset = B("Reset", (s, e) => ResetAll());
            DockPanel.SetDock(reset, Dock.Right);
            top.Children.Insert(0, reset);
            DockPanel.SetDock(top, Dock.Top);
            layout.Children.Add(top);

            var bottom = new StackPanel();
            bottom.Children.Add(targets);
            bottom.Children.Add(new Border { Height = 8 });
            var devicesText = T("Pointing devices: loading...", 12, false, "TextMuted");
            bottom.Children.Add(devicesText);
            // WMI query off the UI thread so the test opens instantly.
            System.Threading.Tasks.Task.Run(() => string.Join(", ", Wmi.Query("SELECT Name FROM Win32_PointingDevice").Select(o => Wmi.Str(o, "Name")).Where(n => n.Length > 0).Distinct()))
                .ContinueWith(t => Ui(() => devicesText.Text = "Pointing devices detected: " + (t.Result.Length > 0 ? t.Result : "none reported")));
            DockPanel.SetDock(bottom, Dock.Bottom);
            layout.Children.Add(bottom);
            layout.Children.Add(new Border { Child = _grid, BorderBrush = Res("CardBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true });

            Content = layout;
            UpdateStatus();

            // Short windows (small screens / high DPI): put the targets beside the grid as 2×2
            // so the movement grid keeps a usable height.
            SizeChanged += (s, e) =>
            {
                bool side = e.NewSize.Height < 380;
                DockPanel.SetDock(bottom, side ? Dock.Right : Dock.Bottom);
                bottom.Width = side ? 320 : double.NaN;
                bottom.Margin = new Thickness(side ? 10 : 0, 0, 0, 0);
                targets.Columns = side ? 2 : 4;
                foreach (var b in new[] { _leftT, _rightT, _doubleT, _scrollT }) b.Height = side ? 58 : 76;
            };
        }

        private Border Target(string text, MouseButtonEventHandler up)
        {
            var b = new Border
            {
                Height = 76, Margin = new Thickness(5), CornerRadius = new CornerRadius(10), Background = Res("BrandSoft"),
                BorderBrush = Res("Brand"), BorderThickness = new Thickness(2),
                Child = new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Brand"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
                Cursor = Cursors.Hand,
            };
            if (up != null) b.MouseUp += up;
            b.ContextMenuOpening += (s, e) => e.Handled = true;
            return b;
        }

        private void Done(Border b)
        {
            b.Background = Res("PassSoft");
            b.BorderBrush = Res("PassBrush");
            var tb = (TextBlock)b.Child;
            tb.Foreground = Res("PassBrush");
            if (!tb.Text.EndsWith("✓")) tb.Text = tb.Text.Replace(" here", "") + "  ✓";
            UpdateStatus();
        }

        private void Grid_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(_grid);
            if (_grid.ActualWidth <= 0 || _grid.ActualHeight <= 0) return;
            int x = (int)(p.X / _grid.ActualWidth * Cols), y = (int)(p.Y / _grid.ActualHeight * Rows);
            if (x < 0 || y < 0 || x >= Cols || y >= Rows || _hit[x, y]) return;
            _hit[x, y] = true;
            _cells[x, y].Fill = Res("PassBrush");
            UpdateStatus();
        }

        private int CoveragePercent()
        {
            int hit = 0;
            foreach (bool b in _hit) if (b) hit++;
            return hit * 100 / (Cols * Rows);
        }

        private void UpdateStatus()
        {
            int cov = CoveragePercent();
            _coverage.Text = "Movement coverage: " + cov + "%";
            bool all = cov >= 95 && _left && _right && _double && _scrollUp && _scrollDown;
            Host?.SetSuggestion(all ? "Movement, clicks and scrolling all registered." : "Complete the grid and all four targets.",
                all ? SuggestionKind.Good : SuggestionKind.Info);
        }

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            UpdateStatus();
        }

        private void ResetAll()
        {
            for (int y = 0; y < Rows; y++) for (int x = 0; x < Cols; x++) { _hit[x, y] = false; _cells[x, y].Fill = Res("NeutralSoft"); }
            _left = _right = _double = _scrollUp = _scrollDown = false;
            foreach (var t in new[] { Tuple.Create(_leftT, "Left click here"), Tuple.Create(_rightT, "Right click here"), Tuple.Create(_doubleT, "Double-tap here"), Tuple.Create(_scrollT, "Scroll up and down here") })
            {
                t.Item1.Background = Res("BrandSoft"); t.Item1.BorderBrush = Res("Brand");
                var tb = (TextBlock)t.Item1.Child; tb.Foreground = Res("Brand"); tb.Text = t.Item2;
            }
            UpdateStatus();
        }

        public override string Notes =>
            "Coverage " + CoveragePercent() + "%; left click " + Mark(_left) + "; right click " + Mark(_right) + "; double tap " + Mark(_double) +
            "; scroll up " + Mark(_scrollUp) + "; scroll down " + Mark(_scrollDown);

        private static string Mark(bool b) => b ? "OK" : "not done";
    }
}

