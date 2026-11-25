using System;
using System.Threading.Tasks;
using BBTech.Core.Audit;
using BBTech.Core.Settings;
using BBTech.Core.Sync;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// Background USB → BBTech server sync, invisible to the technician. The safety rules live in
    /// <see cref="SyncCoordinator"/> (Core, unit-tested); this class only wires the app's paths and settings.
    /// </summary>
    public static class UsbSyncService
    {
        public static IBBTechApiClient CreateClient()
        {
            var s = SettingsService.Store;
            return new BBTechApiClient(s.EffectiveBbtechApiUrl, s.EffectiveBbtechApiKey);
        }

        /// <summary>One sync pass now, with the outcome (for the admin "Sync now" button).</summary>
        public static async Task<SyncRunOutcome> RunNowAsync(OperatorRecord actor)
        {
            try
            {
                var coordinator = new SyncCoordinator(CreateClient(), new UsbSyncQueue(AppPaths.SyncQueuePath), new AuditStore(AppPaths.AuditLogPath));
                return await coordinator.RunOnceAsync(actor).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Sync pass failed; USB records kept", ex);
                return new SyncRunOutcome { Message = "Synchronization failed: " + ex.Message + ". The records stay on the USB." };
            }
        }

        /// <summary>Starts one sync pass in the background; never throws, never blocks the UI.</summary>
        public static void TriggerInBackground(OperatorRecord actor)
        {
            Task.Run(async () =>
            {
                try
                {
                    var coordinator = new SyncCoordinator(CreateClient(), new UsbSyncQueue(AppPaths.SyncQueuePath), new AuditStore(AppPaths.AuditLogPath));
                    var outcome = await coordinator.RunOnceAsync(actor).ConfigureAwait(false);
                    if (outcome.Recovered > 0) AppLog.Info("Sync: " + outcome.Recovered + " interrupted record(s) queued again.");
                    if (outcome.Attempted) AppLog.Info("Sync: " + outcome.Message);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Sync pass failed; USB records kept", ex);
                }
            });
        }
    }
}
