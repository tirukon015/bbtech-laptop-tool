using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BBTech.Core.Identity;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using Xunit;

namespace BBTech.Core.Tests
{
    public class ItmsConnectorTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-itms-tests-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private QcRepository OpenRepo()
        {
            var r = QcRepository.Open(_dir, new FakeMachine(1), QcStoreTests.LaptopA);
            Assert.True(r.Ok, r.Message);
            return r.Repository;
        }

        private static DetectionResult CreateTestDetection() => new DetectionResult
        {
            Brand = "Lenovo",
            Model = "ThinkPad T14",
            ModelNumber = "20UD-000B",
            Cpu = "AMD Ryzen 5 PRO 4650U",
            Generation = "Zen 2",
            Ram = "16 GB",
            Storage = "512 GB SSD",
            BatteryHealth = "92%",
            ToolVersion = "1.0.0",
            SubmissionId = Guid.NewGuid().ToString("D"),
            System = new SystemDetails
            {
                RawManufacturer = "Lenovo",
                RawModel = "ThinkPad T14 Gen 1",
                SerialNumber = "PC123456",
                SystemUuid = "6A3F2C10-1B2D-11B2-A85C-D5A1A3B0C9E1",
                BoardSerial = "L1HF0001",
            }
        };

        [Fact]
        public void IsEnabled_ReturnsTrueOnlyWhenConfigured()
        {
            var c1 = new HttpItmsConnector(() => "", () => "");
            Assert.False(c1.IsEnabled);

            var c2 = new HttpItmsConnector(() => "http://localhost:8000", () => "");
            Assert.False(c2.IsEnabled);

            var c3 = new HttpItmsConnector(() => "", () => "secret-key");
            Assert.False(c3.IsEnabled);

            var c4 = new HttpItmsConnector(() => "http://localhost:8000", () => "secret-key");
            Assert.True(c4.IsEnabled);
        }

        [Fact]
        public async Task SendDeviceAsync_ReturnsReceivedOnHttp201()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"success\":true,\"status\":\"queued\",\"request_no\":1005,\"item_id\":\"item-abc\"}")
                }
            };
            var connector = new HttpItmsConnector(() => "http://localhost:8000", () => "test-api-key", handler);
            var detection = CreateTestDetection();
            var payload = ItmsDevicePayload.From(detection);
            var submission = new ItmsSubmission(detection.SubmissionId, payload, detection);

            var result = await connector.SendDeviceAsync(submission);

            Assert.Equal(ItmsSendOutcome.Received, result.Outcome);
            Assert.Equal("Request #1005", result.Receipt);
            Assert.Contains("Queued in ITMS as Request #1005", result.Message);
            Assert.Contains("test-api-key", handler.LastRequest.Headers.GetValues("x-itms-api-key"));
        }

        [Fact]
        public async Task SendDeviceAsync_ReturnsReceivedOnHttp200AlreadyReceived()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"success\":true,\"status\":\"already_received\",\"request_no\":1005,\"item_id\":\"item-abc\"}")
                }
            };
            var connector = new HttpItmsConnector(() => "http://localhost:8000", () => "test-api-key", handler);
            var detection = CreateTestDetection();
            var payload = ItmsDevicePayload.From(detection);
            var submission = new ItmsSubmission(detection.SubmissionId, payload, detection);

            var result = await connector.SendDeviceAsync(submission);

            Assert.Equal(ItmsSendOutcome.Received, result.Outcome);
            Assert.Equal("Request #1005", result.Receipt);
            Assert.Contains("Already received by ITMS as Request #1005", result.Message);
        }

        [Fact]
        public async Task SendDeviceAsync_HandlesAuthFailureOnHttp401()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"success\":false,\"error\":\"Unauthorized\"}")
                }
            };
            var connector = new HttpItmsConnector(() => "http://localhost:8000", () => "bad-key", handler);
            var detection = CreateTestDetection();
            var payload = ItmsDevicePayload.From(detection);
            var submission = new ItmsSubmission(detection.SubmissionId, payload, detection);

            var result = await connector.SendDeviceAsync(submission);

            Assert.Equal(ItmsSendOutcome.Failed, result.Outcome);
            Assert.Contains("authentication failed", result.Message);
        }

        [Fact]
        public async Task SendDeviceAsync_HandlesValidationErrorOnHttp422()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage((HttpStatusCode)422)
                {
                    Content = new StringContent("{\"success\":false,\"error\":\"Hardware serial placeholder rejected\"}")
                }
            };
            var connector = new HttpItmsConnector(() => "http://localhost:8000", () => "test-key", handler);
            var detection = CreateTestDetection();
            var payload = ItmsDevicePayload.From(detection);
            var submission = new ItmsSubmission(detection.SubmissionId, payload, detection);

            var result = await connector.SendDeviceAsync(submission);

            Assert.Equal(ItmsSendOutcome.Rejected, result.Outcome);
            Assert.Contains("Hardware serial placeholder rejected", result.Message);
        }

        [Fact]
        public async Task SendDeviceAsync_HandlesNetworkTimeoutGracefully()
        {
            var handler = new MockHttpMessageHandler
            {
                ExceptionToThrow = new TaskCanceledException("Timeout")
            };
            var connector = new HttpItmsConnector(() => "http://localhost:8000", () => "test-key", handler);
            var detection = CreateTestDetection();
            var payload = ItmsDevicePayload.From(detection);
            var submission = new ItmsSubmission(detection.SubmissionId, payload, detection);

            var result = await connector.SendDeviceAsync(submission);

            Assert.Equal(ItmsSendOutcome.Failed, result.Outcome);
            Assert.Contains("timed out", result.Message);
        }

        [Fact]
        public async Task ItmsTransmissionService_PreservesSameRequestIdOnRetry()
        {
            var repo = OpenRepo();
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":\"Temporary server failure\"}")
                }
            };
            var connector = new HttpItmsConnector(() => "http://localhost:8000", () => "key", handler);
            var service = new ItmsTransmissionService(repo, connector);

            var detection = CreateTestDetection();
            var identity = IdentityFactory.From(detection);
            var laptop = service.ResolveLaptop(identity, IdentityFactory.SpecsFrom(detection));
            Assert.True(laptop.Ok);

            // Attempt 1 -> fails with 500
            var outcome1 = await service.SendOrContinueAsync(laptop.Device, detection);
            Assert.Equal(TransmissionState.Failed, outcome1.Record.SyncStatus);
            var firstRequestId = outcome1.Record.RequestId;
            Assert.False(string.IsNullOrWhiteSpace(firstRequestId));

            // Attempt 2 -> server recovers and returns 201
            handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"success\":true,\"status\":\"queued\",\"request_no\":2001}")
            };

            var outcome2 = await service.SendOrContinueAsync(laptop.Device, detection);
            Assert.Equal(TransmissionState.ItmsReceived, outcome2.Record.SyncStatus);
            Assert.Equal("Request #2001", outcome2.Record.RemoteId);
            // CRITICAL: RequestId must be identical across retries
            Assert.Equal(firstRequestId, outcome2.Record.RequestId);
            Assert.Equal(2, outcome2.Record.AttemptCount);
        }

        [Fact]
        public void ItmsTransmissionService_RecoversInterruptedSendingAsFailed()
        {
            var repo = OpenRepo();
            var connector = new HttpItmsConnector(() => "http://localhost:8000", () => "key");
            var service = new ItmsTransmissionService(repo, connector);

            var detection = CreateTestDetection();
            var identity = IdentityFactory.From(detection);
            var laptop = service.ResolveLaptop(identity, IdentityFactory.SpecsFrom(detection));

            var record = repo.GetOrCreateDeviceSync(laptop.Device.DeviceId);
            record = repo.TransitionSync(record.SyncId, TransmissionState.ReadyToSend);
            record = repo.TransitionSync(record.SyncId, TransmissionState.Sending);
            Assert.Equal(TransmissionState.Sending, record.SyncStatus);

            // Simulate recovery after crash
            var recovered = service.RecoverInterrupted(laptop.Device);
            Assert.Equal(TransmissionState.Failed, recovered.SyncStatus);
            Assert.Contains("interrupted", recovered.LastError);
        }
    }
}
