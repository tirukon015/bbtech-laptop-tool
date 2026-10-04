using System;
using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    public class TestCatalogTests
    {
        [Fact]
        public void Catalog_is_built_once_and_cannot_be_modified_by_callers()
        {
            Assert.Same(TestCatalog.All, TestCatalog.All);
            var list = Assert.IsAssignableFrom<IList<HardwareTest>>(TestCatalog.All);
            Assert.True(list.IsReadOnly);
        }

        [Fact]
        public void Every_hardware_test_appears_exactly_once()
        {
            var all = TestCatalog.All;
            Assert.Equal(Enum.GetValues(typeof(HardwareTest)).Length, all.Count);
            Assert.Equal(all.Count, all.Distinct().Count());
        }

        [Fact]
        public void Required_tests_come_first_in_display_order()
        {
            Assert.Equal(TestCatalog.RequiredTests, TestCatalog.All.Take(4));
            Assert.True(TestCatalog.Order(HardwareTest.Display) < TestCatalog.Order(HardwareTest.Storage));
        }

        [Fact]
        public void Unknown_test_values_sort_last()
        {
            Assert.Equal(int.MaxValue, TestCatalog.Order((HardwareTest)999));
            Assert.False(TestCatalog.IsRequired((HardwareTest)999));
        }
    }
}
