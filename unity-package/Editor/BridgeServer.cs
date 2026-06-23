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

        static BridgeServer()
        {
            // Asset Import Workers (-adb2 -batchMode) and other secondary processes load the editor
            // domain and run [InitializeOnLoad] too. Only the main Editor process should own the
            // listener socket; otherwise each worker fights the main process for the port and logs
            // "Only one usage of each socket address…" after every recompile. Bail out in workers.
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;

            // delayCall ensures EditorPrefs / other statics are ready before we read the port.
            EditorApplication.delayCall += Start;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
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
                // Stop() closes the accepted connections, but their server sockets linger briefly in
                // TIME_WAIT; SO_REUSEADDR lets the fresh listener bind over those instead of failing
                // with "address already in use" on the next domain reload / restart.
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
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
                Debug.LogError($"[Agen-Link] Failed to start on port {port}: {e.Message}. " +
                               "Another Editor may be using it — change the port in the Agen-Link ▸ Settings tab.");
            }
        }

        public static void Stop()
        {
            if (!_running && _listener == null) return;
            _running = false;
            try { _listener?.Stop(); } catch { /* ignored */ }
            _listener = null;
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
                        try
                        {
                            // Hop to the main thread to touch Unity APIs, then block this socket thread for the result.
                            response = MainThreadDispatcher.RunAsync(() => CommandHandlers.Dispatch(line))
                                                            .GetAwaiter().GetResult();
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
