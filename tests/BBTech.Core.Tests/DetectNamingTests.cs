using BBTech.Core.Parsing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>More laptop names and CPU strings seen on refurbished stock.</summary>
    public class DetectNamingTests
    {
        private static void Split(string full, string brand, string model, string number)
        {
            var r = ModelParser.ParseFullName(full);
            Assert.Equal(brand, r.Brand);
            Assert.Equal(model, r.Model);
            Assert.Equal(number, r.ModelNumber);
        }

        [Theory]
        [InlineData("Dell Latitude 7490", "Latitude", "7490")]
        [InlineData("Dell Precision 5550", "Precision", "5550")]
        [InlineData("Dell Vostro 3510", "Vostro", "3510")]
        [InlineData("Dell Inspiron 15 3511", "Inspiron", "15 3511")]
        public void Dell_names(string full, string model, string number) => Split(full, "Dell", model, number);

        [Theory]
        [InlineData("HP EliteBook 840 G8 Notebook PC", "EliteBook", "840 G8")]
        [InlineData("HP ZBook Firefly 14 G8 Mobile Workstation PC", "ZBook Firefly", "14 G8")]
        [InlineData("HP ProBook 450 G9", "ProBook", "450 G9")]
        public void Hp_names(string full, string model, string number) => Split(full, "HP", model, number);

        [Theory]
        [InlineData("Lenovo ThinkPad X1 Carbon Gen 9", "ThinkPad", "X1 Carbon Gen 9")]
        [InlineData("Lenovo ThinkPad E14 Gen 2", "ThinkPad", "E14 Gen 2")]
        [InlineData("Lenovo ThinkBook 14 G2 ITL", "ThinkBook", "14 G2 ITL")]
        public void Lenovo_names(string full, string model, string number) => Split(full, "Lenovo", model, number);

        [Theory]
        [InlineData("DELL LATITUDE 5420", "Dell", "Latitude", "5420")]
        [InlineData("HP ELITEBOOK 830 G7", "HP", "EliteBook", "830 G7")]
        public void All_caps_firmware_names_get_marketing_casing(string full, string brand, string model, string number) => Split(full, brand, model, number);

        [Theory]
        [InlineData("Intel(R) Celeron(R) N4020 CPU @ 1.10GHz", "Celeron", "Intel")]
        [InlineData("Intel(R) Pentium(R) Silver N5030 CPU @ 1.10GHz", "Pentium", "Intel")]
        [InlineData("Intel(R) Processor N100", "Intel N-series", "Intel")]
        [InlineData("Intel(R) Xeon(R) W-10855M CPU @ 2.80GHz", "Xeon", "Intel")]
        [InlineData("AMD Athlon Silver 3050U with Radeon Graphics", "Athlon", "AMD")]
        [InlineData("AMD A9-9425 RADEON R5, 5 COMPUTE CORES 2C+3G", "AMD A9", "AMD")]
        [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", "Snapdragon X Elite", "Qualcomm")]
        [InlineData("Intel(R) Core(TM)2 Duo CPU P8600 @ 2.40GHz", "Core 2", "Intel")]
        public void Other_cpu_families(string name, string family, string vendor)
        {
            var r = CpuParser.Parse(name);
            Assert.Equal(family, r.Family);
            Assert.Equal(vendor, r.Vendor);
            Assert.Equal("Unknown", r.Generation);
        }

    }
}
