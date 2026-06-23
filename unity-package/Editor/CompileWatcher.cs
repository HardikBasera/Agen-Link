using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;

namespace AgenLink
{
    /// <summary>
    /// Collects C# compiler errors/warnings. These are NOT routed through Application.logMessageReceived,
    /// so we hook the compilation pipeline directly. When a compile fails, Unity keeps the current domain
    /// loaded (no reload), so this static buffer survives and `get_compile_errors` reports the real errors.
    /// </summary>
    [InitializeOnLoad]
    internal static class CompileWatcher
    {
        public struct Msg
        {
            public string Type;     // Error | Warning
            public string Message;
            public string File;
            public int Line;
        }

        private static readonly List<Msg> Messages = new List<Msg>();
        private static readonly object Lock = new object();

        static CompileWatcher()
        {
            CompilationPipeline.compilationStarted -= OnStarted;
            CompilationPipeline.compilationStarted += OnStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
        }

        private static void OnStarted(object context)
        {
            lock (Lock) { Messages.Clear(); }
        }

        private static void OnAssemblyFinished(string assemblyPath, CompilerMessage[] messages)
        {
            lock (Lock)
            {
                foreach (var m in messages)
                {
                    if (m.type == CompilerMessageType.Error || m.type == CompilerMessageType.Warning)
                    {
                        Messages.Add(new Msg
                        {
                            Type = m.type.ToString(),
                            Message = m.message,
                            File = m.file,
                            Line = m.line
                        });
                    }
                }
            }
        }

        public static List<Msg> Snapshot()
        {
            lock (Lock) { return new List<Msg>(Messages); }
        }
    }
}
