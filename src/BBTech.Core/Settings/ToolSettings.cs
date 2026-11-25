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
        [DataMember] public string UserId { get => OperatorId; set => OperatorId = value; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Role { get; set; }
        [DataMember] public bool Active { get; set; }
        [DataMember] public List<string> Departments { get; set; } = new List<string>();
        [DataMember] public List<string> Permissions { get; set; } = new List<string>();
        [DataMember] public DateTime CreatedAt { get; set; }
        [DataMember] public DateTime UpdatedAt { get; set; }

        public bool IsAdmin => string.Equals(Role, "Super Admin", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(Role, "Admin / Co-Founder", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(Role, "Admin", StringComparison.OrdinalIgnoreCase) ||
                               (Permissions != null && Permissions.Any(p => string.Equals(p, AppPermissions.All, StringComparison.OrdinalIgnoreCase) ||
                                                                           string.Equals(p, "admin", StringComparison.OrdinalIgnoreCase)));

        public bool CanPerform(string permission)
        {
            if (!Active) return false;
            if (IsAdmin) return true;
            if (string.IsNullOrWhiteSpace(permission)) return false;
            if (Permissions == null || Permissions.Count == 0) return false;

            if (Permissions.Any(p => string.Equals(p, AppPermissions.All, StringComparison.OrdinalIgnoreCase))) return true;

            if (Permissions.Any(p => string.Equals(p, permission, StringComparison.OrdinalIgnoreCase))) return true;

            // Backward compatibility with legacy permission names
            if (string.Equals(permission, AppPermissions.HardwareTest, StringComparison.OrdinalIgnoreCase) &&
                Permissions.Any(p => string.Equals(p, "Hardware Testing", StringComparison.OrdinalIgnoreCase))) return true;

            if (string.Equals(permission, AppPermissions.SoftwareTest, StringComparison.OrdinalIgnoreCase) &&
                Permissions.Any(p => string.Equals(p, "Software Tools", StringComparison.OrdinalIgnoreCase))) return true;

            if (string.Equals(permission, AppPermissions.QcInspect, StringComparison.OrdinalIgnoreCase) &&
                Permissions.Any(p => string.Equals(p, "QC Inspection", StringComparison.OrdinalIgnoreCase))) return true;

            if (string.Equals(permission, AppPermissions.Ecommerce, StringComparison.OrdinalIgnoreCase) &&
                Permissions.Any(p => string.Equals(p, "E-Commerce", StringComparison.OrdinalIgnoreCase))) return true;

            return false;
        }

        public bool HasPermission(string perm) => CanPerform(perm);

        public bool HasDepartment(string dept)
        {
            if (IsAdmin) return true;
            if (Departments == null || Departments.Count == 0)
            {
                if (!string.IsNullOrEmpty(Role) && Role.IndexOf(dept, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }
            return Departments.Any(d => string.Equals(d, dept, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(d, AppPermissions.All, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(d, "General", StringComparison.OrdinalIgnoreCase));
        }

        public bool CanAccessHardware => Active && (CanPerform(AppPermissions.HardwareTest) || HasDepartment("Hardware") || IsAdmin);
        public bool CanAccessQc => Active && (CanPerform(AppPermissions.QcInspect) || HasDepartment("QC") || IsAdmin);
        public bool CanAccessSoftware => Active && (CanPerform(AppPermissions.SoftwareTest) || HasDepartment("Software") || IsAdmin);
        public bool CanAccessEcommerce => Active && (CanPerform(AppPermissions.Ecommerce) || HasDepartment("E-Commerce") || IsAdmin);

        public bool CanInstallSoftware => Active && CanPerform(AppPermissions.SoftwareInstall);
        public bool CanCheckActivation => Active && CanPerform(AppPermissions.SoftwareActivationCheck);
        public bool CanExecuteActivation => Active && CanPerform(AppPermissions.SoftwareActivationExecute);
        public bool CanManageUsers => Active && CanPerform(AppPermissions.UsersManage);
        public bool CanManagePackages => Active && CanPerform(AppPermissions.SoftwarePackagesManage);
        public bool CanViewAudit => Active && CanPerform(AppPermissions.AuditView);
        public bool CanDeleteRecords => Active && CanPerform(AppPermissions.RecordsDelete);
        public bool CanManageConfiguration => Active && CanPerform(AppPermissions.ConfigurationManage);
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

    /// <summary>
    /// Persistent operator session stored in the BBTech USB and workstation context.
    /// Default duration is 1 hour. Provides convenience restoration while enforcing account validity.
    /// </summary>
    [DataContract]
    public sealed class OperatorSession
    {
        [DataMember] public string OperatorId { get; set; }
        [DataMember] public string OperatorName { get; set; }
        [DataMember] public DateTime SelectedAt { get; set; }
        [DataMember] public DateTime SessionExpiresAt { get; set; }
        /// <summary>Keyed tag over the fields above; a hand-edited or forged session file is rejected.</summary>
        [DataMember] public string Signature { get; set; }

        /// <summary>Longest session ever accepted, even if a file claims otherwise.</summary>
        public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(1);

        /// <summary>
        /// Expired when past its end, when it claims to last longer than <see cref="MaxDuration"/>,
        /// or when the clock is earlier than the selection time (clock moved back).
        /// </summary>
        public bool IsExpired(DateTime? now = null)
        {
            var current = now ?? DateTime.Now;
            if (SessionExpiresAt - SelectedAt > MaxDuration + TimeSpan.FromSeconds(1)) return true;
            if (current < SelectedAt - TimeSpan.FromMinutes(5)) return true;
            return current >= SessionExpiresAt;
        }

        private string SignedText() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}|{1}|{2}|{3}", OperatorId, OperatorName, SelectedAt.ToUniversalTime().Ticks, SessionExpiresAt.ToUniversalTime().Ticks);

        public void Sign() => Signature = RecordMac.Compute("operator-session", SignedText());

        public bool HasValidSignature() => RecordMac.Verify("operator-session", SignedText(), Signature);

        public static OperatorSession Create(OperatorRecord op, DateTime now, TimeSpan? duration = null)
        {
            if (op == null) throw new ArgumentNullException(nameof(op));
            var validDuration = duration ?? MaxDuration;
            if (validDuration > MaxDuration) validDuration = MaxDuration;
            var session = new OperatorSession
            {
                OperatorId = op.OperatorId,
                OperatorName = op.Name,
                SelectedAt = now,
                SessionExpiresAt = now.Add(validDuration)
            };
            session.Sign();
            return session;
        }
    }

    /// <summary>Everything an admin manages. Lives encrypted on the USB (see SettingsStore).</summary>
    [DataContract]
    public sealed class ToolSettings
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public List<OperatorRecord> Operators { get; set; } = new List<OperatorRecord>();
        /// <summary>The operator selected for the current QC session (kept until changed).</summary>
        [DataMember] public string CurrentOperatorId { get; set; }
        /// <summary>The active persistent operator session (1 hour duration).</summary>
        [DataMember] public OperatorSession ActiveSession { get; set; }
        /// <summary>The active department: "Hardware", "QC", "Software" (kept until changed).</summary>
        [DataMember] public string CurrentDepartment { get; set; }
        [DataMember] public string AdminPinSalt { get; set; }
        [DataMember] public string AdminPinHash { get; set; }
        [DataMember] public int AdminPinIterations { get; set; }
        [DataMember] public List<WifiNetwork> WifiNetworks { get; set; } = new List<WifiNetwork>();
        [DataMember] public string ItmsBaseUrl { get; set; }
        [DataMember] public string ItmsApiKey { get; set; }
        /// <summary>Central BBTech API (sync, master data). Separate from ITMS. Empty = sync disabled.</summary>
        [DataMember] public string BbtechApiBaseUrl { get; set; }
        [DataMember] public string BbtechApiKey { get; set; }
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

        // ---------------- Operators & Session ----------------

        private string WorkstationSessionPath
        {
            get
            {
                if (!string.IsNullOrEmpty(Path) && Path.IndexOf(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Path + ".session.json";
                }
                return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BBTech Laptop Tool", "operator_session.json");
            }
        }

        private void SaveWorkstationSession(OperatorSession session)
        {
            try
            {
                if (session == null)
                {
                    ClearWorkstationSession();
                    return;
                }
                string path = WorkstationSessionPath;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                SafeFile.WriteAllBytesAtomic(path, Json.ToBytes(session));
            }
            catch { }
        }

        private OperatorSession LoadWorkstationSession()
        {
            try
            {
                string path = WorkstationSessionPath;
                if (!File.Exists(path)) return null;
                byte[] bytes = File.ReadAllBytes(path);
                var session = Json.FromBytes<OperatorSession>(bytes);
                // A session file edited or written outside the app is ignored (never trusted).
                return session != null && session.HasValidSignature() ? session : null;
            }
            catch { return null; }
        }

        private void ClearWorkstationSession()
        {
            try
            {
                string path = WorkstationSessionPath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public IEnumerable<OperatorRecord> ActiveOperators => Settings.Operators.Where(o => o.Active).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase);

        public OperatorRecord FindOperator(string id) => Settings.Operators.FirstOrDefault(o => o.OperatorId == id);

        /// <summary>
        /// Creates or updates the active persistent operator session (default: 1 hour).
        /// Persists to both the encrypted USB settings and the local workstation context.
        /// </summary>
        public OperatorSession CreateSession(OperatorRecord op, TimeSpan? duration = null)
        {
            if (op == null || !op.Active) throw new ArgumentException("Choose an active operator.");
            var now = _clock();
            var session = OperatorSession.Create(op, now, duration ?? TimeSpan.FromHours(1));
            Settings.ActiveSession = session;
            Settings.CurrentOperatorId = op.OperatorId;
            Save();
            SaveWorkstationSession(session);
            return session;
        }

        /// <summary>
        /// Validates and returns the active operator from the persistent session (1-hour expiry).
        /// Rejects expired sessions and deactivated accounts immediately.
        /// </summary>
        public OperatorRecord GetActiveSessionOperator()
        {
            var session = Settings.ActiveSession;
            if (session == null)
            {
                var ws = LoadWorkstationSession();
                if (ws != null)
                {
                    session = ws;
                    Settings.ActiveSession = ws;
                }
            }

            // No session means no operator. (The old CurrentOperatorId-only fallback is gone: it kept an
            // operator signed in forever and bypassed the 1-hour expiry.)
            if (session == null) return null;

            if (session.IsExpired(_clock()))
            {
                ClearSession();
                return null;
            }

            var op = FindOperator(session.OperatorId);
            if (op == null || !op.Active)
            {
                // Deactivated or deleted operator account - invalidate cached session
                ClearSession();
                return null;
            }

            return op;
        }

        /// <summary>Clears the active session from USB and workstation context.</summary>
        public void ClearSession()
        {
            Settings.ActiveSession = null;
            Settings.CurrentOperatorId = null;
            ClearWorkstationSession();
            Save();
        }

        /// <summary>The selected operator from the active session, if still valid and unexpired.</summary>
        public OperatorRecord CurrentOperator => GetActiveSessionOperator();

        public OperatorRecord AddOperator(string name, string role) => AddOperator(name, role, null, null);

        public OperatorRecord AddOperator(string name, string role, IEnumerable<string> departments, IEnumerable<string> permissions)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("Enter a name.");
            EnsureUniqueActiveName(name, null);
            var now = _clock();
            var depts = departments != null ? departments.ToList() : new List<string>();
            var perms = permissions != null ? permissions.ToList() : new List<string>();

            if (depts.Count == 0 && !string.IsNullOrWhiteSpace(role))
            {
                if (role.IndexOf("Hardware", StringComparison.OrdinalIgnoreCase) >= 0) depts.Add("Hardware");
                if (role.IndexOf("QC", StringComparison.OrdinalIgnoreCase) >= 0) depts.Add("QC");
                if (role.IndexOf("Software", StringComparison.OrdinalIgnoreCase) >= 0) depts.Add("Software");
                if (role.IndexOf("E-Commerce", StringComparison.OrdinalIgnoreCase) >= 0) depts.Add("E-Commerce");
                if (role.IndexOf("Admin", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    depts.AddRange(new[] { "Hardware", "Software", "QC", "E-Commerce", "General" });
                    perms.Add("ALL");
                }
            }
            if (depts.Count == 0) depts.Add("QC");

            var op = new OperatorRecord
            {
                OperatorId = Guid.NewGuid().ToString("N"),
                Name = name,
                Role = string.IsNullOrWhiteSpace(role) ? "QC" : role.Trim(),
                Active = true,
                Departments = depts,
                Permissions = perms,
                CreatedAt = now,
                UpdatedAt = now
            };
            Settings.Operators.Add(op);
            Save();
            return op;
        }

        /// <summary>Renaming never changes history: inspections keep their operator name snapshot.</summary>
        public void UpdateOperator(string id, string name, string role) => UpdateOperator(id, name, role, null, null);

        public void UpdateOperator(string id, string name, string role, IEnumerable<string> departments, IEnumerable<string> permissions)
        {
            var op = FindOperator(id) ?? throw new ArgumentException("Unknown operator.");
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("Enter a name.");
            if (op.Active) EnsureUniqueActiveName(name, op.OperatorId);
            op.Name = name;
            op.Role = string.IsNullOrWhiteSpace(role) ? op.Role : role.Trim();
            if (departments != null) op.Departments = departments.ToList();
            if (permissions != null) op.Permissions = permissions.ToList();
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
            if (active && !op.Active) EnsureUniqueActiveName(op.Name, op.OperatorId);
            op.Active = active;
            op.UpdatedAt = _clock();
            if (!active)
            {
                if (Settings.CurrentOperatorId == id || Settings.ActiveSession?.OperatorId == id)
                {
                    ClearSession();
                }
            }
            Save();
        }

        /// <summary>Selects the operator for the session; establishes a persistent 1-hour session.</summary>
        public void SelectOperator(string id)
        {
            var op = FindOperator(id);
            if (op == null || !op.Active) throw new ArgumentException("Choose an active operator.");
            CreateSession(op, TimeSpan.FromHours(1));
        }

        /// <summary>
        /// Selects user profile by operator ID. If the profile has only one department, automatically
        /// activates that department without asking. Stays persisted for 1 hour.
        /// </summary>
        public void SelectProfile(string operatorId)
        {
            var op = FindOperator(operatorId);
            if (op == null || !op.Active) throw new ArgumentException("Choose an active user profile.");
            CreateSession(op, TimeSpan.FromHours(1));

            if (op.Departments != null && op.Departments.Count == 1)
            {
                Settings.CurrentDepartment = op.Departments[0];
            }
            else if (op.Departments != null && op.Departments.Count > 1)
            {
                if (string.IsNullOrEmpty(Settings.CurrentDepartment) || !op.HasDepartment(Settings.CurrentDepartment))
                {
                    Settings.CurrentDepartment = op.Departments[0];
                }
            }
            else if (!string.IsNullOrEmpty(op.Role))
            {
                if (op.Role.IndexOf("Hardware", StringComparison.OrdinalIgnoreCase) >= 0) Settings.CurrentDepartment = "Hardware";
                else if (op.Role.IndexOf("QC", StringComparison.OrdinalIgnoreCase) >= 0) Settings.CurrentDepartment = "QC";
                else if (op.Role.IndexOf("E-Commerce", StringComparison.OrdinalIgnoreCase) >= 0) Settings.CurrentDepartment = "E-Commerce";
            }

            Save();
        }

        /// <summary>Explicitly clears the active session operator and department (intentional sign out / station reset).</summary>
        public void ResetSession()
        {
            Settings.CurrentDepartment = null;
            ClearSession();
        }

        /// <summary>
        /// Pre-populates the standard BBTech team profiles (Kamal, Shadir, Jimmy, Riyad, Bijoy, Hamed, Touhidul, Talal)
        /// with their respective departments and permissions if they are not already present.
        /// </summary>
        public void EnsureDefaultTeamProfiles()
        {
            bool changed = false;
            var team = new[]
            {
                new { Name = "Kamal", Role = "Technician", Depts = new[] { "Hardware" }, Perms = new[] { AppPermissions.HardwareTest, "Hardware Testing" } },
                new { Name = "Shadir", Role = "Technician", Depts = new[] { "Hardware", "Software" }, Perms = new[] { AppPermissions.HardwareTest, AppPermissions.SoftwareTest, AppPermissions.SoftwareInstall, AppPermissions.SoftwareActivationCheck, "Hardware Testing", "Software Tools" } },
                new { Name = "Jimmy", Role = "QC Inspector", Depts = new[] { "QC" }, Perms = new[] { AppPermissions.QcInspect, AppPermissions.QcApprove, "QC Inspection" } },
                new { Name = "Riyad", Role = "E-Commerce Specialist", Depts = new[] { "E-Commerce" }, Perms = new[] { AppPermissions.Ecommerce, "E-Commerce" } },
                new { Name = "Bijoy", Role = "E-Commerce Specialist", Depts = new[] { "E-Commerce" }, Perms = new[] { AppPermissions.Ecommerce, "E-Commerce" } },
                new { Name = "Hamed", Role = "E-Commerce Specialist", Depts = new[] { "E-Commerce" }, Perms = new[] { AppPermissions.Ecommerce, "E-Commerce" } },
                new { Name = "Touhidul", Role = "Super Admin", Depts = new[] { "Hardware", "Software", "QC", "E-Commerce", "General" }, Perms = new[] { AppPermissions.All } },
                new { Name = "Talal", Role = "Admin / Co-Founder", Depts = new[] { "Hardware", "Software", "QC", "E-Commerce", "General" }, Perms = new[] { AppPermissions.All } },
            };

            foreach (var m in team)
            {
                var existing = Settings.Operators.FirstOrDefault(o => o.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    var now = _clock();
                    Settings.Operators.Add(new OperatorRecord
                    {
                        OperatorId = Guid.NewGuid().ToString("N"),
                        Name = m.Name,
                        Role = m.Role,
                        Active = true,
                        Departments = m.Depts.ToList(),
                        Permissions = m.Perms.ToList(),
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                    changed = true;
                }
                else
                {
                    if (existing.Departments == null || existing.Departments.Count == 0)
                    {
                        existing.Departments = m.Depts.ToList();
                        changed = true;
                    }
                    if (existing.Permissions == null || existing.Permissions.Count == 0)
                    {
                        existing.Permissions = m.Perms.ToList();
                        changed = true;
                    }
                }
            }

            if (changed) Save();
        }

        /// <summary>The active department for the session (Hardware, QC, Software).</summary>
        public string CurrentDepartment => Settings.CurrentDepartment;

        /// <summary>Selects the department for the workstation/session; stays selected until changed.</summary>
        public void SelectDepartment(string department)
        {
            Settings.CurrentDepartment = department;
            Save();
        }

        /// <summary>Sets both active department and current operator, establishing a persistent session.</summary>
        public void SelectDepartmentAndOperator(string department, string operatorId)
        {
            Settings.CurrentDepartment = department;
            var op = FindOperator(operatorId);
            if (op != null && op.Active)
            {
                CreateSession(op, TimeSpan.FromHours(1));
            }
            else
            {
                Settings.CurrentOperatorId = operatorId;
                Save();
            }
        }

        /// <summary>Finds an active operator by name or adds a new one; avoids repeated user setup friction.</summary>
        public OperatorRecord EnsureOperator(string name, string role = null)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("Enter a name.");
            var existing = Settings.Operators.FirstOrDefault(o => o.Active && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(role) && !string.Equals(existing.Role, role, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Role = role.Trim();
                    existing.UpdatedAt = _clock();
                    Save();
                }
                return existing;
            }
            return AddOperator(name, role);
        }

        // ---------------- Managed Wi-Fi networks ----------------

        public IEnumerable<WifiNetwork> WifiNetworks => Settings.WifiNetworks.OrderBy(n => n.Ssid, StringComparer.OrdinalIgnoreCase);

        public WifiNetwork AddOrUpdateWifi(string networkId, string ssid, WifiSecurity security, string password, bool hidden)
        {
            string err = Wifi.WlanProfileXml.Validate(ssid, security, password);
            if (err != null) throw new ArgumentException(err);
            var existing = networkId == null ? null : Settings.WifiNetworks.FirstOrDefault(n => n.NetworkId == networkId);
            if (Settings.WifiNetworks.Any(n => n != existing && n.Ssid == ssid)) throw new ArgumentException("This network is already in the list.");
            string newPassword = security == WifiSecurity.Open ? "" : password;
            if (existing != null && existing.Ssid == ssid && existing.Security == security && existing.Password == newPassword && existing.Hidden == hidden)
                return existing; // nothing changed: keep the version so laptops are not re-provisioned
            if (existing == null)
            {
                existing = new WifiNetwork { NetworkId = Guid.NewGuid().ToString("N"), Version = 0 };
                Settings.WifiNetworks.Add(existing);
            }
            existing.Ssid = ssid;
            existing.Security = security;
            existing.Password = newPassword;
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

        // ---------------- ITMS Integration ----------------

        public string EffectiveItmsUrl =>
            !string.IsNullOrWhiteSpace(Settings.ItmsBaseUrl)
                ? Settings.ItmsBaseUrl.TrimEnd('/')
                : (Environment.GetEnvironmentVariable("ITMS_URL") ?? "").TrimEnd('/');

        public string EffectiveItmsApiKey =>
            !string.IsNullOrWhiteSpace(Settings.ItmsApiKey)
                ? Settings.ItmsApiKey.Trim()
                : (Environment.GetEnvironmentVariable("ITMS_API_KEY") ?? "").Trim();

        public bool IsItmsConfigured =>
            !string.IsNullOrWhiteSpace(EffectiveItmsUrl) && !string.IsNullOrWhiteSpace(EffectiveItmsApiKey);

        /// <summary>
        /// Server-authoritative operator list: replaces the local operators with the server's (same ids),
        /// keeps local operators the server does not know as INACTIVE (history keeps their names), and ends
        /// the current session if that operator was removed or deactivated. Refuses an empty list or a list
        /// without an active administrator. Returns the number of changed operators.
        /// </summary>
        public int ApplyServerOperators(IEnumerable<OperatorRecord> serverOperators)
        {
            var incoming = (serverOperators ?? Enumerable.Empty<OperatorRecord>()).Where(o => o != null && !string.IsNullOrWhiteSpace(o.OperatorId)).ToList();
            if (incoming.Count == 0 || !incoming.Any(o => o.Active && o.IsAdmin))
                throw new ArgumentException("The server operator list is empty or has no active administrator; it was not applied.");

            int changed = 0;
            var byId = incoming.ToDictionary(o => o.OperatorId);
            foreach (var local in Settings.Operators.Where(o => !byId.ContainsKey(o.OperatorId) && o.Active))
            {
                local.Active = false;
                local.UpdatedAt = _clock();
                changed++;
            }
            foreach (var s in incoming)
            {
                var local = FindOperator(s.OperatorId);
                if (local == null)
                {
                    Settings.Operators.Add(new OperatorRecord
                    {
                        OperatorId = s.OperatorId, Name = s.Name, Role = s.Role, Active = s.Active,
                        Departments = (s.Departments ?? new List<string>()).ToList(),
                        Permissions = (s.Permissions ?? new List<string>()).ToList(),
                        CreatedAt = s.CreatedAt == default(DateTime) ? _clock() : s.CreatedAt,
                        UpdatedAt = _clock(),
                    });
                    changed++;
                    continue;
                }
                bool same = local.Name == s.Name && local.Role == s.Role && local.Active == s.Active &&
                            (local.Departments ?? new List<string>()).SequenceEqual(s.Departments ?? new List<string>()) &&
                            (local.Permissions ?? new List<string>()).SequenceEqual(s.Permissions ?? new List<string>());
                if (same) continue;
                local.Name = s.Name; local.Role = s.Role; local.Active = s.Active;
                local.Departments = (s.Departments ?? new List<string>()).ToList();
                local.Permissions = (s.Permissions ?? new List<string>()).ToList();
                local.UpdatedAt = _clock();
                changed++;
            }
            var sessionOp = Settings.ActiveSession == null ? null : FindOperator(Settings.ActiveSession.OperatorId);
            if (Settings.ActiveSession != null && (sessionOp == null || !sessionOp.Active)) ClearSession();
            else if (changed > 0) Save();
            return changed;
        }

        // ---------------- Central BBTech API ----------------

        public string EffectiveBbtechApiUrl =>
            !string.IsNullOrWhiteSpace(Settings.BbtechApiBaseUrl)
                ? Settings.BbtechApiBaseUrl.Trim().TrimEnd('/')
                : (Environment.GetEnvironmentVariable("BBTECH_API_URL") ?? "").Trim().TrimEnd('/');

        public string EffectiveBbtechApiKey =>
            !string.IsNullOrWhiteSpace(Settings.BbtechApiKey)
                ? Settings.BbtechApiKey.Trim()
                : (Environment.GetEnvironmentVariable("BBTECH_API_KEY") ?? "").Trim();

        public void SetBbtechApiConfig(string url, string apiKey)
        {
            url = (url ?? "").Trim().TrimEnd('/');
            if (url.Length > 0 && !Sync.BBTechApiClient.IsUsableUrl(url))
                throw new ArgumentException("The BBTech server address must start with https://");
            Settings.BbtechApiBaseUrl = url;
            Settings.BbtechApiKey = (apiKey ?? "").Trim();
            Save();
        }

        public void SetItmsConfig(string url, string apiKey)
        {
            Settings.ItmsBaseUrl = (url ?? "").Trim().TrimEnd('/');
            Settings.ItmsApiKey = (apiKey ?? "").Trim();
            Save();
        }
    }
}
