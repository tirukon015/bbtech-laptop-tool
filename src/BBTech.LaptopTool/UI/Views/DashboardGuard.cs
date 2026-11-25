using System;
using System.Windows;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.UI.Views
{
    /// <summary>
    /// Shared checks for the Hardware / Software / QC dashboards, so a technician never runs a test
    /// whose result cannot be saved, and never saves a result under someone else's name.
    /// (The same rules are enforced again in InspectionManager; this is only the friendly front door.)
    /// </summary>
    internal static class DashboardGuard
    {
        /// <summary>True when a test may start. Otherwise explains why and, if the session ended, asks for the name again.</summary>
        public static bool CanStartTest(FrameworkElement view, MainWindow shell, string saveBlockedReason, string testId = null, string department = null)
        {
            bool ok = CanStartCore(view, shell, saveBlockedReason);
            if (ok && testId != null)
            {
                try
                {
                    new BBTech.Core.Audit.AuditStore(AppPaths.AuditLogPath).Log(SettingsService.Store.CurrentOperator,
                        department == "QC" ? BBTech.Core.Audit.AuditActions.QcStarted : BBTech.Core.Audit.AuditActions.TestExecuted,
                        testId, null, null, department);
                }
                catch (Exception ex) { AppLog.Warn("Test audit failed: " + ex.Message); }
            }
            return ok;
        }

        private static bool CanStartCore(FrameworkElement view, MainWindow shell, string saveBlockedReason)
        {
            var owner = Window.GetWindow(view);
            if (SettingsService.Store.CurrentOperator == null)
            {
                Dialogs.Info(owner, "Select your name", "Your session has ended. Select your name to continue.");
                shell.ShowUserProfileSetup(allowCancel: false);
                return false;
            }
            if (saveBlockedReason != null)
            {
                Dialogs.Info(owner, "Results cannot be saved", saveBlockedReason);
                return false;
            }
            return true;
        }

        /// <summary>Runs a save; on refusal or failure tells the technician the result was NOT saved.</summary>
        public static bool TrySave(FrameworkElement view, Action save)
        {
            try
            {
                save();
                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
            {
                AppLog.Warn("Result not saved: " + ex.Message);
                Dialogs.Info(Window.GetWindow(view), "Result not saved", ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Error("Result save failed", ex);
                Dialogs.Info(Window.GetWindow(view), "Result not saved", "The result could not be saved. Please run the test again.\n\n" + ex.Message);
                return false;
            }
        }
    }
}
