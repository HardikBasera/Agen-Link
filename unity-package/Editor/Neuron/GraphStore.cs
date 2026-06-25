using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgenLink.Neuron
{
    /// <summary>Holds the cached project graph for the session, schedules (re)builds off the request path, and
    /// lazily reloads the on-disk cache after a domain reload. Both the MCP handlers and the IMGUI view read
    /// <see cref="EnsureLoaded"/> / <see cref="Current"/>.</summary>
    internal static class GraphStore
    {
        public static ProjectGraph Current { get; private set; }
        public static bool Building { get; private set; }
        public static HashSet<string> IncludeFolders;   // null/empty = all Assets folders
        public static event Action OnChanged;

        /// <summary>Persistent Claude name cache: cluster signature → human name. Survives rebuilds so a system
        /// whose membership is unchanged keeps its name with zero LLM calls.</summary>
        public static Dictionary<string, string> SystemNames = new Dictionary<string, string>();

        private static bool _triedLoad;

        /// <summary>Returns the cached graph, lazily loading from disk once per domain (after a reload).</summary>
        public static ProjectGraph EnsureLoaded()
        {
            if (Current == null && !_triedLoad)
            {
                _triedLoad = true;
                try { Current = GraphSerializer.Load(out var names); SystemNames = names; }
                catch { Current = null; }
                if (Current != null) OnChanged?.Invoke();
            }
            return Current;
        }

        /// <summary>Overlay cached Claude names onto a freshly built graph's systems (by signature).</summary>
        private static void ApplyCachedNames(ProjectGraph g)
        {
            if (g == null || SystemNames == null) return;
            foreach (var sys in g.AllSystems())
                if (SystemNames.TryGetValue(sys.Signature(), out var nm) && !string.IsNullOrEmpty(nm))
                    sys.Name = nm;
        }

        /// <summary>Apply Claude-provided names to systems (by id), persist them in the signature cache, and
        /// repaint. Runs on the main thread (called from the bridge request handler). Returns count applied.</summary>
        public static int NameSystems(Dictionary<string, string> idToName)
        {
            var g = Current;
            if (g == null || idToName == null) return 0;
            if (SystemNames == null) SystemNames = new Dictionary<string, string>();
            int applied = 0;
            foreach (var kv in idToName)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if (g.TryGetSystem(kv.Key, out var sys))
                {
                    sys.Name = kv.Value;
                    SystemNames[sys.Signature()] = kv.Value;
                    applied++;
                }
            }
            if (applied > 0)
            {
                try { GraphSerializer.Save(g, SystemNames); } catch { /* best-effort */ }
                OnChanged?.Invoke();
            }
            return applied;
        }

        /// <summary>Schedule a (re)build on the main thread. Safe to call from a bridge request handler — it
        /// returns immediately and the build runs on the next editor tick (never inside the 15s request).</summary>
        public static void RequestRebuild()
        {
            if (Building) return;
            Building = true;
            EditorApplication.delayCall += RunBuild;
        }

        private static void RunBuild()
        {
            try
            {
                var g = GraphBuilder.BuildProjectGraph(IncludeFolders);
                ApplyCachedNames(g);
                Current = g;
                _triedLoad = true;
                try { GraphSerializer.Save(g, SystemNames); } catch { /* cache is best-effort */ }
            }
            catch (Exception e) { Debug.LogError("[Agen-Link] Neuron build failed: " + e.Message); }
            finally { Building = false; OnChanged?.Invoke(); }
        }
    }
}
