using System;
using System.Collections.Generic;

namespace AgenLink.Terminal
{
    /// <summary>Wire framing shared with pty-host/protocol.js: [1b type][4b BE length][payload].</summary>
    internal static class TerminalProtocol
    {
        public const byte AUTH = 0x00, INPUT = 0x01, RESIZE = 0x02, PING = 0x03;
        public const byte HELLO = 0x80, OUTPUT = 0x81, EXIT = 0x82, PONG = 0x83;

        public struct Frame { public byte Type; public byte[] Payload; }

        public static byte[] Encode(byte type, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            var buf = new byte[5 + payload.Length];
            buf[0] = type;
            buf[1] = (byte)(payload.Length >> 24);
            buf[2] = (byte)(payload.Length >> 16);
            buf[3] = (byte)(payload.Length >> 8);
            buf[4] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, buf, 5, payload.Length);
            return buf;
        }

        public static byte[] EncodeResize(int cols, int rows)
        {
            var p = new byte[4];
            p[0] = (byte)(cols >> 8); p[1] = (byte)cols; p[2] = (byte)(rows >> 8); p[3] = (byte)rows;
            return Encode(RESIZE, p);
        }

        public sealed class Decoder
        {
            private byte[] _buf = Array.Empty<byte>();
            private int _len;

            public List<Frame> Push(byte[] chunk, int count)
            {
                Append(chunk, count);
                var frames = new List<Frame>();
                int pos = 0;
                while (_len - pos >= 5)
                {
                    int plen = (_buf[pos + 1] << 24) | (_buf[pos + 2] << 16) | (_buf[pos + 3] << 8) | _buf[pos + 4];
                    if (_len - pos - 5 < plen) break;
                    var payload = new byte[plen];
                    Buffer.BlockCopy(_buf, pos + 5, payload, 0, plen);
                    frames.Add(new Frame { Type = _buf[pos], Payload = payload });
                    pos += 5 + plen;
                }
                Compact(pos);
                return frames;
            }

            private void Append(byte[] chunk, int count)
            {
                if (_len + count > _buf.Length)
                {
                    int cap = Math.Max(_buf.Length * 2, _len + count);
                    var nb = new byte[cap];
                    Buffer.BlockCopy(_buf, 0, nb, 0, _len);
                    _buf = nb;
                }
                Buffer.BlockCopy(chunk, 0, _buf, _len, count);
                _len += count;
            }

            private void Compact(int consumed)
            {
                if (consumed == 0) return;
                int remain = _len - consumed;
                if (remain > 0) Buffer.BlockCopy(_buf, consumed, _buf, 0, remain);
                _len = remain;
            }
        }
    }
}
