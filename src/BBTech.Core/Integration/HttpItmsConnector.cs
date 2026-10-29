using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BBTech.Core.Export;
using BBTech.Core.Models;

namespace BBTech.Core.Integration
{
    /// <summary>
    /// Real HTTP connector to the ITMS /api/devices/requests endpoint.
    /// Provides idempotent submissions using the submission's RequestId.
    /// </summary>
    public sealed class HttpItmsConnector : ItmsConnectorBase
    {
        private readonly Func<string> _baseUrlProvider;
        private readonly Func<string> _apiKeyProvider;
        private readonly HttpMessageHandler _customHandler;

        public HttpItmsConnector(Func<string> baseUrlProvider, Func<string> apiKeyProvider, HttpMessageHandler customHandler = null)
        {
            _baseUrlProvider = baseUrlProvider ?? (() => "");
            _apiKeyProvider = apiKeyProvider ?? (() => "");
            _customHandler = customHandler;
        }

        public override bool IsEnabled
        {
            get
            {
                var url = _baseUrlProvider();
                var key = _apiKeyProvider();
                return !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key);
            }
        }

        public override async Task<ItmsSendResult> SendDeviceAsync(ItmsSubmission submission)
        {
            if (submission == null)
            {
                return ItmsSendResult.Failed("No submission payload provided.");
            }

            if (!IsEnabled)
            {
                return ItmsSendResult.NotEnabled();
            }

            var baseUrl = (_baseUrlProvider() ?? "").Trim().TrimEnd('/');
            var apiKey = (_apiKeyProvider() ?? "").Trim();

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri baseUri))
            {
                return ItmsSendResult.Failed("Invalid ITMS Server URL: \"" + baseUrl + "\". Please enter a valid HTTP or HTTPS address.");
            }

            var endpoint = baseUrl + "/api/devices/requests";
            var requestId = submission.RequestId;

            string jsonPayload;
            if (submission.Detection != null)
            {
                jsonPayload = ItmsPayload.BuildInspectionPayload(submission.Detection, requestId);
            }
            else
            {
                var root = new JsonObject
                {
                    { "schema_version", "1.0" },
                    { "request_id", requestId },
                    { "source", "bbtech-laptop-tool" },
                    { "tool_version", "1.0.0" },
                    { "inspection_date", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                    { "inspection_time", DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) },
                    { "overall_result", "DETECTED" },
                    { "device", submission.Device.ToJsonObject() },
                };
                jsonPayload = JsonWriter.Serialize(root);
            }

            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                using (var client = _customHandler != null ? new HttpClient(_customHandler, false) : new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    var version = submission.Detection?.ToolVersion;
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "BBTech-LaptopTool/" + (string.IsNullOrEmpty(version) ? "1.0.0" : version));
                    client.DefaultRequestHeaders.TryAddWithoutValidation("x-itms-api-key", apiKey);

                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    using (var response = await client.PostAsync(endpoint, content).ConfigureAwait(false))
                    {
                        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (response.IsSuccessStatusCode)
                        {
                            long reqNo = 0;
                            var reqNoMatch = Regex.Match(responseBody, @"""request_no""\s*:\s*(\d+)");
                            if (reqNoMatch.Success) long.TryParse(reqNoMatch.Groups[1].Value, out reqNo);

                            string status = "";
                            var statusMatch = Regex.Match(responseBody, @"""status""\s*:\s*""([^""]+)""");
                            if (statusMatch.Success) status = statusMatch.Groups[1].Value;

                            string receipt = reqNo > 0 ? "Request #" + reqNo : "Received";
                            string msg = reqNo > 0
                                ? (status == "already_received" ? "Already received by ITMS as Request #" + reqNo : "Queued in ITMS as Request #" + reqNo)
                                : "Successfully received by ITMS.";

                            return ItmsSendResult.Received(receipt, msg);
                        }

                        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            return ItmsSendResult.Failed("ITMS authentication failed. Verify the API key in Settings.");
                        }

                        if ((int)response.StatusCode == 400 || (int)response.StatusCode == 413 || (int)response.StatusCode == 422)
                        {
                            var errMsgMatch = Regex.Match(responseBody, @"""error""\s*:\s*""([^""]+)""");
                            string errDetail = errMsgMatch.Success ? errMsgMatch.Groups[1].Value : "Validation rejected by ITMS.";
                            return ItmsSendResult.Rejected(errDetail);
                        }

                        return ItmsSendResult.Failed("ITMS responded with HTTP " + (int)response.StatusCode + " (" + response.ReasonPhrase + ").");
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return ItmsSendResult.Failed("Connection timed out connecting to ITMS (" + baseUrl + "). Check your network and try again.");
            }
            catch (HttpRequestException ex)
            {
                return ItmsSendResult.Failed("Network error connecting to ITMS: " + ex.Message);
            }
            catch (Exception ex)
            {
                return ItmsSendResult.Failed("Unexpected submission error: " + ex.Message);
            }
        }

        public override Task<ItmsSendResult> SendBatchAsync(ItmsBatchPayload batch) =>
            Task.FromResult(ItmsSendResult.Failed("Batch submission is not supported. Submissions are processed one laptop at a time."));

        public override Task<ItmsSendResult> GetStatusAsync(string requestId) =>
            Task.FromResult(ItmsSendResult.RequestSent("Waiting for ITMS to confirm receipt."));
    }
}
