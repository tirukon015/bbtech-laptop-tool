using System;
using System.Diagnostics;
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
    /// Captures safe diagnostics (without secrets) and exposes rich HTTP status details.
    /// </summary>
    public sealed class HttpItmsConnector : ItmsConnectorBase
    {
        private readonly Func<string> _baseUrlProvider;
        private readonly Func<string> _apiKeyProvider;
        private readonly HttpMessageHandler _customHandler;
        private readonly Action<string> _diagnosticLogger;

        public HttpItmsConnector(
            Func<string> baseUrlProvider,
            Func<string> apiKeyProvider,
            HttpMessageHandler customHandler = null,
            Action<string> diagnosticLogger = null)
        {
            _baseUrlProvider = baseUrlProvider ?? (() => "");
            _apiKeyProvider = apiKeyProvider ?? (() => "");
            _customHandler = customHandler;
            _diagnosticLogger = diagnosticLogger;
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
                jsonPayload = ItmsPayload.BuildInspectionPayload(submission.Detection, requestId, traceability: submission.Traceability);
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
                if (submission.Traceability != null) { root.Add("bbtech_traceability", submission.Traceability); root.Add("traceability_sha256", submission.TraceabilitySha256); }
                jsonPayload = JsonWriter.Serialize(root);
            }

            var sw = Stopwatch.StartNew();

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
                        sw.Stop();
                        var elapsedMs = sw.ElapsedMilliseconds;
                        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        int statusCode = (int)response.StatusCode;
                        string reason = response.ReasonPhrase ?? "";

                        // Parse server error and machine code if present in JSON
                        string serverError = null;
                        var errMatch = Regex.Match(responseBody, @"""error""\s*:\s*""([^""]+)""");
                        if (errMatch.Success) serverError = errMatch.Groups[1].Value;
                        if (string.IsNullOrEmpty(serverError))
                        {
                            var msgMatch = Regex.Match(responseBody, @"""message""\s*:\s*""([^""]+)""");
                            if (msgMatch.Success) serverError = msgMatch.Groups[1].Value;
                        }

                        string serverCode = null;
                        var codeMatch = Regex.Match(responseBody, @"""code""\s*:\s*""([^""]+)""");
                        if (codeMatch.Success) serverCode = codeMatch.Groups[1].Value;

                        string safeDiag = string.Format(
                            CultureInfo.InvariantCulture,
                            "ITMS HTTP POST {0} -> Status: {1} ({2}), Elapsed: {3}ms, RequestId: {4}, Code: {5}, Body: {6}",
                            endpoint,
                            statusCode,
                            reason,
                            elapsedMs,
                            requestId,
                            serverCode ?? "NONE",
                            TruncateSafe(responseBody, 250));

                        _diagnosticLogger?.Invoke(safeDiag);

                        // 1. HTTP 200–299: Success
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

                            var received = ItmsSendResult.Received(receipt, msg, statusCode, safeDiag);
                            var echo = System.Text.RegularExpressions.Regex.Match(responseBody ?? "", @"""traceability_sha256""\s*:\s*""([A-Fa-f0-9]{64})""");
                            if (echo.Success) received.EchoedTraceabilitySha256 = echo.Groups[1].Value.ToLowerInvariant();
                            return received;
                        }

                        // 2. HTTP 401 / 403: Authentication failure (client credential issue, not retryable)
                        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            return ItmsSendResult.Failed(
                                "ITMS authentication failed (HTTP " + statusCode + "). Verify the API key in Settings.",
                                statusCode,
                                "UNAUTHORIZED",
                                isRetryable: false,
                                diagnosticSummary: safeDiag);
                        }

                        // 3. HTTP 400 / 413 / 422: Validation or payload rejected (client issue, do not blind-retry)
                        if (statusCode == 400 || statusCode == 413 || statusCode == 422)
                        {
                            string errDetail = !string.IsNullOrEmpty(serverError)
                                ? serverError
                                : "Validation rejected by ITMS (HTTP " + statusCode + ").";
                            return ItmsSendResult.Rejected(errDetail, statusCode, serverCode, safeDiag);
                        }

                        // 4. HTTP 503: Service Unavailable / Misconfigured (transient / retryable with detailed diagnostics)
                        if (statusCode == 503)
                        {
                            string msg;
                            if (serverCode == "SERVER_MISCONFIGURED")
                            {
                                msg = "ITMS is temporarily unavailable (HTTP 503 SERVER_MISCONFIGURED): " +
                                      (!string.IsNullOrEmpty(serverError) ? serverError : "the server is not configured to accept Laptop Tool submissions.") +
                                      " Check ITMS server configuration or try again later.";
                            }
                            else if (!string.IsNullOrEmpty(serverError))
                            {
                                msg = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "ITMS is temporarily unavailable (HTTP 503{0}): {1}. Try again later.",
                                    !string.IsNullOrEmpty(serverCode) ? " " + serverCode : "",
                                    serverError);
                            }
                            else
                            {
                                msg = "ITMS is temporarily unavailable (HTTP 503 Service Unavailable). The service may be restarting or undergoing maintenance. Try again later.";
                            }

                            return ItmsSendResult.Failed(msg, statusCode, serverCode, isRetryable: true, diagnosticSummary: safeDiag);
                        }

                        // 5. HTTP 500 / 502: Internal Server Error / Bad Gateway (retryable with existing request ID)
                        if (statusCode == 500 || statusCode == 502)
                        {
                            string msg = !string.IsNullOrEmpty(serverError)
                                ? string.Format(CultureInfo.InvariantCulture, "ITMS server error (HTTP {0}{1}): {2}. Try again.", statusCode, !string.IsNullOrEmpty(serverCode) ? " " + serverCode : "", serverError)
                                : string.Format(CultureInfo.InvariantCulture, "ITMS server error (HTTP {0} {1}). Try again; resending the same request is safe.", statusCode, reason);

                            return ItmsSendResult.Failed(msg, statusCode, serverCode, isRetryable: true, diagnosticSummary: safeDiag);
                        }

                        // 6. HTTP 504: Gateway Timeout (retryable)
                        if (statusCode == 504)
                        {
                            return ItmsSendResult.Failed(
                                "ITMS gateway timed out (HTTP 504 Gateway Timeout). The server took too long to respond. Try again.",
                                statusCode,
                                serverCode ?? "GATEWAY_TIMEOUT",
                                isRetryable: true,
                                diagnosticSummary: safeDiag);
                        }

                        // 7. Other HTTP status codes
                        string genericMsg = !string.IsNullOrEmpty(serverError)
                            ? string.Format(CultureInfo.InvariantCulture, "ITMS responded with HTTP {0} ({1}): {2}", statusCode, reason, serverError)
                            : string.Format(CultureInfo.InvariantCulture, "ITMS responded with HTTP {0} ({1}).", statusCode, reason);

                        return ItmsSendResult.Failed(genericMsg, statusCode, serverCode, isRetryable: false, diagnosticSummary: safeDiag);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                string diag = string.Format(CultureInfo.InvariantCulture, "ITMS HTTP POST {0} -> Timed out after {1}ms, RequestId: {2}", endpoint, sw.ElapsedMilliseconds, requestId);
                _diagnosticLogger?.Invoke(diag);
                return ItmsSendResult.Failed(
                    "Connection timed out connecting to ITMS (" + baseUrl + "). Check your network and try again.",
                    httpStatusCode: null,
                    serverCode: "TIMEOUT",
                    isRetryable: true,
                    diagnosticSummary: diag);
            }
            catch (HttpRequestException ex)
            {
                sw.Stop();
                string diag = string.Format(CultureInfo.InvariantCulture, "ITMS HTTP POST {0} -> Network error after {1}ms, RequestId: {2}, Message: {3}", endpoint, sw.ElapsedMilliseconds, requestId, ex.Message);
                _diagnosticLogger?.Invoke(diag);
                return ItmsSendResult.Failed(
                    "Network error connecting to ITMS: " + ex.Message,
                    httpStatusCode: null,
                    serverCode: "NETWORK_ERROR",
                    isRetryable: true,
                    diagnosticSummary: diag);
            }
            catch (Exception ex)
            {
                sw.Stop();
                string diag = string.Format(CultureInfo.InvariantCulture, "ITMS HTTP POST {0} -> Unexpected error after {1}ms, RequestId: {2}, Message: {3}", endpoint, sw.ElapsedMilliseconds, requestId, ex.Message);
                _diagnosticLogger?.Invoke(diag);
                return ItmsSendResult.Failed(
                    "Unexpected submission error: " + ex.Message,
                    httpStatusCode: null,
                    serverCode: "UNEXPECTED",
                    isRetryable: false,
                    diagnosticSummary: diag);
            }
        }

        private static string TruncateSafe(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string clean = Regex.Replace(text, @"\s+", " ").Trim();
            if (clean.Length <= maxLen) return clean;
            return clean.Substring(0, maxLen) + "…";
        }

        public override Task<ItmsSendResult> SendBatchAsync(ItmsBatchPayload batch) =>
            Task.FromResult(ItmsSendResult.Failed("Batch submission is not supported. Submissions are processed one laptop at a time."));

        public override Task<ItmsSendResult> GetStatusAsync(string requestId) =>
            Task.FromResult(ItmsSendResult.RequestSent("Waiting for ITMS to confirm receipt."));
    }
}
