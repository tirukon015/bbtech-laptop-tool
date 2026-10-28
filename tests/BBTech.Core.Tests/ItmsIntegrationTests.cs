using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Settings;
using Xunit;

namespace BBTech.Core.Tests
{
    public class MockHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage LastRequest { get; private set; }
        public string LastRequestBody { get; private set; }
        public HttpResponseMessage ResponseToReturn { get; set; } = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"success\":true,\"status\":\"queued\",\"request_no\":1002,\"item_id\":\"item-uuid-123\"}")
        };
        public Exception ExceptionToThrow { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync();
            }

            if (ExceptionToThrow != null) throw ExceptionToThrow;
            return ResponseToReturn;
        }
    }

    public class ItmsIntegrationTests
    {
        [Fact]
        public void IsConfigured_ReturnsTrueOnlyWhenUrlAndKeyProvided()
        {
            var c1 = new HttpItmsClient(() => "", () => "");
            Assert.False(c1.IsConfigured);

            var c2 = new HttpItmsClient(() => "http://localhost:8000", () => "");
            Assert.False(c2.IsConfigured);

            var c3 = new HttpItmsClient(() => "http://localhost:8000", () => "my-key");
            Assert.True(c3.IsConfigured);
        }

        [Fact]
        public async Task SubmitNewDeviceAsync_SendsAgreedHeadersAndPayload()
        {
            var handler = new MockHttpMessageHandler();
            var client = new HttpItmsClient(() => "https://itms.local.dev", () => "secret-key-xyz", handler);

            var detection = new DetectionResult
            {
                Brand = "Lenovo",
                Model = "ThinkPad T480",
                ModelNumber = "20L5",
                Cpu = "Intel Core i5-8350U",
                Generation = "8th Gen",
                Ram = "16 GB",
                Storage = "256 GB SSD",
                BatteryHealth = "85%",
                ToolVersion = "1.0.0",
            };
            detection.System.SerialNumber = "PF123456";
            detection.System.SystemUuid = "01234567-89AB-CDEF-0123-456789ABCDEF";

            var result = await client.SubmitNewDeviceAsync(detection);

            Assert.True(result.Success);
            Assert.Equal(1002, result.RequestNo);
            Assert.Equal("item-uuid-123", result.DeviceId);
            Assert.Equal(detection.SubmissionId, result.RequestId);

            // Verify HTTP Request
            Assert.NotNull(handler.LastRequest);
            Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
            Assert.Equal("https://itms.local.dev/api/devices/requests", handler.LastRequest.RequestUri.ToString());

            // Verify Auth Header
            Assert.True(handler.LastRequest.Headers.Contains("x-itms-api-key"));
            Assert.Contains("secret-key-xyz", handler.LastRequest.Headers.GetValues("x-itms-api-key"));

            // Verify User-Agent
            Assert.True(handler.LastRequest.Headers.Contains("User-Agent"));

            // Verify Payload contains stable SubmissionId
            Assert.Contains(detection.SubmissionId, handler.LastRequestBody);
            Assert.Contains("\"schema_version\": \"1.0\"", handler.LastRequestBody);
            Assert.Contains("PF123456", handler.LastRequestBody);
        }

        [Fact]
        public async Task SubmitNewDeviceAsync_Handles401UnauthorizedWithoutExposingKey()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"success\":false,\"error\":\"Unauthorized\"}")
                }
            };
            var client = new HttpItmsClient(() => "https://itms.local.dev", () => "wrong-key", handler);

            var detection = new DetectionResult { Brand = "Lenovo" };
            var result = await client.SubmitNewDeviceAsync(detection);

            Assert.False(result.Success);
            Assert.Contains("authentication failed", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wrong-key", result.Message);
        }

        [Fact]
        public async Task SubmitNewDeviceAsync_Handles422ValidationError()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage((HttpStatusCode)422)
                {
                    Content = new StringContent("{\"success\":false,\"error\":\"No valid hardware identity found\"}")
                }
            };
            var client = new HttpItmsClient(() => "https://itms.local.dev", () => "valid-key", handler);

            var detection = new DetectionResult { Brand = "Lenovo" };
            var result = await client.SubmitNewDeviceAsync(detection);

            Assert.False(result.Success);
            Assert.Contains("validation failed", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No valid hardware identity found", result.Message);
        }

        [Fact]
        public async Task SubmitNewDeviceAsync_HandlesTimeoutGracefully()
        {
            var handler = new MockHttpMessageHandler
            {
                ExceptionToThrow = new TaskCanceledException("Timed out")
            };
            var client = new HttpItmsClient(() => "https://itms.local.dev", () => "valid-key", handler);

            var detection = new DetectionResult { Brand = "Lenovo" };
            var result = await client.SubmitNewDeviceAsync(detection);

            Assert.False(result.Success);
            Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ItmsPayload_BuildInspectionPayload_GeneratesValidJsonStructure()
        {
            var detection = new DetectionResult
            {
                Brand = "Dell",
                Model = "Latitude 7490",
                ModelNumber = "7490",
                Cpu = "i7-8650U",
                Generation = "8th Gen",
                Ram = "16 GB",
                Storage = "512 GB SSD",
                BatteryHealth = "90%",
                ToolVersion = "1.0.0"
            };
            detection.System.SerialNumber = "ABC1234";

            string json = ItmsPayload.BuildInspectionPayload(detection, "req-uuid-999", "Tester Bob", "PASS");

            Assert.Contains("\"schema_version\": \"1.0\"", json);
            Assert.Contains("\"request_id\": \"req-uuid-999\"", json);
            Assert.Contains("\"overall_result\": \"PASS\"", json);
            Assert.Contains("\"tested_by\": \"Tester Bob\"", json);
            Assert.Contains("\"serial_number\": \"ABC1234\"", json);
            Assert.Contains("\"brand\": \"Dell\"", json);
        }
    }
}
