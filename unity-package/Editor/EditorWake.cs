using UnityEditor;
#if UNITY_EDITOR_WIN
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
#endif

namespace AgenLink
{
    /// <summary>
    /// Wakes the Unity Editor's OS message loop on demand. When the Editor is unfocused and idle — or has
    /// just finished a domain reload while in the background — it parks its main thread to save CPU, so the
    /// MainThreadDispatcher queue (and therefore every bridge request) stalls until the user clicks into
    /// Unity. No managed callback (EditorApplication.update, OnInspectorUpdate, delayCall) fires on a parked
    /// loop; only an OS event revives it. So whenever the bridge enqueues main-thread work we
    /// PostMessage(WM_NULL) to the editor window, which breaks its GetMessage wait and forces an editor tick
    /// that drains the queue. A short background pulse keeps ticking for a few seconds so a compile / domain
    /// reload triggered by that work (which needs sustained ticks, not a single wake) can run to completion.
    /// Windows-only for now; other platforms fall back to the old focus-to-refresh behavior.
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorWake
    {
#if UNITY_EDITOR_WIN
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private const uint WM_NULL = 0x0000;

        private static IntPtr _hwnd = IntPtr.Zero;
        private static readonly object _gate = new object();
        private static bool _pulsing;
        private static long _pulseUntilTicks;   // UtcNow.Ticks; the background pulse runs until this time

        static EditorWake()
        {
            // Grab the handle once the editor UI is up. delayCall runs on the main thread, but the lookup
            // itself (Process.MainWindowHandle, Win32 EnumWindows) is thread-safe, so Nudge can also refresh
            // it from the bridge's background thread without touching any Unity API.
            EditorApplication.delayCall += CaptureHandle;
        }

        private static void CaptureHandle()
        {
            try { _hwnd = Process.GetCurrentProcess().MainWindowHandle; } catch { _hwnd = IntPtr.Zero; }
        }

        /// <summary>
        /// Wake the editor now and keep nudging for <paramref name="sustainMs"/> so a compile / domain reload
        /// kicked off by the queued work can finish. Safe to call from any thread.
        /// </summary>
        public static void Nudge(int sustainMs = 4000)
        {
            if (_hwnd == IntPtr.Zero)
                CaptureHandle(); // thread-safe Win32 lookup — no Unity API, OK from the background thread

            long until = DateTime.UtcNow.AddMilliseconds(sustainMs).Ticks;
            lock (_gate)
            {
                if (until > _pulseUntilTicks) _pulseUntilTicks = until;
                if (!_pulsing)
                {
                    _pulsing = true;
                    var t = new Thread(PulseLoop) { IsBackground = true, Name = "AgenLink.Wake" };
                    t.Start();
                }
            }
            Post();
        }

        private static void PulseLoop()
        {
            while (true)
            {
                lock (_gate)
                {
                    if (DateTime.UtcNow.Ticks >= _pulseUntilTicks) { _pulsing = false; return; }
                }
                Post();
                Thread.Sleep(100);
            }
        }

        private static void Post()
        {
            var h = _hwnd;
            if (h != IntPtr.Zero)
                try { PostMessage(h, WM_NULL, IntPtr.Zero, IntPtr.Zero); } catch { /* window gone */ }
        }
#else
        static EditorWake() { }
        public static void Nudge(int sustainMs = 4000) { /* non-Windows: editor focus drives refresh */ }
#endif
    }
}
