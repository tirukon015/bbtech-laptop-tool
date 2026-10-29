using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBTech.Core.Models;

namespace BBTech.Core.Integration
{
    public enum ItmsSendOutcome
    {
        /// <summary>No live ITMS connection exists in this version. Nothing was sent.</summary>
        NotEnabled,
        /// <summary>The request reached ITMS, but ITMS has not confirmed receipt yet.</summary>
        RequestSent,
        /// <summary>ITMS confirmed it received the laptop (a receipt reference is present).</summary>
        Received,
        /// <summary>ITMS answered and refused the payload.</summary>
        Rejected,
        /// <summary>The request could not be completed (network, timeout, server error).</summary>
        Failed,
    }

    /// <summary>Answer of the transport. Only <see cref="ItmsSendOutcome.Received"/> means "registered in ITMS".</summary>
    public sealed class ItmsSendResult
    {
        public ItmsSendOutcome Outcome { get; private set; }
        /// <summary>ITMS reference of the received laptop (Received only).</summary>
        public string Receipt { get; private set; }
        /// <summary>Staff-readable message. Never contains credentials or payload secrets.</summary>
        public string Message { get; private set; } = "";

        private ItmsSendResult() { }

        public static ItmsSendResult NotEnabled() => new ItmsSendResult
        {
            Outcome = ItmsSendOutcome.NotEnabled,
            Message = "Sending to ITMS is not available in this version. The laptop's information is kept on this laptop.",
        };

        public static ItmsSendResult RequestSent(string message = "Sent to ITMS. Waiting for ITMS to confirm.") =>
            new ItmsSendResult { Outcome = ItmsSendOutcome.RequestSent, Message = message ?? "" };

        public static ItmsSendResult Received(string receipt, string message = "ITMS received this laptop.")
        {
            if (string.IsNullOrWhiteSpace(receipt)) throw new ArgumentException("A received result needs the ITMS receipt reference.", nameof(receipt));
            return new ItmsSendResult { Outcome = ItmsSendOutcome.Received, Receipt = receipt.Trim(), Message = message ?? "" };
        }

        public static ItmsSendResult Rejected(string message) =>
            new ItmsSendResult { Outcome = ItmsSendOutcome.Rejected, Message = string.IsNullOrWhiteSpace(message) ? "ITMS refused this laptop's information." : message };

        public static ItmsSendResult Failed(string message) =>
            new ItmsSendResult { Outcome = ItmsSendOutcome.Failed, Message = string.IsNullOrWhiteSpace(message) ? "The laptop could not be sent to ITMS." : message };
    }

    /// <summary>
    /// Transport to ITMS. The normal operation is ONE laptop: <see cref="PrepareDevice"/> →
    /// <see cref="SendDeviceAsync"/> → (if needed) <see cref="GetStatusAsync"/> until ITMS confirms.
    /// Every send carries a request id (UUID); a retry sends the SAME request id, so the ITMS service
    /// can recognise a request it already processed. Duplicate detection of laptops is done by ITMS.
    /// <see cref="PrepareBatch"/> / <see cref="SendBatchAsync"/> are a future transport capability only;
    /// the application never collects several laptops locally.
    /// </summary>
    public interface IItmsConnector
    {
        /// <summary>False in this version: no live ITMS connection exists.</summary>
        bool IsEnabled { get; }

        ItmsDevicePayload PrepareDevice(DetectionResult detection);
        ItmsBatchPayload PrepareBatch(IEnumerable<ItmsDevicePayload> devices);
        Task<ItmsSendResult> SendDeviceAsync(ItmsSubmission submission);
        Task<ItmsSendResult> SendBatchAsync(ItmsBatchPayload batch);
        /// <summary>Asks ITMS whether the request with this request id was received.</summary>
        Task<ItmsSendResult> GetStatusAsync(string requestId);
        /// <summary>Sends the same submission (same request id) again after a failed attempt.</summary>
        Task<ItmsSendResult> RetryAsync(ItmsSubmission submission);
    }

    /// <summary>Payload preparation shared by every connector (pure, offline).</summary>
    public abstract class ItmsConnectorBase : IItmsConnector
    {
        public abstract bool IsEnabled { get; }

        public ItmsDevicePayload PrepareDevice(DetectionResult detection) => ItmsDevicePayload.From(detection);

        public ItmsBatchPayload PrepareBatch(IEnumerable<ItmsDevicePayload> devices) => new ItmsBatchPayload(ItmsSubmission.NewRequestId(), devices);

        public abstract Task<ItmsSendResult> SendDeviceAsync(ItmsSubmission submission);
        public abstract Task<ItmsSendResult> SendBatchAsync(ItmsBatchPayload batch);
        public abstract Task<ItmsSendResult> GetStatusAsync(string requestId);
        public virtual Task<ItmsSendResult> RetryAsync(ItmsSubmission submission) => SendDeviceAsync(submission);
    }

    /// <summary>
    /// The connector shipped in this version. Payloads are prepared normally; every transport call
    /// returns <see cref="ItmsSendOutcome.NotEnabled"/>. It has no network code and never reports success.
    /// </summary>
    public sealed class DisabledItmsConnector : ItmsConnectorBase
    {
        public override bool IsEnabled => false;

        public override Task<ItmsSendResult> SendDeviceAsync(ItmsSubmission submission) => Task.FromResult(ItmsSendResult.NotEnabled());
        public override Task<ItmsSendResult> SendBatchAsync(ItmsBatchPayload batch) => Task.FromResult(ItmsSendResult.NotEnabled());
        public override Task<ItmsSendResult> GetStatusAsync(string requestId) => Task.FromResult(ItmsSendResult.NotEnabled());
    }
}
