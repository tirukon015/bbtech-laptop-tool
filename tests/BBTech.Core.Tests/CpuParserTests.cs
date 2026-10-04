using BBTech.Core.Parsing;
using Xunit;

namespace BBTech.Core.Tests
{
    public class CpuParserTests
    {
        [Theory]
        [InlineData("Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz", "i5", "8th Gen")]
        [InlineData("Intel(R) Core(TM) i5-10210U CPU @ 1.60GHz", "i5", "10th Gen")]
        [InlineData("11th Gen Intel(R) Core(TM) i5-1135G7 @ 2.40GHz", "i5", "11th Gen")]
        [InlineData("12th Gen Intel(R) Core(TM) i5-1240P", "i5", "12th Gen")]
        [InlineData("13th Gen Intel(R) Core(TM) i5-1335U", "i5", "13th Gen")]
        [InlineData("Intel(R) Core(TM) i5-14400", "i5", "14th Gen")]
        [InlineData("11th Gen Intel(R) Core(TM) i5-1145G7 @ 2.60GHz", "i5", "11th Gen")]
        [InlineData("Intel Core i5-1145G7", "i5", "11th Gen")]
        public void Required_intel_examples(string name, string cpu, string gen)
        {
            var r = CpuParser.Parse(name);
            Assert.Equal(cpu, r.Family);
            Assert.Equal(gen, r.Generation);
        }

        // Short forms listed in the requirements ("Intel i5-8250U").
        [Theory]
        [InlineData("Intel i5-8250U", "8th Gen")]
        [InlineData("Intel i5-10210U", "10th Gen")]
        [InlineData("Intel i5-1135G7", "11th Gen")]
        [InlineData("Intel i5-1240P", "12th Gen")]
        [InlineData("Intel i5-1335U", "13th Gen")]
        [InlineData("Intel i5-14400", "14th Gen")]
        public void Short_intel_forms(string name, string gen)
        {
            var r = CpuParser.Parse(name);
            Assert.Equal("i5", r.Family);
            Assert.Equal(gen, r.Generation);
        }

        [Fact]
        public void Main_cpu_value_does_not_expose_full_model()
        {
            var r = CpuParser.Parse("Intel Core i5-1145G7");
            Assert.Equal("i5", r.Family);
            Assert.DoesNotContain("1145", r.Family);
            Assert.Equal("1145G7", r.ModelCode); // detail only
        }

        [Theory]
        [InlineData("Intel(R) Core(TM) i3-7100U CPU @ 2.40GHz", "i3", "7th Gen")]
        [InlineData("Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz", "i7", "10th Gen")]
        [InlineData("Intel(R) Core(TM) i7-1065G7 CPU @ 1.30GHz", "i7", "10th Gen")]
        [InlineData("13th Gen Intel(R) Core(TM) i9-13900H", "i9", "13th Gen")]
        [InlineData("Intel(R) Core(TM) i7-4510U CPU @ 2.00GHz", "i7", "4th Gen")]
        [InlineData("Intel(R) Core(TM) i5-2410M CPU @ 2.30GHz", "i5", "2nd Gen")]
        [InlineData("Intel(R) Core(TM) i5 CPU       M 520  @ 2.40GHz", "i5", "1st Gen")]
        [InlineData("Intel(R) Core(TM) i3-N305", "i3", "Unknown")]
        public void Other_intel_core_i(string name, string cpu, string gen)
        {
            var r = CpuParser.Parse(name);
            Assert.Equal(cpu, r.Family);
            Assert.Equal(gen, r.Generation);
        }

        [Theory]
        [InlineData("AMD Ryzen 3 3250U with Radeon Graphics", "Ryzen 3", "3000 Series")]
        [InlineData("AMD Ryzen 5 5500U with Radeon Graphics", "Ryzen 5", "5000 Series")]
        [InlineData("AMD Ryzen 7 PRO 4750U with Radeon Graphics", "Ryzen 7", "4000 Series")]
        [InlineData("AMD Ryzen 9 7940HS w/ Radeon 780M Graphics", "Ryzen 9", "7000 Series")]
        [InlineData("AMD Ryzen 5", "Ryzen 5", "Unknown")]
        [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", "Ryzen AI 9", "AI 300 Series")]
        public void Amd_ryzen(string name, string cpu, string gen)
        {
            var r = CpuParser.Parse(name);
            Assert.Equal(cpu, r.Family);
            Assert.Equal(gen, r.Generation);
        }

        [Theory]
        [InlineData("Intel(R) Core(TM) Ultra 7 155H", "Core Ultra 7", "Series 1")]
        [InlineData("Intel(R) Core(TM) Ultra 5 226V", "Core Ultra 5", "Series 2")]
        [InlineData("Intel(R) Core(TM) m3-7Y30 CPU @ 1.00GHz", "m3", "7th Gen")]
        [InlineData("Intel(R) Celeron(R) N4020 CPU @ 1.10GHz", "Celeron", "Unknown")]
        [InlineData("Intel(R) Pentium(R) Silver N5030 CPU @ 1.10GHz", "Pentium", "Unknown")]
        [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", "Snapdragon X Elite", "Unknown")]
        public void Other_families(string name, string cpu, string gen)
        {
            var r = CpuParser.Parse(name);
            Assert.Equal(cpu, r.Family);
            Assert.Equal(gen, r.Generation);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Some Future Processor 9000")]
        public void Unrecognised_cpu_is_unknown_not_invented(string name)
        {
            var r = CpuParser.Parse(name);
            Assert.Equal("Unknown", r.Family);
            Assert.Equal("Unknown", r.Generation);
        }

        [Theory]
        [InlineData("520", 1)]
        [InlineData("8250", 8)]
        [InlineData("1145", 11)]
        [InlineData("1240", 12)]
        [InlineData("10210", 10)]
        [InlineData("14400", 14)]
        [InlineData("99999", 0)]
        public void Generation_from_digits(string digits, int expected) =>
            Assert.Equal(expected, CpuParser.IntelGenerationFromDigits(digits));

        [Theory]
        [InlineData("Intel(R) Core(TM) i7 CPU       Q 720  @ 1.60GHz", "i7", "1st Gen", "720")]
        [InlineData("Intel(R) Core(TM) i7 CPU       X 920  @ 2.00GHz", "i7", "1st Gen", "920")]
        [InlineData("Intel(R) Core(TM) i5 CPU       M 520  @ 2.40GHz", "i5", "1st Gen", "520")]
        [InlineData("Intel(R) Core(TM) i3 CPU       M 370  @ 2.40GHz", "i3", "1st Gen", "370")]
        public void First_generation_mobile_names_with_a_letter_before_the_number(string name, string cpu, string gen, string code)
        {
            var r = CpuParser.Parse(name);
            Assert.Equal(cpu, r.Family);
            Assert.Equal(gen, r.Generation);
            Assert.Equal(code, r.ModelCode);
        }
    }
}
