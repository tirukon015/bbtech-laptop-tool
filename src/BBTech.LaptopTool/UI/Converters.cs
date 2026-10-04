using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BBTech.Core.Testing;

namespace BBTech.LaptopTool.UI
{
    /// <summary>Maps test state / overall result to the status colours (always paired with text).</summary>
    public sealed class StateToBrushConverter : IValueConverter
    {
        public bool Soft { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string key;
            switch (value)
            {
                case TestState s:
                    key = s == TestState.Pass ? "Pass" : s == TestState.Fail ? "Fail" : s == TestState.Testing ? "Warn" : s == TestState.Skipped ? "Skip" : "Neutral";
                    break;
                case OverallResult r:
                    key = r == OverallResult.Pass ? "Pass" : r == OverallResult.Fail ? "Fail" : "Warn";
                    break;
                default:
                    key = "Neutral";
                    break;
            }
            if (key == "Skip") return Application.Current.FindResource(Soft ? "NeutralSoft" : "SkipBrush");
            return Application.Current.FindResource(key + (Soft ? "Soft" : "Brush")) as Brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
