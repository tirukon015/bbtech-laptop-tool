using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using BBTech.Core.Identity;
using BBTech.Core.Security;
using BBTech.Core.Settings;
using BBTech.Core.Storage;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// The laptop's own encrypted QC history in C:\ProgramData\BBTech\LaptopTool\ (local to the laptop,
    /// independent of any USB, survives restarts and USB removal).
    ///
    /// Protection: the data is encrypted (journal + dual-locked key, see KeyVault); in addition the folder
    /// is hidden and its permissions are restricted to SYSTEM, Administrators and signed-in users (the
    /// app runs as a normal user and must be able to write). The Hidden attribute is cosmetic only.
    /// Nothing in this folder is ever deleted by the application.
    /// </summary>
    public static class QcStoreService
    {
        private static readonly object Sync = new object();
        private static QcOpenResult _open;
        private static string _openedFor;

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BBTech", "LaptopTool");

        /// <summary>Opens the store for this laptop (background thread). Cached per identity.</summary>
        /// <param name="readOnly">DETECT lookups: do not create a store on a laptop that was never tested.</param>
        public static Task<QcOpenResult> OpenAsync(DeviceIdentity identity, bool readOnly = false) => Task.Run(() => Open(identity, readOnly));

        public static QcOpenResult Open(DeviceIdentity identity, bool readOnly = false)
        {
            lock (Sync)
            {
                string key = identity?.Fingerprint ?? "none";
                if (_open != null && _open.Ok && _openedFor == key) return _open;
                try
                {
                    if (readOnly && !Directory.Exists(Folder))
                        return new QcOpenResult { NoStore = true, Message = "No QC inspection found for this device." };
                    if (!readOnly) EnsureProtectedFolder();
                    var result = QcRepository.Open(Folder, new DpapiMachineKeyProtector(), identity, null, createIfMissing: !readOnly);
                    if (result.NoStore) return result;
                    _open = result;
                    _openedFor = key;
                    AppLog.Info("QC store opened: " + _open.KeyState + (_open.Ok ? "" : " (not readable)") +
                                (_open.Stats != null && (_open.Stats.Corrupted > 0 || _open.Stats.TruncatedTailBytes > 0)
                                    ? " | repaired: corrupted=" + _open.Stats.Corrupted + ", truncatedTail=" + _open.Stats.TruncatedTailBytes : ""));
                }
                catch (UnauthorizedAccessException ex)
                {
                    AppLog.Error("QC store access denied", ex);
                    _open = new QcOpenResult { Message = "QC history storage on this laptop is not accessible (permission denied)." };
                }
                catch (Exception ex)
                {
                    AppLog.Error("QC store open failed", ex);
                    _open = new QcOpenResult { Message = "QC history storage on this laptop could not be opened." };
                }
                return _open;
            }
        }

        public static QcRepository Current => _open != null && _open.Ok ? _open.Repository : null;

        private static void EnsureProtectedFolder()
        {
            string root = Path.GetDirectoryName(Folder); // ...\BBTech
            bool created = !Directory.Exists(Folder);
            Directory.CreateDirectory(Folder);
            if (!created) return;
            try
            {
                foreach (var dir in new[] { root, Folder })
                {
                    var sec = new DirectorySecurity();
                    sec.SetAccessRuleProtection(true, false); // no inherited "Users: read/create" from ProgramData
                    var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                    sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                    sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                    sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
                    Directory.SetAccessControl(dir, sec);
                }
                new DirectoryInfo(Folder).Attributes |= FileAttributes.Hidden;
                new DirectoryInfo(root).Attributes |= FileAttributes.Hidden;
            }
            catch (Exception ex)
            {
                // Encryption still protects the data; permissions are an extra layer.
                AppLog.Warn("Could not restrict QC folder permissions", ex);
            }
        }
    }

    /// <summary>The encrypted admin settings on the USB (operators, current operator, PIN, Wi-Fi networks).</summary>
    public static class SettingsService
    {
        private static SettingsStore _store;
        private static readonly object Sync = new object();

        public static string FilePath => Path.Combine(AppPaths.DataDir, "config", "settings.dat");

        public static SettingsStore Store
        {
            get
            {
                lock (Sync)
                {
                    if (_store == null)
                    {
                        _store = SettingsStore.Load(FilePath);
                        if (_store.LoadFailed) AppLog.Warn("USB settings file is damaged or was modified; it was not used.");
                    }
                    return _store;
                }
            }
        }

        public static void Reload() { lock (Sync) _store = null; }
    }
}
