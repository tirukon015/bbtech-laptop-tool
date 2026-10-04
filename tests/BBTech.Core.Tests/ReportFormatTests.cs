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

    }
}
