using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Girt.Services
{
    public static class ClipboardHelper
    {
        // The Windows clipboard is a single shared, cross-process resource: any other app
        // (clipboard managers, RDP, antivirus scanners) can hold it open for a few
        // milliseconds, which makes OpenClipboard - and so Clipboard.SetText - fail
        // transiently with CLIPBRD_E_CANT_OPEN (0x800401D0). This can happen even after the
        // text has already landed on the clipboard: SetText's own internal flush-to-persist
        // step is a second, separate Open/Close that can lose the same race. Retry briefly
        // before giving up, rather than letting a transient failure surface as a crash dialog
        // for an operation that (from the user's perspective) already worked.
        public static bool TrySetText(string text)
        {
            const int maxAttempts = 10;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch (COMException)
                {
                    // Catch every attempt, including the last - an uncaught final attempt
                    // escapes straight to WPF's global unhandled-exception handler as a crash
                    // dialog instead of falling through to the graceful `return false` below.
                    if (attempt < maxAttempts)
                    {
                        Thread.Sleep(50);
                    }
                }
            }

            return false;
        }
    }
}
