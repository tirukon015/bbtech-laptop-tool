using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BBTech.Core.Security;
using BBTech.Core.Settings;
using BBTech.Core.Storage;

namespace BBTech.Core.Wifi
{
    /// <summary>Thin abstraction over the Windows Native Wifi API (wlanapi.dll), so provisioning logic is testable.</summary>
    public interface IWlanApi
    {
        /// <summary>The first wireless interface, or null if none.</summary>
        WlanInterface GetInterface();
        /// <summary>Installed profile XML by name, or null if not installed.</summary>
        string GetProfileXml(Guid iface, string profileName);
        /// <summary>Installs/overwrites a profile. Returns a Win32 error code (0 = success) and a reason text.</summary>
        int SetProfile(Guid iface, string profileXml, bool allUsers, out string reason);
        /// <summary>Starts a connection using a profile. Returns a Win32 error code (0 = request accepted).</summary>
        int Connect(Guid iface, string profileName);
        /// <summary>SSIDs currently visible, or null if a scan is not possible.</summary>
        HashSet<string> VisibleSsids(Guid iface);
        /// <summary>Profile name of the current connection, or null.</summary>
        string ConnectedProfile(Guid iface);
    }

    public sealed class WlanInterface
    {
        public Guid Id { get; set; }
        public string Description { get; set; } = "";
        /// <summary>False when Wi-Fi is switched off (software or hardware switch / airplane mode).</summary>
        public bool RadioOn { get; set; } = true;
    }

    public enum ProvisionOutcome { Installed, Updated, AlreadyInstalled, Failed }

    public sealed class ProvisionResult
    {
        public string Ssid { get; set; }
        public ProvisionOutcome Outcome { get; set; }
        /// <summary>True when installed for all users; false when Windows only allowed the current user.</summary>
        public bool AllUsers { get; set; }
        public bool InRange { get; set; }
        /// <summary>True only when the profile read back from Windows has connectionMode = auto.</summary>
        public bool AutoConnectVerified { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Installs the managed BBTech networks as native Windows Wi-Fi profiles with automatic connection.
    ///  • Already installed and unchanged → left untouched ("Already installed").
    ///  • Changed by an admin (new password/security) → updated.
    ///  • Out of range → still installed; Windows connects automatically when the network appears.
    ///  • All-user profile first; if Windows policy forbids it, falls back to the current user.
    /// Passwords are never logged, returned in messages, or persisted outside the encrypted settings.
    /// Profiles are never removed by this tool.
    /// </summary>
    public sealed class WifiProvisioner
    {
        public const int ErrorAccessDenied = 5;
        public const int ErrorBadProfile = 1206;
        public const int ErrorNotFound = 1168;

        private readonly IWlanApi _api;
        private readonly Func<string, WifiProvisionRecord> _getState;
        private readonly Action<WifiProvisionRecord> _setState;

        /// <param name="getState">Reads this laptop's provisioning record (from the encrypted local store); may return null.</param>
        /// <param name="setState">Saves this laptop's provisioning record; may be a no-op if the store is unavailable.</param>
        public WifiProvisioner(IWlanApi api, Func<string, WifiProvisionRecord> getState, Action<WifiProvisionRecord> setState)
        {
            _api = api;
            _getState = getState ?? (_ => null);
            _setState = setState ?? (_ => { });
        }

        /// <summary>Keyed fingerprint of a network's settings (incl. password) – detects edits without storing the password.</summary>
        public static string Fingerprint(WifiNetwork n)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(AppSecret.Value("wifi-fingerprint"))))
                return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(
                    string.Join("\u001f", n.Ssid, n.Security.ToString(), n.Password ?? "", n.Hidden ? "1" : "0"))));
        }

        /// <summary>Checks the adapter before provisioning. Returns null if usable, otherwise a friendly message.</summary>
        public string CheckAdapter(out WlanInterface iface)
        {
            iface = null;
            try { iface = _api.GetInterface(); }
            catch (DllNotFoundException) { return "Wi-Fi is not available on this laptop."; }
            if (iface == null) return "Wi-Fi is not available on this laptop (no Wi-Fi adapter found, or it is disabled).";
            return null;
        }

        public List<ProvisionResult> ProvisionAll(IEnumerable<WifiNetwork> networks)
        {
            var results = new List<ProvisionResult>();
            var list = (networks ?? Enumerable.Empty<WifiNetwork>()).ToList();
            string adapterError = CheckAdapter(out var iface);
            if (adapterError != null)
            {
                results.AddRange(list.Select(n => new ProvisionResult { Ssid = n.Ssid, Outcome = ProvisionOutcome.Failed, Message = adapterError }));
                return results;
            }

            HashSet<string> visible = null;
            try { visible = iface.RadioOn ? _api.VisibleSsids(iface.Id) : null; } catch { }

            foreach (var n in list)
                results.Add(ProvisionOne(iface, n, visible));
            return results;
        }

        private ProvisionResult ProvisionOne(WlanInterface iface, WifiNetwork n, HashSet<string> visible)
        {
            var r = new ProvisionResult { Ssid = n.Ssid, InRange = visible != null && visible.Contains(n.Ssid) };
            string xml;
            try { xml = WlanProfileXml.Build(n); }
            catch (ArgumentException ex) { r.Outcome = ProvisionOutcome.Failed; r.Message = "Invalid network settings: " + ex.Message; return r; }

            string fp = Fingerprint(n);
            var installed = WlanProfileXml.Parse(SafeGetProfile(iface.Id, n.Ssid));
            var state = _getState(n.Ssid);
            bool profileOk = installed != null && installed.Ssid == n.Ssid &&
                             installed.Authentication.Equals(WlanProfileXml.AuthName(n.Security), StringComparison.OrdinalIgnoreCase) &&
                             installed.ConnectionMode.Equals("auto", StringComparison.OrdinalIgnoreCase);

            if (profileOk && state != null && state.SettingsFingerprint == fp)
            {
                r.Outcome = ProvisionOutcome.AlreadyInstalled;
                r.AllUsers = state.AllUsers;
                r.AutoConnectVerified = true; // read from the Windows profile itself
                r.Message = "Already installed – Windows connects automatically when in range.";
                return r;
            }

            // A working profile set up outside the tool: the laptop is connected through an equivalent
            // auto-connect profile right now. Keep it instead of overwriting a proven key.
            if (profileOk && state == null && SafeConnected(iface.Id) == n.Ssid)
            {
                _setState(new WifiProvisionRecord { Ssid = n.Ssid, SettingsFingerprint = fp, AllUsers = true, ProvisionedAt = DateTime.Now });
                r.Outcome = ProvisionOutcome.AlreadyInstalled;
                r.AllUsers = true;
                r.AutoConnectVerified = true;
                r.InRange = true;
                r.Message = "Already set up on this laptop and connected – Windows connects automatically (kept the existing profile).";
                return r;
            }

            int rc = _api.SetProfile(iface.Id, xml, true, out string reason);
            bool allUsers = true;
            if (rc == ErrorAccessDenied)
            {
                rc = _api.SetProfile(iface.Id, xml, false, out reason); // policy forbids all-user profiles
                allUsers = false;
            }

            if (rc != 0)
            {
                r.Outcome = ProvisionOutcome.Failed;
                r.Message = rc == ErrorBadProfile ? "Windows rejected the network settings (" + (string.IsNullOrEmpty(reason) ? "invalid profile" : reason) + ")."
                          : rc == ErrorAccessDenied ? "Windows did not allow installing this Wi-Fi profile (permission denied)."
                          : "The Wi-Fi profile could not be installed (error " + rc + (string.IsNullOrEmpty(reason) ? "" : ", " + reason) + ").";
                return r;
            }

            // Verify what Windows actually stored (not just what we sent): automatic connection must be on.
            var stored = WlanProfileXml.Parse(SafeGetProfile(iface.Id, n.Ssid));
            if (stored == null || stored.Ssid != n.Ssid ||
                !stored.ConnectionMode.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                !stored.Authentication.Equals(WlanProfileXml.AuthName(n.Security), StringComparison.OrdinalIgnoreCase))
            {
                r.Outcome = ProvisionOutcome.Failed;
                r.Message = stored == null
                    ? "Windows did not keep the Wi-Fi profile."
                    : "Windows stored the profile without automatic connection (" + (stored.ConnectionMode.Length > 0 ? stored.ConnectionMode : "unknown") + ").";
                return r;
            }

            _setState(new WifiProvisionRecord { Ssid = n.Ssid, SettingsFingerprint = fp, AllUsers = allUsers, ProvisionedAt = DateTime.Now });
            r.Outcome = installed == null ? ProvisionOutcome.Installed : ProvisionOutcome.Updated;
            r.AllUsers = allUsers;
            r.AutoConnectVerified = true;
            r.Message = (r.Outcome == ProvisionOutcome.Installed ? "Installed" : "Updated") +
                        (allUsers ? " for all users" : " for the current Windows user only") +
                        " · verified in Windows: Connect automatically" +
                        (r.InRange ? " – in range." : " – will connect automatically when in range.");
            return r;
        }

        /// <summary>Connect now to a managed network (installs it first if needed).</summary>
        public string ConnectNow(WifiNetwork n)
        {
            string adapterError = CheckAdapter(out var iface);
            if (adapterError != null) return adapterError;
            if (!iface.RadioOn) return "Wi-Fi is switched off. Turn on Wi-Fi (or turn off airplane mode) and try again.";

            var p = ProvisionOne(iface, n, null);
            if (p.Outcome == ProvisionOutcome.Failed) return p.Message;

            HashSet<string> visible = null;
            try { visible = _api.VisibleSsids(iface.Id); } catch { }
            if (visible != null && !n.Hidden && !visible.Contains(n.Ssid))
                return n.Ssid + " is not in range right now. The profile is installed and Windows will connect automatically when it is available.";

            int rc = _api.Connect(iface.Id, n.Ssid);
            return rc == 0 ? null : "Could not connect to " + n.Ssid + " (error " + rc + "). The profile stays installed for automatic connection.";
        }

        private string SafeGetProfile(Guid iface, string name)
        {
            try { return _api.GetProfileXml(iface, name); } catch { return null; }
        }

        private string SafeConnected(Guid iface)
        {
            try { return _api.ConnectedProfile(iface); } catch { return null; }
        }

        /// <summary>Reads the Windows profile and reports whether automatic connection is on (null = not installed).</summary>
        public bool? IsAutoConnect(string ssid)
        {
            if (CheckAdapter(out var iface) != null) return null;
            var p = WlanProfileXml.Parse(SafeGetProfile(iface.Id, ssid));
            if (p == null) return null;
            return p.ConnectionMode.Equals("auto", StringComparison.OrdinalIgnoreCase);
        }
    }
}
