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

    }
}
