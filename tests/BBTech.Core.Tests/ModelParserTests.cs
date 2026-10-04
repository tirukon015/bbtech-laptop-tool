using BBTech.Core.Parsing;
using Xunit;

namespace BBTech.Core.Tests
{
    public class ModelParserTests
    {
        private static void AssertParsed(ModelParseResult r, string brand, string model, string number)
        {
            Assert.Equal(brand, r.Brand);
            Assert.Equal(model, r.Model);
            Assert.Equal(number, r.ModelNumber);
        }

        // ---- Required examples (single "Brand Model Number" string) ----

        [Fact]
        public void Dell_Latitude_5420_keeps_model_and_number_separate()
        {
            var r = ModelParser.ParseFullName("Dell Latitude 5420");
            AssertParsed(r, "Dell", "Latitude", "5420");
            Assert.NotEqual("Latitude 5420", r.Model);
        }

        [Fact]
        public void HP_EliteBook_845_G11() =>
            AssertParsed(ModelParser.ParseFullName("HP EliteBook 845 G11"), "HP", "EliteBook", "845 G11");

        [Fact]
        public void Lenovo_ThinkPad_T14() =>
            AssertParsed(ModelParser.ParseFullName("Lenovo ThinkPad T14"), "Lenovo", "ThinkPad", "T14");

        [Fact]
        public void Lenovo_ThinkPad_T14_Gen_2() =>
            AssertParsed(ModelParser.ParseFullName("Lenovo ThinkPad T14 Gen 2"), "Lenovo", "ThinkPad", "T14 Gen 2");

        [Fact]
        public void Acer_Aspire_without_number_reports_unknown_number() =>
            AssertParsed(ModelParser.ParseFullName("Acer Aspire"), "Acer", "Aspire", "Unknown");

        // ---- Real firmware value combinations ----

        [Fact]
        public void Dell_firmware_values() =>
            AssertParsed(ModelParser.Parse("Dell Inc.", "Latitude 5420", "Latitude", "Not Specified"), "Dell", "Latitude", "5420");

        [Fact]
        public void HP_firmware_name_with_marketing_noise() =>
            AssertParsed(ModelParser.Parse("HP", "HP EliteBook 845 14 inch G11 Notebook PC", "103C_5336AN HP EliteBook", ""),
                "HP", "EliteBook", "845 G11");

        [Fact]
        public void HP_screen_size_kept_when_it_is_the_model_number() =>
            AssertParsed(ModelParser.Parse("HP", "HP ZBook Firefly 14 inch G8 Mobile Workstation PC"), "HP", "ZBook Firefly", "14 G8");

        [Fact]
        public void HP_ProBook() =>
            AssertParsed(ModelParser.Parse("Hewlett-Packard", "HP ProBook 450 G8 Notebook PC"), "HP", "ProBook", "450 G8");

        [Fact]
        public void HP_Pavilion_middle_laptop_word_removed() =>
            AssertParsed(ModelParser.Parse("HP", "HP Pavilion Laptop 15-eg0xxx"), "HP", "Pavilion", "15-eg0xxx");

        [Fact]
        public void Lenovo_uses_product_version_not_machine_type()
        {
            var r = ModelParser.Parse("LENOVO", "20W0S22200", "ThinkPad T14 Gen 2i", "ThinkPad T14 Gen 2i");
            AssertParsed(r, "Lenovo", "ThinkPad", "T14 Gen 2i");
            Assert.Equal("20W0S22200", r.ProductCode);
        }

        [Fact]
        public void Lenovo_IdeaPad() =>
            AssertParsed(ModelParser.Parse("LENOVO", "82H8", "IdeaPad 3 15ITL6", "IdeaPad 3 15ITL6"), "Lenovo", "IdeaPad", "3 15ITL6");

        [Fact]
        public void Lenovo_with_only_machine_type_does_not_invent_model()
        {
            var r = ModelParser.Parse("LENOVO", "20W0S22200", "", "");
            AssertParsed(r, "Lenovo", "Unknown", "20W0S22200");
        }

        [Fact]
        public void Asus_underscore_duplicates_cleaned() =>
            AssertParsed(ModelParser.Parse("ASUSTeK COMPUTER INC.", "VivoBook_ASUSLaptop X515EA_X515EA"), "ASUS", "VivoBook", "X515EA");

        [Fact]
        public void Asus_TUF_gaming() =>
            AssertParsed(ModelParser.Parse("ASUSTeK COMPUTER INC.", "ASUS TUF Gaming F15 FX506HC_FX506HC"), "ASUS", "TUF Gaming", "F15 FX506HC");

        [Fact]
        public void Acer_Aspire_with_number() =>
            AssertParsed(ModelParser.Parse("Acer", "Aspire A515-56", "Aspire 5"), "Acer", "Aspire", "A515-56");

        [Fact]
        public void Msi_modern() =>
            AssertParsed(ModelParser.Parse("Micro-Star International Co., Ltd.", "Modern 14 B11MOU"), "MSI", "Modern", "14 B11MOU");

        [Fact]
        public void Microsoft_surface_laptop_keeps_laptop_word() =>
            AssertParsed(ModelParser.Parse("Microsoft Corporation", "Surface Laptop 4"), "Microsoft", "Surface Laptop", "4");

        [Fact]
        public void Microsoft_surface_pro() =>
            AssertParsed(ModelParser.Parse("Microsoft Corporation", "Surface Pro 7+"), "Microsoft", "Surface Pro", "7+");

        [Fact]
        public void Huawei_uses_family_when_model_is_code()
        {
            var r = ModelParser.Parse("HUAWEI", "BoDE-WXX9", "MateBook D 14");
            AssertParsed(r, "Huawei", "MateBook D", "14");
            Assert.Equal("BoDE-WXX9", r.ProductCode);
        }

        [Fact]
        public void Samsung_galaxy_book() =>
            AssertParsed(ModelParser.Parse("SAMSUNG ELECTRONICS CO., LTD.", "950XED", "Galaxy Book2 Pro"), "Samsung", "Galaxy Book", "2 Pro");

        [Fact]
        public void Dell_G_series_line_with_digits() =>
            AssertParsed(ModelParser.Parse("Dell Inc.", "Dell G15 5520"), "Dell", "G15", "5520");

        [Fact]
        public void Dell_XPS() =>
            AssertParsed(ModelParser.Parse("Dell Inc.", "XPS 13 9310"), "Dell", "XPS", "13 9310");

        [Fact]
        public void Unknown_manufacturer_is_title_cased_and_model_still_split()
        {
            var r = ModelParser.Parse("ACME LAPTOP CORPORATION", "Voyager 15 Pro");
            Assert.Equal("Acme Laptop", r.Brand);
            Assert.Equal("Voyager", r.Model);
            Assert.Equal("15 Pro", r.ModelNumber);
        }

        [Fact]
        public void Placeholder_values_give_unknown() =>
            AssertParsed(ModelParser.Parse("To be filled by O.E.M.", "System Product Name", "Default string", "To be filled by O.E.M."),
                "Unknown", "Unknown", "Unknown");

        [Fact]
        public void Missing_manufacturer_brand_taken_from_model() =>
            AssertParsed(ModelParser.Parse("", "Dell Latitude 7420"), "Dell", "Latitude", "7420");
    }
}

