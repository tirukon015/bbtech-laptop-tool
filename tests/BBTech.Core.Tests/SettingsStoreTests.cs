using System;
using System.IO;
using System.Linq;
using System.Text;
using BBTech.Core.Settings;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>The encrypted settings file on the BBTech USB (example names and passwords only).</summary>
    public class SettingsStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-store-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_dir, "config", "settings.dat");
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly DateTime Fixed = new DateTime(2026, 10, 5, 8, 0, 0);

        [Fact]
        public void A_missing_settings_file_gives_defaults_and_is_not_created_by_loading()
        {
            var s = SettingsStore.Load(FilePath);
            Assert.False(s.LoadFailed);
            Assert.False(s.HasPin);
            Assert.Empty(s.ActiveOperators);
            Assert.Empty(s.WifiNetworks);
            Assert.False(File.Exists(FilePath));
        }

        [Fact]
        public void Saved_settings_have_the_magic_header_and_no_readable_content()
        {
            var s = SettingsStore.Load(FilePath);
            s.AddOperator("Operator One", "QC");
            s.AddOrUpdateWifi(null, "BBTECH_EXAMPLE", WifiSecurity.Wpa2Personal, "example-pass", false);
            byte[] raw = File.ReadAllBytes(FilePath);
            Assert.Equal("BBTS0001", Encoding.ASCII.GetString(raw, 0, 8));
            string text = Encoding.UTF8.GetString(raw);
            Assert.DoesNotContain("Operator One", text);
            Assert.DoesNotContain("BBTECH_EXAMPLE", text);
            Assert.DoesNotContain("example-pass", text);
        }

        [Fact]
        public void Each_save_uses_a_new_salt()
        {
            var s = SettingsStore.Load(FilePath);
            s.AddOperator("Operator One", "QC");
            byte[] first = File.ReadAllBytes(FilePath);
            s.Save();
            byte[] second = File.ReadAllBytes(FilePath);
            Assert.NotEqual(first.Skip(8).Take(16), second.Skip(8).Take(16));
            Assert.Equal("Operator One", SettingsStore.Load(FilePath).ActiveOperators.Single().Name);
        }

        [Fact]
        public void A_tampered_file_is_rejected_and_never_overwritten()
        {
            var s = SettingsStore.Load(FilePath);
            s.AddOperator("Operator One", "QC");
            File.Delete(FilePath + ".bak");
            byte[] raw = File.ReadAllBytes(FilePath);
            raw[raw.Length / 2] ^= 0x20;
            File.WriteAllBytes(FilePath, raw);

            var damaged = SettingsStore.Load(FilePath);
            Assert.True(damaged.LoadFailed);
            Assert.Throws<InvalidOperationException>(() => damaged.Save());
            Assert.Throws<InvalidOperationException>(() => damaged.AddOperator("Operator Two", "QC"));
            Assert.Equal(raw, File.ReadAllBytes(FilePath));
        }

        [Fact]
        public void The_backup_copy_is_used_when_the_main_file_is_damaged()
        {
            var s = SettingsStore.Load(FilePath);
            s.AddOperator("Operator One", "QC");
            s.AddOperator("Operator Two", "QC");   // the previous version is now in settings.dat.bak
            File.WriteAllBytes(FilePath, Encoding.ASCII.GetBytes("BBTS0001 garbage"));
            var again = SettingsStore.Load(FilePath);
            Assert.False(again.LoadFailed);
            Assert.Equal(new[] { "Operator One" }, again.ActiveOperators.Select(o => o.Name));
        }

        [Fact]
        public void A_file_with_a_foreign_header_is_treated_as_damaged()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, "{\"Operators\":[]}");
            Assert.True(SettingsStore.Load(FilePath).LoadFailed);
        }

        [Fact]
        public void Admin_pin_is_hashed_verified_and_persisted()
        {
            var s = SettingsStore.Load(FilePath);
            Assert.False(s.VerifyPin("2468"));
            s.SetPin("2468");
            Assert.True(s.HasPin);
            Assert.Equal(120000, s.Settings.AdminPinIterations);
            var again = SettingsStore.Load(FilePath);
            Assert.True(again.VerifyPin("2468"));
            Assert.False(again.VerifyPin("2469"));
            Assert.DoesNotContain("2468", again.Settings.AdminPinHash);
        }

        [Theory]
        [InlineData("12")]
        [InlineData("12ab")]
        [InlineData("1234567890123")]
        [InlineData(null)]
        public void Invalid_pins_are_rejected(string pin)
        {
            var s = SettingsStore.Load(FilePath);
            Assert.Throws<ArgumentException>(() => s.SetPin(pin));
            Assert.False(s.HasPin);
        }

        [Fact]
        public void A_pin_without_a_stored_iteration_count_uses_the_default()
        {
            var s = SettingsStore.Load(FilePath);
            s.SetPin("8642");
            s.Settings.AdminPinIterations = 0;
            s.Save();
            Assert.True(SettingsStore.Load(FilePath).VerifyPin("8642"));
        }

        [Fact]
        public void New_operators_get_the_qc_role_by_default_and_a_trimmed_name()
        {
            var s = SettingsStore.Load(FilePath, () => Fixed);
            var op = s.AddOperator("  Operator One  ", "  ");
            Assert.Equal("Operator One", op.Name);
            Assert.Equal("QC", op.Role);
            Assert.True(op.Active);
            Assert.Equal(Fixed, op.CreatedAt);
            Assert.Equal(32, op.OperatorId.Length);
        }

        [Fact]
        public void Editing_without_a_role_keeps_the_existing_role()
        {
            var s = SettingsStore.Load(FilePath);
            var op = s.AddOperator("Operator One", "Senior QC");
            s.UpdateOperator(op.OperatorId, "Operator 1", "");
            var again = SettingsStore.Load(FilePath).FindOperator(op.OperatorId);
            Assert.Equal("Operator 1", again.Name);
            Assert.Equal("Senior QC", again.Role);
        }

        [Fact]
        public void Unknown_operator_ids_are_rejected()
        {
            var s = SettingsStore.Load(FilePath);
            Assert.Throws<ArgumentException>(() => s.UpdateOperator("missing", "X", "QC"));
            Assert.Throws<ArgumentException>(() => s.SetOperatorActive("missing", false));
            Assert.Throws<ArgumentException>(() => s.SelectOperator("missing"));
            Assert.Null(s.FindOperator(null));
        }

        [Fact]
        public void Active_operators_are_sorted_by_name_ignoring_case()
        {
            var s = SettingsStore.Load(FilePath);
            s.AddOperator("rahim", "QC");
            s.AddOperator("Anika", "QC");
            s.AddOperator("jimmy", "QC");
            Assert.Equal(new[] { "Anika", "jimmy", "rahim" }, s.ActiveOperators.Select(o => o.Name));
        }

        [Fact]
        public void Open_networks_are_saved_without_a_password()
        {
            var s = SettingsStore.Load(FilePath);
            var n = s.AddOrUpdateWifi(null, "BBTECH_GUEST_EXAMPLE", WifiSecurity.Open, "ignored-pass", true);
            Assert.Equal("", n.Password);
            Assert.True(SettingsStore.Load(FilePath).WifiNetworks.Single().Hidden);
        }

    }
}
