using System.Collections.Generic;
using System.Threading.Tasks;
using BBTech.Core.Settings;

namespace BBTech.Core.Sync
{
    /// <summary>
    /// Contract for the authoritative central BBTech HTTPS API (backed by PostgreSQL).
    /// The client application never connects directly to PostgreSQL.
    /// </summary>
    public interface IBBTechApiClient
    {
        string BaseUrl { get; }
        /// <summary>False when no usable server URL is set; nothing is sent.</summary>
        bool IsConfigured { get; }
        Task<bool> CheckConnectivityAsync();
        Task<SyncUploadResult> UploadRecordsAsync(SyncUploadBatch batch);
        /// <summary>Null when the server cannot be reached or answers with something unusable.</summary>
        Task<MasterConfigResponse> FetchMasterConfigAsync();
        /// <summary>Null when unknown/unreachable.</summary>
        Task<DeviceStatusResponse> GetDeviceStatusAsync(string deviceId);
        /// <summary>Every stored record of the laptop (to restore a wiped local history). Null when unknown/unreachable.</summary>
        Task<List<DeviceHistoryRecord>> GetDeviceHistoryAsync(string deviceId);
    }
}
