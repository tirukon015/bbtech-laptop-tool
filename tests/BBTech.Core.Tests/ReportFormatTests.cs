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

        [Fact]
        public void Compact_json_keeps_insertion_order()
        {
            var o = new JsonObject { { "z", 1 }, { "a", new[] { 1, 2 } }, { "m", new JsonObject() }, { "e", new int[0] } };
            Assert.Equal("{\"z\":1,\"a\":[1,2],\"m\":{},\"e\":[]}", JsonWriter.Serialize(o, false));
        }

        [Fact]
        public void Indented_json_uses_two_spaces_per_level()
        {
            var o = new JsonObject { { "a", new JsonObject { { "b", 1 } } } };
            Assert.Equal("{\n  \"a\": {\n    \"b\": 1\n  }\n}", JsonWriter.Serialize(o));
        }

        [Fact]
        public void Dictionaries_are_written_as_objects()
        {
            var d = new Dictionary<string, object> { { "brand", "Dell" }, { "count", 2 } };
            Assert.Equal("{\"brand\":\"Dell\",\"count\":2}", JsonWriter.Serialize(d, false));
        }

        [Theory]
        [InlineData("CON", "CON-")]
        [InlineData("nul", "nul-")]
        [InlineData("COM1", "COM1-")]
        [InlineData("CONSOLE", "CONSOLE")]
        public void Reserved_device_names_are_made_safe(string value, string expected)
        {
            Assert.Equal(expected, ReportNaming.Part(value, "X", 30));
        }

        [Theory]
        [InlineData("A/B:C*?\"<>|D", "ABCD")]
        [InlineData("ThinkPad X1 Carbon Gen 9", "ThinkPad-X1-Carbon-Gen-9")]
        [InlineData("845_G11", "845-G11")]
        [InlineData("  ___  ", "FALLBACK")]
        [InlineData("Unknown", "FALLBACK")]
        [InlineData("Model (2021)", "Model-2021")]
        public void File_name_parts_are_sanitised(string value, string expected)
        {
            Assert.Equal(expected, ReportNaming.Part(value, "FALLBACK", 30));
        }

        [Fact]
        public void Compatibility_characters_are_normalised()
        {
            Assert.Equal("Book-XII", ReportNaming.Part("Book Ⅻ", "X", 30));
            Assert.Equal("AB12", ReportNaming.Part("ＡＢ１２", "X", 30));
        }

        [Fact]
        public void Long_parts_are_capped_without_a_trailing_hyphen()
        {
            Assert.Equal(new string('A', 30), ReportNaming.Part(new string('A', 45), "X", 30));
            Assert.Equal(new string('A', 9), ReportNaming.Part(new string('A', 9) + " BBBB", "X", 10));
        }

    }
}
