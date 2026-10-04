using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BBTech.LaptopTool.UI.Tests
{
    public sealed class DisplayTestPanel : TestPanelBase
    {
        private readonly TextBlock _status;
        private int _patternsViewed;
        private int _patternCount;

        public override string Instructions =>
            "Start the full-screen test and look for dead or stuck pixels, lines, discolouration, uneven backlight and flicker. " +
            "Click or press Space / → for the next pattern, ← for the previous one, Esc to stop.";

        public DisplayTestPanel()
        {
            var screen = SystemParameters.PrimaryScreenWidth + " × " + SystemParameters.PrimaryScreenHeight;
            _status = T("Not started yet.", 14, false, "TextMuted");
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Children =
                    {
                        Card("Full-screen pattern test",
                            T("Patterns: red, green, blue, white, black, grey, gradient, colour bars and grid.", 14),
                            new Border { Height = 12 },
                            Row(B("Start display test", (s, e) => StartPatterns(), "PrimaryButton")),
                            _status),
                        Card("Screen", KeyValue(new[] { KV("Resolution (scaled)", screen), KV("Monitors", Forms.MonitorCount().ToString()) })),
                        Card("Confirm what you saw",
                            Check("No dead or stuck pixels"),
                            Check("No lines, bands or cracks"),
                            Check("Colours look correct"),
                            Check("Backlight even, no flicker or bleeding")),
                    }
                }
            };
        }

        private void StartPatterns()
        {
            var w = new DisplayPatternWindow { Owner = Window.GetWindow(this) };
            w.ShowDialog();
            _patternsViewed = Math.Max(_patternsViewed, w.ViewedCount);
            _patternCount = w.PatternCount;
            _status.Text = "Viewed " + _patternsViewed + " of " + _patternCount + " patterns.";
            Host?.SetSuggestion(_patternsViewed >= _patternCount ? "All patterns shown. Confirm the checklist, then choose PASS or FAIL."
                                                                 : "Not all patterns were shown.", _patternsViewed >= _patternCount ? SuggestionKind.Info : SuggestionKind.Bad);
        }

        public override string Notes => Join("Patterns viewed " + _patternsViewed + "/" + _patternCount, ChecklistNotes());

        private static class Forms
        {
            public static int MonitorCount()
            {
                try { return GetSystemMetrics(80); } catch { return 1; } // SM_CMONITORS
            }
            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        }
    }

    /// <summary>Borderless full-screen window that cycles through test patterns.</summary>
    public sealed class DisplayPatternWindow : Window
    {
        private readonly Tuple<string, Brush>[] _patterns;
        private readonly Border _surface = new Border();
        private readonly TextBlock _hint = new TextBlock();
        private int _index;

        public int ViewedCount { get; private set; }
        public int PatternCount => _patterns.Length;

        public DisplayPatternWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Topmost = true;
            ShowInTaskbar = false;
            Background = Brushes.Black;
            Cursor = Cursors.None;

            _patterns = new[]
            {
                Tuple.Create("Red", (Brush)Brushes.Red),
                Tuple.Create("Green", (Brush)new SolidColorBrush(Color.FromRgb(0, 255, 0))),
                Tuple.Create("Blue", (Brush)Brushes.Blue),
                Tuple.Create("White", (Brush)Brushes.White),
                Tuple.Create("Black", (Brush)Brushes.Black),
                Tuple.Create("Grey 50%", (Brush)new SolidColorBrush(Color.FromRgb(128, 128, 128))),
                Tuple.Create("Gradient", (Brush)new LinearGradientBrush(Colors.Black, Colors.White, 0)),
                Tuple.Create("Colour bars", ColourBars()),
                Tuple.Create("Grid", GridBrush()),
            };

            _hint.Foreground = Brushes.White;
            _hint.Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
            _hint.Padding = new Thickness(16, 10, 16, 10);
            _hint.FontSize = 18;
            _hint.HorizontalAlignment = HorizontalAlignment.Center;
            _hint.VerticalAlignment = VerticalAlignment.Bottom;
            _hint.Margin = new Thickness(0, 0, 0, 40);
            _hint.IsHitTestVisible = false;

            var grid = new Grid();
            grid.Children.Add(_surface);
            grid.Children.Add(_hint);
            Content = grid;

            Show(0);
            KeyDown += OnKey;
            MouseLeftButtonUp += (s, e) => Next();
            MouseRightButtonUp += (s, e) => Show(Math.Max(0, _index - 1));
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Left: case Key.Back: Show(Math.Max(0, _index - 1)); break;
                default: Next(); break;
            }
            e.Handled = true;
        }

        private void Next()
        {
            if (_index + 1 >= _patterns.Length) { Close(); return; }
            Show(_index + 1);
        }

        private void Show(int i)
        {
            _index = i;
            ViewedCount = Math.Max(ViewedCount, i + 1);
            _surface.Background = _patterns[i].Item2;
            _hint.Text = (i + 1) + "/" + _patterns.Length + "  " + _patterns[i].Item1 + "   ·   click / Space = next   ·   ← = back   ·   Esc = stop";
            _hint.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1)) { BeginTime = TimeSpan.FromSeconds(2) });
        }

        private static Brush ColourBars()
        {
            var colors = new[] { Colors.White, Colors.Yellow, Colors.Cyan, Color.FromRgb(0, 255, 0), Colors.Magenta, Colors.Red, Colors.Blue, Colors.Black };
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (int i = 0; i < colors.Length; i++)
            {
                g.GradientStops.Add(new GradientStop(colors[i], i / (double)colors.Length));
                g.GradientStops.Add(new GradientStop(colors[i], (i + 1) / (double)colors.Length));
            }
            return g;
        }

        private static Brush GridBrush()
        {
            var geo = new GeometryGroup();
            geo.Children.Add(new RectangleGeometry(new Rect(0, 0, 40, 1)));
            geo.Children.Add(new RectangleGeometry(new Rect(0, 0, 1, 40)));
            return new DrawingBrush(new GeometryDrawing(Brushes.White, null, geo))
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 40, 40),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 40, 40),
                ViewboxUnits = BrushMappingMode.Absolute,
            };
        }
    }
}
