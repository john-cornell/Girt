using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Girt
{
    public partial class App : System.Windows.Application
    {
        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        // Manual SplashScreen instead of the <SplashScreen> MSBuild build item: that item's
        // "autoClose" actually closes on the first UI-thread idle, not on MainWindow rendering -
        // any async startup work (even just awaiting a delay) makes the thread go idle almost
        // immediately, closing the splash near-instantly instead of after a real minimum
        // duration. Showing MainWindow immediately (no artificial delay to real startup) and
        // closing the splash ourselves after a guaranteed 1 second sidesteps that entirely.
        // App.xaml no longer sets StartupUri, since that would show MainWindow before this
        // splash even gets a chance to show first.
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Only one Girt instance should ever run - a second launch (double-clicking the
            // shortcut again, opening a repo from Explorer, etc.) should activate the existing
            // window instead of piling up duplicates. TryAcquireMutex returning false is a hard
            // OS error (rare); fail open rather than block startup entirely over it.
            if (Services.SingleInstanceService.TryAcquireMutex(out var ownsMutex) && !ownsMutex)
            {
                Services.SingleInstanceService.TryNotifyRunningInstance();
                Shutdown();
                return;
            }

            var splash = new SplashScreen("Assets/splash.png");
            splash.Show(false, true);

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            Services.SingleInstanceService.StartListeningForActivation(mainWindow.RestoreFromTray);

            mainWindow.Show();

            _ = CloseSplashAfterMinimumDurationAsync(splash);
        }

        private static async Task CloseSplashAfterMinimumDurationAsync(SplashScreen splash)
        {
            await Task.Delay(1000);
            splash.Close(TimeSpan.FromMilliseconds(300));
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogAndShowException(e.Exception, "Unhandled UI Exception");
            e.Handled = true;
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogAndShowException(ex, "Fatal Domain Exception");
            }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogAndShowException(e.Exception, "Unobserved Task Exception");
            e.SetObserved();
        }

        private static void LogAndShowException(Exception ex, string title)
        {
            try
            {
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Girt");
                Directory.CreateDirectory(appData);
                var logFile = Path.Combine(appData, "crash.log");
                var message = $"[{DateTime.UtcNow:O}] {title}\nException: {ex}\n\n";
                File.AppendAllText(logFile, message);

                MessageBox.Show($"An unexpected error occurred:\n\n{ex.Message}\n\nDetails logged to: {logFile}", title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                MessageBox.Show($"An unexpected error occurred:\n\n{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
