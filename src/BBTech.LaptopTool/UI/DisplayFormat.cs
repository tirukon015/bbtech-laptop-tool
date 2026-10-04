using System;
using System.Globalization;

namespace BBTech.LaptopTool.UI
{
    /// <summary>
    /// Dates shown in the (English) user interface. Formatted with the invariant culture so a laptop set
    /// to another region (e.g. Bengali or Thai) does not mix other digits, month names or calendars
    /// into the BBTech screens.
    /// </summary>
    public static class DisplayFormat
    {
        public static string Date(DateTime t) => t.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

        public static string DateTime(DateTime t) => t.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

        public static string Time(DateTime t) => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
