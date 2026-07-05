using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace AgenLink.Ops
{
    /// <summary>
    /// Compiles and runs a short C# snippet inside the Editor without touching Assets or triggering a domain
    /// reload, using UnityEditor.Compilation.AssemblyBuilder (the project's own Roslyn). OFF by default — it is
    /// a real capability surface, gated behind the Agen-Link ▸ Settings toggle. Async: the compile finishes on
    /// a later editor frame, and the result is bridged back to the waiting socket thread without blocking the
    /// main thread meanwhile.
    /// </summary>
    internal static class CodeExecutor
    {
        private static bool _busy;

        public static Task<string> ExecuteAsync(string code)
        {
            if (!BridgeSettings.AllowCodeExecution)
                throw new Exception("agen_execute_code is disabled — ask the user to enable 'Allow code execution' in Agen-Link ▸ Settings, then retry.");
            if (string.IsNullOrWhiteSpace(code))
                throw new Exception("execute_code requires a non-empty code snippet");
            if (EditorApplication.isCompiling)
                throw new Exception("the editor is compiling — wait for it to finish, then retry");
            if (_busy)
                throw new Exception("a previous code execution is still running — retry shortly");

            _busy = true;
            var tcs = new TaskCompletionSource<string>();
            try { StartBuild(code, tcs); }
            catch (Exception e) { _busy = false; tcs.SetException(e); }
            return tcs.Task;
        }

        private static void StartBuild(string code, TaskCompletionSource<string> tcs)
        {
            string stamp = Guid.NewGuid().ToString("N").Substring(0, 8);
            string dir = Path.Combine(ConfigBuilder.ProjectRoot(), "AgenLink~", "codeexec");
            Directory.CreateDirectory(dir);
            string cs = Path.Combine(dir, "exec_" + stamp + ".cs");
            string dll = Path.Combine(dir, "exec_" + stamp + ".dll");
            string className = "__AgenLinkExec_" + stamp;
            File.WriteAllText(cs, WrapCode(code, className), new UTF8Encoding(false));

            var builder = new AssemblyBuilder(dll, cs)
            {
                flags = AssemblyBuilderFlags.EditorAssembly,
                referencesOptions = ReferencesOptions.UseEngineModules,
            };
            var refs = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Select(a => a.outputPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
            refs.Add(typeof(EditorApplication).Assembly.Location);
            builder.additionalReferences = refs.Distinct().ToArray();

            DateTime startedAt = DateTime.UtcNow;
            bool settled = false;
            var gate = new object();
            void Settle(Action complete) { lock (gate) { if (settled) return; settled = true; _busy = false; complete(); } }

            builder.buildFinished += (path, messages) =>
            {
                try
                {
                    var errors = messages.Where(m => m.type == CompilerMessageType.Error).Select(m => m.message).ToList();
                    if (errors.Count > 0)
                    {
                        Settle(() => tcs.SetException(new Exception("compile error:\n" + string.Join("\n", errors))));
                        return;
                    }
                    string result = RunAssembly(dll, className, startedAt, messages);
                    Settle(() => tcs.SetResult(result));
                }
                catch (Exception e) { Settle(() => tcs.SetException(e)); }
                finally { TryDelete(cs, dll); }
            };

            if (!builder.Build())
            {
                Settle(() => tcs.SetException(new Exception("could not start the compile (editor busy) — retry shortly")));
                return;
            }

            // Watchdog: fail the request if the compile/run never reports back within 12s.
            void Watch()
            {
                if (settled) { EditorApplication.update -= Watch; return; }
                if ((DateTime.UtcNow - startedAt).TotalSeconds > 12)
                {
                    EditorApplication.update -= Watch;
                    Settle(() => tcs.SetException(new Exception("code execution timed out after 12s")));
                }
            }
            EditorApplication.update += Watch;
        }

        private static string RunAssembly(string dll, string className, DateTime startedAt, CompilerMessage[] messages)
        {
            var logs = new List<string>();
            Application.LogCallback cb = (msg, stack, type) => logs.Add(type + ": " + msg);
            Application.logMessageReceived += cb;
            try
            {
                Assembly asm = Assembly.Load(File.ReadAllBytes(dll));
                Type t = asm.GetType(className);
                MethodInfo run = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                object ret;
                try { ret = run.Invoke(null, null); }
                catch (TargetInvocationException tie)
                {
                    Exception inner = tie.InnerException ?? tie;
                    throw new Exception("runtime exception: " + inner.Message +
                                        (logs.Count > 0 ? " | logs: " + string.Join(" / ", logs) : ""));
                }

                int warnings = messages.Count(m => m.type == CompilerMessageType.Warning);
                var logElems = logs.Select(Json.Str);
                return new JObj()
                    .Raw("returnValue", ret == null ? "null" : Json.Str(ret.ToString()))
                    .Raw("logs", Json.Arr(logElems))
                    .N("durationMs", (long)(DateTime.UtcNow - startedAt).TotalMilliseconds)
                    .N("compileWarnings", warnings)
                    .Build();
            }
            finally { Application.logMessageReceived -= cb; }
        }

        private static string WrapCode(string code, string className)
        {
            // Hoist any leading "using ...;" lines to file scope; the remainder becomes the Run() body.
            var usings = new StringBuilder();
            var body = new StringBuilder();
            bool inHeader = true;
            foreach (string raw in code.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = raw.Trim();
                if (inHeader && trimmed.StartsWith("using ") && trimmed.EndsWith(";")) usings.AppendLine(trimmed);
                else { inHeader = false; body.AppendLine(raw); }
            }
            return
                "using System;\nusing System.Linq;\nusing System.Collections;\nusing System.Collections.Generic;\n" +
                "using UnityEngine;\nusing UnityEditor;\nusing UnityEngine.SceneManagement;\nusing UnityEditor.SceneManagement;\n" +
                usings +
                "public static class " + className + " {\n" +
                "  public static object Run() {\n" +
                body +
                "\n    return null;\n" +
                "  }\n}\n";
        }

        private static void TryDelete(params string[] paths)
        {
            foreach (string p in paths) { try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ } }
        }
    }
}
