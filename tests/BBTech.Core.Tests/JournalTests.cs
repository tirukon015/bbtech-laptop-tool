using System;
using System.IO;
using System.Linq;
using System.Text;
using BBTech.Core.Security;
using BBTech.Core.Storage;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>The append-only encrypted journal (qc.journal) on its own.</summary>
    public class JournalTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-journal-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_dir, EncryptedJournal.FileName);
        private readonly EnvelopeCrypto _crypto = new EnvelopeCrypto(EnvelopeCrypto.NewKey());

        public JournalTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { if (File.Exists(FilePath)) File.SetAttributes(FilePath, FileAttributes.Normal); Directory.Delete(_dir, true); } catch { }
        }

        private EncryptedJournal Journal() => new EncryptedJournal(FilePath, _crypto);

        private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

        [Fact]
        public void A_read_only_journal_with_a_partial_tail_is_still_readable()
        {
            var j = Journal();
            j.Append(B("one"));
            j.Append(B("two"));
            File.AppendAllText(FilePath, "BQR1\x10\x00"); // crash in the middle of the next record header
            long size = new FileInfo(FilePath).Length;
            File.SetAttributes(FilePath, FileAttributes.ReadOnly);

            var records = Journal().ReadAll(out var stats);

            Assert.Equal(new[] { "one", "two" }, records.Select(r => Encoding.UTF8.GetString(r)));
            Assert.True(stats.TruncatedTailBytes > 0);
            Assert.True(stats.TailRepairFailed);
            Assert.Equal(size, new FileInfo(FilePath).Length);
        }

        [Fact]
        public void Records_are_read_back_in_the_order_they_were_appended()
        {
            var j = Journal();
            for (int i = 0; i < 25; i++) j.Append(B("record " + i));
            var records = Journal().ReadAll(out var stats);
            Assert.Equal(25, stats.Valid);
            Assert.Equal(Enumerable.Range(0, 25).Select(i => "record " + i), records.Select(r => Encoding.UTF8.GetString(r)));
        }

        private static int CountOf(string s, string part)
        {
            int n = 0;
            for (int i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + 1, StringComparison.Ordinal)) n++;
            return n;
        }

        [Fact]
        public void The_file_header_is_written_once_at_the_start()
        {
            var j = Journal();
            j.Append(B("a"));
            j.Append(B("b"));
            byte[] data = File.ReadAllBytes(FilePath);
            string text = Encoding.ASCII.GetString(data);
            Assert.StartsWith("BBQCJ001BQR1", text);
            Assert.Equal(1, CountOf(text, "BBQCJ001"));
        }

        [Fact]
        public void Journal_files_contain_no_readable_record_text()
        {
            Journal().Append(B("Tested By: Operator One | Serial SERIAL-EXAMPLE | PASS"));
            string raw = Encoding.UTF8.GetString(File.ReadAllBytes(FilePath));
            Assert.DoesNotContain("Operator One", raw);
            Assert.DoesNotContain("SERIAL-EXAMPLE", raw);
            Assert.DoesNotContain("PASS", raw);
        }

        [Fact]
        public void HasData_is_false_for_missing_or_header_only_files()
        {
            Assert.False(EncryptedJournal.HasData(FilePath));
            File.WriteAllBytes(FilePath, Encoding.ASCII.GetBytes("BBQCJ001"));
            Assert.False(EncryptedJournal.HasData(FilePath));
            Journal().Append(B("x"));
            Assert.True(EncryptedJournal.HasData(FilePath));
        }

    }
}
