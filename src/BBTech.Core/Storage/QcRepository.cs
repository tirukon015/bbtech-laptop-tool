using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBTech.Core.Identity;
using BBTech.Core.Security;

namespace BBTech.Core.Storage
{
    public sealed class QcOpenResult
    {
        public QcRepository Repository { get; set; }
        public KeyState KeyState { get; set; }
        public JournalReadStats Stats { get; set; }
        public string Message { get; set; } = "";
        /// <summary>True when no QC store exists on this laptop (it was never tested here).</summary>
        public bool NoStore { get; set; }
        public bool Ok => Repository != null;
    }

    /// <summary>The operator recorded on an inspection (a snapshot: later renames never change history).</summary>
    public sealed class OperatorSnapshot
    {
        public string OperatorId { get; set; }
        public string Name { get; set; }
        public string Role { get; set; }
    }

    /// <summary>
    /// Local, encrypted QC history of the laptop it runs on (Device → Inspections → TestResults).
    ///
    /// Source of truth: the encrypted journal in the laptop's protected data folder. All changes are
    /// appended and flushed immediately (each test result is persisted the moment it is recorded).
    /// Completed inspections are immutable: later writes to them are rejected. Starting a new QC session
    /// always creates a new inspection; earlier ones are never overwritten or deleted.
    /// </summary>
    public sealed class QcRepository
    {
        private readonly EncryptedJournal _journal;
        private readonly object _sync = new object();
        private readonly Func<DateTime> _clock;

        private readonly Dictionary<string, DeviceRecord> _devices = new Dictionary<string, DeviceRecord>();
        private readonly Dictionary<string, InspectionRecord> _inspections = new Dictionary<string, InspectionRecord>();
        private readonly Dictionary<string, Dictionary<string, TestResultRecord>> _results = new Dictionary<string, Dictionary<string, TestResultRecord>>();
        private readonly Dictionary<string, SyncRecord> _sync2 = new Dictionary<string, SyncRecord>();
        private readonly Dictionary<string, WifiProvisionRecord> _wifi = new Dictionary<string, WifiProvisionRecord>(StringComparer.Ordinal);

        public string Folder { get; }

        private QcRepository(string folder, EncryptedJournal journal, Func<DateTime> clock)
        {
            Folder = folder;
            _journal = journal;
            _clock = clock ?? (() => DateTime.Now);
        }

        /// <summary>Opens (or creates) the store in <paramref name="folder"/>.</summary>
        /// <param name="createIfMissing">false for read-only lookups (DETECT): nothing is created on a laptop that was never tested.</param>
        public static QcOpenResult Open(string folder, IMachineKeyProtector machine, DeviceIdentity identity, Func<DateTime> clock = null, bool createIfMissing = true)
        {
            string journalPath = Path.Combine(folder, EncryptedJournal.FileName);
            if (!createIfMissing && !File.Exists(journalPath) && !File.Exists(Path.Combine(folder, KeyVault.KeyFileName)))
                return new QcOpenResult { NoStore = true, Message = "No QC inspection found for this device." };
            Directory.CreateDirectory(folder);
            bool hasData = EncryptedJournal.HasData(journalPath);

            var unlock = KeyVault.Open(folder, machine, identity, hasData);
            if (!unlock.Ok)
                return new QcOpenResult { KeyState = unlock.State, Message = unlock.Message };

            var journal = new EncryptedJournal(journalPath, new EnvelopeCrypto(unlock.Key));
            var repo = new QcRepository(folder, journal, clock);
            var records = journal.ReadAll(out var stats);
            foreach (var r in records)
            {
                JournalEntry e;
                try { e = Json.FromBytes<JournalEntry>(r); }
                catch { stats.Corrupted++; continue; }
                repo.Apply(e);
            }

            string msg = unlock.Message;
            if (stats.Corrupted > 0) msg = (msg + " " + stats.Corrupted + " damaged history record(s) could not be read and were skipped.").Trim();
            return new QcOpenResult { Repository = repo, KeyState = unlock.State, Stats = stats, Message = msg };
        }

        // ---- Replay (rebuilds the in-memory view; enforces immutability of completed inspections) ----

        private void Apply(JournalEntry e)
        {
            switch (e.Kind)
            {
                case JournalEntry.KindDevice when e.Device != null:
                    _devices[e.Device.DeviceId] = e.Device;
                    break;
                case JournalEntry.KindInspection when e.Inspection != null:
                    if (_inspections.TryGetValue(e.Inspection.InspectionId, out var existing) && !existing.IsOpen) break; // immutable
                    _inspections[e.Inspection.InspectionId] = e.Inspection;
                    break;
                case JournalEntry.KindResult when e.Result != null:
                    if (_inspections.TryGetValue(e.Result.InspectionId, out var insp) && !insp.IsOpen) break; // immutable
                    if (!_results.TryGetValue(e.Result.InspectionId, out var map)) _results[e.Result.InspectionId] = map = new Dictionary<string, TestResultRecord>();
                    map[e.Result.TestId] = e.Result;
                    break;
                case JournalEntry.KindSync when e.Sync != null:
                    _sync2[e.Sync.SyncId] = e.Sync;
                    break;
                case JournalEntry.KindWifi when e.Wifi != null:
                    _wifi[e.Wifi.Ssid] = e.Wifi;
                    break;
            }
        }

        private void Write(JournalEntry e)
        {
            e.At = _clock();
            _journal.Append(Json.ToBytes(e)); // persisted (flushed) before the in-memory view changes
            Apply(e);
        }

        // ---- Devices ----

        public List<DeviceRecord> Devices { get { lock (_sync) return _devices.Values.ToList(); } }

        public MatchResult FindDevice(DeviceIdentity identity)
        {
            lock (_sync) return DeviceMatcher.Match(identity, _devices.Values);
        }

        /// <summary>
        /// Creates a device record for this laptop. Call only after FindDevice returned NotFound,
        /// or (with <paramref name="identityNote"/>) after staff confirmed a conflict.
        /// </summary>
        public DeviceRecord RegisterDevice(DeviceIdentity id, DeviceSpecs specs, string identityNote = null)
        {
            if (id == null || !id.HasStrongIdentifier) throw new InvalidOperationException("A device needs at least one stable identifier.");
            lock (_sync)
            {
                var now = _clock();
                var d = new DeviceRecord
                {
                    DeviceId = Guid.NewGuid().ToString("N"),
                    SerialNumber = id.SerialNumber, SystemUuid = id.SystemUuid, BoardSerial = id.BoardSerial,
                    SerialHash = id.SerialHash, UuidHash = id.UuidHash, BoardSerialHash = id.BoardSerialHash,
                    HardwareFingerprint = id.Fingerprint,
                    Manufacturer = id.Manufacturer, Model = id.Model, ModelNumber = id.ModelNumber,
                    Specs = specs, IdentityNote = identityNote, CreatedAt = now, UpdatedAt = now,
                };
                Write(new JournalEntry { Kind = JournalEntry.KindDevice, Device = d });
                return d;
            }
        }

        /// <summary>Refreshes the latest hardware summary and fills identifiers that were missing before.</summary>
        public DeviceRecord UpdateDevice(string deviceId, DeviceIdentity id, DeviceSpecs specs)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            lock (_sync)
            {
                if (deviceId == null || !_devices.TryGetValue(deviceId, out var old)) throw new InvalidOperationException("Unknown device.");
                var d = Clone(old);
                d.Specs = specs ?? old.Specs;
                if (d.SerialHash == null && id.SerialHash != null) { d.SerialHash = id.SerialHash; d.SerialNumber = id.SerialNumber; }
                if (d.UuidHash == null && id.UuidHash != null) { d.UuidHash = id.UuidHash; d.SystemUuid = id.SystemUuid; }
                if (d.BoardSerialHash == null && id.BoardSerialHash != null) { d.BoardSerialHash = id.BoardSerialHash; d.BoardSerial = id.BoardSerial; }
                d.HardwareFingerprint = id.Fingerprint ?? d.HardwareFingerprint;
                d.UpdatedAt = _clock();
                Write(new JournalEntry { Kind = JournalEntry.KindDevice, Device = d });
                return d;
            }
        }

        // ---- Inspections ----

        public InspectionRecord StartInspection(string deviceId, OperatorSnapshot op, string appVersion, DeviceSpecs specs,
                                                string testMode = null, IEnumerable<string> selectedTests = null)
        {
            if (op == null || string.IsNullOrWhiteSpace(op.Name)) throw new InvalidOperationException("An operator must be selected.");
            lock (_sync)
            {
                if (deviceId == null || !_devices.ContainsKey(deviceId)) throw new InvalidOperationException("Unknown device.");
                var now = _clock();
                var i = new InspectionRecord
                {
                    InspectionId = Guid.NewGuid().ToString("N"),
                    DeviceId = deviceId,
                    Number = _inspections.Values.Count(x => x.DeviceId == deviceId) + 1,
                    OperatorId = op.OperatorId,
                    OperatorNameSnapshot = op.Name,
                    OperatorRoleSnapshot = op.Role,
                    Status = InspectionStatus.InProgress,
                    AppVersion = appVersion,
                    Specs = specs,
                    TestMode = testMode,
                    SelectedTests = selectedTests?.ToList(),
                    StartedAt = now, CreatedAt = now, UpdatedAt = now,
                };
                Write(new JournalEntry { Kind = JournalEntry.KindInspection, Inspection = i });
                return i;
            }
        }

        /// <summary>Records the test mode and the exact set of selected tests of an open inspection (persisted immediately).</summary>
        public InspectionRecord UpdateTestSelection(string inspectionId, string testMode, IEnumerable<string> selectedTests)
        {
            lock (_sync)
            {
                var insp = Clone(RequireOpen(inspectionId));
                insp.TestMode = testMode;
                insp.SelectedTests = selectedTests?.ToList();
                insp.UpdatedAt = _clock();
                Write(new JournalEntry { Kind = JournalEntry.KindInspection, Inspection = insp });
                return insp;
            }
        }

        /// <summary>The unfinished inspection of a device (e.g. after a crash), or null.</summary>
        public InspectionRecord GetOpenInspection(string deviceId)
        {
            lock (_sync)
                return _inspections.Values.Where(i => i.DeviceId == deviceId && i.IsOpen).OrderByDescending(i => i.StartedAt).ThenByDescending(i => i.Number).FirstOrDefault();
        }

        /// <summary>Test status labels that may be stored (see TestText.Label).</summary>
        public static readonly string[] ResultStatuses = { "NOT TESTED", "TESTING", "PASS", "FAIL", "SKIPPED" };

        /// <summary>Records one test's current state; persisted immediately.</summary>
        public TestResultRecord SaveResult(string inspectionId, string testId, string testName, string status, bool required, string comment, string details = null)
        {
            if (string.IsNullOrWhiteSpace(testId)) throw new ArgumentException("A test id is required.", nameof(testId));
            if (Array.IndexOf(ResultStatuses, status) < 0) throw new ArgumentException("Unknown test status: " + status, nameof(status));
            lock (_sync)
            {
                RequireOpen(inspectionId);
                var now = _clock();
                _results.TryGetValue(inspectionId, out var map);
                TestResultRecord prev = null;
                map?.TryGetValue(testId, out prev);
                bool finished = status == "PASS" || status == "FAIL" || status == "SKIPPED";
                var r = new TestResultRecord
                {
                    TestResultId = prev?.TestResultId ?? Guid.NewGuid().ToString("N"),
                    InspectionId = inspectionId,
                    TestId = testId,
                    TestName = testName,
                    Status = status,
                    Required = required,
                    Comment = comment ?? "",
                    Details = details,
                    StartedAt = prev?.StartedAt ?? (status == "NOT TESTED" ? (DateTime?)null : now),
                    CompletedAt = finished ? now : (DateTime?)null,
                };
                Write(new JournalEntry { Kind = JournalEntry.KindResult, Result = r });
                return r;
            }
        }

        /// <summary>Finishes an inspection. It becomes immutable history.</summary>
        public InspectionRecord CompleteInspection(string inspectionId, string overallStatus)
        {
            if (overallStatus != "PASS" && overallStatus != "FAIL" && overallStatus != "INCOMPLETE")
                throw new ArgumentException("Unknown overall result: " + overallStatus, nameof(overallStatus));
            lock (_sync)
            {
                var insp = Clone(RequireOpen(inspectionId));
                var now = _clock();
                insp.Status = InspectionStatus.Completed;
                insp.OverallStatus = overallStatus;
                insp.CompletedAt = now;
                insp.UpdatedAt = now;
                Write(new JournalEntry { Kind = JournalEntry.KindInspection, Inspection = insp });
                // Future ITMS sync bookkeeping (no network activity).
                Write(new JournalEntry
                {
                    Kind = JournalEntry.KindSync,
                    Sync = new SyncRecord { SyncId = Guid.NewGuid().ToString("N"), EntityType = "inspection", EntityId = inspectionId, SyncStatus = SyncStatus.LocalOnly },
                });
                return insp;
            }
        }

        /// <summary>Closes an unfinished inspection that will not be resumed. It is KEPT as history.</summary>
        public InspectionRecord AbandonInspection(string inspectionId)
        {
            lock (_sync)
            {
                var insp = Clone(RequireOpen(inspectionId));
                insp.Status = InspectionStatus.Abandoned;
                insp.OverallStatus = "INCOMPLETE";
                insp.UpdatedAt = _clock();
                Write(new JournalEntry { Kind = JournalEntry.KindInspection, Inspection = insp });
                return insp;
            }
        }

        public InspectionRecord GetInspection(string inspectionId)
        {
            if (inspectionId == null) return null;
            lock (_sync) return _inspections.TryGetValue(inspectionId, out var i) ? i : null;
        }

        public List<TestResultRecord> GetResults(string inspectionId)
        {
            if (inspectionId == null) return new List<TestResultRecord>();
            lock (_sync)
                return _results.TryGetValue(inspectionId, out var map) ? map.Values.ToList() : new List<TestResultRecord>();
        }

        /// <summary>All inspections of a device, newest first, with their results.</summary>
        public List<InspectionView> GetHistory(string deviceId)
        {
            lock (_sync)
                return _inspections.Values.Where(i => i.DeviceId == deviceId)
                    .OrderByDescending(i => i.StartedAt).ThenByDescending(i => i.Number)
                    .Select(i => new InspectionView { Inspection = i, Results = GetResults(i.InspectionId) })
                    .ToList();
        }

        public List<SyncRecord> SyncRecords { get { lock (_sync) return _sync2.Values.ToList(); } }

        // ---- Wi-Fi provisioning state of this laptop (no passwords) ----

        public WifiProvisionRecord GetWifiState(string ssid)
        {
            lock (_sync) return _wifi.TryGetValue(ssid, out var w) ? w : null;
        }

        public void SetWifiState(WifiProvisionRecord record)
        {
            lock (_sync) Write(new JournalEntry { Kind = JournalEntry.KindWifi, Wifi = record });
        }

        // ---- helpers ----

        private InspectionRecord RequireOpen(string inspectionId)
        {
            if (inspectionId == null || !_inspections.TryGetValue(inspectionId, out var insp)) throw new InvalidOperationException("Unknown inspection.");
            if (!insp.IsOpen) throw new InvalidOperationException("This inspection is completed and can no longer be changed.");
            return insp;
        }

        private static T Clone<T>(T value) => Json.FromBytes<T>(Json.ToBytes(value));
    }
}
