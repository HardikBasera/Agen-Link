using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgenLink
{
    /// <summary>
    /// Captures Unity console log messages into a bounded ring buffer so the MCP `read_console` tool can
    /// report recent logs/warnings/errors without a live profiler. Uses the threaded callback so it also
    /// catches logs emitted off the main thread.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConsoleCapture
    {
        public struct Entry
        {
            public string Type;     // LogType: Error, Assert, Warning, Log, Exception
            public string Message;
            public string Stack;
        }

        private const int Capacity = 500;
        private static readonly List<Entry> Buffer = new List<Entry>(Capacity + 8);
        private static readonly object Lock = new object();

        static ConsoleCapture()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            lock (Lock)
            {
                Buffer.Add(new Entry { Type = type.ToString(), Message = condition, Stack = stackTrace });
                if (Buffer.Count > Capacity)
                    Buffer.RemoveRange(0, Buffer.Count - Capacity);
            }
        }

        /// <summary>Oldest-to-newest copy of the buffer.</summary>
        public static List<Entry> Snapshot()
        {
            lock (Lock) { return new List<Entry>(Buffer); }
        }

        public static void Clear()
        {
            lock (Lock) { Buffer.Clear(); }
        }
    }
}
