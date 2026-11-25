using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BBTech.Core.Audit;
using BBTech.Core.Settings;
using BBTech.Core.Software;
using BBTech.Core.Sync;
using BBTech.Core.Testing;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// Server-controlled master data (operators, approved packages, test requirements, laptop models).
    /// Online: fetched from the BBTech server and cached encrypted on the USB. Offline: the cache is used.
    /// Never configured: built-in defaults. The technician never sees any of this.
    /// </summary>
    public static class MasterDataService
    {
        private static readonly object Gate = new object();
        private static MasterConfigResponse _current;
        private static bool _loaded;

        public static string CachePath => Path.Combine(AppPaths.DataDir, "config", "master.dat");

        public static MasterConfigResponse Current
        {
            get
            {
                lock (Gate)
                {
                    if (!_loaded) { _current = MasterConfigCache.Load(CachePath); _loaded = true; }
                    return _current;
                }
            }
        }

        /// <summary>Test requirements QC uses (server → cache → built-in default).</summary>
        public static QcRequirements Requirements
        {
            get
            {
                var r = Current?.Requirements;
                return r != null && r.IsValid ? r : QcRequirements.Default();
            }
        }

        /// <summary>Approved packages only (invalid definitions are dropped; Office default always present).</summary>
        public static List<SoftwarePackage> Packages => SoftwareCatalog.Effective(Current?.Packages);

        public static SoftwarePackage OfficePackage => Packages.First(p => p.DetectRule == "office");

        /// <summary>Where the configuration in use came from (for the admin Settings page).</summary>
        public static string SourceDescription =>
            Current == null ? "Built-in defaults (no server configuration received yet)"
                            : "Server configuration version " + Current.ConfigVersion + " (" + Current.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + ")";

        /// <summary>Fetches and applies the server configuration. Returns a message for logs/admins; never throws.</summary>
        public static async Task<string> RefreshAsync(OperatorRecord actor)
        {
            try
            {
                var api = UsbSyncService.CreateClient();
                if (!api.IsConfigured) return "BBTech server not configured; using " + SourceDescription + ".";
                var cfg = await api.FetchMasterConfigAsync().ConfigureAwait(false);
                if (cfg == null) return "Server unavailable; using " + SourceDescription + ".";

                string why = MasterConfigCache.TrySave(CachePath, cfg);
                if (why != null) { AppLog.Warn("Master configuration not applied: " + why); return "Configuration not applied: " + why; }
                lock (Gate) { _current = cfg; _loaded = true; }

                int changed = SettingsService.Store.ApplyServerOperators(cfg.Operators);
                new AuditStore(AppPaths.AuditLogPath).Log(actor, AuditActions.ConfigChanged, "MasterConfig", null, cfg.ConfigVersion.ToString(),
                    string.Format("Server configuration v{0} applied ({1} operator change(s), {2} package(s))", cfg.ConfigVersion, changed, cfg.Packages?.Count ?? 0));
                return "Server configuration version " + cfg.ConfigVersion + " applied.";
            }
            catch (Exception ex)
            {
                AppLog.Warn("Master configuration refresh failed", ex);
                return "Configuration refresh failed: " + ex.Message;
            }
        }
    }
}
