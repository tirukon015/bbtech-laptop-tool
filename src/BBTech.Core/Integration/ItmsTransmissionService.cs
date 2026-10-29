using System;
using System.Threading.Tasks;
using BBTech.Core.Identity;
using BBTech.Core.Storage;

namespace BBTech.Core.Integration
{
    /// <summary>Result of one step of the per-laptop ITMS flow.</summary>
    public sealed class ItmsTransmissionOutcome
    {
        /// <summary>The stored transmission record after the step.</summary>
        public SyncRecord Record { get; set; }
        /// <summary>The transport answer (null when no transport call was made).</summary>
        public ItmsSendResult Transport { get; set; }
        public string Message { get; set; } = "";
        /// <summary>True only when ITMS confirmed receipt of this laptop.</summary>
        public bool Confirmed => TransmissionState.IsConfirmed(Record);
    }

    /// <summary>The stored record of the laptop being sent, or why it cannot be sent.</summary>
    public sealed class ItmsLaptop
    {
        public DeviceRecord Device { get; set; }
        public string Message { get; set; } = "";
        public bool Ok => Device != null;
    }

    /// <summary>
    /// The per-laptop ITMS flow on top of the local store:
    ///
    ///   CURRENT LAPTOP → DETECT → PREPARE ONE DEVICE PAYLOAD → SEND → CONFIRM RECEIVED → DONE
    ///
    /// Every state change is written to the laptop's QC store before and after the transport call,
    /// so an interrupted send is visible afterwards. A laptop is never treated as registered because
    /// a request was attempted: only an ITMS receipt moves it to ITMS_RECEIVED.
    ///
    /// With the shipped <see cref="DisabledItmsConnector"/> nothing is attempted and the state does not change.
    /// </summary>
    public sealed class ItmsTransmissionService
    {
        private readonly QcRepository _repo;
        private readonly IItmsConnector _connector;

        public ItmsTransmissionService(QcRepository repo, IItmsConnector connector)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        }

        public bool IsEnabled => _connector.IsEnabled;

        public SyncRecord GetStatus(DeviceRecord device) => _repo.GetOrCreateDeviceSync(Require(device).DeviceId);

        /// <summary>
        /// Finds (and refreshes) or creates the stored record of THIS physical laptop, so its transmission
        /// state can be kept. An identity conflict or a laptop without stable identifiers is not resolved:
        /// the message explains why it cannot be sent.
        /// </summary>
        public ItmsLaptop ResolveLaptop(DeviceIdentity identity, DeviceSpecs specs)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            var match = _repo.FindDevice(identity);
            switch (match.Status)
            {
                case MatchStatus.Matched:
                    return new ItmsLaptop { Device = _repo.UpdateDevice(match.Device.DeviceId, identity, specs) };
                case MatchStatus.NotFound:
                    return new ItmsLaptop { Device = _repo.RegisterDevice(identity, specs) };
                default:
                    return new ItmsLaptop { Message = match.Reason };
            }
        }

        /// <summary>
        /// The one action behind the "Send to ITMS" button: does the next step for this laptop.
        ///   LOCAL → mark ready and send · READY_TO_SEND → send · FAILED → retry (same request id)
        ///   SENT → ask ITMS whether it received the laptop · ITMS_RECEIVED → nothing to do.
        /// A send left unfinished by a crash is first recovered as FAILED. With the connector disabled
        /// nothing is written and nothing is sent.
        /// </summary>
        public async Task<ItmsTransmissionOutcome> SendOrContinueAsync(DeviceRecord device)
        {
            Require(device);
            if (!_connector.IsEnabled)
                return new ItmsTransmissionOutcome
                {
                    Record = _repo.GetSyncRecord(SyncEntity.Device, device.DeviceId),
                    Transport = ItmsSendResult.NotEnabled(),
                    Message = ItmsSendResult.NotEnabled().Message,
                };

            var record = RecoverInterrupted(device);
            switch (record.SyncStatus)
            {
                case TransmissionState.ItmsReceived:
                    return new ItmsTransmissionOutcome { Record = record, Message = Describe(record) };
                case TransmissionState.Sent:
                    return await CheckReceiptAsync(device).ConfigureAwait(false);
                case TransmissionState.Failed:
                    return await RetryAsync(device).ConfigureAwait(false);
                case TransmissionState.Local:
                    var ready = MarkReady(device);
                    if (ready.Record.SyncStatus != TransmissionState.ReadyToSend) return ready;
                    return await SendAsync(device).ConfigureAwait(false);
                default:
                    return await SendAsync(device).ConfigureAwait(false);
            }
        }

        /// <summary>LOCAL → READY_TO_SEND after checking that the payload is usable. Offline.</summary>
        public ItmsTransmissionOutcome MarkReady(DeviceRecord device)
        {
            var payload = ItmsDevicePayload.From(Require(device));
            var record = _repo.GetOrCreateDeviceSync(device.DeviceId);
            if (!payload.IsValid)
                return new ItmsTransmissionOutcome { Record = record, Message = string.Join(" ", payload.Problems) };
            if (record.SyncStatus == TransmissionState.ReadyToSend)
                return new ItmsTransmissionOutcome { Record = record, Message = "Ready to send." };
            if (record.SyncStatus != TransmissionState.Local && record.SyncStatus != TransmissionState.Failed)
                return new ItmsTransmissionOutcome { Record = record, Message = Describe(record) };
            record = _repo.TransitionSync(record.SyncId, TransmissionState.ReadyToSend);
            return new ItmsTransmissionOutcome { Record = record, Message = "Ready to send." };
        }

        /// <summary>Sends this laptop (READY_TO_SEND → SENDING → SENT/ITMS_RECEIVED or FAILED).</summary>
        public async Task<ItmsTransmissionOutcome> SendAsync(DeviceRecord device)
        {
            var payload = ItmsDevicePayload.From(Require(device));
            var record = _repo.GetOrCreateDeviceSync(device.DeviceId);
            if (!_connector.IsEnabled)
                return new ItmsTransmissionOutcome { Record = record, Transport = ItmsSendResult.NotEnabled(), Message = ItmsSendResult.NotEnabled().Message };
            if (record.SyncStatus != TransmissionState.ReadyToSend)
                return new ItmsTransmissionOutcome { Record = record, Message = Describe(record) };

            // Same request id for the first attempt and every retry (idempotent on the ITMS side).
            var submission = new ItmsSubmission(record.RequestId, payload);
            record = _repo.TransitionSync(record.SyncId, TransmissionState.Sending);
            ItmsSendResult result;
            try { result = await _connector.SendDeviceAsync(submission).ConfigureAwait(false); }
            catch (Exception) { result = ItmsSendResult.Failed(null); } // details stay out of the store and the UI
            return Apply(record, result ?? ItmsSendResult.Failed(null));
        }

        /// <summary>SENT → asks ITMS whether it received the laptop (→ ITMS_RECEIVED, or FAILED when refused).</summary>
        public async Task<ItmsTransmissionOutcome> CheckReceiptAsync(DeviceRecord device)
        {
            var record = _repo.GetOrCreateDeviceSync(Require(device).DeviceId);
            if (record.SyncStatus != TransmissionState.Sent || !_connector.IsEnabled)
                return new ItmsTransmissionOutcome { Record = record, Message = Describe(record) };

            ItmsSendResult result;
            try { result = await _connector.GetStatusAsync(record.RequestId).ConfigureAwait(false); }
            catch (Exception) { return new ItmsTransmissionOutcome { Record = record, Message = "ITMS could not be asked right now. Try again later." }; }
            result = result ?? ItmsSendResult.Failed(null);

            switch (result.Outcome)
            {
                case ItmsSendOutcome.Received:
                    record = _repo.TransitionSync(record.SyncId, TransmissionState.ItmsReceived, receipt: result.Receipt);
                    break;
                case ItmsSendOutcome.Rejected:
                    record = _repo.TransitionSync(record.SyncId, TransmissionState.Failed, error: result.Message);
                    break;
                // RequestSent / Failed / NotEnabled: still waiting; nothing changes.
            }
            return new ItmsTransmissionOutcome { Record = record, Transport = result, Message = result.Message };
        }

        /// <summary>FAILED → READY_TO_SEND → send again with the SAME request id.</summary>
        public Task<ItmsTransmissionOutcome> RetryAsync(DeviceRecord device)
        {
            var record = _repo.GetOrCreateDeviceSync(Require(device).DeviceId);
            if (record.SyncStatus != TransmissionState.Failed)
                return Task.FromResult(new ItmsTransmissionOutcome { Record = record, Message = Describe(record) });
            if (!_connector.IsEnabled)
                return Task.FromResult(new ItmsTransmissionOutcome { Record = record, Transport = ItmsSendResult.NotEnabled(), Message = ItmsSendResult.NotEnabled().Message });
            _repo.TransitionSync(record.SyncId, TransmissionState.ReadyToSend);
            return SendAsync(device);
        }

        /// <summary>
        /// A record left in SENDING means the tool stopped (crash, USB removed) before ITMS answered. It
        /// is marked FAILED so it can be retried; it is never assumed to have arrived.
        /// </summary>
        public SyncRecord RecoverInterrupted(DeviceRecord device)
        {
            var record = _repo.GetOrCreateDeviceSync(Require(device).DeviceId);
            if (record.SyncStatus != TransmissionState.Sending) return record;
            return _repo.TransitionSync(record.SyncId, TransmissionState.Failed, error: "Sending was interrupted before ITMS answered.");
        }

        private ItmsTransmissionOutcome Apply(SyncRecord sending, ItmsSendResult result)
        {
            SyncRecord record;
            switch (result.Outcome)
            {
                case ItmsSendOutcome.Received:
                    record = _repo.TransitionSync(sending.SyncId, TransmissionState.Sent);
                    record = _repo.TransitionSync(record.SyncId, TransmissionState.ItmsReceived, receipt: result.Receipt);
                    break;
                case ItmsSendOutcome.RequestSent:
                    record = _repo.TransitionSync(sending.SyncId, TransmissionState.Sent);
                    break;
                default: // Rejected, Failed, or a connector that turned out to be disabled
                    record = _repo.TransitionSync(sending.SyncId, TransmissionState.Failed, error: result.Message);
                    break;
            }
            return new ItmsTransmissionOutcome { Record = record, Transport = result, Message = result.Message };
        }

        /// <summary>Staff-readable description of a transmission state.</summary>
        public static string Describe(SyncRecord r)
        {
            switch (r?.SyncStatus)
            {
                case TransmissionState.Local: return "Not sent to ITMS.";
                case TransmissionState.ReadyToSend: return "Ready to send to ITMS.";
                case TransmissionState.Sending: return "Sending to ITMS…";
                case TransmissionState.Sent: return "Sent to ITMS. Waiting for ITMS to confirm.";
                case TransmissionState.ItmsReceived: return "ITMS received this laptop.";
                case TransmissionState.Failed: return "Sending to ITMS failed: " + r.LastError;
                default: return "ITMS status unknown.";
            }
        }

        private static DeviceRecord Require(DeviceRecord d) => d ?? throw new ArgumentNullException("device");
    }
}
