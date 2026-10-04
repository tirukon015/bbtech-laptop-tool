using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using BBTech.Core.Export;
using BBTech.Core.Models;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>JSON writer, report file names and exporter output details.</summary>
    public class ReportFormatTests
    {
        private static readonly DateTime When = new DateTime(2026, 10, 5, 14, 30, 0);

        [Fact]
        public void Json_primitives_are_written_as_json_literals()
        {
            Assert.Equal("null", JsonWriter.Serialize(null));
            Assert.Equal("true", JsonWriter.Serialize(true));
            Assert.Equal("42", JsonWriter.Serialize(42));
            Assert.Equal("9000000000", JsonWriter.Serialize(9000000000L));
            Assert.Equal("\"Pass\"", JsonWriter.Serialize(TestState.Pass));
            Assert.Equal("\"2026-10-05T14:30:00\"", JsonWriter.Serialize(When));
        }

        [Fact]
        public void Json_numbers_use_a_dot_in_every_windows_region()
        {
            var old = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                Assert.Equal("2.5", JsonWriter.Serialize(2.5));
                Assert.Equal("1.235", JsonWriter.Serialize(1.23456));
                Assert.Equal("1.5", JsonWriter.Serialize(1.5f));
                Assert.Equal("1.10", JsonWriter.Serialize(1.10m));
            }
            finally { Thread.CurrentThread.CurrentCulture = old; }
        }

        [Fact]
        public void Non_finite_numbers_become_null()
        {
            Assert.Equal("null", JsonWriter.Serialize(double.NaN));
            Assert.Equal("null", JsonWriter.Serialize(double.PositiveInfinity));
        }

        [Fact]
        public void Json_strings_escape_special_and_control_characters()
        {
            Assert.Equal("\"a\\\"b\\\\c\\n\\t\\u0001\"", JsonWriter.Serialize("a\"b\\c\n\t\u0001"));
            Assert.Equal("\"Sound – left/right ✓\"", JsonWriter.Serialize("Sound – left/right ✓"));
        }

    }
}
