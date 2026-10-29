using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BBTech.Core.Identity;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using Xunit;

namespace BBTech.Core.Tests
{
    public class ItmsPayloadTests
    {
        private const string Rid = "0f8fad5b-d9cb-469f-a165-70867728950e";

        private static DetectionResult Lenovo()
        {
            var r = new DetectionResult
            {
                Brand = "Lenovo", Model = "ThinkPad", ModelNumber = "T14 Gen 2", Cpu = "i5", Generation = "11th Gen",
                Ram = "16 GB", Storage = "512 GB SSD", BatteryHealth = "87%",
            };
            r.System.SerialNumber = "ABC12345";
            return r;
        }

        private static ItmsDevicePayload P(string serial) =>
            new ItmsDevicePayload(serial, null, null, null, "Dell", "Latitude", "5420", "i5", "11th Gen", "16 GB", "512 GB SSD", "90%");

        [Fact]
        public void Device_payload_has_the_agreed_keys_values_and_order()
        {
            var r = Lenovo();
            r.System.SystemUuid = "6a3f2c10-1b2d-11b2-a85c-d5a1a3b0c9e1";
            r.System.BoardSerial = "L1HF0001";
            var p = ItmsDevicePayload.From(r);
            Assert.Equal(
                "{\n  \"serial_number\": \"ABC12345\",\n  \"system_uuid\": \"6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1\",\n" +
                "  \"board_serial\": \"L1HF0001\",\n  \"hardware_fingerprint\": \"" + p.HardwareFingerprint + "\",\n" +
                "  \"brand\": \"Lenovo\",\n  \"model\": \"ThinkPad\",\n  \"model_number\": \"T14 Gen 2\",\n" +
                "  \"cpu\": \"i5\",\n  \"generation\": \"11th Gen\",\n  \"ram\": \"16 GB\",\n  \"storage\": \"512 GB SSD\",\n  \"battery_health\": \"87%\"\n}",
                p.ToJson());
        }

        [Fact]
        public void Payload_never_contains_itms_reference_ids()
        {
            var device = ItmsDevicePayload.From(Lenovo());
            Assert.DoesNotMatch(new Regex("\"[a-z_]*_id\""), device.ToJson());
            string all = new ItmsSubmission(ItmsSubmission.NewRequestId(), device).ToJson() +
                         new ItmsBatchPayload(ItmsSubmission.NewRequestId(), new[] { device }).ToJson();
            foreach (var key in new[] { "brand_id", "model_id", "cpu_id", "ram_id", "storage_id", "generation_id", "source_id" })
                Assert.DoesNotContain(key, all);
            Assert.Equal(ItmsDevicePayload.Keys, Regex.Matches(device.ToJson(), "\"([a-z_]+)\":").Cast<Match>().Select(m => m.Groups[1].Value));
        }

        [Fact]
        public void Unknown_values_are_sent_as_null_not_as_text()
        {
            var r = Lenovo();
            r.Generation = DetectionResult.Unknown;
            r.BatteryHealth = "unknown";
            r.ModelNumber = "  ";
            var p = ItmsDevicePayload.From(r);
            Assert.Null(p.Generation);
            Assert.Null(p.BatteryHealth);
            Assert.Null(p.ModelNumber);
            Assert.Contains("\"generation\": null", p.ToJson());
            Assert.DoesNotContain("Unknown", p.ToJson(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Values_are_trimmed()
        {
            var r = Lenovo();
            r.Brand = "  Lenovo ";
            r.System.SerialNumber = " ABC12345 ";
            var p = ItmsDevicePayload.From(r);
            Assert.Equal("Lenovo", p.Brand);
            Assert.Equal("ABC12345", p.SerialNumber);
        }

        [Fact]
        public void A_laptop_without_serial_number_cannot_be_sent()
        {
            var r = Lenovo();
            r.System.SerialNumber = "";
            r.System.SystemUuid = "6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1"; // supporting identity is not enough
            var p = ItmsDevicePayload.From(r);
            Assert.False(p.IsValid);
            Assert.Contains("serial number", p.Problems.Single());
        }

        [Fact]
        public void A_laptop_with_serial_number_is_valid_even_with_unknown_fields()
        {
            var r = new DetectionResult();
            r.System.SerialNumber = "XYZ987";
            Assert.True(ItmsDevicePayload.From(r).IsValid);
        }

        [Fact]
        public void Fingerprint_is_the_device_identity_fingerprint()
        {
            var r = Lenovo();
            r.System.SystemUuid = "6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1";
            Assert.Equal(IdentityFactory.From(r).Fingerprint, ItmsDevicePayload.From(r).HardwareFingerprint);
            Assert.Matches("^[0-9a-f]{64}$", ItmsDevicePayload.From(r).HardwareFingerprint);
        }

        [Fact]
        public void Fingerprint_ignores_runtime_and_swappable_values()
        {
            var b = Lenovo();
            b.Ram = "32 GB";
            b.Storage = "1 TB SSD";
            b.BatteryHealth = "50%";
            b.Battery.ChargePercent = 12;
            b.System.OperatingSystem = "Windows 10";
            b.Memory.TotalBytes = 1;
            Assert.Equal(ItmsDevicePayload.From(Lenovo()).HardwareFingerprint, ItmsDevicePayload.From(b).HardwareFingerprint);
        }

        [Fact]
        public void Fingerprint_changes_with_a_different_serial()
        {
            var b = Lenovo();
            b.System.SerialNumber = "OTHER999";
            Assert.NotEqual(ItmsDevicePayload.From(Lenovo()).HardwareFingerprint, ItmsDevicePayload.From(b).HardwareFingerprint);
        }

        [Fact]
        public void Firmware_placeholders_are_never_sent_as_identity()
        {
            var r = Lenovo();
            r.System.SerialNumber = "To be filled by O.E.M.";
            r.System.SystemUuid = "00000000-0000-0000-0000-000000000000";
            r.System.BoardSerial = "Default string";
            var p = ItmsDevicePayload.From(r);
            Assert.Null(p.SerialNumber);
            Assert.Null(p.SystemUuid);
            Assert.Null(p.BoardSerial);
            Assert.Null(p.HardwareFingerprint); // never a fingerprint from the model alone
            Assert.False(p.IsValid);
        }

        [Fact]
        public void Stored_device_and_live_detection_give_the_same_identity()
        {
            var r = Lenovo();
            r.System.SystemUuid = "6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1";
            var id = IdentityFactory.From(r);
            var stored = new DeviceRecord
            {
                SerialNumber = id.SerialNumber, SystemUuid = id.SystemUuid, BoardSerial = id.BoardSerial,
                HardwareFingerprint = id.Fingerprint, Manufacturer = r.Brand, Model = r.Model, ModelNumber = r.ModelNumber,
            };
            var live = ItmsDevicePayload.From(r);
            var fromStore = ItmsDevicePayload.From(stored);
            Assert.Equal(live.SerialNumber, fromStore.SerialNumber);
            Assert.Equal(live.SystemUuid, fromStore.SystemUuid);
            Assert.Equal(live.HardwareFingerprint, fromStore.HardwareFingerprint);
        }

        [Fact]
        public void Payload_from_the_stored_device_record_uses_its_latest_specs()
        {
            var d = new DeviceRecord
            {
                SerialNumber = "ABC12345", Manufacturer = "Dell", Model = "Latitude", ModelNumber = "5420",
                Specs = new DeviceSpecs { Cpu = "i7", Generation = "11th Gen", Ram = "32 GB", Storage = "1 TB SSD", BatteryHealth = "90%" },
            };
            var p = ItmsDevicePayload.From(d);
            Assert.Equal(new[] { "ABC12345", "Dell", "Latitude", "5420", "i7", "11th Gen", "32 GB", "1 TB SSD", "90%" },
                         new[] { p.SerialNumber, p.Brand, p.Model, p.ModelNumber, p.Cpu, p.Generation, p.Ram, p.Storage, p.BatteryHealth });
        }

        [Fact]
        public void Device_record_without_specs_gives_null_specs()
        {
            var p = ItmsDevicePayload.From(new DeviceRecord { SerialNumber = "S1", Manufacturer = "HP" });
            Assert.Null(p.Cpu);
            Assert.Null(p.Ram);
        }

        [Fact]
        public void Null_sources_are_rejected()
        {
            Assert.Throws<ArgumentNullException>(() => ItmsDevicePayload.From((DetectionResult)null));
            Assert.Throws<ArgumentNullException>(() => ItmsDevicePayload.From((DeviceRecord)null));
        }

        [Fact]
        public void Submission_wraps_one_device_with_its_request_id()
        {
            string json = new ItmsSubmission(Rid, ItmsDevicePayload.From(Lenovo())).ToJson(indent: false);
            Assert.StartsWith("{\"request_id\":\"" + Rid + "\",\"device\":{\"serial_number\":\"ABC12345\"", json);
            Assert.EndsWith("}}", json);
        }

        [Fact]
        public void Submission_needs_a_uuid_request_id_and_a_valid_device()
        {
            var device = ItmsDevicePayload.From(Lenovo());
            Assert.Throws<ArgumentException>(() => new ItmsSubmission("not-a-uuid", device));
            Assert.Throws<ArgumentException>(() => new ItmsSubmission(null, device));
            Assert.Throws<ArgumentNullException>(() => new ItmsSubmission(Rid, null));
            Assert.Throws<ArgumentException>(() => new ItmsSubmission(Rid, P(null)));
        }

        [Fact]
        public void New_request_ids_are_unique_uuids()
        {
            var ids = Enumerable.Range(0, 50).Select(_ => ItmsSubmission.NewRequestId()).ToList();
            Assert.Equal(50, ids.Distinct().Count());
            Assert.All(ids, i => Assert.True(Guid.TryParse(i, out _)));
        }

        [Fact]
        public void Batch_payload_has_a_request_id_and_a_devices_array()
        {
            string json = new ItmsBatchPayload(Rid, new[] { P("A1"), P("B2") }).ToJson(indent: false);
            Assert.StartsWith("{\"request_id\":\"" + Rid + "\",\"devices\":[{\"serial_number\":\"A1\"", json);
            Assert.Contains("},{\"serial_number\":\"B2\"", json);
            Assert.EndsWith("}]}", json);
        }

        [Fact]
        public void Batch_rejects_empty_duplicate_and_invalid_devices()
        {
            Assert.Throws<ArgumentException>(() => new ItmsBatchPayload(Rid, new ItmsDevicePayload[0]));
            Assert.Throws<ArgumentException>(() => new ItmsBatchPayload(Rid, new[] { P("A1"), P("a1") }));
            Assert.Throws<ArgumentException>(() => new ItmsBatchPayload(Rid, new[] { P("A1"), P(null) }));
            Assert.Throws<ArgumentException>(() => new ItmsBatchPayload(Rid, new[] { P("A1"), null }));
            Assert.Throws<ArgumentException>(() => new ItmsBatchPayload("x", new[] { P("A1") }));
            Assert.Throws<ArgumentNullException>(() => new ItmsBatchPayload(Rid, null));
        }

        [Fact]
        public void Connector_prepares_single_and_batch_payloads_offline()
        {
            IItmsConnector c = new DisabledItmsConnector();
            var device = c.PrepareDevice(Lenovo());
            Assert.Equal("ABC12345", device.SerialNumber);
            var batch = c.PrepareBatch(new[] { device });
            Assert.Single(batch.Devices);
            Assert.True(Guid.TryParse(batch.RequestId, out _));
        }

        [Fact]
        public async Task Disabled_connector_reports_not_enabled_for_every_transport_call()
        {
            IItmsConnector c = new DisabledItmsConnector();
            var s = new ItmsSubmission(Rid, P("A1"));
            foreach (var r in new[]
            {
                await c.SendDeviceAsync(s), await c.SendBatchAsync(new ItmsBatchPayload(Rid, new[] { P("A1") })),
                await c.GetStatusAsync(Rid), await c.RetryAsync(s),
            })
            {
                Assert.Equal(ItmsSendOutcome.NotEnabled, r.Outcome);
                Assert.Null(r.Receipt);
                Assert.Contains("not available", r.Message);
            }
        }

        [Fact]
        public void A_received_result_requires_a_receipt()
        {
            Assert.Throws<ArgumentException>(() => ItmsSendResult.Received(""));
            Assert.Equal("R-1", ItmsSendResult.Received(" R-1 ").Receipt);
        }

        [Fact]
        public void Rejected_and_failed_results_have_a_readable_default_message()
        {
            Assert.False(string.IsNullOrWhiteSpace(ItmsSendResult.Rejected(null).Message));
            Assert.False(string.IsNullOrWhiteSpace(ItmsSendResult.Failed("").Message));
        }
    }

    public class TransmissionStateTests
    {
        private const string Rid = "0f8fad5b-d9cb-469f-a165-70867728950e";
        private static readonly DateTime Now = new DateTime(2026, 10, 5, 12, 0, 0);
        private static SyncRecord R(string state) =>
            new SyncRecord { SyncId = "s", EntityType = SyncEntity.Device, EntityId = "d", SyncStatus = state, RequestId = state == "LOCAL" ? null : Rid };

        [Theory]
        [InlineData("LOCAL", "READY_TO_SEND")]
        [InlineData("READY_TO_SEND", "LOCAL")]
        [InlineData("READY_TO_SEND", "SENDING")]
        [InlineData("SENDING", "SENT")]
        [InlineData("SENDING", "FAILED")]
        [InlineData("SENT", "ITMS_RECEIVED")]
        [InlineData("SENT", "FAILED")]
        [InlineData("FAILED", "READY_TO_SEND")]
        public void Allowed_transitions(string from, string to) => Assert.True(TransmissionState.CanTransition(from, to));

        [Theory]
        [InlineData("LOCAL", "SENDING")]
        [InlineData("LOCAL", "SENT")]
        [InlineData("LOCAL", "ITMS_RECEIVED")]
        [InlineData("READY_TO_SEND", "ITMS_RECEIVED")]
        [InlineData("SENDING", "ITMS_RECEIVED")] // a request must be sent before it can be received
        [InlineData("SENDING", "READY_TO_SEND")]
        [InlineData("FAILED", "SENT")]
        [InlineData("FAILED", "ITMS_RECEIVED")]
        [InlineData("ITMS_RECEIVED", "LOCAL")]
        [InlineData("ITMS_RECEIVED", "FAILED")]
        [InlineData("LOCAL", "LOCAL")]
        [InlineData("LOCAL", "SYNCED")]
        [InlineData(null, "LOCAL")]
        public void Forbidden_transitions(string from, string to)
        {
            Assert.False(TransmissionState.CanTransition(from, to));
            Assert.Throws<InvalidOperationException>(() => TransmissionState.Transition(R(from), to, Now));
        }

        [Fact]
        public void Sending_counts_the_attempt_and_clears_the_last_error()
        {
            var ready = R(TransmissionState.ReadyToSend);
            ready.AttemptCount = 2;
            ready.LastError = "old";
            var next = TransmissionState.Transition(ready, TransmissionState.Sending, Now);
            Assert.Equal(3, next.AttemptCount);
            Assert.Equal(Now, next.LastAttemptAt);
            Assert.Null(next.LastError);
            Assert.Equal(Now, next.UpdatedAt);
        }

        [Fact]
        public void Transition_returns_a_new_record_and_keeps_the_old_one()
        {
            var old = R(TransmissionState.Local);
            var next = TransmissionState.Transition(old, TransmissionState.ReadyToSend, Now, newRequestId: Rid);
            Assert.NotSame(old, next);
            Assert.Equal(TransmissionState.Local, old.SyncStatus);
            Assert.Equal(new[] { "s", "device", "d" }, new[] { next.SyncId, next.EntityType, next.EntityId });
        }

        [Fact]
        public void Failed_requires_a_reason()
        {
            Assert.Throws<ArgumentException>(() => TransmissionState.Transition(R(TransmissionState.Sending), TransmissionState.Failed, Now));
            Assert.Equal("timeout", TransmissionState.Transition(R(TransmissionState.Sending), TransmissionState.Failed, Now, error: " timeout ").LastError);
        }

        [Fact]
        public void Received_requires_the_itms_receipt()
        {
            Assert.Throws<ArgumentException>(() => TransmissionState.Transition(R(TransmissionState.Sent), TransmissionState.ItmsReceived, Now));
            var done = TransmissionState.Transition(R(TransmissionState.Sent), TransmissionState.ItmsReceived, Now, receipt: "Q-77");
            Assert.Equal("Q-77", done.RemoteId);
            Assert.True(TransmissionState.IsConfirmed(done));
        }

        [Fact]
        public void Sent_is_not_confirmed()
        {
            var sent = TransmissionState.Transition(R(TransmissionState.Sending), TransmissionState.Sent, Now);
            Assert.False(TransmissionState.IsConfirmed(sent));
            Assert.Null(sent.RemoteId);
        }

        [Fact]
        public void Received_without_remote_id_is_not_confirmed() =>
            Assert.False(TransmissionState.IsConfirmed(R(TransmissionState.ItmsReceived)));

        [Fact]
        public void Marking_ready_assigns_the_request_id_of_the_send_operation()
        {
            Assert.Throws<ArgumentException>(() => TransmissionState.Transition(R(TransmissionState.Local), TransmissionState.ReadyToSend, Now));
            Assert.Throws<ArgumentException>(() => TransmissionState.Transition(R(TransmissionState.Local), TransmissionState.ReadyToSend, Now, newRequestId: "abc"));
            Assert.Equal(Rid, TransmissionState.Transition(R(TransmissionState.Local), TransmissionState.ReadyToSend, Now, newRequestId: Rid).RequestId);
        }

        [Fact]
        public void Retry_keeps_the_original_request_id()
        {
            var retry = TransmissionState.Transition(R(TransmissionState.Failed), TransmissionState.ReadyToSend, Now, newRequestId: "0f8fad5b-0000-469f-a165-70867728950e");
            Assert.Equal(Rid, retry.RequestId);
            Assert.Equal(Rid, TransmissionState.Transition(retry, TransmissionState.Sending, Now).RequestId);
        }

        [Fact]
        public void A_retry_without_the_original_request_id_is_refused()
        {
            var failed = R(TransmissionState.Failed);
            failed.RequestId = null;
            Assert.Throws<InvalidOperationException>(() => TransmissionState.Transition(failed, TransmissionState.ReadyToSend, Now));
        }

        [Fact]
        public void Withdrawing_drops_the_request_id() =>
            Assert.Null(TransmissionState.Transition(R(TransmissionState.ReadyToSend), TransmissionState.Local, Now).RequestId);

        [Theory]
        [InlineData("LOCAL_ONLY", "LOCAL")]
        [InlineData("SYNC_PENDING", "READY_TO_SEND")]
        [InlineData("SYNCING", "SENDING")]
        [InlineData("SYNCED", "SENT")]
        [InlineData("SYNC_FAILED", "FAILED")]
        [InlineData("LOCAL", "LOCAL")]
        public void Legacy_statuses_map_to_transmission_states(string legacy, string state) =>
            Assert.Equal(state, TransmissionState.FromLegacy(legacy));

        [Fact]
        public void All_states_are_known() =>
            Assert.All(TransmissionState.All, s => Assert.True(TransmissionState.IsKnown(s)));
    }

    /// <summary>Transport used only by tests, to exercise the state handling without any network.</summary>
    internal sealed class ScriptedConnector : ItmsConnectorBase
    {
        public bool Enabled = true;
        public readonly Queue<Func<ItmsSendResult>> Sends = new Queue<Func<ItmsSendResult>>();
        public readonly Queue<Func<ItmsSendResult>> Statuses = new Queue<Func<ItmsSendResult>>();
        public readonly List<ItmsSubmission> Sent = new List<ItmsSubmission>();
        public readonly List<string> StatusQueries = new List<string>();
        public override bool IsEnabled => Enabled;

        public override Task<ItmsSendResult> SendDeviceAsync(ItmsSubmission submission)
        {
            Sent.Add(submission);
            return Task.FromResult(Sends.Dequeue()());
        }

        public override Task<ItmsSendResult> SendBatchAsync(ItmsBatchPayload batch) => throw new NotSupportedException();

        public override Task<ItmsSendResult> GetStatusAsync(string requestId)
        {
            StatusQueries.Add(requestId);
            return Task.FromResult(Statuses.Dequeue()());
        }
    }

    public class ItmsTransmissionServiceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-itms-tests-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private QcRepository Open()
        {
            var r = QcRepository.Open(_dir, new FakeMachine(1), QcStoreTests.LaptopB);
            Assert.True(r.Ok, r.Message);
            return r.Repository;
        }

        private static DeviceRecord Register(QcRepository repo, string serial = "ABC12345")
        {
            var id = new DeviceIdentity("Dell", "Latitude", "5420", serial, null, null);
            return repo.RegisterDevice(id, new DeviceSpecs { Cpu = "i5", Generation = "11th Gen", Ram = "16 GB", Storage = "256 GB SSD", BatteryHealth = "91%" });
        }

        [Fact]
        public void A_new_device_starts_local()
        {
            var repo = Open();
            var svc = new ItmsTransmissionService(repo, new DisabledItmsConnector());
            var d = Register(repo);
            Assert.Equal(TransmissionState.Local, svc.GetStatus(d).SyncStatus);
            Assert.Same(svc.GetStatus(d), svc.GetStatus(d)); // created once
            Assert.Single(repo.SyncRecords);
        }

        [Fact]
        public void Mark_ready_is_offline_and_persisted()
        {
            var repo = Open();
            var d = Register(repo);
            var outcome = new ItmsTransmissionService(repo, new DisabledItmsConnector()).MarkReady(d);
            Assert.Equal(TransmissionState.ReadyToSend, outcome.Record.SyncStatus);
            Assert.Equal(TransmissionState.ReadyToSend, Open().GetSyncRecord(SyncEntity.Device, d.DeviceId).SyncStatus);
        }

        [Fact]
        public void Mark_ready_refuses_a_laptop_without_serial_number()
        {
            var repo = Open();
            var d = repo.RegisterDevice(new DeviceIdentity("Dell", "Latitude", "5420", null, "4C4C4544-0042-3510-8051-B8C04F4E3532", null), null);
            var outcome = new ItmsTransmissionService(repo, new DisabledItmsConnector()).MarkReady(d);
            Assert.Equal(TransmissionState.Local, outcome.Record.SyncStatus);
            Assert.Null(outcome.Record.RequestId);
            Assert.Contains("serial number", outcome.Message);
        }

        [Fact]
        public void Request_id_survives_restart_of_the_tool()
        {
            var repo = Open();
            var d = Register(repo);
            string rid = new ItmsTransmissionService(repo, new DisabledItmsConnector()).MarkReady(d).Record.RequestId;
            Assert.True(Guid.TryParse(rid, out _));
            Assert.Equal(rid, Open().GetSyncRecord(SyncEntity.Device, d.DeviceId).RequestId);
        }

        [Fact]
        public void Mark_ready_twice_keeps_one_request_id()
        {
            var repo = Open();
            var d = Register(repo);
            var svc = new ItmsTransmissionService(repo, new DisabledItmsConnector());
            string first = svc.MarkReady(d).Record.RequestId;
            Assert.Equal(first, svc.MarkReady(d).Record.RequestId);
        }

        [Fact]
        public void Different_laptops_get_different_request_ids()
        {
            var repo = Open();
            var svc = new ItmsTransmissionService(repo, new DisabledItmsConnector());
            Assert.NotEqual(svc.MarkReady(Register(repo, "SERIAL-A")).Record.RequestId, svc.MarkReady(Register(repo, "SERIAL-B")).Record.RequestId);
        }

        [Fact]
        public async Task Disabled_connector_never_changes_the_state_or_counts_an_attempt()
        {
            var repo = Open();
            var d = Register(repo);
            var svc = new ItmsTransmissionService(repo, new DisabledItmsConnector());
            svc.MarkReady(d);
            var outcome = await svc.SendAsync(d);
            Assert.Equal(ItmsSendOutcome.NotEnabled, outcome.Transport.Outcome);
            Assert.Equal(TransmissionState.ReadyToSend, outcome.Record.SyncStatus);
            Assert.Equal(0, outcome.Record.AttemptCount);
            Assert.False(outcome.Confirmed);
            Assert.False(svc.IsEnabled);
        }

        [Fact]
        public async Task Received_answer_ends_in_itms_received_with_the_receipt()
        {
            var repo = Open();
            var d = Register(repo);
            var c = new ScriptedConnector();
            c.Sends.Enqueue(() => ItmsSendResult.Received("ITMS-Q-1001"));
            var svc = new ItmsTransmissionService(repo, c);
            svc.MarkReady(d);
            var outcome = await svc.SendAsync(d);
            Assert.True(outcome.Confirmed);
            Assert.Equal("ITMS-Q-1001", outcome.Record.RemoteId);
            Assert.Equal(1, outcome.Record.AttemptCount);
            Assert.Equal("ABC12345", c.Sent.Single().Device.SerialNumber);
            Assert.Equal(outcome.Record.RequestId, c.Sent.Single().RequestId);
            Assert.Equal(TransmissionState.ItmsReceived, Open().GetSyncRecord(SyncEntity.Device, d.DeviceId).SyncStatus);
        }

        [Fact]
        public async Task A_sent_request_is_not_registered_until_itms_confirms()
        {
            var repo = Open();
            var d = Register(repo);
            var c = new ScriptedConnector();
            c.Sends.Enqueue(() => ItmsSendResult.RequestSent());
            c.Statuses.Enqueue(() => ItmsSendResult.RequestSent());
            c.Statuses.Enqueue(() => ItmsSendResult.Received("ITMS-Q-2"));
            var svc = new ItmsTransmissionService(repo, c);
            svc.MarkReady(d);

            var sent = await svc.SendAsync(d);
            Assert.Equal(TransmissionState.Sent, sent.Record.SyncStatus);
            Assert.False(sent.Confirmed);

            var stillWaiting = await svc.CheckReceiptAsync(d);
            Assert.Equal(TransmissionState.Sent, stillWaiting.Record.SyncStatus);

            var confirmed = await svc.CheckReceiptAsync(d);
            Assert.True(confirmed.Confirmed);
            Assert.Equal("ITMS-Q-2", confirmed.Record.RemoteId);
            Assert.All(c.StatusQueries, q => Assert.Equal(sent.Record.RequestId, q)); // asked by request id
        }

        [Fact]
        public async Task Rejection_after_sending_becomes_failed()
        {
            var repo = Open();
            var d = Register(repo);
            var c = new ScriptedConnector();
            c.Sends.Enqueue(() => ItmsSendResult.RequestSent());
            c.Statuses.Enqueue(() => ItmsSendResult.Rejected("Duplicate serial number in ITMS."));
            var svc = new ItmsTransmissionService(repo, c);
            svc.MarkReady(d);
            await svc.SendAsync(d);
            var outcome = await svc.CheckReceiptAsync(d);
            Assert.Equal(TransmissionState.Failed, outcome.Record.SyncStatus);
            Assert.Equal("Duplicate serial number in ITMS.", outcome.Record.LastError);
        }

        [Fact]
        public async Task Failure_then_retry_resends_the_same_request()
        {
            var repo = Open();
            var d = Register(repo);
            var c = new ScriptedConnector();
            c.Sends.Enqueue(() => ItmsSendResult.Failed("ITMS could not be reached."));
            c.Sends.Enqueue(() => ItmsSendResult.Received("ITMS-Q-3"));
            var svc = new ItmsTransmissionService(repo, c);
            svc.MarkReady(d);

            var failed = await svc.SendAsync(d);
            Assert.Equal(TransmissionState.Failed, failed.Record.SyncStatus);
            Assert.Equal("ITMS could not be reached.", failed.Record.LastError);

            var retried = await svc.RetryAsync(d);
            Assert.True(retried.Confirmed);
            Assert.Equal(2, retried.Record.AttemptCount);
            Assert.Null(retried.Record.LastError);
            // Idempotency: the retry is the same request, so ITMS can recognise it if the first one arrived.
            Assert.Equal(c.Sent[0].RequestId, c.Sent[1].RequestId);
        }

        [Fact]
        public async Task A_transport_exception_becomes_failed_with_a_generic_message()
        {
            var repo = Open();
            var d = Register(repo);
            var c = new ScriptedConnector();
            c.Sends.Enqueue(() => throw new InvalidOperationException("secret-token=abc"));
            var svc = new ItmsTransmissionService(repo, c);
            svc.MarkReady(d);
            var outcome = await svc.SendAsync(d);
            Assert.Equal(TransmissionState.Failed, outcome.Record.SyncStatus);
            Assert.DoesNotContain("secret", outcome.Record.LastError);
        }

        [Fact]
        public async Task Send_without_mark_ready_does_nothing()
        {
            var repo = Open();
            var d = Register(repo);
            var c = new ScriptedConnector();
            var outcome = await new ItmsTransmissionService(repo, c).SendAsync(d);
            Assert.Equal(TransmissionState.Local, outcome.Record.SyncStatus);
            Assert.Empty(c.Sent);
        }

        [Fact]
        public async Task Retry_is_only_possible_after_a_failure()
        {
            var repo = Open();
            var d = Register(repo);
            var c = new ScriptedConnector();
            var outcome = await new ItmsTransmissionService(repo, c).RetryAsync(d);
            Assert.Equal(TransmissionState.Local, outcome.Record.SyncStatus);
            Assert.Empty(c.Sent);
        }

        [Fact]
        public void An_interrupted_send_is_recovered_as_failed_never_as_received()
        {
            var repo = Open();
            var d = Register(repo);
            var rec = repo.GetOrCreateDeviceSync(d.DeviceId);
            repo.TransitionSync(rec.SyncId, TransmissionState.ReadyToSend);
            repo.TransitionSync(rec.SyncId, TransmissionState.Sending); // the tool stopped here

            var reopened = Open();
            var recovered = new ItmsTransmissionService(reopened, new DisabledItmsConnector()).RecoverInterrupted(reopened.Devices.Single());
            Assert.Equal(TransmissionState.Failed, recovered.SyncStatus);
            Assert.Contains("interrupted", recovered.LastError);
            Assert.Equal(rec.SyncId, recovered.SyncId);
        }

        [Fact]
        public void Each_laptop_has_its_own_transmission_record()
        {
            var repo = Open();
            var a = Register(repo, "SERIAL-A");
            var b = Register(repo, "SERIAL-B");
            var svc = new ItmsTransmissionService(repo, new DisabledItmsConnector());
            svc.MarkReady(a);
            Assert.Equal(TransmissionState.ReadyToSend, svc.GetStatus(a).SyncStatus);
            Assert.Equal(TransmissionState.Local, svc.GetStatus(b).SyncStatus);
        }

        [Fact]
        public void Repository_rejects_invalid_transitions_and_unknown_records()
        {
            var repo = Open();
            var d = Register(repo);
            var rec = repo.GetOrCreateDeviceSync(d.DeviceId);
            Assert.Throws<InvalidOperationException>(() => repo.TransitionSync(rec.SyncId, TransmissionState.ItmsReceived, receipt: "x"));
            Assert.Throws<InvalidOperationException>(() => repo.TransitionSync("missing", TransmissionState.ReadyToSend));
            Assert.Throws<InvalidOperationException>(() => repo.GetOrCreateDeviceSync("missing-device"));
        }

        private static readonly DeviceSpecs Specs = new DeviceSpecs { Cpu = "i5", Generation = "11th Gen", Ram = "16 GB", Storage = "256 GB SSD", BatteryHealth = "91%" };

        [Fact]
        public void Resolve_creates_the_record_of_a_new_laptop_once()
        {
            var repo = Open();
            var svc = new ItmsTransmissionService(repo, new DisabledItmsConnector());
            var first = svc.ResolveLaptop(QcStoreTests.LaptopB, Specs);
            var again = svc.ResolveLaptop(QcStoreTests.LaptopB, Specs);
            Assert.True(first.Ok);
            Assert.Equal(first.Device.DeviceId, again.Device.DeviceId);
            Assert.Single(repo.Devices);
        }

        [Fact]
        public void Resolve_uses_the_existing_qc_record_and_refreshes_specs()
        {
            var repo = Open();
            var qc = repo.RegisterDevice(QcStoreTests.LaptopB, new DeviceSpecs { Ram = "8 GB" });
            var laptop = new ItmsTransmissionService(repo, new DisabledItmsConnector()).ResolveLaptop(QcStoreTests.LaptopB, Specs);
            Assert.Equal(qc.DeviceId, laptop.Device.DeviceId);
            Assert.Equal("16 GB", laptop.Device.Specs.Ram);
        }

        [Fact]
        public void Resolve_refuses_an_identity_conflict()
        {
            var repo = Open();
            repo.RegisterDevice(QcStoreTests.LaptopB, Specs);
            var swappedBoard = new DeviceIdentity("Dell", "Latitude", "5420", "ABC12345", "4C4C4544-0042-3510-8051-B8C04F4E3532", "OTHER-BOARD");
            var laptop = new ItmsTransmissionService(repo, new DisabledItmsConnector()).ResolveLaptop(swappedBoard, Specs);
            Assert.False(laptop.Ok);
            Assert.Contains("conflict", laptop.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(repo.Devices);
        }

        [Fact]
        public async Task One_button_with_the_connector_disabled_writes_and_sends_nothing()
        {
            var repo = Open();
            var d = Register(repo);
            int before = repo.SyncRecords.Count;
            var outcome = await new ItmsTransmissionService(repo, new DisabledItmsConnector()).SendOrContinueAsync(d);
            Assert.Equal(ItmsSendOutcome.NotEnabled, outcome.Transport.Outcome);
            Assert.Null(outcome.Record);
            Assert.Equal(before, repo.SyncRecords.Count);
        }

        [Fact]
        public async Task One_button_sends_a_new_laptop_and_ends_received()
        {
            var repo = Open();
            var d = Register(repo);
            var c = new ScriptedConnector();
            c.Sends.Enqueue(() => ItmsSendResult.Received("ITMS-Q-10"));
            var outcome = await new ItmsTransmissionService(repo, c).SendOrContinueAsync(d);
            Assert.True(outcome.Confirmed);
            Assert.Single(c.Sent);
        }

        [Fact]
        public async Task One_button_walks_through_sent_failed_retry_and_received()
        {
            var repo = Open();
            var d = Register(repo);
            var c = new ScriptedConnector();
            c.Sends.Enqueue(() => ItmsSendResult.Failed("ITMS could not be reached."));
            c.Sends.Enqueue(() => ItmsSendResult.RequestSent());
            c.Statuses.Enqueue(() => ItmsSendResult.Received("ITMS-Q-11"));
            var svc = new ItmsTransmissionService(repo, c);

            Assert.Equal(TransmissionState.Failed, (await svc.SendOrContinueAsync(d)).Record.SyncStatus);
            Assert.Equal(TransmissionState.Sent, (await svc.SendOrContinueAsync(d)).Record.SyncStatus);   // retry
            Assert.True((await svc.SendOrContinueAsync(d)).Confirmed);                                    // status check

            var done = await svc.SendOrContinueAsync(d); // already received: nothing more is sent
            Assert.True(done.Confirmed);
            Assert.Equal(2, c.Sent.Count);
            Assert.Equal(c.Sent[0].RequestId, c.Sent[1].RequestId);
        }

        [Fact]
        public async Task One_button_recovers_an_interrupted_send_and_retries_it()
        {
            var repo = Open();
            var d = Register(repo);
            var rec = repo.GetOrCreateDeviceSync(d.DeviceId);
            string rid = repo.TransitionSync(rec.SyncId, TransmissionState.ReadyToSend).RequestId;
            repo.TransitionSync(rec.SyncId, TransmissionState.Sending); // the tool stopped here

            var c = new ScriptedConnector();
            c.Sends.Enqueue(() => ItmsSendResult.Received("ITMS-Q-12"));
            var outcome = await new ItmsTransmissionService(Open(), c).SendOrContinueAsync(Open().Devices.Single());
            Assert.True(outcome.Confirmed);
            Assert.Equal(rid, c.Sent.Single().RequestId);
        }

        [Fact]
        public async Task One_button_does_not_send_a_laptop_without_serial_number()
        {
            var repo = Open();
            var d = repo.RegisterDevice(new DeviceIdentity("Dell", "Latitude", "5420", null, "4C4C4544-0042-3510-8051-B8C04F4E3532", null), null);
            var c = new ScriptedConnector();
            var outcome = await new ItmsTransmissionService(repo, c).SendOrContinueAsync(d);
            Assert.Equal(TransmissionState.Local, outcome.Record.SyncStatus);
            Assert.Empty(c.Sent);
        }

        [Fact]
        public void Describe_gives_staff_readable_text_for_every_state()
        {
            foreach (var s in TransmissionState.All)
                Assert.False(string.IsNullOrWhiteSpace(ItmsTransmissionService.Describe(new SyncRecord { SyncStatus = s, LastError = "x" })));
            Assert.Equal("ITMS status unknown.", ItmsTransmissionService.Describe(null));
        }
    }
}
