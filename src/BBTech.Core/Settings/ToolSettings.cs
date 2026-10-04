using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using BBTech.Core.Security;
using BBTech.Core.Storage;

namespace BBTech.Core.Settings
{
    [DataContract]
    public sealed class OperatorRecord
    {
        [DataMember] public string OperatorId { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Role { get; set; }
        [DataMember] public bool Active { get; set; }
        [DataMember] public DateTime CreatedAt { get; set; }
        [DataMember] public DateTime UpdatedAt { get; set; }
    }

    public enum WifiSecurity { Wpa2Personal, Wpa3Personal, Open }

    [DataContract]
    public sealed class WifiNetwork
    {
        [DataMember] public string NetworkId { get; set; }
        [DataMember] public string Ssid { get; set; }
        [DataMember] public WifiSecurity Security { get; set; }
        [DataMember] public string Password { get; set; }
        [DataMember] public bool Hidden { get; set; }
        /// <summary>Incremented on every edit, so laptops re-provision changed networks.</summary>
        [DataMember] public int Version { get; set; }
        [DataMember] public DateTime UpdatedAt { get; set; }
    }

    /// <summary>Everything an admin manages. Lives encrypted on the USB (see SettingsStore).</summary>
    [DataContract]
    public sealed class ToolSettings
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public List<OperatorRecord> Operators { get; set; } = new List<OperatorRecord>();
        /// <summary>The operator selected for the current QC session (kept until changed).</summary>
        [DataMember] public string CurrentOperatorId { get; set; }
        [DataMember] public string AdminPinSalt { get; set; }
        [DataMember] public string AdminPinHash { get; set; }
        [DataMember] public int AdminPinIterations { get; set; }
        [DataMember] public List<WifiNetwork> WifiNetworks { get; set; } = new List<WifiNetwork>();
    }

    /// <summary>
    /// Encrypted settings file on the BBTech USB (BBTech Data\config\settings.dat): operators, the
    /// current operator, the admin PIN hash and the managed Wi-Fi networks (with passwords).
    ///
    /// Protection level (accepted by BBTech for one-click Wi-Fi provisioning): AES-256 + HMAC with a key
    /// derived from the application constant and a random per-file salt. Nothing is readable or editable
    /// with normal tools, and any hand-edit (e.g. removing the PIN) breaks authentication and is rejected.
    /// A determined attacker with the EXE and the USB could recover the contents.
    /// </summary>
    public sealed class SettingsStore
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BBTS0001");
        private const int KdfIterations = 20000;
        private readonly Func<DateTime> _clock;

        public string Path { get; }
        public ToolSettings Settings { get; private set; } = new ToolSettings();
        /// <summary>True if the file existed but could not be read (damaged/tampered); it is not overwritten.</summary>
        public bool LoadFailed { get; private set; }

        private SettingsStore(string path, Func<DateTime> clock) { Path = path; _clock = clock ?? (() => DateTime.Now); }

        public static SettingsStore Load(string path, Func<DateTime> clock = null)
        {
            var s = new SettingsStore(path, clock);
            var loaded = TryRead(path) ?? TryRead(path + ".bak");
            if (loaded != null) s.Settings = loaded;
            else if (File.Exists(path)) s.LoadFailed = true;
            return s;
        }

        public void Save()
        {
            if (LoadFailed) throw new InvalidOperationException("The settings file is damaged and was not overwritten. Restore it from another BBTech USB.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            byte[] salt = EnvelopeCrypto.RandomBytes(16);
            byte[] body = new EnvelopeCrypto(Key(salt)).Encrypt(Json.ToBytes(Settings));
            var file = new byte[Magic.Length + 16 + body.Length];
            Buffer.BlockCopy(Magic, 0, file, 0, Magic.Length);
            Buffer.BlockCopy(salt, 0, file, Magic.Length, 16);
            Buffer.BlockCopy(body, 0, file, Magic.Length + 16, body.Length);
            SafeFile.WriteAllBytesAtomic(Path, file);
        }

        private static ToolSettings TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < Magic.Length + 16 || !Magic.SequenceEqual(data.Take(Magic.Length))) return null;
                byte[] salt = data.Skip(Magic.Length).Take(16).ToArray();
                byte[] plain = new EnvelopeCrypto(Key(salt)).TryDecrypt(data.Skip(Magic.Length + 16).ToArray());
                return plain == null ? null : Json.FromBytes<ToolSettings>(plain);
            }
            catch { return null; }
        }

        private static byte[] Key(byte[] salt) => EnvelopeCrypto.DeriveKey(AppSecret.Value("usb-settings"), salt, KdfIterations);

        // ---------------- Admin PIN ----------------

        public bool HasPin => !string.IsNullOrEmpty(Settings.AdminPinHash);

        public void SetPin(string pin)
        {
            if (!PinHasher.IsValidPin(pin)) throw new ArgumentException("The PIN must be 4 to 12 digits.");
            Settings.AdminPinSalt = PinHasher.NewSalt();
            Settings.AdminPinIterations = PinHasher.Iterations;
            Settings.AdminPinHash = PinHasher.Hash(pin, Settings.AdminPinSalt, Settings.AdminPinIterations);
            Save();
        }

        public bool VerifyPin(string pin) =>
            HasPin && PinHasher.Verify(pin, Settings.AdminPinSalt, Settings.AdminPinHash, Settings.AdminPinIterations > 0 ? Settings.AdminPinIterations : PinHasher.Iterations);

        // ---------------- Operators ----------------

        public IEnumerable<OperatorRecord> ActiveOperators => Settings.Operators.Where(o => o.Active).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase);

        public OperatorRecord FindOperator(string id) => Settings.Operators.FirstOrDefault(o => o.OperatorId == id);

        /// <summary>The selected operator, if still active.</summary>
        public OperatorRecord CurrentOperator
        {
            get
            {
                var o = FindOperator(Settings.CurrentOperatorId);
                return o != null && o.Active ? o : null;
            }
        }

        public OperatorRecord AddOperator(string name, string role)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("Enter a name.");
            EnsureUniqueActiveName(name, null);
            var now = _clock();
            var op = new OperatorRecord { OperatorId = Guid.NewGuid().ToString("N"), Name = name, Role = string.IsNullOrWhiteSpace(role) ? "QC" : role.Trim(), Active = true, CreatedAt = now, UpdatedAt = now };
            Settings.Operators.Add(op);
            Save();
            return op;
        }

        /// <summary>Renaming never changes history: inspections keep their operator name snapshot.</summary>
        public void UpdateOperator(string id, string name, string role)
        {
            var op = FindOperator(id) ?? throw new ArgumentException("Unknown operator.");
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("Enter a name.");
            if (op.Active) EnsureUniqueActiveName(name, op.OperatorId);
            op.Name = name;
            op.Role = string.IsNullOrWhiteSpace(role) ? op.Role : role.Trim();
            op.UpdatedAt = _clock();
            Save();
        }

        private void EnsureUniqueActiveName(string name, string exceptId)
        {
            if (Settings.Operators.Any(o => o.Active && o.OperatorId != exceptId && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("An active operator with this name already exists.");
        }

        /// <summary>Deactivated operators are hidden for new inspections but kept for history.</summary>
        public void SetOperatorActive(string id, bool active)
        {
            var op = FindOperator(id) ?? throw new ArgumentException("Unknown operator.");
            op.Active = active;
            op.UpdatedAt = _clock();
            if (!active && Settings.CurrentOperatorId == id) Settings.CurrentOperatorId = null;
            Save();
        }

        /// <summary>Selects the operator for the session; stays selected (also across laptops) until changed.</summary>
        public void SelectOperator(string id)
        {
            var op = FindOperator(id);
            if (op == null || !op.Active) throw new ArgumentException("Choose an active operator.");
            Settings.CurrentOperatorId = id;
            Save();
        }

        // ---------------- Managed Wi-Fi networks ----------------

        public IEnumerable<WifiNetwork> WifiNetworks => Settings.WifiNetworks.OrderBy(n => n.Ssid, StringComparer.OrdinalIgnoreCase);

        public WifiNetwork AddOrUpdateWifi(string networkId, string ssid, WifiSecurity security, string password, bool hidden)
        {
            string err = Wifi.WlanProfileXml.Validate(ssid, security, password);
            if (err != null) throw new ArgumentException(err);
            var existing = networkId == null ? null : Settings.WifiNetworks.FirstOrDefault(n => n.NetworkId == networkId);
            if (Settings.WifiNetworks.Any(n => n != existing && n.Ssid == ssid)) throw new ArgumentException("This network is already in the list.");
            if (existing == null)
            {
                existing = new WifiNetwork { NetworkId = Guid.NewGuid().ToString("N"), Version = 0 };
                Settings.WifiNetworks.Add(existing);
            }
            existing.Ssid = ssid;
            existing.Security = security;
            existing.Password = security == WifiSecurity.Open ? "" : password;
            existing.Hidden = hidden;
            existing.Version++;
            existing.UpdatedAt = _clock();
            Save();
            return existing;
        }

        /// <summary>Removes a network from the managed list only. Profiles already on laptops stay (by design).</summary>
        public void RemoveWifi(string networkId)
        {
            Settings.WifiNetworks.RemoveAll(n => n.NetworkId == networkId);
            Save();
        }
    }
}
