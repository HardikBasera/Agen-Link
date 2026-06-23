using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

namespace AgenLink.Analysis
{
    /// <summary>
    /// Play-mode performance sampling via ProfilerRecorder counters (frame time, batches, SetPass calls,
    /// triangles, GC, memory). Built around the bridge's request/response model AND the domain reload
    /// that entering play mode triggers: perf_start arms recording in SessionState (which survives the
    /// reload) and optionally enters play mode; this [InitializeOnLoad] class resumes after the reload,
    /// samples N frames from EditorApplication.update, and parks the result JSON in SessionState for
    /// perf_report to fetch. Editor numbers are indicative — on-device profiling remains the truth.
    /// </summary>
    [InitializeOnLoad]
    internal static class PerfRecorder
    {
        private const string KeyArmed   = "AgenLink.Perf.Armed";
        private const string KeyTarget  = "AgenLink.Perf.Target";
        private const string KeyExit    = "AgenLink.Perf.Exit";
        private const string KeyDone    = "AgenLink.Perf.Done";
        private const string KeyPartial = "AgenLink.Perf.Partial";
        private const string KeyResult  = "AgenLink.Perf.Result";
        private const string KeyFrames  = "AgenLink.Perf.Frames";
        private const string KeyAuto    = "AgenLink.Perf.Auto";
        private const int WarmupFrames = 5;        // discard play-mode startup spikes
        private const int MaxAutoFrames = 100_000; // unbounded-run safety cap (~23 min at 72 fps)

        private sealed class Counter
        {
            public string Key;                 // JSON field
            public ProfilerCategory Category;
            public string StatName;
            public double Scale;               // raw -> reported unit
            public ProfilerRecorder Recorder;
            public List<double> Samples = new List<double>();
        }

        private static Counter[] _counters;
        private static int _warmupLeft;

        static PerfRecorder()
        {
            EditorApplication.update += Tick;
        }

        /// <summary>"Record when playing" checkbox: arm automatically whenever play mode starts;
        /// the run ends (and the report parks) when the user exits play mode — no frame limit.</summary>
        internal static bool AutoRecord
        {
            get => SessionState.GetBool(KeyAuto, false);
            set => SessionState.SetBool(KeyAuto, value);
        }

        public static string Start(int frames, bool enterPlayMode, bool exitPlayMode)
        {
            int target = frames > 0 ? Mathf.Min(frames, 5000) : 300;
            Arm(target, exitPlayMode);

            bool playing = EditorApplication.isPlaying;
            if (!playing && enterPlayMode)
                EditorApplication.isPlaying = true;   // triggers a domain reload; we resume after it

            return new JObj()
                .S("status", "recording armed")
                .N("frames", target)
                .B("wasPlaying", playing)
                .B("enteringPlayMode", !playing && enterPlayMode)
                .S("hint", "Poll perf_status until ready=true, then call perf_report. Entering play mode " +
                           "briefly restarts this bridge (domain reload) — just retry/poll.")
                .Build();
        }

        /// <summary>target 0 = unbounded (record until play mode exits).</summary>
        private static void Arm(int target, bool exitPlayMode)
        {
            SessionState.SetBool(KeyArmed, true);
            SessionState.SetInt(KeyTarget, target);
            SessionState.SetBool(KeyExit, exitPlayMode);
            SessionState.SetBool(KeyDone, false);
            SessionState.SetBool(KeyPartial, false);
            SessionState.SetString(KeyResult, "");
            SessionState.SetInt(KeyFrames, 0);
            _counters = null;
        }

        public static string Status()
        {
            return new JObj()
                .B("armed", SessionState.GetBool(KeyArmed, false))
                .B("playing", EditorApplication.isPlaying)
                .N("framesDone", SessionState.GetInt(KeyFrames, 0))
                .N("framesTarget", SessionState.GetInt(KeyTarget, 0))
                .B("ready", SessionState.GetBool(KeyDone, false))
                .Build();
        }

        public static string Report()
        {
            if (!SessionState.GetBool(KeyDone, false))
                return new JObj().B("ready", false)
                    .S("hint", "No finished recording. Call perf_start, poll perf_status until ready=true.")
                    .Build();
            return new JObj()
                .B("ready", true)
                .B("partial", SessionState.GetBool(KeyPartial, false))
                .Raw("stats", SessionState.GetString(KeyResult, "{}"))
                .S("caveat", "Editor play-mode numbers are indicative only — editor overhead included, " +
                             "device GPU/CPU differ. Profile on the target device for ground truth.")
                .Build();
        }

        // ----- typed accessors for the Analysis tab (pure SessionState reads; JSON stays in here) -----

        internal static bool Armed   => SessionState.GetBool(KeyArmed, false);
        internal static bool Done    => SessionState.GetBool(KeyDone, false);
        internal static bool Partial => SessionState.GetBool(KeyPartial, false);
        internal static int  FramesDone   => SessionState.GetInt(KeyFrames, 0);
        internal static int  FramesTarget => SessionState.GetInt(KeyTarget, 0);

        /// <summary>One counter's stats line from the parked report, pre-formatted for display.</summary>
        internal sealed class PerfRow
        {
            public string Key, Min, Avg, P95, Max;
        }

        /// <summary>The parked stats JSON of the last finished recording ("{}" when none).</summary>
        internal static string ReportStatsJson() => SessionState.GetString(KeyResult, "{}");

        internal static int ReportFramesSampled()
        {
            try { return (int?)JObject.Parse(ReportStatsJson())["framesSampled"] ?? 0; }
            catch { return 0; }
        }

        /// <summary>One aggregated CPU marker line from the parked report.</summary>
        internal sealed class MarkerRow
        {
            public string Name, AvgMs;
        }

        /// <summary>Marker rows ("markers" = PlayerLoop stages, "scriptMarkers" = script methods); null when absent.</summary>
        internal static List<MarkerRow> ReportMarkers(string field)
        {
            if (!Done) return null;
            try
            {
                var o = JObject.Parse(ReportStatsJson());
                if (!(o[field] is JArray arr) || arr.Count == 0) return null;
                var rows = new List<MarkerRow>();
                foreach (JToken t in arr)
                    rows.Add(new MarkerRow { Name = (string)t["name"], AvgMs = t["avgMs"]?.ToString() });
                return rows;
            }
            catch { return null; }
        }

        /// <summary>Counter rows of the finished recording, or null when no report is parked.</summary>
        internal static List<PerfRow> ReportRows()
        {
            if (!Done) return null;
            try
            {
                var o = JObject.Parse(SessionState.GetString(KeyResult, "{}"));
                var rows = new List<PerfRow>();
                foreach (JProperty prop in o.Properties())
                {
                    if (!(prop.Value is JObject v)) continue;   // skips framesSampled
                    rows.Add(new PerfRow
                    {
                        Key = prop.Name,
                        Min = v["min"]?.ToString(),
                        Avg = v["avg"]?.ToString(),
                        P95 = v["p95"]?.ToString(),
                        Max = v["max"]?.ToString(),
                    });
                }
                return rows;
            }
            catch { return null; }
        }

        private static bool _wasPlaying;

        private static void Tick()
        {
            bool playing = EditorApplication.isPlaying;
            bool armed = SessionState.GetBool(KeyArmed, false);

            // "Record when playing": arm on the not-playing -> playing transition. Checking the
            // transition (not just isPlaying) prevents an instant re-arm after a finished run.
            if (!armed && AutoRecord && playing && !_wasPlaying)
            {
                Arm(0, exitPlayMode: false);
                armed = true;
            }
            _wasPlaying = playing;
            if (!armed) return;

            if (!playing)
            {
                // Fixed-length (bridge) runs cut short are partial; unbounded auto runs END by
                // exiting play mode — that's their normal completion.
                if (_counters != null) Finalize(partial: SessionState.GetInt(KeyTarget, 0) > 0);
                return;
            }

            if (_counters == null) Begin();
            else Sample();
        }

        private static void Begin()
        {
            _counters = new[]
            {
                New("mainThreadMs",     ProfilerCategory.Internal, "Main Thread",            1e-6),  // ns -> ms
                New("batches",          ProfilerCategory.Render,   "Batches Count",          1),
                New("setPassCalls",     ProfilerCategory.Render,   "SetPass Calls Count",    1),
                New("drawCalls",        ProfilerCategory.Render,   "Draw Calls Count",       1),
                New("triangles",        ProfilerCategory.Render,   "Triangles Count",        1),
                New("vertices",         ProfilerCategory.Render,   "Vertices Count",         1),
                New("gcAllocPerFrameB", ProfilerCategory.Memory,   "GC Allocated In Frame",  1),
                New("totalMemoryMB",    ProfilerCategory.Memory,   "Total Used Memory",      1.0 / (1024 * 1024)),
                New("gpuFrameMs",       ProfilerCategory.Render,   "GPU Frame Time",         1e-6),  // often n/a in editor
            };
            _warmupLeft = WarmupFrames;
            PerfMarkers.BeginCapture();   // CPU marker breakdown for "what is slow"
        }

        private static Counter New(string key, ProfilerCategory cat, string stat, double scale)
        {
            var c = new Counter { Key = key, Category = cat, StatName = stat, Scale = scale };
            try { c.Recorder = ProfilerRecorder.StartNew(cat, stat, 1); } catch { /* counter unavailable */ }
            return c;
        }

        private static void Sample()
        {
            if (_warmupLeft > 0) { _warmupLeft--; return; }

            foreach (Counter c in _counters)
                if (c.Recorder.Valid)
                    c.Samples.Add(c.Recorder.LastValueAsDouble * c.Scale);

            int done = SessionState.GetInt(KeyFrames, 0) + 1;
            SessionState.SetInt(KeyFrames, done);
            int target = SessionState.GetInt(KeyTarget, 300);
            if (target > 0 ? done >= target : done >= MaxAutoFrames) Finalize(partial: false);
        }

        private static void Finalize(bool partial)
        {
            var obj = new JObj().N("framesSampled", SessionState.GetInt(KeyFrames, 0));
            foreach (Counter c in _counters)
            {
                if (c.Samples.Count > 0) obj.Raw(c.Key, StatsJson(c.Samples));
                if (c.Recorder.Valid) c.Recorder.Dispose();
            }
            PerfMarkers.EndCaptureAndCollect(out string stages, out string scripts);
            if (stages != null) obj.Raw("markers", stages);
            if (scripts != null) obj.Raw("scriptMarkers", scripts);
            SessionState.SetString(KeyResult, obj.Build());
            SessionState.SetBool(KeyPartial, partial);
            SessionState.SetBool(KeyDone, true);
            SessionState.SetBool(KeyArmed, false);
            bool exit = SessionState.GetBool(KeyExit, false);
            _counters = null;
            if (exit && EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }

        private static string StatsJson(List<double> samples)
        {
            var sorted = new List<double>(samples);
            sorted.Sort();
            double sum = 0;
            foreach (double v in sorted) sum += v;
            double p95 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))];
            return new JObj()
                .Raw("min", Fmt(sorted[0]))
                .Raw("avg", Fmt(sum / sorted.Count))
                .Raw("p95", Fmt(p95))
                .Raw("max", Fmt(sorted[sorted.Count - 1]))
                .Build();
        }

        private static string Fmt(double v) =>
            v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
