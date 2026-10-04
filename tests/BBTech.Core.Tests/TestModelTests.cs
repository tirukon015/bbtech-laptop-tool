using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using BBTech.Core.Export;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Test items, plans, labels and the ITMS extension points.</summary>
    public class TestModelTests
    {
        [Fact]
        public void Test_items_notify_the_ui_about_state_and_notes_changes()
        {
            var item = new TestItem(HardwareTest.Display, "Colours and patterns");
            var changed = new List<string>();
            item.PropertyChanged += (s, e) => changed.Add(e.PropertyName);
            item.State = TestState.Pass;
            item.Notes = "no dead pixels";
            Assert.Contains("State", changed);
            Assert.Contains("StateLabel", changed);
            Assert.Contains("Notes", changed);
        }

    }
}
