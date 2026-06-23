using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace AgenLink.Analysis
{
    /// <summary>
    /// CPU marker breakdown for perf recordings: while PerfRecorder samples counters, this captures
    /// profiler frames (ProfilerDriver) and, at finalize, aggregates the main-thread PlayerLoop into
    /// per-stage averages ("markers") plus per-script averages under the ScriptRun* stages
    /// ("scriptMarkers") — answering WHAT is slow, not just how slow. Capture adds editor overhead,
    /// so absolute frame times read slightly worse while it's on.
    /// </summary>
    internal static class PerfMarkers
    {
        private const int MaxFramesAnalyzed = 300;   // profiler ring buffer is bounded anyway
        private const int TopCount = 8;
        private const double NoiseFloorMs = 0.05;

        private static bool _wasEnabled;
        private static bool _capturing;

        public static void BeginCapture()
        {
            try
            {
                _wasEnabled = ProfilerDriver.enabled;
                ProfilerDriver.ClearAllFrames();
                ProfilerDriver.profileEditor = false;
                ProfilerDriver.enabled = true;
                _capturing = true;
            }
            catch { _capturing = false; }
        }

        /// <summary>Stop capturing and aggregate; both outputs null when nothing usable was captured.</summary>
        public static void EndCaptureAndCollect(out string stagesJson, out string scriptsJson)
        {
            stagesJson = null;
            scriptsJson = null;
            if (!_capturing) return;
            _capturing = false;
            try
            {
                ProfilerDriver.enabled = _wasEnabled;
                Collect(out stagesJson, out scriptsJson);
            }
            catch
            {
                try { ProfilerDriver.enabled = _wasEnabled; } catch { /* leave as-is */ }
            }
        }

        private static void Collect(out string stagesJson, out string scriptsJson)
        {
            stagesJson = null;
            scriptsJson = null;
            int last = ProfilerDriver.lastFrameIndex;
            int first = Math.Max(ProfilerDriver.firstFrameIndex, last - MaxFramesAnalyzed + 1);
            if (last < 0 || first < 0 || last < first) return;

            var stageTotals = new Dictionary<string, double>();    // PlayerLoop stage -> summed total ms
            var scriptTotals = new Dictionary<string, double>();   // script marker -> summed total ms
            int frames = 0;
            var topLevel = new List<int>();
            var stages = new List<int>();
            var leaves = new List<int>();

            for (int frame = first; frame <= last; frame++)
            {
                using (HierarchyFrameDataView view = ProfilerDriver.GetHierarchyFrameDataView(
                           frame, 0, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                           HierarchyFrameDataView.columnTotalTime, false))
                {
                    if (view == null || !view.valid) continue;
                    frames++;
                    topLevel.Clear();
                    view.GetItemChildren(view.GetRootItemID(), topLevel);
                    foreach (int top in topLevel)
                    {
                        if (view.GetItemName(top) != "PlayerLoop") continue;   // skip EditorLoop etc.
                        stages.Clear();
                        view.GetItemChildren(top, stages);
                        foreach (int stage in stages)
                        {
                            string name = view.GetItemName(stage);
                            Accumulate(stageTotals, name,
                                view.GetItemColumnDataAsFloat(stage, HierarchyFrameDataView.columnTotalTime));

                            // one level deeper under the script-update stages = user script methods
                            if (name.IndexOf("ScriptRun", StringComparison.Ordinal) < 0) continue;
                            leaves.Clear();
                            view.GetItemChildren(stage, leaves);
                            foreach (int leaf in leaves)
                                Accumulate(scriptTotals, view.GetItemName(leaf),
                                    view.GetItemColumnDataAsFloat(leaf, HierarchyFrameDataView.columnTotalTime));
                        }
                    }
                }
            }
            if (frames == 0) return;

            stagesJson = TopJson(stageTotals, frames);
            scriptsJson = TopJson(scriptTotals, frames);
        }

        private static void Accumulate(Dictionary<string, double> totals, string name, double ms)
        {
            totals.TryGetValue(name, out double sum);
            totals[name] = sum + ms;
        }

        private static string TopJson(Dictionary<string, double> totals, int frames)
        {
            if (totals.Count == 0) return null;
            var list = new List<KeyValuePair<string, double>>(totals);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var elems = new List<string>();
            for (int i = 0; i < list.Count && i < TopCount; i++)
            {
                double avg = list[i].Value / frames;
                if (avg < NoiseFloorMs) break;   // sorted, so everything after is noise too
                elems.Add(new JObj()
                    .S("name", list[i].Key)
                    .Raw("avgMs", avg.ToString("0.###", CultureInfo.InvariantCulture))
                    .Build());
            }
            return elems.Count > 0 ? Json.Arr(elems) : null;
        }
    }
}
