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

        /// <summary>Records a final QC PASS for the laptop (ITMS only accepts QC-passed laptops).</summary>
        private static void QcPass(QcRepository repo, DeviceRecord device)
        {
            // The real lifecycle: Hardware → handover → Software → handover → QC checklist → QC PASS.
            var req = BBTech.Core.Testing.QcRequirements.Default();
            foreach (var (dept, op, handover) in new[] { ("Hardware", "Kamal", BBTech.Core.Testing.LifecycleEvents.HardwareHandover), ("Software", "Shadir", BBTech.Core.Testing.LifecycleEvents.SoftwareHandover), ("QC", "Jimmy", null) })
            {
                foreach (var t in req.Required(dept)) repo.RecordTest(device.DeviceId, t, t, "PASS", op, dept);
                System.Threading.Thread.Sleep(15);
                if (handover != null) { repo.RecordTest(device.DeviceId, handover, handover, "PASS", op, dept); System.Threading.Thread.Sleep(15); }
            }
            repo.RecordTest(device.DeviceId, BBTech.Core.Testing.QcGate.QcDecisionTestId, "Final QC Decision", "PASS", "Jimmy", "QC");
        }

        [Fact]
        public async Task ItmsSend_IsRefused_WithoutFinalQcPass_AndNothingIsSent()
        {
            var repo = OpenRepo();
            var handler = new MockHttpMessageHandler();
            var service = new ItmsTransmissionService(repo, new HttpItmsConnector(() => "http://localhost:8000", () => "key", handler));
            var detection = CreateTestDetection();
            var laptop = service.ResolveLaptop(IdentityFactory.From(detection), IdentityFactory.SpecsFrom(detection));

            var noDecision = await service.SendOrContinueAsync(laptop.Device, detection);
            Assert.False(noDecision.Confirmed);
            Assert.Contains("no final QC decision", noDecision.Message);

            repo.RecordTest(laptop.Device.DeviceId, BBTech.Core.Testing.QcGate.QcDecisionTestId, "Final QC Decision", "FAIL", "Jimmy", "QC");
            var failed = await service.ResendAsync(laptop.Device, detection);
            Assert.Contains("FAIL", failed.Message);
            Assert.Null(handler.LastRequest); // nothing reached ITMS
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
            QcPass(repo, laptop.Device);

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
            Assert.StartsWith("Request #2001 ", outcome2.Record.RemoteId + " ");
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

        [Fact]
        public async Task ItmsTransmissionService_SupportsMultipleSubmissionsAndResend()
        {
            var repo = OpenRepo();
            var handler = new MockHttpMessageHandler();
            var connector = new HttpItmsConnector(() => "http://localhost:8000", () => "key", handler);
            var service = new ItmsTransmissionService(repo, connector);

            var detection = CreateTestDetection();
            detection.System.SerialNumber = "PF49SKL8";
            var identity = IdentityFactory.From(detection);
            var laptop = service.ResolveLaptop(identity, IdentityFactory.SpecsFrom(detection));
            Assert.True(laptop.Ok);
            QcPass(repo, laptop.Device);

            // Submission #1 -> Request #1004
            handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"success\":true,\"status\":\"queued\",\"request_no\":1004}")
            };
            var outcome1 = await service.SendOrContinueAsync(laptop.Device, detection);
            Assert.Equal(TransmissionState.ItmsReceived, outcome1.Record.SyncStatus);
            Assert.StartsWith("Request #1004 ", outcome1.Record.RemoteId + " ");
            var req1 = outcome1.Record.RequestId;

            // Submission #2 (Resend) -> Request #1012
            handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"success\":true,\"status\":\"queued\",\"request_no\":1012}")
            };
            var outcome2 = await service.ResendAsync(laptop.Device, detection);
            Assert.Equal(TransmissionState.ItmsReceived, outcome2.Record.SyncStatus);
            Assert.StartsWith("Request #1012 ", outcome2.Record.RemoteId + " ");
            var req2 = outcome2.Record.RequestId;
            Assert.NotEqual(req1, req2); // Must get a fresh request id for a new submission

            // Submission #3 (Resend) -> Request #1018
            handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"success\":true,\"status\":\"queued\",\"request_no\":1018}")
            };
            var outcome3 = await service.ResendAsync(laptop.Device, detection);
            Assert.Equal(TransmissionState.ItmsReceived, outcome3.Record.SyncStatus);
            Assert.StartsWith("Request #1018 ", outcome3.Record.RemoteId + " ");

            // Submission #4 (Resend) -> Request #1021
            handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"success\":true,\"status\":\"queued\",\"request_no\":1021}")
            };
            var outcome4 = await service.ResendAsync(laptop.Device, detection);
            Assert.Equal(TransmissionState.ItmsReceived, outcome4.Record.SyncStatus);
            Assert.StartsWith("Request #1021 ", outcome4.Record.RemoteId + " ");

            // Verify Submission History: all 4 records preserved in chronological order
            var history = service.GetHistory(laptop.Device);
            Assert.Equal(4, history.Count);
            Assert.StartsWith("Request #1021 ", history[0].RemoteId + " ");
            Assert.StartsWith("Request #1018 ", history[1].RemoteId + " ");
            Assert.StartsWith("Request #1012 ", history[2].RemoteId + " ");
            Assert.StartsWith("Request #1004 ", history[3].RemoteId + " ");

            // Latest status query returns latest submission
            var latest = service.GetStatus(laptop.Device);
            Assert.StartsWith("Request #1021 ", latest.RemoteId + " ");
            Assert.Equal(TransmissionState.ItmsReceived, latest.SyncStatus);
        }

        [Fact]
        public async Task SendDeviceAsync_HandlesServiceMisconfigured503_WithServerDetail()
        {
            var loggedDiagnostics = new System.Collections.Generic.List<string>();
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"success\":false,\"error\":\"Intake is temporarily unavailable: the server is not configured to accept Laptop Tool submissions.\",\"code\":\"SERVER_MISCONFIGURED\"}")
                }
            };
            var connector = new HttpItmsConnector(() => "https://itms.neksdev.tech", () => "secret-key", handler, msg => loggedDiagnostics.Add(msg));
            var detection = CreateTestDetection();
            var payload = ItmsDevicePayload.From(detection);
            var submission = new ItmsSubmission(detection.SubmissionId, payload, detection);

            var result = await connector.SendDeviceAsync(submission);

            Assert.Equal(ItmsSendOutcome.Failed, result.Outcome);
            Assert.Equal(503, result.HttpStatusCode);
            Assert.Equal("SERVER_MISCONFIGURED", result.ServerCode);
            Assert.True(result.IsRetryable);
            Assert.Contains("SERVER_MISCONFIGURED", result.Message);
            Assert.Contains("the server is not configured to accept Laptop Tool submissions", result.Message);

            // Safe diagnostic capture verified
            Assert.NotEmpty(loggedDiagnostics);
            var diag = loggedDiagnostics[0];
            Assert.Contains("POST https://itms.neksdev.tech/api/devices/requests", diag);
            Assert.Contains("Status: 503", diag);
            Assert.Contains("SERVER_MISCONFIGURED", diag);
            Assert.DoesNotContain("secret-key", diag); // No secrets leaked
        }

        [Fact]
        public async Task SendDeviceAsync_HandlesGatewayTimeout504()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
                {
                    Content = new StringContent("Gateway Timeout")
                }
            };
            var connector = new HttpItmsConnector(() => "http://localhost:8000", () => "key", handler);
            var detection = CreateTestDetection();
            var payload = ItmsDevicePayload.From(detection);
            var submission = new ItmsSubmission(detection.SubmissionId, payload, detection);

            var result = await connector.SendDeviceAsync(submission);

            Assert.Equal(ItmsSendOutcome.Failed, result.Outcome);
            Assert.Equal(504, result.HttpStatusCode);
            Assert.True(result.IsRetryable);
            Assert.Contains("504", result.Message);
        }

        [Fact]
        public async Task SendDeviceAsync_HandlesInternalServerError500_WithRetryableFlag()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":\"Database connection failed\",\"code\":\"DB_DOWN\"}")
                }
            };
            var connector = new HttpItmsConnector(() => "http://localhost:8000", () => "key", handler);
            var detection = CreateTestDetection();
            var payload = ItmsDevicePayload.From(detection);
            var submission = new ItmsSubmission(detection.SubmissionId, payload, detection);

            var result = await connector.SendDeviceAsync(submission);

            Assert.Equal(ItmsSendOutcome.Failed, result.Outcome);
            Assert.Equal(500, result.HttpStatusCode);
            Assert.Equal("DB_DOWN", result.ServerCode);
            Assert.True(result.IsRetryable);
            Assert.Contains("Database connection failed", result.Message);
        }
    }
}
