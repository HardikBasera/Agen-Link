using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AgenLink.Analysis
{
    /// <summary>One interpreted conclusion from a perf recording.</summary>
    internal sealed class PerfVerdict
    {
        public string Severity;   // "ok" | "warn" | "critical"
        public string Text;
    }

    /// <summary>
    /// Turns a PerfRecorder stats report into plain-language conclusions against the same mobile/VR
    /// budgets the audit uses (SceneAuditRules). Pure string-in/verdicts-out so it's unit-testable.
    /// </summary>
    internal static class PerfAssessment
    {
        public static List<PerfVerdict> Build(string statsJson)
        {
            var v = new List<PerfVerdict>();
            JObject o;
            try { o = JObject.Parse(statsJson); } catch { return v; }

            double? frameAvg = Num(o, "mainThreadMs", "avg"), frameP95 = Num(o, "mainThreadMs", "p95");
            if (frameAvg is double ms && ms > 0)
            {
                double fps = 1000.0 / ms;
                if (ms <= SceneAuditRules.FrameMsBudget72)
                    v.Add(Ok($"Frame time {ms:0.##} ms avg (≈{fps:0} fps) — within the 72 Hz mobile-VR budget ({SceneAuditRules.FrameMsBudget72:0.#} ms)."));
                else if (ms <= SceneAuditRules.FrameMsBudget60)
                    v.Add(Warn($"Frame time {ms:0.##} ms avg (≈{fps:0} fps) — misses the 72 Hz VR budget ({SceneAuditRules.FrameMsBudget72:0.#} ms) but holds 60 fps. On-device will be slower than the editor."));
                else
                    v.Add(Crit($"Frame time {ms:0.##} ms avg (≈{fps:0} fps) — over the 60 fps budget ({SceneAuditRules.FrameMsBudget60:0.#} ms); the scene needs optimization before shipping on mobile/VR."));

                if (frameP95 is double p95 && p95 > ms * SceneAuditRules.FrameSpikeRatio)
                    v.Add(Warn($"Frame pacing is spiky: p95 {p95:0.##} ms vs {ms:0.##} ms avg — players feel this as stutter. Usual causes: GC spikes, asset loads, instantiation bursts."));
                else
                    v.Add(Ok("Frame pacing is steady (p95 close to the average) — no stutter signature."));

                // attribute the cost when over budget and a marker breakdown exists
                if (ms > SceneAuditRules.FrameMsBudget72 && o["markers"] is JArray mk && mk.Count > 0)
                    v.Add(Info($"Biggest CPU cost: {(string)mk[0]["name"]} (~{mk[0]["avgMs"]} ms of the frame) — start optimizing there."));
            }

            if (Num(o, "gpuFrameMs", "avg") is double gpu && gpu > 0)
            {
                if (gpu > SceneAuditRules.FrameMsBudget60)
                    v.Add(Crit($"GPU frame {gpu:0.##} ms avg — GPU-bound past the 60 fps budget; reduce overdraw, resolution, or shader cost."));
                else if (gpu > SceneAuditRules.FrameMsBudget72)
                    v.Add(Warn($"GPU frame {gpu:0.##} ms avg — over the 72 Hz VR budget on the editor GPU; expect worse on the device GPU."));
                else
                    v.Add(Ok($"GPU frame {gpu:0.##} ms avg — within budget on the editor GPU (device GPUs are weaker; verify on device)."));
            }

            if (Num(o, "gcAllocPerFrameB", "avg") is double g)
            {
                if (g >= SceneAuditRules.GcCriticalBytes)
                    v.Add(Crit($"Scripts allocate ~{g / 1024:0.#} KB of GC garbage per frame — this causes periodic GC stutter. Hunt per-frame allocations (strings, LINQ, boxing, new in Update)."));
                else if (g >= SceneAuditRules.GcWarnBytes)
                    v.Add(Warn($"Scripts allocate ~{g / 1024:0.#} KB of GC garbage per frame; aim for 0 so the garbage collector never spikes mid-play."));
                else
                    v.Add(Ok(g <= 0 ? "Zero GC allocation per frame — no garbage-collection stutter risk."
                                    : $"GC allocation is tiny (~{g:0} B/frame) — no garbage-collection stutter risk."));
            }

            if (Num(o, "batches", "avg") is double b)
            {
                if (b >= SceneAuditRules.BatchesCritical)
                    v.Add(Crit($"{b:0} draw batches avg — far past the ~{SceneAuditRules.BatchesWarn} mobile budget. Enable static batching, mark non-moving objects static, merge materials."));
                else if (b >= SceneAuditRules.BatchesWarn)
                    v.Add(Warn($"{b:0} draw batches avg — above the ~{SceneAuditRules.BatchesWarn} comfortable mobile budget; static flags and batching would claw this back."));
                else
                    v.Add(Ok($"{b:0} draw batches avg — comfortable for mobile (CPU submit cost starts to hurt around {SceneAuditRules.BatchesWarn})."));
            }

            if (Num(o, "setPassCalls", "avg") is double sp)
            {
                if (sp >= SceneAuditRules.SetPassCritical)
                    v.Add(Crit($"{sp:0} SetPass calls avg — heavy material/shader switching; share materials and atlas textures."));
                else if (sp >= SceneAuditRules.SetPassWarn)
                    v.Add(Warn($"{sp:0} SetPass calls avg — moderate material switching; fewer unique materials would help."));
                else
                    v.Add(Ok($"{sp:0} SetPass calls avg — material switching is cheap here."));
            }

            if (Num(o, "triangles", "avg") is double t)
            {
                string sev = SceneAuditRules.Classify((long)t, SceneAuditRules.SceneTriWarn, SceneAuditRules.SceneTriCritical);
                if (sev == "critical")
                    v.Add(Crit($"{t:n0} triangles rendered per frame — well past the {SceneAuditRules.SceneTriWarn:n0} mobile budget; decimate, add LODs, or cull."));
                else if (sev == "warn")
                    v.Add(Warn($"{t:n0} triangles rendered per frame — above the {SceneAuditRules.SceneTriWarn:n0} comfortable mobile budget; LODs and occlusion culling would help."));
                else
                    v.Add(Ok($"{t:n0} triangles rendered per frame — within the mobile budget ({SceneAuditRules.SceneTriWarn:n0})."));
            }

            return v;
        }

        /// <summary>Overall one-liner: worst severity wins.</summary>
        public static string Summary(List<PerfVerdict> verdicts)
        {
            int warn = 0, crit = 0;
            foreach (PerfVerdict p in verdicts)
            {
                if (p.Severity == "critical") crit++;
                else if (p.Severity == "warn") warn++;
            }
            if (crit > 0) return $"Verdict: NOT optimized — {crit} critical and {warn} warning signal(s). Editor numbers are optimistic; on-device it will be worse.";
            if (warn > 0) return $"Verdict: mostly healthy, with {warn} thing(s) worth improving.";
            return "Verdict: this sample looks well optimized for mobile/VR.";
        }

        private static PerfVerdict Ok(string t) => new PerfVerdict { Severity = "ok", Text = t };
        private static PerfVerdict Info(string t) => new PerfVerdict { Severity = "info", Text = t };
        private static PerfVerdict Warn(string t) => new PerfVerdict { Severity = "warn", Text = t };
        private static PerfVerdict Crit(string t) => new PerfVerdict { Severity = "critical", Text = t };

        private static double? Num(JObject o, string key, string field)
        {
            try
            {
                if (o[key] is JObject stat && stat[field] != null) return (double?)stat[field];
            }
            catch { /* malformed stat — skip the verdict */ }
            return null;
        }
    }
}
