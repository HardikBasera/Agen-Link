using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AgenLink
{
    /// <summary>
    /// Marshals work onto Unity's main thread. The TCP bridge runs on background threads, but almost every
    /// Unity API (AssetDatabase, scene, Selection, EditorApplication state) must be touched on the main
    /// thread. Background callers enqueue a function via <see cref="RunAsync{T}"/> and block on the returned
    /// Task; the queue is drained by <see cref="Pump"/>, driven from <see cref="EditorApplication.update"/>.
    /// EditorApplication.update parks when the editor is unfocused or just after a domain reload, which would
    /// strand bridge requests until the user clicks into Unity — so the Agen-Link window also calls
    /// <see cref="Pump"/> from OnInspectorUpdate (which fires ~10x/sec regardless of focus) to keep draining.
    /// </summary>
    [InitializeOnLoad]
    internal static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        static MainThreadDispatcher()
        {
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
        }

        /// <summary>
        /// Drain queued main-thread work. Called from <see cref="EditorApplication.update"/> and, so the
        /// bridge stays responsive while the editor is unfocused / after a domain reload, from the Agen-Link
        /// window's OnInspectorUpdate. Both callers are main-thread editor callbacks, so they never overlap.
        /// </summary>
        public static void Pump()
        {
            // Bound work per frame so a flood of requests can't stall the editor; the rest run next frame.
            int processed = 0;
            while (processed < 64 && Queue.TryDequeue(out var action))
            {
                processed++;
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        public static Task<T> RunAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();
            Queue.Enqueue(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception e) { tcs.SetException(e); }
            });
            EditorWake.Nudge(); // a parked/backgrounded editor won't drain this on its own
            return tcs.Task;
        }

        public static Task RunAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();
            Queue.Enqueue(() =>
            {
                try { action(); tcs.SetResult(true); }
                catch (Exception e) { tcs.SetException(e); }
            });
            EditorWake.Nudge(); // a parked/backgrounded editor won't drain this on its own
            return tcs.Task;
        }

        /// <summary>
        /// Like <see cref="RunAsync{T}"/> but for work that is itself asynchronous — the function is invoked
        /// on the main thread and may return a Task that completes on a *later* editor frame (e.g. a
        /// background compile). The main thread is not blocked while that inner task is pending; only the
        /// caller awaiting the returned Task waits. The inner task's completion is bridged to the returned one.
        /// </summary>
        public static Task<T> RunAsyncTask<T>(Func<Task<T>> func)
        {
            var tcs = new TaskCompletionSource<T>();
            Queue.Enqueue(() =>
            {
                try
                {
                    func().ContinueWith(t =>
                    {
                        if (t.IsFaulted) tcs.SetException(t.Exception.InnerExceptions);
                        else if (t.IsCanceled) tcs.SetCanceled();
                        else tcs.SetResult(t.Result);
                    }, TaskContinuationOptions.ExecuteSynchronously);
                }
                catch (Exception e) { tcs.SetException(e); }
            });
            EditorWake.Nudge(); // a parked/backgrounded editor won't drain this on its own
            return tcs.Task;
        }
    }
}
