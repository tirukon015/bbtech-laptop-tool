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

    }
}
