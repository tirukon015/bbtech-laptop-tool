using System;

namespace BBTech.Core.Software
{
    /// <summary>What Windows reports about itself (Win32_OperatingSystem).</summary>
    public sealed class WindowsInfo
    {
        public string Edition { get; set; }
        public string Version { get; set; }
        public string Build { get; set; }
        public string Architecture { get; set; }
        public DateTime? InstallDate { get; set; }

        public bool Readable => !string.IsNullOrWhiteSpace(Edition) && !string.IsNullOrWhiteSpace(Build);

        public string Describe() => Readable
            ? string.Format("{0} {1} build {2} {3}, installed {4}", Edition, Version, Build, Architecture,
                InstallDate.HasValue ? InstallDate.Value.ToString("yyyy-MM-dd HH:mm") : "UNKNOWN").Replace("  ", " ").Trim()
            : "UNKNOWN";
    }

    public sealed class WindowsInstallVerdict
    {
        /// <summary>PASS or FAIL.</summary>
        public string Status { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// Verifies a Windows installation from the resulting system state, not from "the installer was started":
    /// if an installation was started for this laptop, Windows must report an install date AFTER that moment.
    /// Without a start record the existing Windows is accepted as it is (and said so).
    /// </summary>
    public static class WindowsInstallCheck
    {
        public static WindowsInstallVerdict Evaluate(DateTime? startedAt, WindowsInfo windows)
        {
            if (windows == null || !windows.Readable)
                return new WindowsInstallVerdict { Status = "FAIL", Note = "Windows information could not be read (UNKNOWN)." };
            if (!startedAt.HasValue)
                return new WindowsInstallVerdict { Status = "PASS", Note = "Existing Windows kept: " + windows.Describe() };
            if (!windows.InstallDate.HasValue)
                return new WindowsInstallVerdict { Status = "FAIL", Note = "Windows does not report its install date, so the new installation cannot be confirmed. " + windows.Describe() };
            // A few minutes of tolerance for clock differences between the old and the new installation.
            if (windows.InstallDate.Value < startedAt.Value.AddMinutes(-10))
                return new WindowsInstallVerdict
                {
                    Status = "FAIL",
                    Note = "Windows was not reinstalled: it was installed " + windows.InstallDate.Value.ToString("yyyy-MM-dd HH:mm") +
                           ", before the installation was started (" + startedAt.Value.ToString("yyyy-MM-dd HH:mm") + ").",
                };
            return new WindowsInstallVerdict { Status = "PASS", Note = "New installation verified: " + windows.Describe() };
        }
    }
}
