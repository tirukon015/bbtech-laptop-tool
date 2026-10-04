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
    }

    /// <summary>
    /// Extension point for the future "DETECT -> Send to ITMS -> ITMS New Device" flow.
    /// The ITMS API is not defined yet, so the app ships with <see cref="NotConfiguredItmsClient"/>.
    /// A real implementation only needs to map <see cref="ItmsPayload.Build"/> to the API call.
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
            Task.FromResult(new ItmsSubmitResult { Success = false, Message = "ITMS integration is not configured yet." });
    }

    /// <summary>
    /// Future synchronisation of the laptop's local QC history to ITMS.
    /// Flow: local device + completed inspections (SyncRecord = LOCAL_ONLY) → SYNC_PENDING → SYNCING →
    /// SYNCED / SYNC_FAILED, with the ITMS device id stored in SyncRecord.RemoteId.
    /// Not implemented yet: the app ships with <see cref="LocalOnlySyncService"/>, which never touches the network.
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
            Task.FromResult(current); // stays LOCAL_ONLY
    }

    public static class ItmsPayload
    {
        /// <summary>The JSON body agreed for ITMS (the eight primary fields).</summary>
        public static string Build(DetectionResult detection) => JsonWriter.Serialize(DetectionExporter.ToItmsObject(detection));
    }
}
