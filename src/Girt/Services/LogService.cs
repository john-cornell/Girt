using System;
using System.IO;

namespace Girt.Services
{
    /// <summary>Lightweight file logger for diagnosing slow repos and silent git failures after
    /// the fact, without needing a repro in front of the developer. Two things are always
    /// logged regardless of settings - an exception from a git invocation, and any single git
    /// command slow enough to feel like lag (SlowThresholdMs) - since those are exactly what
    /// you'd want in the log the one time something actually goes wrong. Per-command timing for
    /// every git call, slow or not, is much noisier and only useful when actively investigating
    /// performance, so that's gated behind EnableTimingLogs (Settings > Diagnostics).</summary>
    public static class LogService
    {
        public const int SlowThresholdMs = 750;

        private static readonly object _lock = new();

        public static bool EnableTimingLogs { get; set; }

        public static string LogFilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Girt", "girt.log");

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message, Exception ex) => Write("ERROR", $"{message}\n{ex}");

        /// <summary>Call after any timed operation. Logs a WARN unconditionally once the
        /// operation crosses SlowThresholdMs; otherwise logs an INFO only if EnableTimingLogs is
        /// on, so normal operation doesn't fill the log with a line per git invocation.</summary>
        public static void Timing(string operation, long elapsedMs)
        {
            if (elapsedMs >= SlowThresholdMs)
            {
                Write("WARN", $"SLOW ({elapsedMs}ms): {operation}");
            }
            else if (EnableTimingLogs)
            {
                Write("INFO", $"{operation} took {elapsedMs}ms");
            }
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    var dir = Path.GetDirectoryName(LogFilePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.AppendAllText(LogFilePath, $"[{DateTime.UtcNow:O}] [{level}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never be the reason the app breaks.
            }
        }
    }
}
