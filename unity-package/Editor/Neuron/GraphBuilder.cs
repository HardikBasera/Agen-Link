using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace AgenLink.Neuron
{
    /// <summary>Builds a <see cref="ProjectGraph"/> from the open project. MAIN-THREAD ONLY (AssetDatabase /
    /// MonoScript). This is a STRUCTURAL graph — script inheritance/interface/serialized-field composition and
    /// prefab/scene component + asset wiring — NOT a method-call graph (reflection can't see call edges).</summary>
    internal static class GraphBuilder
    {
        private static HashSet<string> _includeFolders;   // null/empty = all Assets folders
        private static readonly Regex IdentifierRx = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

        /// <summary>A path is in graph scope: under Assets/ (optionally narrowed to selected top-level folders).
        /// Neuron is Assets-only by design — Packages and other roots are never indexed.</summary>
        private static bool InScope(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!path.StartsWith("Assets/")) return false;
            if (_includeFolders == null || _includeFolders.Count == 0) return true;
            foreach (var f in _includeFolders) if (path == f || path.StartsWith(f + "/")) return true;
            return false;
        }

        public static ProjectGraph BuildProjectGraph(HashSet<string> includeFolders = null)
        {
            _includeFolders = includeFolders;
            var g = new ProjectGraph { ProjectRoot = ConfigBuilder.ProjectRoot() };
            var pathToId = new Dictionary<string, string>();
            try
            {
                BuildScripts(g, pathToId);
                BuildAssets(g, pathToId);
                ComputeSceneMembership(g, pathToId);
                GraphClustering.Assign(g);   // scene › system › main clusters (structural, offline)
            }
            finally { EditorUtility.ClearProgressBar(); }
            g.BuiltAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return g;
        }

        /// <summary>Tags each node with the scenes that (transitively) include it, via recursive
        /// <c>GetDependencies</c>. A node in ≥2 scenes is "shared"; a node in 0 scenes is project-level.
        /// This gives the top tier of the nested cluster hierarchy (scene = container).</summary>
        private static void ComputeSceneMembership(ProjectGraph g, Dictionary<string, string> pathToId)
        {
            var scenes = new List<GraphNode>();
            foreach (var n in g.AllNodes()) if (n.Kind == NodeKind.Scene) scenes.Add(n);

            for (int i = 0; i < scenes.Count; i++)
            {
                var scene = scenes[i];
                Progress("Mapping scenes", i, scenes.Count);
                if (string.IsNullOrEmpty(scene.Path)) continue;
                string[] deps;
                try { deps = AssetDatabase.GetDependencies(scene.Path, true); } catch { continue; }
                foreach (var dep in deps)
                {
                    if (dep == scene.Path) continue;
                    if (pathToId.TryGetValue(dep, out var depId) && g.TryGetNode(depId, out var dn))
                        if (!dn.SceneIds.Contains(scene.Id)) dn.SceneIds.Add(scene.Id);
                }
            }
        }

        private static void BuildScripts(ProjectGraph g, Dictionary<string, string> pathToId)
        {
            string[] guids = AssetDatabase.FindAssets("t:MonoScript");
            var typeToId = new Dictionary<Type, string>();
            var nameToId = new Dictionary<string, string>();           // short name -> id (null = ambiguous)
            var sources = new List<KeyValuePair<string, string>>();    // (nodeId, assetPath)

            for (int i = 0; i < guids.Length; i++)
            {
                if ((i & 31) == 0) Progress("Indexing scripts", i, guids.Length);
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!InScope(path)) continue;
                MonoScript ms;
                try { ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path); } catch { continue; }
                Type t = ms != null ? ms.GetClass() : null;

                string id, shortName, typeName;
                if (t != null && !string.IsNullOrEmpty(t.FullName)) { id = t.FullName; shortName = t.Name; typeName = t.FullName; typeToId[t] = id; }
                else { id = guids[i]; shortName = System.IO.Path.GetFileNameWithoutExtension(path); typeName = null; } // plain/editor class

                g.AddNode(id, NodeKind.Script, shortName, path, guids[i], typeName);
                pathToId[path] = id;
                sources.Add(new KeyValuePair<string, string>(id, path));
                RegisterName(nameToId, shortName, id);
            }

            // structural edges from reflection (resolved types only)
            foreach (var kv in typeToId)
            {
                Type t = kv.Key; string from = kv.Value;
                if (t.BaseType != null && typeToId.TryGetValue(t.BaseType, out var baseId))
                    g.AddEdge(from, baseId, EdgeRelation.Inherits);
                foreach (var itf in t.GetInterfaces())
                    if (typeToId.TryGetValue(itf, out var itfId))
                        g.AddEdge(from, itfId, EdgeRelation.Implements);
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!IsSerializedField(f)) continue;
                    Type ft = ElementType(f.FieldType);
                    if (ft != null && typeToId.TryGetValue(ft, out var ftId))
                        g.AddEdge(from, ftId, EdgeRelation.HasField);
                }
            }

            // code-usage edges: scan each script's source for references to other project types — catches
            // method calls, GetComponent<T>, non-serialized fields, editor->runtime links, etc. that reflection
            // cannot see. Token-level (whole identifiers), unique-name only, so it's lightweight and low-noise.
            string root = ConfigBuilder.ProjectRoot();
            for (int s = 0; s < sources.Count; s++)
            {
                if ((s & 15) == 0) Progress("Linking scripts", s, sources.Count);
                string fromId = sources[s].Key;
                string text;
                try { text = System.IO.File.ReadAllText(System.IO.Path.Combine(root, sources[s].Value)); } catch { continue; }
                var seen = new HashSet<string>();
                foreach (Match m in IdentifierRx.Matches(text))
                {
                    string tok = m.Value;
                    if (!seen.Add(tok)) continue;
                    if (nameToId.TryGetValue(tok, out var toId) && toId != null && toId != fromId)
                        g.AddEdge(fromId, toId, EdgeRelation.References);
                }
            }
        }

        private static void RegisterName(Dictionary<string, string> map, string name, string id)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (map.TryGetValue(name, out var existing)) { if (existing != id) map[name] = null; } // ambiguous -> null
            else map[name] = id;
        }

        private static void BuildAssets(ProjectGraph g, Dictionary<string, string> pathToId)
        {
            string[] prefabs = AssetDatabase.FindAssets("t:Prefab");
            for (int i = 0; i < prefabs.Length; i++)
            {
                if ((i & 15) == 0) Progress("Indexing prefabs", i, prefabs.Length);
                string path = AssetDatabase.GUIDToAssetPath(prefabs[i]);
                if (!InScope(path)) continue;
                string id = prefabs[i];
                g.AddNode(id, NodeKind.Prefab, System.IO.Path.GetFileNameWithoutExtension(path), path, id);
                pathToId[path] = id;
                AddAssetDeps(g, pathToId, id, path);
                AddPrefabComponents(g, id, path);
            }

            string[] scenes = AssetDatabase.FindAssets("t:Scene");
            for (int i = 0; i < scenes.Length; i++)
            {
                if ((i & 7) == 0) Progress("Indexing scenes", i, scenes.Length);
                string path = AssetDatabase.GUIDToAssetPath(scenes[i]);
                if (!InScope(path)) continue;
                string id = scenes[i];
                g.AddNode(id, NodeKind.Scene, System.IO.Path.GetFileNameWithoutExtension(path), path, id);
                pathToId[path] = id;
                AddAssetDeps(g, pathToId, id, path); // scenes contribute via dependency edges; not opened in v1
            }
        }

        private static void AddAssetDeps(ProjectGraph g, Dictionary<string, string> pathToId, string fromId, string path)
        {
            string[] deps;
            try { deps = AssetDatabase.GetDependencies(path, false); } catch { return; }
            foreach (var dep in deps)
            {
                if (dep == path || !InScope(dep)) continue;
                string depId = EnsureAssetNode(g, pathToId, dep);
                if (depId != null) g.AddEdge(fromId, depId, EdgeRelation.AssetRef);
            }
        }

        private static void AddPrefabComponents(ProjectGraph g, string prefabId, string path)
        {
            try
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) return;
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue; // missing script
                    var ms = MonoScript.FromMonoBehaviour(mb);
                    var t = ms != null ? ms.GetClass() : null;
                    if (t != null && t.FullName != null)
                        g.AddEdge(prefabId, t.FullName, EdgeRelation.Component); // skipped if not a project script node
                }
            }
            catch { /* broken prefab / missing scripts — skip */ }
        }

        private static string EnsureAssetNode(ProjectGraph g, Dictionary<string, string> pathToId, string path)
        {
            if (pathToId.TryGetValue(path, out var id)) return id;
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return null;
            g.AddNode(guid, NodeKind.Asset, System.IO.Path.GetFileName(path), path, guid);
            pathToId[path] = guid;
            return guid;
        }

        private static bool IsSerializedField(FieldInfo f)
        {
            if (f.IsStatic || f.IsLiteral || f.IsInitOnly) return false;
            if (f.IsPublic) return f.GetCustomAttribute<NonSerializedAttribute>() == null;
            return f.GetCustomAttribute<SerializeField>() != null;
        }

        private static Type ElementType(Type t)
        {
            if (t == null) return null;
            if (t.IsArray) return t.GetElementType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                return t.GetGenericArguments()[0];
            return t;
        }

        private static void Progress(string label, int i, int total)
        {
            if (total <= 0) return;
            EditorUtility.DisplayProgressBar("Neuron", $"{label}… {i}/{total}", (float)i / total);
        }
    }
}
