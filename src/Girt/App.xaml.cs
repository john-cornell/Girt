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
