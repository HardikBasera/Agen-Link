#if AGENLINK_HAS_TESTFRAMEWORK
using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace AgenLink.Ops
{
    /// <summary>
    /// Runs Unity tests through the Test Runner API. The run's state lives in AgenLink~/tests-run.json, NOT in
    /// memory, because a PlayMode run reloads the domain mid-flight and would otherwise lose in-memory results;
    /// the callbacks are re-registered on every load so post-reload results are still captured. Long-running,
    /// so it follows the start -> poll status -> report pattern.
    /// </summary>
    internal static class TestRunnerOps
    {
        private static string StateFile => Path.Combine(ConfigBuilder.ProjectRoot(), "AgenLink~", "tests-run.json");

        [InitializeOnLoadMethod]
        private static void RegisterPersistentCallbacks()
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks());
        }

        public static string Handle(CommandHandlers.RequestParams p)
        {
            switch ((p.action ?? "status").ToLowerInvariant())
            {
                case "start": return Start(p);
                case "status": return Status();
                case "report": return Report();
                default: throw new Exception("run_tests action must be 'start', 'status', or 'report'");
            }
        }

        private static string Start(CommandHandlers.RequestParams p)
        {
            JObject prev = ReadState();
            if (prev != null && (string)prev["status"] == "running")
                throw new Exception("a test run is already in progress — poll {action:'status'} until finished:true");

            TestMode mode = string.Equals(p.mode, "PlayMode", StringComparison.OrdinalIgnoreCase) ? TestMode.PlayMode : TestMode.EditMode;
            var filter = new Filter { testMode = mode };
            if (!string.IsNullOrEmpty(p.testFilter)) filter.groupNames = new[] { p.testFilter };

            WriteState(new JObject
            {
                ["status"] = "running",
                ["mode"] = mode.ToString(),
                ["filter"] = p.testFilter ?? "",
                ["passed"] = 0,
                ["failed"] = 0,
                ["skipped"] = 0,
                ["failures"] = new JArray(),
            });

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.Execute(new ExecutionSettings(filter));
            return new JObj().S("status", "started").S("mode", mode.ToString()).S("filter", p.testFilter ?? "").Build();
        }

        private static string Status()
        {
            JObject s = ReadState();
            if (s == null)
                return new JObj().S("status", "idle").B("finished", false).B("isCompiling", EditorApplication.isCompiling).Build();
            string status = (string)s["status"];
            return new JObj()
                .S("status", status)
                .B("finished", status == "finished")
                .B("isCompiling", EditorApplication.isCompiling)
                .N("passed", (long)(s["passed"] ?? 0))
                .N("failed", (long)(s["failed"] ?? 0))
                .N("skipped", (long)(s["skipped"] ?? 0))
                .Build();
        }

        private static string Report()
        {
            JObject s = ReadState();
            if (s == null) throw new Exception("no test run found — start one with {action:'start'}");
            if ((string)s["status"] != "finished") throw new Exception("the test run has not finished — poll {action:'status'} until finished:true");
            return s.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JObject ReadState()
        {
            try { return File.Exists(StateFile) ? JObject.Parse(File.ReadAllText(StateFile)) : null; }
            catch { return null; }
        }

        private static void WriteState(JObject state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
            File.WriteAllText(StateFile, state.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
        }

        /// <summary>Registered on every domain load, so a PlayMode run's results (delivered after the reload)
        /// are still written to the state file.</summary>
        private class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                int passed = 0, failed = 0, skipped = 0;
                var failures = new JArray();
                Walk(result, ref passed, ref failed, ref skipped, failures);

                JObject state = ReadState() ?? new JObject();
                state["status"] = "finished";
                state["passed"] = passed;
                state["failed"] = failed;
                state["skipped"] = skipped;
                state["failures"] = failures;
                try { WriteState(state); } catch { /* best effort */ }
            }

            private void Walk(ITestResultAdaptor r, ref int passed, ref int failed, ref int skipped, JArray failures)
            {
                if (r.Children == null || !r.Children.Any())
                {
                    switch (r.TestStatus)
                    {
                        case TestStatus.Passed: passed++; break;
                        case TestStatus.Failed:
                            failed++;
                            failures.Add(new JObject
                            {
                                ["test"] = r.Test != null ? r.Test.FullName : r.Name,
                                ["message"] = Trunc(r.Message, 1000),
                                ["stack"] = Trunc(r.StackTrace, 2000),
                            });
                            break;
                        default: skipped++; break;
                    }
                    return;
                }
                foreach (ITestResultAdaptor c in r.Children) Walk(c, ref passed, ref failed, ref skipped, failures);
            }

            private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length > n ? s.Substring(0, n) : s);
        }
    }
}
#else
using System;

namespace AgenLink.Ops
{
    /// <summary>Stub used when com.unity.test-framework is not installed — the real implementation is gated
    /// behind the AGENLINK_HAS_TESTFRAMEWORK versionDefine on the assembly.</summary>
    internal static class TestRunnerOps
    {
        public static string Handle(CommandHandlers.RequestParams p) =>
            throw new Exception("the Unity Test Framework package (com.unity.test-framework) is not installed — install it to use agen_run_tests");
    }
}
#endif
