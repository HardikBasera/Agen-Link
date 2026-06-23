using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AgenLink
{
    /// <summary>
    /// Parses one request line and produces one response line. Runs entirely on the main thread (invoked via
    /// MainThreadDispatcher) so every Unity API call is safe. Request envelope: {"id","command","params"}.
    /// Response: {"id","ok":true,"data":{...}} or {"id","ok":false,"error":"..."}.
    /// </summary>
    internal static class CommandHandlers
    {
        [Serializable]
        private class RequestEnvelope
        {
            public string id;
            public string command;
            public RequestParams @params;
        }

        [Serializable]
        private class RequestParams
        {
            public string type;      // read_console: all|error|warning|log
            public int max;          // read_console / find_assets cap
            public string query;     // find_assets query (e.g. "t:MonoScript")
            public int maxDepth;     // get_scene_hierarchy depth

            // graph_query
            public string entity;    // focus: id | guid | path | type full name | display/short name
            public string direction; // out | in | both
            public int depth;        // neighborhood hops
            public string kinds;     // CSV of NodeKind
            public string relations; // CSV of EdgeRelation
            public int limit;        // max nodes

            // graph_name_systems (parallel arrays — JsonUtility has no dictionary support)
            public string[] systemIds;
            public string[] systemNames;

            // perf_* (analysis)
            public int frames;          // perf_start: frames to sample
            public bool enterPlayMode;  // perf_start: enter play mode if not playing
            public bool exitPlayMode;   // perf_start: leave play mode when done
        }

        public static string Dispatch(string line)
        {
            string id = null;
            try
            {
                var req = JsonUtility.FromJson<RequestEnvelope>(line);
                if (req == null || string.IsNullOrEmpty(req.command))
                    return Error(req?.id, "Missing or invalid command");
                id = req.id;
                var p = req.@params ?? new RequestParams();
                // apply_fixes carries a nested object array JsonUtility can't parse — hand it the raw line.
                string data = req.command == "apply_fixes"
                    ? Analysis.FixApplier.Apply(line)
                    : Handle(req.command, p);
                return new JObj().S("id", id).B("ok", true).Raw("data", data).Build();
            }
            catch (Exception e)
            {
                return Error(id, e.Message);
            }
        }

        public static string Error(string id, string message)
        {
            return new JObj().S("id", id).B("ok", false).S("error", message).Build();
        }

        private static string Handle(string command, RequestParams p)
        {
            switch (command)
            {
                case "ping": return new JObj().B("pong", true).S("editor", Application.unityVersion).Build();
                case "get_project_info": return ProjectInfo();
                case "read_console": return ReadConsole(p);
                case "get_compile_errors": return CompileErrors();
                case "refresh_assets": return RefreshAssets();
                case "get_scene_hierarchy": return SceneHierarchy(p);
                case "get_selection": return SelectionInfo();
                case "find_assets": return FindAssets(p);
                case "graph_status": return GraphStatus();
                case "graph_build": return GraphBuild();
                case "graph_query": return GraphQuery(p);
                case "graph_systems": return GraphSystems();
                case "graph_name_systems": return GraphNameSystems(p);
                case "audit_scene": return Analysis.SceneAuditor.Run(p.max);
                case "audit_assets": return Analysis.AssetAuditor.Run(p.max);
                case "perf_start": return Analysis.PerfRecorder.Start(p.frames, p.enterPlayMode, p.exitPlayMode);
                case "perf_status": return Analysis.PerfRecorder.Status();
                case "perf_report": return Analysis.PerfRecorder.Report();
                default: throw new Exception($"Unknown command: {command}");
            }
        }

        private static string ProjectInfo()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            return new JObj()
                .S("unityVersion", Application.unityVersion)
                .S("projectPath", Directory.GetParent(Application.dataPath)?.FullName)
                .S("productName", Application.productName)
                .S("companyName", Application.companyName)
                .S("platform", EditorUserBuildSettings.activeBuildTarget.ToString())
                .S("renderPipeline", rp != null ? rp.GetType().Name : "Built-in Render Pipeline")
                .S("activeScene", SceneManager.GetActiveScene().path)
                .B("isPlaying", EditorApplication.isPlaying)
                .B("isCompiling", EditorApplication.isCompiling)
                .Build();
        }

        private static string ReadConsole(RequestParams p)
        {
            int max = p.max > 0 ? p.max : 50;
            string filter = string.IsNullOrEmpty(p.type) ? "all" : p.type.ToLowerInvariant();
            var entries = ConsoleCapture.Snapshot();
            var picked = new List<string>();
            for (int i = entries.Count - 1; i >= 0 && picked.Count < max; i--)
            {
                var e = entries[i];
                if (!MatchType(filter, e.Type)) continue;
                picked.Add(new JObj().S("type", e.Type).S("message", e.Message).S("stack", Truncate(e.Stack, 2000)).Build());
            }
            picked.Reverse(); // chronological order
            return new JObj().N("count", picked.Count).Raw("entries", Json.Arr(picked)).Build();
        }

        private static bool MatchType(string filter, string type)
        {
            switch (filter)
            {
                case "all": return true;
                case "error": return type == "Error" || type == "Exception" || type == "Assert";
                case "warning": return type == "Warning";
                case "log": return type == "Log";
                default: return true;
            }
        }

        private static string CompileErrors()
        {
            var msgs = CompileWatcher.Snapshot();
            var elems = new List<string>();
            int errors = 0, warnings = 0;
            foreach (var m in msgs)
            {
                if (m.Type == "Error") errors++; else warnings++;
                elems.Add(new JObj().S("type", m.Type).S("message", m.Message).S("file", m.File).N("line", m.Line).Build());
            }
            return new JObj()
                .B("isCompiling", EditorApplication.isCompiling)
                .N("errorCount", errors)
                .N("warningCount", warnings)
                .Raw("messages", Json.Arr(elems))
                .Build();
        }

        private static string RefreshAssets()
        {
            AssetDatabase.Refresh();
            // Kick a recompile if scripts changed; harmless if nothing is dirty.
            try { CompilationPipeline.RequestScriptCompilation(); } catch { /* older Unity */ }
            return new JObj()
                .S("status", "refresh requested")
                .B("isCompiling", EditorApplication.isCompiling)
                .Build();
        }

        private static string SceneHierarchy(RequestParams p)
        {
            int maxDepth = p.maxDepth > 0 ? p.maxDepth : 3;
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var elems = new List<string>();
            foreach (var go in roots) elems.Add(NodeJson(go, maxDepth, 0));
            return new JObj()
                .S("scene", scene.name)
                .S("scenePath", scene.path)
                .N("rootCount", roots.Length)
                .Raw("roots", Json.Arr(elems))
                .Build();
        }

        private static string NodeJson(GameObject go, int maxDepth, int depth)
        {
            var components = go.GetComponents<Component>();
            var compNames = new List<string>(components.Length);
            foreach (var c in components)
                compNames.Add(Json.Str(c == null ? "<MissingScript>" : c.GetType().Name));

            string childrenJson = "[]";
            if (depth < maxDepth && go.transform.childCount > 0)
            {
                var childElems = new List<string>();
                foreach (Transform t in go.transform)
                    childElems.Add(NodeJson(t.gameObject, maxDepth, depth + 1));
                childrenJson = Json.Arr(childElems);
            }

            return new JObj()
                .S("name", go.name)
                .B("activeSelf", go.activeSelf)
                .N("childCount", go.transform.childCount)
                .Raw("components", Json.Arr(compNames))
                .Raw("children", childrenJson)
                .Build();
        }

        private static string SelectionInfo()
        {
            var elems = new List<string>();
            foreach (var o in Selection.objects)
            {
                if (o == null) continue;
                string path = AssetDatabase.GetAssetPath(o);
                elems.Add(new JObj()
                    .S("name", o.name)
                    .S("type", o.GetType().Name)
                    .S("assetPath", string.IsNullOrEmpty(path) ? null : path)
                    .Build());
            }
            return new JObj()
                .S("activeObject", Selection.activeObject != null ? Selection.activeObject.name : null)
                .N("count", elems.Count)
                .Raw("objects", Json.Arr(elems))
                .Build();
        }

        private static string FindAssets(RequestParams p)
        {
            int max = p.max > 0 ? p.max : 100;
            string query = p.query ?? "";
            string[] guids = AssetDatabase.FindAssets(query);
            var elems = new List<string>();
            for (int i = 0; i < guids.Length && elems.Count < max; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                elems.Add(new JObj().S("guid", guids[i]).S("path", path).Build());
            }
            return new JObj()
                .N("total", guids.Length)
                .N("returned", elems.Count)
                .Raw("assets", Json.Arr(elems))
                .Build();
        }

        // ===================== Neuron graph =====================

        private static string GraphStatus()
        {
            var g = Neuron.GraphStore.EnsureLoaded();
            return new JObj()
                .B("building", Neuron.GraphStore.Building)
                .B("hasCache", g != null)
                .N("nodeCount", g != null ? g.NodeCount : 0)
                .N("edgeCount", g != null ? g.EdgeCount : 0)
                .N("systemCount", g != null ? g.SystemCount : 0)
                .N("builtAtUnixMs", g != null ? g.BuiltAtUnixMs : 0L)
                .S("projectRoot", g != null ? g.ProjectRoot : null)
                .Build();
        }

        private static string GraphBuild()
        {
            bool was = Neuron.GraphStore.Building;
            Neuron.GraphStore.RequestRebuild();
            return new JObj().S("status", was ? "already_building" : "building").B("building", true).Build();
        }

        private static string GraphQuery(RequestParams p)
        {
            var g = Neuron.GraphStore.EnsureLoaded();
            if (g == null)
                return new JObj().B("ready", false)
                    .S("hint", "Cache empty — call graph_build, then poll graph_status until building=false and hasCache=true.")
                    .Build();

            var kinds = ParseKinds(p.kinds);
            var relations = ParseRelations(p.relations);
            int limit = p.limit > 0 ? Math.Min(p.limit, 500) : 200;

            Neuron.NeighborhoodResult res;
            if (!string.IsNullOrEmpty(p.entity))
            {
                string id = g.ResolveEntityId(p.entity);
                if (id == null)
                {
                    var cands = g.ResolveCandidates(p.entity, 10);
                    var celems = new List<string>();
                    foreach (var c in cands) celems.Add(NodeJson(c, g));
                    return new JObj().B("ready", true).B("resolved", false)
                        .S("entity", p.entity)
                        .Raw("candidates", Json.Arr(celems))
                        .Build();
                }
                int depth = p.depth > 0 ? p.depth : 1;
                res = g.Neighbors(id, depth, ParseDirection(p.direction), kinds, relations, limit);
            }
            else
            {
                res = g.Filtered(kinds, relations, limit);
            }

            var nodeElems = new List<string>();
            foreach (var n in res.Nodes) nodeElems.Add(NodeJson(n, g));
            var edgeElems = new List<string>();
            foreach (var e in res.Edges)
                edgeElems.Add(new JObj().S("from", e.From).S("to", e.To).S("relation", e.Relation.ToString()).Build());

            return new JObj()
                .B("ready", true).B("resolved", true)
                .S("centerId", res.CenterId)
                .B("truncated", res.Truncated)
                .N("nodeCount", res.Nodes.Count)
                .N("edgeCount", res.Edges.Count)
                .Raw("nodes", Json.Arr(nodeElems))
                .Raw("edges", Json.Arr(edgeElems))
                .Build();
        }

        private static string GraphSystems()
        {
            var g = Neuron.GraphStore.EnsureLoaded();
            if (g == null)
                return new JObj().B("ready", false)
                    .S("hint", "Cache empty — call graph_build, then poll graph_status until building=false and hasCache=true.")
                    .Build();

            var elems = new List<string>();
            int unnamed = 0;
            foreach (var sys in g.AllSystems())
            {
                bool scattered = sys.Id != null && sys.Id.EndsWith("#scattered");
                bool cached = Neuron.GraphStore.SystemNames != null && Neuron.GraphStore.SystemNames.ContainsKey(sys.Signature());
                bool needsNaming = !scattered && !cached;
                if (needsNaming) unnamed++;

                var members = new List<string>();
                for (int i = 0; i < sys.MemberIds.Count && members.Count < 14; i++)
                    if (g.TryGetNode(sys.MemberIds[i], out var mn)) members.Add(Json.Str(mn.Name));
                string mainName = g.TryGetNode(sys.MainId ?? "", out var main) ? main.Name : null;

                elems.Add(new JObj()
                    .S("id", sys.Id)
                    .S("name", sys.Name)
                    .S("owner", OwnerLabel(g, sys.Owner))
                    .S("mainId", sys.MainId)
                    .S("main", mainName)
                    .N("memberCount", sys.MemberIds.Count)
                    .B("needsNaming", needsNaming)
                    .B("scattered", scattered)
                    .Raw("members", Json.Arr(members))
                    .Build());
            }
            return new JObj()
                .B("ready", true)
                .N("systemCount", g.SystemCount)
                .N("needsNaming", unnamed)
                .Raw("systems", Json.Arr(elems))
                .Build();
        }

        private static string OwnerLabel(Neuron.ProjectGraph g, string owner)
        {
            if (owner == "shared") return "Shared · Core";
            if (owner == "project") return "Project · no scene";
            return g.TryGetNode(owner ?? "", out var n) ? ("Scene · " + n.Name) : owner;
        }

        private static string GraphNameSystems(RequestParams p)
        {
            if (p.systemIds == null || p.systemNames == null || p.systemIds.Length != p.systemNames.Length)
                throw new Exception("graph_name_systems requires equal-length 'systemIds' and 'systemNames' arrays.");
            var map = new Dictionary<string, string>();
            for (int i = 0; i < p.systemIds.Length; i++)
                if (!string.IsNullOrEmpty(p.systemIds[i])) map[p.systemIds[i]] = p.systemNames[i];
            int applied = Neuron.GraphStore.NameSystems(map);
            return new JObj().B("ok", true).N("applied", applied).N("requested", map.Count).Build();
        }

        private static string NodeJson(Neuron.GraphNode n, Neuron.ProjectGraph g)
        {
            string sysName = null;
            if (!string.IsNullOrEmpty(n.SystemId) && g.TryGetSystem(n.SystemId, out var sys)) sysName = sys.Name;
            var sceneIds = new List<string>();
            if (n.SceneIds != null) foreach (var s in n.SceneIds) sceneIds.Add(Json.Str(s));
            return new JObj()
                .S("id", n.Id)
                .S("kind", n.Kind.ToString())
                .S("name", n.Name)
                .S("path", n.Path)
                .S("guid", n.Guid)
                .S("typeName", n.TypeName)
                .S("systemId", n.SystemId)
                .S("system", sysName)
                .B("main", n.IsSystemMain)
                .Raw("sceneIds", Json.Arr(sceneIds))
                .Build();
        }

        private static ISet<Neuron.NodeKind> ParseKinds(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return null;
            var set = new HashSet<Neuron.NodeKind>();
            foreach (var part in csv.Split(','))
                if (Enum.TryParse(part.Trim(), true, out Neuron.NodeKind k)) set.Add(k);
            return set.Count > 0 ? set : null;
        }

        private static ISet<Neuron.EdgeRelation> ParseRelations(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return null;
            var set = new HashSet<Neuron.EdgeRelation>();
            foreach (var part in csv.Split(','))
                if (Enum.TryParse(part.Trim(), true, out Neuron.EdgeRelation r)) set.Add(r);
            return set.Count > 0 ? set : null;
        }

        private static Neuron.Direction ParseDirection(string s)
        {
            if (string.IsNullOrEmpty(s)) return Neuron.Direction.Both;
            switch (s.Trim().ToLowerInvariant())
            {
                case "out": return Neuron.Direction.Out;
                case "in": return Neuron.Direction.In;
                default: return Neuron.Direction.Both;
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }
    }
}
