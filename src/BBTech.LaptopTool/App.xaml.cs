using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.UI;

namespace BBTech.LaptopTool
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppPaths.Init();
            AppLog.Init(AppPaths.LogsDir);
            AppLog.Info("==== BBTech Laptop Tool " + AppPaths.Version + " started from " +
                        (AppPaths.IsRunningFromRemovableDrive ? "removable drive" : "fixed drive") +
                        (AppPaths.UsingFallbackStorage ? " (USB not writable, using local data folder)" : "") +
                        " | " + OsName() + (Environment.Is64BitProcess ? " x64" : " x86"));

            // Never crash with a raw error dialog: log and show a friendly message.
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, a) => AppLog.Error("Unhandled exception", a.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, a) => { AppLog.Error("Unobserved task exception", a.Exception); a.SetObserved(); };

            base.OnStartup(e);

            // Windows 10/11 x64 are the supported targets; older systems get a clear notice.
            if (Environment.OSVersion.Version.Major < 10)
                MessageBox.Show("BBTech Laptop Tool supports Windows 10 and Windows 11.\nSome features may not work on this version of Windows.",
                                "BBTech Laptop Tool", MessageBoxButton.OK, MessageBoxImage.Information);

            // Only the UI is loaded here: no scans, no hardware access, no tool extraction.
            new MainWindow().Show();
        }

        private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Error("UI exception", e.Exception);
            MessageBox.Show("Something went wrong, but the tool is still running.\n\n" + Friendly(e.Exception) +
                            "\n\nDetails were written to the log (BBTech Data\\logs\\app.log).", "BBTech Laptop Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        public static string OsName()
        {
            var v = Environment.OSVersion.Version; // correct thanks to the Windows 10 manifest entry
            string name = v.Major == 10 ? (v.Build >= 22000 ? "Windows 11" : "Windows 10") : "Windows " + v.Major + "." + v.Minor;
            return name + " (build " + v.Build + ")";
        }

        private static string Friendly(Exception ex) =>
            ex is InvalidOperationException && !string.IsNullOrEmpty(ex.Message) ? ex.Message : "The last action could not be completed.";

        protected override void OnExit(ExitEventArgs e)
        {
            AppLog.Info("==== Application closed");
            ToolBundle.Cleanup(); // remove extracted tools from %TEMP%
            base.OnExit(e);
        }
    }
}


