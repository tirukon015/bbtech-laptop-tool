using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using BBTech.Core.Storage;

namespace BBTech.Core.Sync
{
    /// <summary>
    /// HTTPS client for the central BBTech API (server/BBTech.Api; PostgreSQL behind it, never reached
    /// directly). Each workstation authenticates with its own API key (X-BBTech-Api-Key); the server decides
    /// what the workstation and each operator may do. Nothing here invents a successful answer.
    /// </summary>
    public sealed class BBTechApiClient : IBBTechApiClient
    {
        public const string ApiKeyHeader = "X-BBTech-Api-Key";
        public const string IdempotencyHeader = "X-Idempotency-Key";

        private readonly HttpClient _http;
        private readonly string _apiKey;

        public string BaseUrl { get; }

        public BBTechApiClient(string baseUrl = null, string apiKey = null, HttpClient httpClient = null)
        {
            // No default URL: an unconfigured client is disabled instead of posting to a guessed host.
            BaseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
            _apiKey = apiKey ?? "";
            _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        }

        /// <summary>True only with an absolute HTTPS URL (plain HTTP is allowed for localhost development).</summary>
        public bool IsConfigured => IsUsableUrl(BaseUrl);

        public static bool IsUsableUrl(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u)) return false;
            return u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback);
        }

        private HttpRequestMessage Request(HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, BaseUrl + path);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(_apiKey)) req.Headers.Add(ApiKeyHeader, _apiKey);
            return req;
        }

        public async Task<bool> CheckConnectivityAsync()
        {
            if (!IsConfigured) return false;
            try
            {
                using (var req = Request(HttpMethod.Get, "/health"))
                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<SyncUploadResult> UploadRecordsAsync(SyncUploadBatch batch)
        {
            if (!IsConfigured) return SyncUploadResult.Fail("The BBTech server is not configured.");
            if (batch == null || (batch.Records.Count == 0 && batch.AuditRecords.Count == 0))
                return SyncUploadResult.Fail("Nothing to upload.");

            try
            {
                using (var req = Request(HttpMethod.Post, "/v1/sync/upload"))
                {
                    req.Content = new ByteArrayContent(Json.ToBytes(batch));
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    req.Headers.Add(IdempotencyHeader, batch.BatchId);

                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            string err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (err.Length > 300) err = err.Substring(0, 300);
                            return SyncUploadResult.Fail(string.Format("Server returned {0}: {1}", (int)resp.StatusCode, err));
                        }

                        byte[] respBytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        SyncUploadResult res = null;
                        try { if (respBytes.Length > 0) res = Json.FromBytes<SyncUploadResult>(respBytes); } catch { res = null; }
                        // Never invent a receipt: without an explicit, parseable server receipt nothing is verified.
                        if (res == null) return SyncUploadResult.Fail("Server answered without a readable receipt; nothing was verified.");
                        if (res.Success && string.IsNullOrWhiteSpace(res.ServerReceiptId))
                            return SyncUploadResult.Fail("Server answered without a receipt id; nothing was verified.");
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                return SyncUploadResult.Fail("Server unavailable: " + ex.Message);
            }
        }

        public async Task<MasterConfigResponse> FetchMasterConfigAsync()
        {
            if (!IsConfigured) return null;
            try
            {
                using (var req = Request(HttpMethod.Get, "/v1/config/master"))
                using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return bytes.Length == 0 ? null : Json.FromBytes<MasterConfigResponse>(bytes);
                }
            }
            catch
            {
                return null;
            }
        }

        public async Task<System.Collections.Generic.List<DeviceHistoryRecord>> GetDeviceHistoryAsync(string deviceId)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(deviceId)) return null;
            try
            {
                using (var req = Request(HttpMethod.Get, "/v1/devices/" + Uri.EscapeDataString(deviceId) + "/history"))
                using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return bytes.Length == 0 ? null : Json.FromBytes<System.Collections.Generic.List<DeviceHistoryRecord>>(bytes);
                }
            }
            catch
            {
                return null;
            }
        }

        public async Task<DeviceStatusResponse> GetDeviceStatusAsync(string deviceId)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(deviceId)) return null;
            try
            {
                using (var req = Request(HttpMethod.Get, "/v1/devices/" + Uri.EscapeDataString(deviceId) + "/status"))
                using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return bytes.Length == 0 ? null : Json.FromBytes<DeviceStatusResponse>(bytes);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
