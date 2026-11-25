using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security;
using System.Threading.Tasks;
using BBTech.Core.Audit;
using BBTech.Core.Security;
using BBTech.Core.Settings;
using BBTech.Core.Software;
using BBTech.LaptopTool.Logging;
using Microsoft.Win32;

namespace BBTech.LaptopTool.Services
{
    public sealed class InstallationStatus
    {
        public bool IsInstalled { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string Details { get; set; }
    }

    /// <summary>
    /// Governed execution service for software installation and activation checks.
    /// Strictly permission-controlled and audit-logged.
    /// </summary>
    public sealed class SoftwareExecutionService
    {
        private readonly AuditStore _audit;

        public SoftwareExecutionService(AuditStore audit = null)
        {
            _audit = audit ?? new AuditStore(AppPaths.AuditLogPath);
        }

        // ====================================================================
        // OFFICE INSTALLATION
        // ====================================================================

        public InstallationStatus CheckOfficeInstallation()
        {
            var status = new InstallationStatus { IsInstalled = false, DisplayName = "Microsoft Office", Version = "Not detected" };

            try
            {
                // 1. Check Click-to-Run configuration key
                using (var c2r = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration"))
                {
                    if (c2r != null)
                    {
                        var product = c2r.GetValue("ProductCodes")?.ToString();
                        var version = c2r.GetValue("VersionToReport")?.ToString();
                        if (!string.IsNullOrEmpty(product) || !string.IsNullOrEmpty(version))
                        {
                            status.IsInstalled = true;
                            status.DisplayName = "Microsoft Office (Click-to-Run)";
                            status.Version = version ?? "Installed";
                            status.Details = "Product: " + product;
                            return status;
                        }
                    }
                }

                // 2. Check 64-bit and 32-bit Uninstall registry hives
                string[] uninstallPaths = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                foreach (var path in uninstallPaths)
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(path))
                    {
                        if (key == null) continue;
                        foreach (var subKeyName in key.GetSubKeyNames())
                        {
                            using (var subKey = key.OpenSubKey(subKeyName))
                            {
                                var name = subKey?.GetValue("DisplayName")?.ToString() ?? "";
                                if (name.IndexOf("Microsoft Office", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Microsoft 365", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    status.IsInstalled = true;
                                    status.DisplayName = name;
                                    status.Version = subKey.GetValue("DisplayVersion")?.ToString() ?? "Installed";
                                    status.Details = name;
                                    return status;
                                }
                            }
                        }
                    }
                }

                // 3. Fallback: Check standard Office program file directories
                string[] commonPaths = new[]
                {
                    @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE",
                    @"C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE",
                    @"C:\Program Files\Microsoft Office\Office16\WINWORD.EXE"
                };

                foreach (var exePath in commonPaths)
                {
                    if (File.Exists(exePath))
                    {
                        var vi = FileVersionInfo.GetVersionInfo(exePath);
                        status.IsInstalled = true;
                        status.DisplayName = "Microsoft Office Word";
                        status.Version = vi.ProductVersion ?? "16.0";
                        status.Details = exePath;
                        return status;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Office installation check error", ex);
                status.Details = ex.Message;
            }

            return status;
        }

        /// <summary>Folder that approved installers are taken from (and downloaded into): "BBTech Data\installers".</summary>
        public static string InstallersRoot => Path.Combine(AppPaths.DataDir, "installers");

        /// <summary>Is the package's product present now? (Used before and after installing.)</summary>
        public InstallationStatus Detect(SoftwarePackage package)
        {
            string rule = package?.DetectRule ?? "office";
            if (rule == "office") return CheckOfficeInstallation();
            if (rule.StartsWith("uninstall:", StringComparison.OrdinalIgnoreCase))
                return FindUninstallEntry(rule.Substring("uninstall:".Length).Trim());
            return new InstallationStatus { IsInstalled = false, DisplayName = package?.Name, Version = "Unknown detection rule", Details = rule };
        }

        private static InstallationStatus FindUninstallEntry(string displayNamePrefix)
        {
            var status = new InstallationStatus { IsInstalled = false, DisplayName = displayNamePrefix, Version = "Not detected" };
            if (string.IsNullOrWhiteSpace(displayNamePrefix)) return status;
            foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using (var key = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                        using (var k = key.OpenSubKey(sub))
                        {
                            var name = k?.GetValue("DisplayName")?.ToString() ?? "";
                            if (!name.StartsWith(displayNamePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                            status.IsInstalled = true;
                            status.DisplayName = name;
                            status.Version = k.GetValue("DisplayVersion")?.ToString() ?? "Installed";
                            return status;
                        }
                }
            }
            return status;
        }

        /// <summary>
        /// Installs an APPROVED package only (there is no "run any EXE"):
        ///  1. the operator must hold software.install (no operator = refused);
        ///  2. the package definition must validate (file name, arguments, HTTPS URL, checksum format);
        ///  3. the installer comes only from "BBTech Data\installers" (or the admin-set offline folder),
        ///     downloaded there from the approved HTTPS URL if needed;
        ///  4. integrity: the SHA-256 must match when configured AND the Authenticode publisher must match
        ///     when configured (at least one of the two is always required);
        ///  5. it runs with the package's fixed arguments, elevated only when the package requires it;
        ///  6. the exit code is captured and the product is detected afterwards;
        ///  7. every step is audited. Success = exit code 0 (or 3010 = restart needed) AND product detected.
        /// </summary>
        public async Task<InstallOutcome> InstallPackageAsync(SoftwarePackage package, OperatorRecord actor, IProgress<string> progress = null)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (actor == null || !actor.CanInstallSoftware)
            {
                _audit.Log(actor, AuditActions.PermissionDenied, package.Name, null, null, "software.install required");
                throw new SecurityException("Permission denied: your profile cannot install software.");
            }
            string invalid = package.Validate();
            if (invalid != null)
            {
                _audit.Log(actor, AuditActions.InstallRejected, package.Name, null, null, invalid);
                throw new SecurityException("This package cannot be installed: " + invalid);
            }

            var before = Detect(package);
            if (before.IsInstalled)
                return new InstallOutcome { Success = true, AlreadyInstalled = true, Message = before.DisplayName + " is already installed (" + before.Version + ")." };

            progress?.Report("Preparing the approved installer...");
            string installerPath = package.ResolveInstaller(InstallersRoot);
            if (installerPath == null && package.SourceMode == PackageSourceMode.Online && !string.IsNullOrWhiteSpace(package.OnlineDownloadUrl))
                installerPath = await DownloadInstallerAsync(package, progress);
            if (installerPath == null)
            {
                _audit.Log(actor, AuditActions.InstallRejected, package.Name, null, null, "Installer missing");
                throw new FileNotFoundException("Installer missing. Put " +
                    (package.SourceMode == PackageSourceMode.Online ? package.OnlineInstallerName : package.OfflineExecutableName) +
                    " in \"BBTech Data\\installers\" on the USB, or ask an admin to check the package settings.");
            }

            progress?.Report("Verifying the installer...");
            string rejection = await Task.Run(() => VerifyInstaller(package, installerPath));
            if (rejection != null)
            {
                _audit.Log(actor, AuditActions.InstallRejected, package.Name, null, null, rejection + " File: " + installerPath);
                throw new SecurityException("The installer was not run: " + rejection);
            }

            _audit.Log(actor, AuditActions.InstallStarted, package.Name, null, package.Version, "Installer: " + installerPath);
            progress?.Report("Installing. " + (package.RequiresElevation ? "Approve the Windows prompt and wait" : "Please wait") + " for the installer to finish...");

            int exitCode = await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = package.InstallArguments ?? "",
                        WorkingDirectory = Path.GetDirectoryName(installerPath),
                        UseShellExecute = true,
                    };
                    if (package.RequiresElevation && !ElevationHelper.IsAdministrator) psi.Verb = "runas";
                    using (var proc = Process.Start(psi))
                    {
                        if (proc == null) return -1;
                        if (!proc.WaitForExit(1000 * 60 * package.TimeoutMinutes)) return -2;
                        return proc.ExitCode;
                    }
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return -1223; // UAC declined
                }
                catch (Exception ex)
                {
                    AppLog.Error("Installer execution failed", ex);
                    return -1;
                }
            });

            var after = Detect(package);
            var outcome = InstallOutcome.From(exitCode, after.IsInstalled, after.DisplayName, after.Version, package.TimeoutMinutes);
            _audit.Log(actor, AuditActions.InstallCompleted, package.Name, null, outcome.Success ? "SUCCESS" : "FAILED", outcome.Message);
            return outcome;
        }

        /// <summary>Kept for existing callers: installs the approved Office package.</summary>
        public Task<InstallOutcome> InstallOfficeAsync(SoftwarePackage package, OperatorRecord actor, IProgress<string> progress = null) =>
            InstallPackageAsync(package, actor, progress);

        /// <summary>Null when the file passes every configured check (checksum and/or publisher).</summary>
        public static string VerifyInstaller(SoftwarePackage package, string path)
        {
            if (!string.IsNullOrWhiteSpace(package.Sha256Checksum) && !package.VerifyFileIntegrity(path))
                return "Checksum mismatch: the file is not the approved installer.";
            if (!string.IsNullOrWhiteSpace(package.Publisher))
            {
                string why = AuthenticodeVerifier.Check(path, package.Publisher.Trim());
                if (why != null) return why;
            }
            return null;
        }

        /// <summary>Downloads the approved bootstrapper over HTTPS into the installers folder, then it is verified like any other file.</summary>
        private static async Task<string> DownloadInstallerAsync(SoftwarePackage package, IProgress<string> progress)
        {
            Directory.CreateDirectory(InstallersRoot);
            string target = Path.Combine(InstallersRoot, Path.GetFileName(package.OnlineInstallerName));
            string partial = target + ".download";
            progress?.Report("Downloading the approved installer...");
            try
            {
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                using (var resp = await http.GetAsync(package.OnlineDownloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) throw new IOException("Download failed (HTTP " + (int)resp.StatusCode + ").");
                    using (var fs = File.Create(partial))
                        await resp.Content.CopyToAsync(fs);
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(partial, target);
                return target;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                AppLog.Warn("Installer download failed: " + ex.Message);
                throw new IOException("Server unavailable or download failed: " + ex.Message, ex);
            }
        }

        // ====================================================================
        // ACTIVATION CHECKS & WORKFLOWS
        // ====================================================================

        public static string GetSystem32Directory() => ActivationProcessBuilder.GetSystem32Directory();

        public static string FindOsppVbsPath() => ActivationProcessBuilder.FindOsppVbsPath();

        public ProcessStartInfo BuildWindowsActivationProcess(string customProductKey = null, bool visible = true) =>
            ActivationProcessBuilder.BuildWindowsActivationProcess(customProductKey, visible);

        public ProcessStartInfo BuildOfficeActivationProcess(string customProductKey = null, bool visible = true) =>
            ActivationProcessBuilder.BuildOfficeActivationProcess(customProductKey, visible);

        public ActivationStatus CheckOfficeActivation(OperatorRecord actor = null)
        {
            if (actor != null && !actor.CanCheckActivation && !actor.IsAdmin)
            {
                throw new SecurityException("Access Denied: You do not have permission to check software activation (software.activation.check required).");
            }

            var res = new ActivationStatus { ProductName = "Microsoft Office", IsActivated = false, LicenseStatus = "Not Activated" };

            try
            {
                // 1. Query WMI SoftwareLicensingProduct for Office licenses
                using (var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                    "SELECT Name, LicenseStatus, Description FROM SoftwareLicensingProduct WHERE (ApplicationId = '0ff1ce15-a989-479d-af46-f275c6370539' OR Name LIKE '%Office%') AND PartialProductKey IS NOT NULL"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"]?.ToString() ?? "Office";
                        uint status = (uint)(obj["LicenseStatus"] ?? 0);
                        res.ProductName = name;

                        if (status == 1) // 1 = Licensed
                        {
                            res.IsActivated = true;
                            res.LicenseStatus = "Activated (Licensed)";
                            res.Details = name;
                            return res;
                        }
                    }
                }

                // 2. Query WMI OfficeSoftwareProtectionProduct (Office KMS / Volume licensing provider)
                try
                {
                    using (var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                        "SELECT LicenseStatus, Description FROM OfficeSoftwareProtectionProduct WHERE PartialProductKey IS NOT NULL"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            uint status = (uint)(obj["LicenseStatus"] ?? 0);
                            if (status == 1)
                            {
                                res.IsActivated = true;
                                res.LicenseStatus = "Activated (Licensed)";
                                res.Details = obj["Description"]?.ToString() ?? "Microsoft Office Volume Licensed";
                                return res;
                            }
                        }
                    }
                }
                catch { }

                // 3. Fallback: Query ospp.vbs /dstatus if script exists
                string ospp = FindOsppVbsPath();
                if (!string.IsNullOrEmpty(ospp) && File.Exists(ospp))
                {
                    string sysDir = GetSystem32Directory();
                    string cscript = Path.Combine(sysDir, "cscript.exe");
                    if (File.Exists(cscript))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = cscript,
                            Arguments = "//b //nologo \"" + ospp + "\" /dstatus",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true
                        };

                        using (var p = Process.Start(psi))
                        {
                            if (p != null)
                            {
                                string output = p.StandardOutput.ReadToEnd();
                                p.WaitForExit(15000);

                                if (output.IndexOf("---LICENSED---", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    output.IndexOf("LICENSE STATUS:  --- LICENSED ---", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    res.IsActivated = true;
                                    res.LicenseStatus = "Activated (Licensed)";
                                    res.Details = "Microsoft Office is permanently activated";
                                    return res;
                                }
                                else if (output.IndexOf("---NOTIFICATIONS---", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    res.IsActivated = false;
                                    res.LicenseStatus = "Notification / Grace Expired";
                                    res.Details = "License Status: Grace Expired";
                                    return res;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Office activation query error", ex);
                res.Details = ex.Message;
            }

            // Microsoft 365 subscription products are licensed outside Windows Software Protection: "not found there"
            // does not mean "not activated". Say it cannot be confirmed here instead of reporting a false FAIL.
            if (!res.IsActivated && IsOfficeSubscription())
            {
                res.Determinable = false;
                res.LicenseStatus = "Cannot be confirmed here (Microsoft 365 subscription)";
                res.Details = "Microsoft 365 subscription licensing is not reported by Windows Software Protection. Check sign-in / activation in an Office app (File > Account).";
            }
            return res;
        }

        private static bool IsOfficeSubscription()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration"))
                {
                    var ids = k?.GetValue("ProductReleaseIds")?.ToString() ?? "";
                    return ids.IndexOf("O365", StringComparison.OrdinalIgnoreCase) >= 0 || ids.IndexOf("M365", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        public ActivationStatus CheckWindowsActivation(OperatorRecord actor = null)
        {
            if (actor != null && !actor.CanCheckActivation && !actor.IsAdmin)
            {
                throw new SecurityException("Access Denied: You do not have permission to check Windows activation (software.activation.check required).");
            }

            string osName = "Windows";
            var res = new ActivationStatus { ProductName = "Windows", IsActivated = false, LicenseStatus = "Checking..." };

            try
            {
                using (var osSearcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in osSearcher.Get())
                    {
                        osName = obj["Caption"]?.ToString() ?? "Windows";
                        break;
                    }
                }
                res.ProductName = osName;

                // 1. Query SoftwareLicensingProduct for Windows (AppId: 55c92734-d682-4d71-983e-d6ec3f16059f)
                using (var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                    "SELECT LicenseStatus, Description FROM SoftwareLicensingProduct WHERE ApplicationId = '55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        uint status = (uint)(obj["LicenseStatus"] ?? 0);
                        if (status == 1) // 1 = Licensed
                        {
                            res.IsActivated = true;
                            res.LicenseStatus = "Activated";
                            res.Details = obj["Description"]?.ToString() ?? (osName + " is permanently activated");
                            return res;
                        }
                        else
                        {
                            res.LicenseStatus = "Notification / Unlicensed (" + status + ")";
                        }
                    }
                }

                // 2. Fallback: Query slmgr.vbs /dli
                string sysDir = GetSystem32Directory();
                string slmgr = Path.Combine(sysDir, "slmgr.vbs");
                string cscript = Path.Combine(sysDir, "cscript.exe");
                if (File.Exists(slmgr) && File.Exists(cscript))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = cscript,
                        Arguments = "//b //nologo \"" + slmgr + "\" /dli",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };

                    using (var p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            string output = p.StandardOutput.ReadToEnd();
                            p.WaitForExit(15000);
                            if (output.IndexOf("License Status: Licensed", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                res.IsActivated = true;
                                res.LicenseStatus = "Activated";
                                res.Details = osName + " is permanently activated";
                                return res;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("WMI Windows activation query error", ex);
                res.Details = ex.Message;
            }

            return res;
        }



        public async Task<ActivationResult> ActivateWindowsAsync(OperatorRecord actor, string customProductKey = null, bool visible = true)
        {
            if (actor == null || !actor.CanExecuteActivation)
            {
                throw new SecurityException("Access Denied: Only authorized administrators can trigger license activation (software.activation.execute required).");
            }

            _audit.Log(actor, AuditActions.ActivationExecuted, "Windows", null, "INITIATED", "Legitimate slmgr /ato activation workflow (Visible: " + visible + ")");

            return await Task.Run(() =>
            {
                try
                {
                    var psi = BuildWindowsActivationProcess(customProductKey, visible);
                    int exitCode = -1;

                    using (var p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            exitCode = p.WaitForExit(5 * 60 * 1000) ? p.ExitCode : -2; // -2: still running; the status check below decides
                        }
                    }

                    var check = CheckWindowsActivation();
                    var result = new ActivationResult
                    {
                        Success = check.IsActivated,
                        Status = check.IsActivated ? "PASS" : "FAIL",
                        Target = "Windows",
                        ExitCode = exitCode,
                        LicenseStatus = check.LicenseStatus,
                        Details = check.Details ?? check.LicenseStatus
                    };

                    _audit.Log(actor, AuditActions.ActivationExecuted, "Windows", null, result.Status,
                        string.Format("Exit code: {0}, Outcome: {1}", exitCode, result.Details));
                    return result;
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    AppLog.Warn("UAC elevation cancelled for Windows activation");
                    var cancel = new ActivationResult
                    {
                        Success = false,
                        Status = "FAIL",
                        Target = "Windows",
                        ExitCode = 1223,
                        LicenseStatus = "UAC Cancelled",
                        Details = "Administrator permission was declined."
                    };
                    _audit.Log(actor, AuditActions.ActivationExecuted, "Windows", null, "FAIL", "Administrator permission declined (UAC).");
                    return cancel;
                }
                catch (Exception ex)
                {
                    AppLog.Error("Windows activation command error", ex);
                    var err = new ActivationResult
                    {
                        Success = false,
                        Status = "FAIL",
                        Target = "Windows",
                        ExitCode = -1,
                        LicenseStatus = "Error",
                        Details = ex.Message
                    };
                    _audit.Log(actor, AuditActions.ActivationExecuted, "Windows", null, "FAIL", ex.Message);
                    return err;
                }
            });
        }

        public async Task<ActivationResult> ActivateOfficeAsync(OperatorRecord actor, string customProductKey = null, bool visible = true)
        {
            if (actor == null || !actor.CanExecuteActivation)
            {
                throw new SecurityException("Access Denied: Only authorized administrators can trigger license activation (software.activation.execute required).");
            }

            string ospp = FindOsppVbsPath();
            if (string.IsNullOrEmpty(ospp) || !File.Exists(ospp))
            {
                var fail = new ActivationResult
                {
                    Success = false,
                    Status = "FAIL",
                    Target = "Microsoft Office",
                    ExitCode = -1,
                    LicenseStatus = "Not Installed",
                    Details = "Microsoft Office licensing script (ospp.vbs) not found. Verify Office installation."
                };
                _audit.Log(actor, AuditActions.ActivationExecuted, "Microsoft Office", null, "FAIL", fail.Details);
                return fail;
            }

            _audit.Log(actor, AuditActions.ActivationExecuted, "Microsoft Office", null, "INITIATED", "Legitimate ospp.vbs /act activation workflow (Visible: " + visible + ")");

            return await Task.Run(() =>
            {
                try
                {
                    var psi = BuildOfficeActivationProcess(customProductKey, visible);
                    int exitCode = -1;

                    using (var p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            exitCode = p.WaitForExit(5 * 60 * 1000) ? p.ExitCode : -2; // -2: still running; the status check below decides
                        }
                    }

                    var check = CheckOfficeActivation();
                    var result = new ActivationResult
                    {
                        Success = check.IsActivated,
                        Status = check.IsActivated ? "PASS" : "FAIL",
                        Target = "Microsoft Office",
                        ExitCode = exitCode,
                        LicenseStatus = check.LicenseStatus,
                        Details = check.Details ?? check.LicenseStatus
                    };

                    _audit.Log(actor, AuditActions.ActivationExecuted, "Microsoft Office", null, result.Status,
                        string.Format("Exit code: {0}, Outcome: {1}", exitCode, result.Details));
                    return result;
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    AppLog.Warn("UAC elevation cancelled for Office activation");
                    var cancel = new ActivationResult
                    {
                        Success = false,
                        Status = "FAIL",
                        Target = "Microsoft Office",
                        ExitCode = 1223,
                        LicenseStatus = "UAC Cancelled",
                        Details = "Administrator permission was declined."
                    };
                    _audit.Log(actor, AuditActions.ActivationExecuted, "Microsoft Office", null, "FAIL", "Administrator permission declined (UAC).");
                    return cancel;
                }
                catch (Exception ex)
                {
                    AppLog.Error("Office activation command error", ex);
                    var err = new ActivationResult
                    {
                        Success = false,
                        Status = "FAIL",
                        Target = "Microsoft Office",
                        ExitCode = -1,
                        LicenseStatus = "Error",
                        Details = ex.Message
                    };
                    _audit.Log(actor, AuditActions.ActivationExecuted, "Microsoft Office", null, "FAIL", ex.Message);
                    return err;
                }
            });
        }
    }
}
