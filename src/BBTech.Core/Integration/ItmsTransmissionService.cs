using System;
using System.Threading.Tasks;
using BBTech.Core.Identity;
using BBTech.Core.Models;
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
        /// <summary>VERIFIED / UNCONFIRMED / MISMATCH / NOT_SENT for the traceability record of this send.</summary>
        public string TraceabilityVerification { get; set; }
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

        /// <summary>
        /// QC is the final gate: a laptop goes to ITMS only when its LATEST final QC decision is PASS.
        /// Returns null when allowed, otherwise the reason.
        /// </summary>
        public string QcGateProblem(DeviceRecord device)
        {
            // The lifecycle decides: QC PASS must exist AND still count (a later QC return or a newer failure undoes it).
            var state = Testing.LaptopLifecycle.Evaluate(_repo.GetDeviceHistory(Require(device).DeviceId), Requirements);
            if (state.ReadyForItms) return null;
            var decision = _repo.GetLatestTestResult(device.DeviceId, Testing.QcGate.QcDecisionTestId, Testing.Departments.Qc);
            if (decision == null) return "This laptop has no final QC decision yet. QC must record QC PASS before it can be sent to ITMS.";
            if (decision.Status != "PASS") return "The latest final QC decision for this laptop is " + decision.Status + ". It cannot be sent to ITMS.";
            return "This laptop is not ready for ITMS (" + state.Label + "). QC PASS must be recorded after Hardware and Software have handed it over.";
        }

        private ItmsTransmissionOutcome Blocked(DeviceRecord device, string why) => new ItmsTransmissionOutcome
        {
            Record = _repo.GetSyncRecord(SyncEntity.Device, device.DeviceId),
            Message = why,
        };

        public bool IsEnabled => _connector.IsEnabled;

        /// <summary>Test definitions used to describe the lifecycle in the payload (null = built-in defaults).</summary>
        public Testing.QcRequirements Requirements { get; set; }

        public SyncRecord GetStatus(DeviceRecord device) => _repo.GetOrCreateDeviceSync(Require(device).DeviceId);

        public System.Collections.Generic.List<SyncRecord> GetHistory(DeviceRecord device) =>
            _repo.GetSyncHistory(SyncEntity.Device, Require(device).DeviceId);

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
        ///   SENT → ask ITMS whether it received the laptop · ITMS_RECEIVED → returns confirmed status.
        /// If forceResend is true, initiates a new submission attempt with a new request ID.
        /// A send left unfinished by a crash is first recovered as FAILED. With the connector disabled
        /// nothing is written and nothing is sent.
        /// </summary>
        public async Task<ItmsTransmissionOutcome> SendOrContinueAsync(DeviceRecord device, DetectionResult detection = null, bool forceResend = false)
        {
            Require(device);
            if (!_connector.IsEnabled)
                return new ItmsTransmissionOutcome
                {
                    Record = _repo.GetSyncRecord(SyncEntity.Device, device.DeviceId),
                    Transport = ItmsSendResult.NotEnabled(),
                    Message = ItmsSendResult.NotEnabled().Message,
                };

            string qcProblem = QcGateProblem(device);
            if (qcProblem != null) return Blocked(device, qcProblem);

            if (forceResend)
            {
                return await ResendAsync(device, detection).ConfigureAwait(false);
            }

            var record = RecoverInterrupted(device);
            switch (record.SyncStatus)
            {
                case TransmissionState.ItmsReceived:
                    return new ItmsTransmissionOutcome { Record = record, Message = Describe(record) };
                case TransmissionState.Sent:
                    return await CheckReceiptAsync(device).ConfigureAwait(false);
                case TransmissionState.Failed:
                    return await RetryAsync(device, detection).ConfigureAwait(false);
                case TransmissionState.Local:
                    var ready = MarkReady(device);
                    if (ready.Record.SyncStatus != TransmissionState.ReadyToSend) return ready;
                    return await SendAsync(device, detection, ready.Record).ConfigureAwait(false);
                default:
                    return await SendAsync(device, detection, record).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Explicitly creates and executes a new ITMS submission for this physical laptop,
        /// preserving all previous submission records in the history.
        /// </summary>
        public async Task<ItmsTransmissionOutcome> ResendAsync(DeviceRecord device, DetectionResult detection = null)
        {
            Require(device);
            if (!_connector.IsEnabled)
                return new ItmsTransmissionOutcome
                {
                    Record = _repo.GetOrCreateDeviceSync(device.DeviceId),
                    Transport = ItmsSendResult.NotEnabled(),
                    Message = ItmsSendResult.NotEnabled().Message,
                };

            string qcProblem = QcGateProblem(device);
            if (qcProblem != null) return Blocked(device, qcProblem);

            var payload = ItmsDevicePayload.From(device);
            if (!payload.IsValid)
            {
                var current = _repo.GetOrCreateDeviceSync(device.DeviceId);
                return new ItmsTransmissionOutcome { Record = current, Message = string.Join(" ", payload.Problems) };
            }

            // Create a brand new submission record in the history
            var record = _repo.CreateNewDeviceSync(device.DeviceId);
            record = _repo.TransitionSync(record.SyncId, TransmissionState.ReadyToSend);

            if (detection != null)
            {
                detection.SubmissionId = record.RequestId;
            }

            return await SendAsync(device, detection, record).ConfigureAwait(false);
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
        public async Task<ItmsTransmissionOutcome> SendAsync(DeviceRecord device, DetectionResult detection = null, SyncRecord record = null)
        {
            var payload = ItmsDevicePayload.From(Require(device));
            record = record ?? _repo.GetOrCreateDeviceSync(device.DeviceId);
            if (!_connector.IsEnabled)
                return new ItmsTransmissionOutcome { Record = record, Transport = ItmsSendResult.NotEnabled(), Message = ItmsSendResult.NotEnabled().Message };
            // Every transport path passes here: QC PASS is required, whichever button was used.
            string qcProblem = QcGateProblem(device);
            if (qcProblem != null) return new ItmsTransmissionOutcome { Record = record, Message = qcProblem };
            if (record.SyncStatus != TransmissionState.ReadyToSend)
                return new ItmsTransmissionOutcome { Record = record, Message = Describe(record) };

            if (detection != null && !string.IsNullOrEmpty(record.RequestId))
            {
                detection.SubmissionId = record.RequestId;
            }

            // Same request id for the first attempt and every retry (idempotent on the ITMS side).
            var submission = new ItmsSubmission(record.RequestId, payload, detection)
            {
                // Everything traceable about this laptop goes with it (built from the stored records only).
                Traceability = ItmsTraceability.Build(device, _repo.GetDeviceHistory(device.DeviceId), Requirements),
            };
            record = _repo.TransitionSync(record.SyncId, TransmissionState.Sending);
            ItmsSendResult result;
            try { result = await _connector.SendDeviceAsync(submission).ConfigureAwait(false); }
            catch (Exception) { result = ItmsSendResult.Failed(null); } // details stay out of the store and the UI
            return Apply(record, result ?? ItmsSendResult.Failed(null), submission.TraceabilitySha256);
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
        public Task<ItmsTransmissionOutcome> RetryAsync(DeviceRecord device, DetectionResult detection = null)
        {
            var record = _repo.GetOrCreateDeviceSync(Require(device).DeviceId);
            if (record.SyncStatus == TransmissionState.Sending)
                record = RecoverInterrupted(device);
            if (record.SyncStatus != TransmissionState.Failed)
                return Task.FromResult(new ItmsTransmissionOutcome { Record = record, Message = Describe(record) });
            if (!_connector.IsEnabled)
                return Task.FromResult(new ItmsTransmissionOutcome { Record = record, Transport = ItmsSendResult.NotEnabled(), Message = ItmsSendResult.NotEnabled().Message });
            string qcProblem = QcGateProblem(device);
            if (qcProblem != null) return Task.FromResult(new ItmsTransmissionOutcome { Record = record, Message = qcProblem });
            record = _repo.TransitionSync(record.SyncId, TransmissionState.ReadyToSend);
            return SendAsync(device, detection, record);
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

        /// <summary>VERIFIED (ITMS echoed the same traceability hash), UNCONFIRMED (ITMS does not echo it) or MISMATCH.</summary>
        public static string TraceabilityVerification(string sentSha256, string echoedSha256) =>
            string.IsNullOrEmpty(sentSha256) ? "NOT_SENT"
          : string.IsNullOrEmpty(echoedSha256) ? "UNCONFIRMED"
          : string.Equals(sentSha256, echoedSha256, StringComparison.OrdinalIgnoreCase) ? "VERIFIED" : "MISMATCH";

        private ItmsTransmissionOutcome Apply(SyncRecord sending, ItmsSendResult result, string sentTraceabilitySha256 = null)
        {
            SyncRecord record;
            string verification = TraceabilityVerification(sentTraceabilitySha256, result.EchoedTraceabilitySha256);
            switch (result.Outcome)
            {
                case ItmsSendOutcome.Received when verification == "MISMATCH":
                    // ITMS answered, but with different traceability data than was sent: never treated as delivered.
                    record = _repo.TransitionSync(sending.SyncId, TransmissionState.Sent);
                    record = _repo.TransitionSync(record.SyncId, TransmissionState.Failed,
                        error: "ITMS confirmed a different traceability record (hash mismatch). Send again.");
                    return new ItmsTransmissionOutcome { Record = record, Transport = result, Message = record.LastError, TraceabilityVerification = verification };
                case ItmsSendOutcome.Received:
                    record = _repo.TransitionSync(sending.SyncId, TransmissionState.Sent);
                    record = _repo.TransitionSync(record.SyncId, TransmissionState.ItmsReceived,
                        receipt: result.Receipt + (verification == "NOT_SENT" ? "" : " · traceability " + verification));
                    break;
                case ItmsSendOutcome.RequestSent:
                    record = _repo.TransitionSync(sending.SyncId, TransmissionState.Sent);
                    break;
                default: // Rejected, Failed, or a connector that turned out to be disabled
                    record = _repo.TransitionSync(sending.SyncId, TransmissionState.Failed, error: result.Message);
                    break;
            }
            return new ItmsTransmissionOutcome
            {
                Record = record, Transport = result, TraceabilityVerification = verification,
                Message = result.Message + (result.Outcome == ItmsSendOutcome.Received && verification == "UNCONFIRMED"
                    ? " (ITMS did not confirm the traceability content; it needs to echo traceability_sha256.)" : ""),
            };
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
