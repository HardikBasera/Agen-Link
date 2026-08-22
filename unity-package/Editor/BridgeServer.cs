using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AgenLink
{
    /// <summary>
    /// Localhost TCP listener that lets the external MCP server query the live Editor. Newline-delimited
    /// compact JSON: one request line in, one response line out. Each request is executed on the main thread
    /// via <see cref="MainThreadDispatcher"/>. Auto-starts on load and restarts itself after every domain
    /// reload (recompile), so it's always available once the package is installed.
    /// </summary>
    [InitializeOnLoad]
    internal static class BridgeServer
    {
        private static TcpListener _listener;
        private static Thread _acceptThread;
        private static volatile bool _running;
        private static int _activePort = -1;

        // Accepted client connections, tracked so Stop() can close them. An MCP client (kept alive by a
        // running terminal session) otherwise holds the port open past Stop(), so the rebind on the next
        // domain reload / "Restart bridge" fails ("access forbidden" or "address already in use").
        private static readonly List<TcpClient> _clients = new List<TcpClient>();
        private static readonly object _clientsLock = new object();

        public static bool IsRunning => _running;
        public static int ActivePort => _activePort;

        /// <summary>
        /// How long a socket thread waits for Unity's main thread before giving up. Kept just under the MCP
        /// client's own per-request timeout so the caller gets our diagnostic message rather than a bare
        /// client-side timeout. A const, not a setting: BridgeSettings reads EditorPrefs, which is main-thread
        /// only and therefore unusable from here.
        /// </summary>
        private const int MainThreadTimeoutMs = 12000;

        // Health check: re-bind whenever the listener or its accept thread has gone away.
        private const double HealthCheckIntervalSec = 3.0;
        private const double BindErrorLogIntervalSec = 30.0;
        private static double _nextHealthCheckAt;
        private static double _lastBindErrorAt = -1000.0;

        /// <summary>
        /// Runs on every editor tick (throttled). The bridge has now failed three distinct ways — a restart
        /// hook that never fired, a socket orphaned across a domain reload, and an accept thread that outlived
        /// its domain — and in every case the listener was gone or wedged while nothing noticed. Rather than
        /// keep adding one-shot hooks for each new way it can die, this simply asserts the invariant every few
        /// seconds: the listener is bound AND its accept thread is alive. If not, rebuild it.
        /// </summary>
        private static void HealthTick()
        {
            if (EditorApplication.timeSinceStartup < _nextHealthCheckAt) return;
            _nextHealthCheckAt = EditorApplication.timeSinceStartup + HealthCheckIntervalSec;

            if (_running && _listener != null && _acceptThread != null && _acceptThread.IsAlive) return;

            // Half-dead (bound but no accept thread, or flagged running with no listener): tear the remains
            // down first so Stop() closes the socket and joins the thread, then rebind cleanly.
            if (_running || _listener != null) Stop();
            Start();
        }

        /// <summary>
        /// Says which half is actually stuck. If the editor loop is still ticking, the command itself is slow;
        /// if it is not, Unity is blocked or parked and no amount of reconnecting will help.
        /// </summary>
        private static string MainThreadTimeoutMessage()
        {
            long idleMs = MainThreadDispatcher.MsSinceLastPump;
            if (idleMs < CommandHandlers.MainThreadStallMs)
                return $"Agen-Link: this command did not finish within {MainThreadTimeoutMs}ms, but Unity's " +
                       $"editor loop IS ticking (last tick {idleMs}ms ago, {MainThreadDispatcher.QueueDepth} " +
                       "queued). The bridge and the Editor are both healthy — this specific command is slow " +
                       "or stuck. Retry it, or try a narrower request.";

            return $"Agen-Link: Unity's main thread has not ticked for {idleMs}ms, so it is blocked (asset " +
                   "import, shader compile, modal dialog or progress bar) or the editor is parked. The bridge " +
                   "itself is fine — call agen_ping to confirm; it answers without the main thread. Clicking " +
                   "the Editor will NOT help if it is blocked: look at the Unity window, a progress bar there " +
                   "names the operation. Wait for it to clear and retry — do not switch to writing editor scripts.";
        }

        static BridgeServer()
        {
            // Asset Import Workers (-adb2 -batchMode) and other secondary processes load the editor
            // domain and run [InitializeOnLoad] too. Only the main Editor process should own the
            // listener socket; otherwise each worker fights the main process for the port and logs
            // "Only one usage of each socket address…" after every recompile. Bail out in workers.
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;

            // delayCall ensures EditorPrefs / other statics are ready before we read the port.
            EditorApplication.delayCall += Start;

            // ...but delayCall needs an editor TICK to fire, and an unfocused/parked editor does not tick.
            // A domain reload that lands while the editor is in the background therefore leaves the listener
            // dead until the user clicks into Unity — silently, because Start (which logs on both success and
            // failure) is never even called. Confirmed in Editor.log: 4 domain reloads, 2 "Listening" lines,
            // 0 errors; the dead window ran until the *next* recompile happened to land with the editor
            // focused. EditorWake cannot rescue it either — it only nudges when MainThreadDispatcher receives
            // work, and with no listener nothing ever connects to enqueue any.
            //
            // afterAssemblyReload fires as part of the reload itself (right after InitializeOnLoad), so it
            // does not depend on a tick. Start is idempotent, so whichever hook fires first wins.
            AssemblyReloadEvents.afterAssemblyReload += Start;

            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;

            // Backstop for every failure mode we have not thought of yet — see HealthTick.
            EditorApplication.update -= HealthTick;
            EditorApplication.update += HealthTick;
        }

        public static void Restart()
        {
            Stop();
            Start();
        }

        public static void Start()
        {
            if (_running) return;
            int port = BridgeSettings.Port;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);

                // NOT SO_REUSEADDR. On Windows that option lets a socket bind over an *actively listening*
                // one (unlike Unix, where it only covers TIME_WAIT), and which of the two then receives
                // connections is undefined. Verified on this machine: a second listener binds over Unity's
                // live one and succeeds. That turned a leaked accept thread into a silent catastrophe — the
                // orphan kept the port, the new Start bound a second socket and logged "Listening", the
                // kernel handed connections to the orphan, and every request was accepted by stale code that
                // answered nothing. Symptom: port LISTENING, connects fine, even agen_ping times out, and
                // CLOSE_WAIT piles up.
                //
                // ExclusiveAddressUse makes that collision an outright bind failure instead, which we log and
                // the health check retries. Loud and recoverable beats silent and wedged.
                _listener.ExclusiveAddressUse = true;
                _listener.Start();
                _activePort = port;
                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "AgenLink.Accept" };
                _acceptThread.Start();
                Debug.Log($"[Agen-Link] Listening on 127.0.0.1:{port} (bg-wake enabled)");
            }
            catch (Exception e)
            {
                _running = false;
                _activePort = -1;
                _listener = null;
                // The health check retries every few seconds, so a transient collision (the previous socket
                // not yet released after a reload) recovers on its own. Log sparingly so that retry loop
                // cannot spam the Console, but always log the first failure.
                if (EditorApplication.timeSinceStartup - _lastBindErrorAt > BindErrorLogIntervalSec)
                {
                    _lastBindErrorAt = EditorApplication.timeSinceStartup;
                    Debug.LogError($"[Agen-Link] Failed to start on port {port}: {e.Message}. " +
                                   "Retrying every few seconds. If it persists, another Editor may hold the " +
                                   "port — change it in the Agen-Link ▸ Settings tab.");
                }
            }
        }

        public static void Stop()
        {
            if (!_running && _listener == null) return;
            _running = false;

            // Close the underlying socket as well as the listener. Stop() alone has been observed to leave
            // the accept thread parked in AcceptTcpClient; that thread then survives the domain reload (Unity
            // does not abort managed background threads), keeps the port bound, and services later
            // connections with stale code. Closing the socket guarantees AcceptTcpClient throws and the loop
            // exits.
            try { _listener?.Server?.Close(); } catch { /* ignored */ }
            try { _listener?.Stop(); } catch { /* ignored */ }
            _listener = null;

            // Do not return until the accept thread is actually gone, so it cannot outlive this domain.
            try { if (_acceptThread != null && _acceptThread.IsAlive) _acceptThread.Join(1000); }
            catch { /* ignored */ }
            _acceptThread = null;
            // Close live client connections too (e.g. the MCP server kept alive by a running terminal
            // session). Closing only the listener leaves the port held by these, so the next rebind
            // fails. Each HandleClient thread removes itself from the list as its socket tears down.
            lock (_clientsLock)
            {
                foreach (var c in _clients) { try { c.Close(); } catch { /* ignored */ } }
                _clients.Clear();
            }
            _activePort = -1;
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { break; } // listener stopped / disposed
                lock (_clientsLock) _clients.Add(client);
                var t = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "AgenLink.Client" };
                t.Start();
            }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
                {
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        string response;

                        // Health probe answered right here, on the socket thread, touching no Unity API — so
                        // it still replies while the main thread is wedged. That is what tells a caller
                        // "the bridge is alive, Unity is busy" instead of leaving it to guess.
                        if (CommandHandlers.TryHandleOffMainThread(line, out response))
                        {
                            writer.WriteLine(response);
                            continue;
                        }

                        try
                        {
                            // Hop to the main thread to touch Unity APIs, then wait for the result.
                            // DispatchAsync lets a command span multiple editor frames (its Task completes on
                            // a later frame) without holding up the main thread meanwhile.
                            var task = MainThreadDispatcher.RunAsyncTask(() => CommandHandlers.DispatchAsync(line));

                            // BOUNDED wait. This used to be an unconditional GetResult(), which blocked this
                            // thread forever whenever the main thread stalled: the socket was never disposed,
                            // so it sat in CLOSE_WAIT, and every retry leaked another thread and another
                            // socket. Waiting on the handle (rather than Task.Wait) does not wrap a faulted
                            // task in an AggregateException, so GetResult below still surfaces the original.
                            if (((IAsyncResult)task).AsyncWaitHandle.WaitOne(MainThreadTimeoutMs))
                                response = task.GetAwaiter().GetResult();
                            else
                                response = CommandHandlers.Error(null, MainThreadTimeoutMessage());
                        }
                        catch (Exception e)
                        {
                            response = CommandHandlers.Error(null, e.Message);
                        }
                        writer.WriteLine(response);
                    }
                }
            }
            catch
            {
                // Client disconnected or domain reload tore us down; nothing to do.
            }
            finally
            {
                lock (_clientsLock) _clients.Remove(client);
            }
        }
    }
}
