using System;
using System.IO;
using System.Linq;
using BBTech.Core.Settings;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>Rules for the admin-managed operator list (names stay unambiguous in TESTED BY).</summary>
    public class OperatorRulesTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-oprules-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_dir, "config", "settings.dat");
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void Renaming_to_another_active_operators_name_is_rejected()
        {
            var s = SettingsStore.Load(FilePath);
            s.AddOperator("Jimmy", "QC");
            var rahim = s.AddOperator("Rahim", "QC");
            var ex = Assert.Throws<ArgumentException>(() => s.UpdateOperator(rahim.OperatorId, "jimmy", "QC"));
            Assert.Contains("already exists", ex.Message);
            Assert.Equal("Rahim", SettingsStore.Load(FilePath).FindOperator(rahim.OperatorId).Name);
        }

        [Fact]
        public void Renaming_an_operator_to_its_own_name_with_different_case_is_allowed()
        {
            var s = SettingsStore.Load(FilePath);
            var jimmy = s.AddOperator("jimmy", "QC");
            s.UpdateOperator(jimmy.OperatorId, "Jimmy", "Senior QC");
            var again = SettingsStore.Load(FilePath).FindOperator(jimmy.OperatorId);
            Assert.Equal("Jimmy", again.Name);
            Assert.Equal("Senior QC", again.Role);
        }

        [Fact]
        public void Reactivating_is_rejected_when_an_active_operator_already_uses_the_name()
        {
            var s = SettingsStore.Load(FilePath);
            var oldJimmy = s.AddOperator("Jimmy", "QC");
            s.SetOperatorActive(oldJimmy.OperatorId, false);
            s.AddOperator("Jimmy", "QC"); // a new staff member with the same name
            Assert.Throws<ArgumentException>(() => s.SetOperatorActive(oldJimmy.OperatorId, true));
            Assert.Single(SettingsStore.Load(FilePath).ActiveOperators);
        }
    }
}
