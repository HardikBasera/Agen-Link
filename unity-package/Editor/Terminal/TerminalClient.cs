using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AgenLink.Terminal
{
    /// <summary>TCP client to pty-host. The read loop runs on a background thread; all callbacks are
    /// marshalled onto the Unity main thread via MainThreadDispatcher.</summary>
    internal sealed class TerminalClient
    {
        public Action<byte[]> OnOutput;
        public Action<int> OnExit;
        public Action OnConnected;
        public Action OnDisconnected;

        private TcpClient _tcp;
        private NetworkStream _stream;
        private Thread _reader;
        private volatile bool _running;
        public bool Connected => _running && _tcp != null && _tcp.Connected;

        public bool Connect(int port, string token, out string error)
        {
            error = null;
            try
            {
                _tcp = new TcpClient();
                _tcp.Connect("127.0.0.1", port);
                _stream = _tcp.GetStream();
                var auth = TerminalProtocol.Encode(TerminalProtocol.AUTH, Encoding.UTF8.GetBytes(token));
                _stream.Write(auth, 0, auth.Length);
                _running = true;
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "AgenLink.Term" };
                _reader.Start();
                MainThreadDispatcher.RunAsync(() => OnConnected?.Invoke());
                return true;
            }
            catch (Exception e) { error = e.Message; Cleanup(); return false; }
        }

        private void ReadLoop()
        {
            var buf = new byte[16 * 1024];
            var dec = new TerminalProtocol.Decoder();
            try
            {
                while (_running)
                {
                    int n = _stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    foreach (var f in dec.Push(buf, n))
                    {
                        var frame = f;
                        if (frame.Type == TerminalProtocol.OUTPUT)
                            MainThreadDispatcher.RunAsync(() => OnOutput?.Invoke(frame.Payload));
                        else if (frame.Type == TerminalProtocol.EXIT)
                        {
                            int code = (frame.Payload[0] << 24) | (frame.Payload[1] << 16) | (frame.Payload[2] << 8) | frame.Payload[3];
                            MainThreadDispatcher.RunAsync(() => OnExit?.Invoke(code));
                        }
                    }
                }
            }
            catch { /* socket closed */ }
            finally { MainThreadDispatcher.RunAsync(() => OnDisconnected?.Invoke()); }
        }

        public void SendInput(byte[] bytes)
        {
            if (!Connected) return;
            try { var f = TerminalProtocol.Encode(TerminalProtocol.INPUT, bytes); _stream.Write(f, 0, f.Length); } catch { }
        }

        public void Resize(int cols, int rows)
        {
            if (!Connected) return;
            try { var f = TerminalProtocol.EncodeResize(cols, rows); _stream.Write(f, 0, f.Length); } catch { }
        }

        public void Disconnect() { _running = false; Cleanup(); }

        private void Cleanup()
        {
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null; _tcp = null;
        }
    }
}
