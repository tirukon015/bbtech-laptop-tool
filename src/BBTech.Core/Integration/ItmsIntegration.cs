using System;
using System.Collections.Generic;
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
    /// <summary>Result of sending a device to ITMS.</summary>
    public sealed class ItmsSubmitResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        /// <summary>ITMS identifier of the created device, when the API returns one.</summary>
        public string DeviceId { get; set; } = "";
        /// <summary>Human-readable request number assigned by ITMS (e.g. 1002).</summary>
        public long RequestNo { get; set; }
        /// <summary>Idempotency UUID sent to ITMS.</summary>
        public string RequestId { get; set; } = "";
        /// <summary>Submission status returned by ITMS (e.g. "queued" or "already_received").</summary>
        public string Status { get; set; } = "";
    }

    /// <summary>
    /// Service contract for submitting detected laptops to the ITMS review queue.
    /// </summary>
    public interface IItmsClient
    {
        bool IsConfigured { get; }
        Task<ItmsSubmitResult> SubmitNewDeviceAsync(DetectionResult detection);
    }

    public sealed class NotConfiguredItmsClient : IItmsClient
    {
        public bool IsConfigured => false;

        public Task<ItmsSubmitResult> SubmitNewDeviceAsync(DetectionResult detection) =>
            Task.FromResult(new ItmsSubmitResult
            {
                Success = false,
                Message = "ITMS integration is not configured yet."
            });
    }

    /// <summary>
    /// Real HTTP client that submits detection records to the ITMS review queue API.
    /// Fully idempotent using detection.SubmissionId.
    /// </summary>
    public sealed class HttpItmsClient : IItmsClient
    {
        private readonly Func<string> _baseUrlProvider;
        private readonly Func<string> _apiKeyProvider;
        private readonly HttpMessageHandler _customHandler;

        public HttpItmsClient(Func<string> baseUrlProvider, Func<string> apiKeyProvider, HttpMessageHandler customHandler = null)
        {
            _baseUrlProvider = baseUrlProvider ?? (() => "");
            _apiKeyProvider = apiKeyProvider ?? (() => "");
            _customHandler = customHandler;
        }

        public bool IsConfigured
        {
            get
            {
                var url = _baseUrlProvider();
                var key = _apiKeyProvider();
                return !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key);
            }
        }

        public async Task<ItmsSubmitResult> SubmitNewDeviceAsync(DetectionResult detection)
        {
            if (detection == null)
            {
                return new ItmsSubmitResult { Success = false, Message = "No detection result available to submit." };
            }

            var baseUrl = (_baseUrlProvider() ?? "").Trim().TrimEnd('/');
            var apiKey = (_apiKeyProvider() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            {
                return new ItmsSubmitResult
                {
                    Success = false,
                    Message = "ITMS is not configured. Set the ITMS Server URL and API key in Settings or environment variables (ITMS_URL, ITMS_API_KEY)."
                };
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri baseUri))
            {
                return new ItmsSubmitResult
                {
                    Success = false,
                    Message = "Invalid ITMS Server URL: \"" + baseUrl + "\". Please enter a valid HTTP or HTTPS address."
                };
            }

            var endpoint = baseUrl + "/api/devices/requests";
            var requestId = detection.SubmissionId;
            var jsonPayload = ItmsPayload.BuildInspectionPayload(detection, requestId);

            try
            {
                // Force TLS 1.2 compatibility on .NET 4.7.2
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                using (var client = _customHandler != null ? new HttpClient(_customHandler, false) : new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "BBTech-LaptopTool/" + (string.IsNullOrEmpty(detection.ToolVersion) ? "1.0.0" : detection.ToolVersion));
                    client.DefaultRequestHeaders.TryAddWithoutValidation("x-itms-api-key", apiKey);

                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    using (var response = await client.PostAsync(endpoint, content).ConfigureAwait(false))
                    {
                        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (response.IsSuccessStatusCode)
                        {
                            long reqNo = 0;
                            string devId = "";
                            string status = "";

                            var reqNoMatch = Regex.Match(responseBody, @"""request_no""\s*:\s*(\d+)");
                            if (reqNoMatch.Success) long.TryParse(reqNoMatch.Groups[1].Value, out reqNo);

                            var itemIdMatch = Regex.Match(responseBody, @"""item_id""\s*:\s*""([^""]+)""");
                            if (itemIdMatch.Success) devId = itemIdMatch.Groups[1].Value;

                            var statusMatch = Regex.Match(responseBody, @"""status""\s*:\s*""([^""]+)""");
                            if (statusMatch.Success) status = statusMatch.Groups[1].Value;

                            string msg = reqNo > 0
                                ? (status == "already_received" ? "Already received by ITMS as Request #" + reqNo : "Queued in ITMS as Request #" + reqNo)
                                : "Successfully received by ITMS.";

                            return new ItmsSubmitResult
                            {
                                Success = true,
                                RequestNo = reqNo,
                                RequestId = requestId,
                                DeviceId = devId,
                                Status = status,
                                Message = msg
                            };
                        }

                        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            return new ItmsSubmitResult
                            {
                                Success = false,
                                RequestId = requestId,
                                Message = "ITMS authentication failed. Verify the API key in Settings."
                            };
                        }

                        if ((int)response.StatusCode == 422)
                        {
                            var errMsgMatch = Regex.Match(responseBody, @"""error""\s*:\s*""([^""]+)""");
                            string errDetail = errMsgMatch.Success ? errMsgMatch.Groups[1].Value : "Validation rejected by ITMS.";
                            return new ItmsSubmitResult
                            {
                                Success = false,
                                RequestId = requestId,
                                Message = "ITMS validation failed: " + errDetail
                            };
                        }

                        return new ItmsSubmitResult
                        {
                            Success = false,
                            RequestId = requestId,
                            Message = "ITMS responded with HTTP " + (int)response.StatusCode + " (" + response.ReasonPhrase + ")."
                        };
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return new ItmsSubmitResult
                {
                    Success = false,
                    RequestId = requestId,
                    Message = "Connection timed out connecting to ITMS (" + baseUrl + "). Check your network and try again."
                };
            }
            catch (HttpRequestException ex)
            {
                return new ItmsSubmitResult
                {
                    Success = false,
                    RequestId = requestId,
                    Message = "Network error connecting to ITMS: " + ex.Message
                };
            }
            catch (Exception ex)
            {
                return new ItmsSubmitResult
                {
                    Success = false,
                    RequestId = requestId,
                    Message = "Unexpected submission error: " + ex.Message
                };
            }
        }
    }

    /// <summary>
    /// Future synchronisation of the laptop's local QC history to ITMS.
    /// </summary>
    public interface ISyncService
    {
        bool IsEnabled { get; }
        Task<Storage.SyncRecord> SyncInspectionAsync(Storage.DeviceRecord device, Storage.InspectionView inspection, Storage.SyncRecord current);
    }

    public sealed class LocalOnlySyncService : ISyncService
    {
        public bool IsEnabled => false;

        public Task<Storage.SyncRecord> SyncInspectionAsync(Storage.DeviceRecord device, Storage.InspectionView inspection, Storage.SyncRecord current) =>
            Task.FromResult(current);
    }

    public static class ItmsPayload
    {
        /// <summary>The JSON body agreed for ITMS (the eight primary fields).</summary>
        public static string Build(DetectionResult detection) => JsonWriter.Serialize(DetectionExporter.ToItmsObject(detection));

        /// <summary>Builds the full inspection payload complying with ITMS contract version 1.0.</summary>
        public static string BuildInspectionPayload(DetectionResult detection, string requestId, string testedBy = null, string overallResult = "DETECTED")
        {
            var d = detection ?? new DetectionResult();
            var deviceObj = DetectionExporter.ToItmsObject(d);
            deviceObj.Add("serial_number", string.IsNullOrWhiteSpace(d.System?.SerialNumber) ? DetectionResult.Unknown : d.System.SerialNumber);
            deviceObj.Add("system_uuid", d.System?.SystemUuid ?? "");
            deviceObj.Add("board_serial", d.System?.BoardSerial ?? "");

            var root = new JsonObject
            {
                { "schema_version", "1.0" },
                { "request_id", !string.IsNullOrEmpty(requestId) ? requestId : d.SubmissionId },
                { "source", "bbtech-laptop-tool" },
                { "tool_version", d.ToolVersion ?? "1.0.0" },
                { "inspection_date", d.DetectedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "inspection_time", d.DetectedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) },
                { "overall_result", overallResult ?? "DETECTED" },
            };

            if (!string.IsNullOrEmpty(testedBy)) root.Add("tested_by", testedBy);

            root.Add("device", deviceObj);

            root.Add("details", new JsonObject
            {
                { "os", d.System?.OperatingSystem ?? "" },
                { "bios_version", d.System?.BiosVersion ?? "" },
                { "product_code", d.System?.ProductCode ?? "" },
                { "warnings", d.Warnings ?? new List<string>() }
            });

            return JsonWriter.Serialize(root);
        }
    }
}
