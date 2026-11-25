using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BBTech.Core.Software
{
    public enum PackageSourceMode
    {
        /// <summary>Small bootstrapper EXE (e.g. OfficeSetup.exe) that downloads the product itself.</summary>
        Online,
        /// <summary>Large local package folder (several GB) on the USB or a share.</summary>
        Offline
    }

    /// <summary>
    /// An APPROVED software package (defined by an admin / the BBTech server). This is the only thing
    /// the tool will install: there is no "run any EXE" feature. Every field that affects what runs is
    /// validated (<see cref="Validate"/>) before use.
    /// </summary>
    [DataContract]
    public sealed class SoftwarePackage
    {
        [DataMember] public string PackageId { get; set; } = "office-2024";
        [DataMember] public string Name { get; set; } = "Microsoft Office";
        [DataMember] public string Version { get; set; } = "Office 2024";
        [DataMember] public PackageSourceMode SourceMode { get; set; } = PackageSourceMode.Online;
        /// <summary>Online: file name of the bootstrapper inside "BBTech Data\installers" (downloaded there if a URL is set).</summary>
        [DataMember] public string OnlineInstallerName { get; set; } = "OfficeSetup.exe";
        /// <summary>Online: HTTPS URL the approved bootstrapper may be downloaded from (optional).</summary>
        [DataMember] public string OnlineDownloadUrl { get; set; } = "";
        /// <summary>Offline: package folder (absolute, or relative to "BBTech Data\installers").</summary>
        [DataMember] public string OfflineFolderPath { get; set; } = "";
        [DataMember] public string OfflineExecutableName { get; set; } = "Setup.exe";
        /// <summary>Expected SHA-256 of the installer EXE. Required unless <see cref="Publisher"/> is set.</summary>
        [DataMember] public string Sha256Checksum { get; set; } = "";
        /// <summary>Expected Authenticode publisher (exact O= and CN=), e.g. "Microsoft Corporation".</summary>
        [DataMember] public string Publisher { get; set; } = "";
        /// <summary>Fixed command-line arguments (e.g. "/configure config.xml"). Validated: no shell operators.</summary>
        [DataMember] public string InstallArguments { get; set; } = "";
        [DataMember] public bool RequiresElevation { get; set; } = true;
        /// <summary>Whether QC requires this package to be installed.</summary>
        [DataMember] public bool Required { get; set; }
        /// <summary>How success is verified afterwards: "office" or "uninstall:&lt;DisplayName prefix&gt;".</summary>
        [DataMember] public string DetectRule { get; set; } = "office";
        [DataMember] public int TimeoutMinutes { get; set; } = 60;
        [DataMember] public bool Approved { get; set; } = true;
        [DataMember] public string CreatedBy { get; set; } = "Admin";
        [DataMember] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [DataMember] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Result id used when the install outcome is recorded (e.g. "SoftwareInstall:office-2024").</summary>
        public string ResultTestId => PackageId == "office-default" ? Testing.QcRequirements.SoftwareOfficeInstallId : "SoftwareInstall:" + PackageId;

        /// <summary>Built-in default: Microsoft's signed online bootstrapper, verified by publisher.</summary>
        public static SoftwarePackage DefaultOffice()
        {
            return new SoftwarePackage
            {
                PackageId = "office-default",
                Name = "Microsoft Office",
                Version = "Office 2024 / 365",
                SourceMode = PackageSourceMode.Online,
                OnlineInstallerName = "OfficeSetup.exe",
                Publisher = "Microsoft Corporation",
                RequiresElevation = true,
                Required = false,
                DetectRule = "office",
                Approved = true,
                CreatedBy = "System",
            };
        }

        private static readonly Regex SafeName = new Regex(@"^[A-Za-z0-9 ._()\-]{1,120}\.(exe|msi)$", RegexOptions.IgnoreCase);
        private static readonly Regex SafeArgs = new Regex(@"^[A-Za-z0-9 ._:/=\-\\""]{0,400}$");

        /// <summary>Null when the definition is safe to use; otherwise why it is refused.</summary>
        public string Validate()
        {
            if (!Approved) return "This package is not approved.";
            if (string.IsNullOrWhiteSpace(PackageId) || string.IsNullOrWhiteSpace(Name)) return "The package has no id or name.";
            string exe = SourceMode == PackageSourceMode.Online ? OnlineInstallerName : OfflineExecutableName;
            if (string.IsNullOrWhiteSpace(exe) || !SafeName.IsMatch(exe) || exe != Path.GetFileName(exe))
                return "The installer file name is not allowed: " + exe;
            if (string.IsNullOrWhiteSpace(Sha256Checksum) && string.IsNullOrWhiteSpace(Publisher))
                return "The package has neither a SHA-256 checksum nor a trusted publisher, so it cannot be verified.";
            if (!string.IsNullOrWhiteSpace(Sha256Checksum) && !Regex.IsMatch(Sha256Checksum.Trim(), "^[A-Fa-f0-9]{64}$"))
                return "The SHA-256 checksum is not valid.";
            if (!SafeArgs.IsMatch(InstallArguments ?? "") || (InstallArguments ?? "").Contains(".."))
                return "The install arguments contain characters that are not allowed.";
            if (!string.IsNullOrWhiteSpace(OnlineDownloadUrl) &&
                (!Uri.TryCreate(OnlineDownloadUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
                return "The download address must be HTTPS.";
            if (TimeoutMinutes < 1 || TimeoutMinutes > 240) return "The timeout must be between 1 and 240 minutes.";
            return null;
        }

        /// <summary>True only when a checksum is configured and matches (constant-time comparison not needed: the hash is public).</summary>
        public bool VerifyFileIntegrity(string filePath)
        {
            if (string.IsNullOrWhiteSpace(Sha256Checksum)) return false; // never "passes" without a checksum
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
            return string.Equals(ComputeSha256(filePath), Sha256Checksum.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static string ComputeSha256(string filePath)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Resolves the installer path inside the approved roots only. A path that escapes the roots
        /// (e.g. "..\..\Windows") is refused.
        /// </summary>
        public string ResolveInstaller(string installersRoot)
        {
            if (string.IsNullOrEmpty(installersRoot)) return null;
            string root = Path.GetFullPath(installersRoot).TrimEnd('\\') + "\\";
            string candidate;
            if (SourceMode == PackageSourceMode.Offline)
            {
                if (string.IsNullOrWhiteSpace(OfflineFolderPath)) return null;
                string folder = Path.IsPathRooted(OfflineFolderPath) ? OfflineFolderPath : Path.Combine(root, OfflineFolderPath);
                candidate = Path.GetFullPath(Path.Combine(folder, Path.GetFileName(OfflineExecutableName ?? "")));
                // Offline packages may live on another drive/share configured by an admin, but never contain "..".
                if (OfflineFolderPath.Contains("..")) return null;
            }
            else
            {
                candidate = Path.GetFullPath(Path.Combine(root, Path.GetFileName(OnlineInstallerName ?? "")));
                if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return File.Exists(candidate) ? candidate : null;
        }
    }

    /// <summary>The approved package list (from master configuration, or the built-in default).</summary>
    public static class SoftwareCatalog
    {
        public static List<SoftwarePackage> Effective(IEnumerable<SoftwarePackage> fromServer)
        {
            var list = (fromServer ?? Enumerable.Empty<SoftwarePackage>()).Where(p => p != null && p.Validate() == null).ToList();
            if (!list.Any(p => p.DetectRule == "office")) list.Insert(0, SoftwarePackage.DefaultOffice());
            return list;
        }
    }
}
