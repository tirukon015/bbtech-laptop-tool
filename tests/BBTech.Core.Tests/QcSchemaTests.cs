using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BBTech.Core.Integration;
using BBTech.Core.Storage;
using Xunit;

namespace BBTech.Core.Tests
{
    public class QcSchemaTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-schema-tests-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly OperatorSnapshot Op = new OperatorSnapshot { OperatorId = "op", Name = "Anika", Role = "QC" };

        private QcOpenResult OpenResult() => QcRepository.Open(_dir, new FakeMachine(1), QcStoreTests.LaptopA);

        private QcRepository Open()
        {
            var r = OpenResult();
            Assert.True(r.Ok, r.Message);
            return r.Repository;
        }

        /// <summary>Appends an entry exactly as given (simulates an older or newer version of the tool).</summary>
        private static void AppendRaw(QcRepository repo, JournalEntry e)
        {
            var journal = (EncryptedJournal)typeof(QcRepository).GetField("_journal", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(repo);
            journal.Append(Json.ToBytes(e));
        }

        private string Backup(int version) => Path.Combine(_dir, QcRepository.BackupPrefix + version + ".bak");

        [Fact]
        public void New_entries_are_written_with_the_current_schema_version()
        {
            var repo = Open();
            repo.RegisterDevice(QcStoreTests.LaptopA, null);
            var journal = (EncryptedJournal)typeof(QcRepository).GetField("_journal", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(repo);
            var entries = journal.ReadAll(out _).Select(Json.FromBytes<JournalEntry>).ToList();
            Assert.All(entries, e => Assert.Equal(QcSchema.Current, e.SchemaVersion));
        }

        [Fact]
        public void Entries_without_a_version_are_version_1()
        {
            Assert.Equal(1, QcSchema.VersionOf(new JournalEntry()));
            Assert.Equal(2, QcSchema.VersionOf(new JournalEntry { SchemaVersion = 2 }));
        }

        [Fact]
        public void Legacy_sync_statuses_are_upgraded_on_read_without_rewriting_the_file()
        {
            var repo = Open();
            var d = repo.RegisterDevice(QcStoreTests.LaptopA, null);
            var i = repo.StartInspection(d.DeviceId, Op, "1.0.0", null);
            AppendRaw(repo, new JournalEntry
            {
                Kind = JournalEntry.KindSync, SchemaVersion = 0, At = DateTime.Now,
                Sync = new SyncRecord { SyncId = "legacy-1", EntityType = "inspection", EntityId = i.InspectionId, SyncStatus = "LOCAL_ONLY" },
            });
            long sizeBefore = new FileInfo(Path.Combine(_dir, EncryptedJournal.FileName)).Length;

            var reopened = Open();
            Assert.Equal(TransmissionState.Local, reopened.SyncRecords.Single(s => s.SyncId == "legacy-1").SyncStatus);
            Assert.False(reopened.IsReadOnly);
            Assert.Equal(sizeBefore, new FileInfo(Path.Combine(_dir, EncryptedJournal.FileName)).Length);
        }

        [Fact]
        public void A_journal_with_older_entries_is_backed_up_once()
        {
            var repo = Open();
            AppendRaw(repo, new JournalEntry
            {
                Kind = JournalEntry.KindSync, At = DateTime.Now,
                Sync = new SyncRecord { SyncId = "legacy-1", EntityType = "inspection", EntityId = "x", SyncStatus = "SYNC_FAILED" },
            });
            Open();
            Assert.True(File.Exists(Backup(1)));
            var first = File.GetLastWriteTimeUtc(Backup(1));
            long size = new FileInfo(Backup(1)).Length;

            Open().RegisterDevice(QcStoreTests.LaptopA, null); // more data after the backup
            Open();
            Assert.Equal(size, new FileInfo(Backup(1)).Length); // never overwritten
            Assert.Equal(first, File.GetLastWriteTimeUtc(Backup(1)));
        }

        [Fact]
        public void A_current_journal_is_not_backed_up()
        {
            Open().RegisterDevice(QcStoreTests.LaptopA, null);
            Open();
            Assert.Empty(Directory.GetFiles(_dir, QcRepository.BackupPrefix + "*"));
        }

        [Fact]
        public void A_journal_from_a_newer_version_is_shown_but_never_changed()
        {
            var repo = Open();
            var d = repo.RegisterDevice(QcStoreTests.LaptopA, null);
            repo.CompleteInspection(repo.StartInspection(d.DeviceId, Op, "1.0.0", null).InspectionId, "PASS");
            AppendRaw(repo, new JournalEntry
            {
                Kind = JournalEntry.KindSync, SchemaVersion = QcSchema.Current + 1, At = DateTime.Now,
                Sync = new SyncRecord { SyncId = "future", EntityType = "device", EntityId = d.DeviceId, SyncStatus = "SOME_FUTURE_STATE" },
            });
            string path = Path.Combine(_dir, EncryptedJournal.FileName);
            long size = new FileInfo(path).Length;

            var open = OpenResult();
            Assert.True(open.Ok);
            Assert.True(open.Repository.IsReadOnly);
            Assert.Contains("newer version", open.Message);
            Assert.Single(open.Repository.GetHistory(d.DeviceId)); // history is still shown

            Assert.Throws<InvalidOperationException>(() => open.Repository.StartInspection(d.DeviceId, Op, "1.0.0", null));
            Assert.Throws<InvalidOperationException>(() => open.Repository.RegisterDevice(QcStoreTests.LaptopB, null));
            Assert.Equal(size, new FileInfo(path).Length);
        }

        [Fact]
        public void Upgrade_leaves_current_entries_unchanged()
        {
            var e = new JournalEntry
            {
                Kind = JournalEntry.KindSync, SchemaVersion = QcSchema.Current,
                Sync = new SyncRecord { SyncStatus = TransmissionState.Sent },
            };
            Assert.Equal(TransmissionState.Sent, QcSchema.Upgrade(e).Sync.SyncStatus);
            Assert.Equal(QcSchema.Current, e.SchemaVersion);
        }

        [Fact]
        public void Upgrade_of_a_legacy_synced_record_does_not_claim_itms_receipt()
        {
            var e = new JournalEntry { Kind = JournalEntry.KindSync, Sync = new SyncRecord { SyncStatus = "SYNCED", RemoteId = "x" } };
            Assert.Equal(TransmissionState.Sent, QcSchema.Upgrade(e).Sync.SyncStatus);
            Assert.False(TransmissionState.IsConfirmed(e.Sync));
        }

        [Fact]
        public void Upgrade_ignores_entries_without_a_sync_record()
        {
            var e = new JournalEntry { Kind = JournalEntry.KindSync };
            Assert.Same(e, QcSchema.Upgrade(e));
            Assert.Equal(QcSchema.Current, e.SchemaVersion);
        }

        [Fact]
        public void Completing_an_inspection_creates_a_local_transmission_record()
        {
            var repo = Open();
            var d = repo.RegisterDevice(QcStoreTests.LaptopA, null);
            var i = repo.StartInspection(d.DeviceId, Op, "1.0.0", null);
            repo.CompleteInspection(i.InspectionId, "PASS");
            var sync = Open().GetSyncRecord(SyncEntity.Inspection, i.InspectionId);
            Assert.Equal(TransmissionState.Local, sync.SyncStatus);
            Assert.Null(sync.RequestId);
            Assert.NotNull(sync.UpdatedAt);
        }
    }
}
