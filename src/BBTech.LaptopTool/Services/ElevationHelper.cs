using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// Helper for checking administrative privileges and restarting the tool with elevated permissions.
    /// </summary>
    public static class ElevationHelper
    {
        private static bool? _isElevated;

        /// <summary>
        /// Gets whether the current application process has full administrator rights.
        /// </summary>
        public static bool IsAdministrator
        {
            get
            {
                if (!_isElevated.HasValue)
                {
                    try
                    {
                        using (var identity = WindowsIdentity.GetCurrent())
                        {
                            var principal = new WindowsPrincipal(identity);
                            _isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Failed to determine administrator status", ex);
                        _isElevated = false;
                    }
                }
                return _isElevated.Value;
            }
        }

        /// <summary>
        /// Attempts to restart the current executable elevated as Administrator via UAC.
        /// </summary>
        /// <param name="arguments">Optional command line arguments to pass to the elevated instance.</param>
        /// <returns>True if restart was launched; false if cancelled by user or failed.</returns>
        public static bool RestartAsAdministrator(string arguments = "")
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
                {
                    exePath = Assembly.GetEntryAssembly()?.Location;
                }

                if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
                {
                    AppLog.Error("Could not resolve executable location to restart elevated.", null);
                    MessageBox.Show("Unable to find the executable path to restart.", "BBTech Laptop Tool", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                var psi = new ProcessStartInfo(exePath, arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas" // Triggers Windows UAC elevation prompt
                };

                AppLog.Info("Restarting application with Administrator privileges via UAC...");
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    AppLog.Info("Elevated process launched successfully (PID: " + proc.Id + "). Shutting down current instance.");
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                    return true;
                }

                return false;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (user clicked 'No' on UAC)
            {
                AppLog.Info("User cancelled UAC elevation prompt.");
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to restart application as Administrator", ex);
                MessageBox.Show("Failed to restart as Administrator: " + ex.Message, "BBTech Laptop Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
    }
}
