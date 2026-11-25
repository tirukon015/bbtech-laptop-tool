namespace BBTech.Core.Software
{
    /// <summary>What an install attempt achieved, in words a technician understands.</summary>
    public sealed class InstallOutcome
    {
        public bool Success { get; set; }
        public bool AlreadyInstalled { get; set; }
        public bool RestartRequired { get; set; }
        public int ExitCode { get; set; }
        public string Message { get; set; } = "";

        public const int UacDeclined = -1223;
        public const int TimedOut = -2;

        /// <summary>
        /// Success needs BOTH a success exit code (0, or 3010 = success, restart required) AND the product
        /// detected afterwards. An installer that "exits 0" without installing anything is a failure.
        /// </summary>
        public static InstallOutcome From(int exitCode, bool detectedAfter, string displayName, string version, int timeoutMinutes)
        {
            bool exitOk = exitCode == 0 || exitCode == 3010;
            var o = new InstallOutcome { ExitCode = exitCode, RestartRequired = exitCode == 3010, Success = exitOk && detectedAfter };
            if (o.Success)
                o.Message = displayName + " installed (" + version + ")." + (o.RestartRequired ? " Restart the laptop to finish." : "");
            else if (exitCode == UacDeclined)
                o.Message = "Installation cancelled: the Windows administrator prompt was declined.";
            else if (exitCode == TimedOut)
                o.Message = "The installer is still running after " + timeoutMinutes + " minutes. Check the laptop, then press Check.";
            else if (exitOk)
                o.Message = "The installer finished, but the product was not found afterwards. Installation failed.";
            else
                o.Message = "Installation failed (installer exit code " + exitCode + ").";
            return o;
        }
    }
}
