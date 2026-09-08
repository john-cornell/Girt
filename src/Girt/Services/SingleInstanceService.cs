using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;

namespace Girt.Services
{
    /// <summary>Enforces one running Girt instance. A second launch finds the first via a named
    /// mutex, asks it (via a hidden message-only window + a registered window message) to
    /// activate itself, then exits immediately instead of opening a duplicate window. Mirrors
    /// the same proven pattern already used in Clipt (SingleInstanceActivation.cs).</summary>
    public static class SingleInstanceService
    {
        private const string MutexName = @"Global\Girt_SingleInstance_9F3B2A1C4D5E6F708192A3B4C5D6E7F1";
        private const string ActivationMessageName = "Girt_SecondInstanceActivate";
        private const string HiddenWindowTitle = "GirtHidden";

        private static Mutex? _mutex;
        private static HwndSource? _hiddenWindowSource;

        /// <summary>Tries to become the sole Girt instance. Returns false only on a hard OS
        /// error (rare) - "another instance is already running" is reported via ownsMutex=false,
        /// not a false return.</summary>
        public static bool TryAcquireMutex(out bool ownsMutex)
        {
            ownsMutex = false;
            try
            {
                _mutex = new Mutex(initiallyOwned: false, MutexName, out _);
                try
                {
                    ownsMutex = _mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    _mutex.Dispose();
                    _mutex = null;
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                _mutex?.Dispose();
                _mutex = null;
                return false;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                _mutex?.Dispose();
                _mutex = null;
                return false;
            }
        }

        /// <summary>Called by the primary instance once it owns the mutex, so a later launch can
        /// find it and ask it to activate instead of starting a duplicate.</summary>
        public static void StartListeningForActivation(Action onActivateRequested)
        {
            var parameters = new HwndSourceParameters(HiddenWindowTitle)
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };
            _hiddenWindowSource = new HwndSource(parameters);

            var messageId = NativeMethods.RegisterWindowMessage(ActivationMessageName);
            _hiddenWindowSource.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
            {
                if ((uint)msg == messageId)
                {
                    onActivateRequested();
                    handled = true;
                }
                return nint.Zero;
            });
        }

        /// <summary>Called by a second launch that lost the mutex race. Finds the primary
        /// instance's hidden window and asks it to activate; this process should exit right
        /// after, regardless of the result.</summary>
        public static bool TryNotifyRunningInstance()
        {
            var messageId = NativeMethods.RegisterWindowMessage(ActivationMessageName);
            if (messageId == 0) return false;

            for (var attempt = 0; attempt < 40; attempt++)
            {
                var hwnd = NativeMethods.FindWindow(null, HiddenWindowTitle);
                if (hwnd != nint.Zero)
                {
                    return NativeMethods.PostMessage(hwnd, messageId, nint.Zero, nint.Zero);
                }
                Thread.Sleep(50);
            }
            return false;
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern uint RegisterWindowMessage(string lpString);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern nint FindWindow(string? lpClassName, string? lpWindowName);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
        }
    }
}
