using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgenLink.Ops
{
    /// <summary>
    /// Resolves the object-reference strings the bridge tools accept into live objects. A GameObject target
    /// is one of: an instanceID (all digits — preferred, every read tool returns them), a hierarchy path
    /// "Parent/Child/Leaf", or a unique GameObject name. Searches EVERY loaded scene and includes inactive
    /// objects. Throws with a clear message on missing / ambiguous, so the failure text guides the caller.
    /// </summary>
    internal static class ObjectResolver
    {
        public static GameObject ResolveGameObject(string target)
        {
            if (string.IsNullOrEmpty(target)) throw new Exception("missing target (pass an instanceID, hierarchy path, or object name)");

            // instanceID — the unambiguous form every read tool hands back.
            if (int.TryParse(target, out int id))
            {
                var obj = EditorUtility.InstanceIDToObject(id);
                if (obj == null) throw new Exception($"no object with instanceID {id} (it may have been destroyed or reloaded — re-query the scene)");
                if (obj is GameObject go) return go;
                if (obj is Component comp) return comp.gameObject;
                throw new Exception($"instanceID {id} is a {obj.GetType().Name}, not a GameObject");
            }

            var matches = new List<GameObject>();
            bool isPath = target.IndexOf('/') >= 0;
            string[] parts = isPath ? target.Split('/') : null;

            foreach (GameObject root in AllRoots())
            {
                if (isPath) MatchPath(root.transform, parts, 0, matches);
                else MatchName(root.transform, target, matches);
            }

            if (matches.Count == 0)
                throw new Exception(isPath
                    ? $"no GameObject at path '{target}' in any loaded scene"
                    : $"no GameObject named '{target}' in any loaded scene");
            if (matches.Count > 1)
            {
                var paths = new List<string>();
                for (int i = 0; i < matches.Count && i < 6; i++) paths.Add(PathOf(matches[i].transform));
                throw new Exception($"'{target}' is ambiguous ({matches.Count} matches: {string.Join(", ", paths)}). " +
                                    "Use an instanceID or a full hierarchy path.");
            }
            return matches[0];
        }

        /// <summary>Resolve a component on a target GameObject by type name, picking the nth of that type.</summary>
        public static Component ResolveComponent(GameObject go, string typeName, int index)
        {
            Type t = TypeResolver.RequireComponentType(typeName);
            Component[] comps = go.GetComponents(t);
            if (comps.Length == 0) throw new Exception($"'{go.name}' has no {t.Name}");
            if (index < 0 || index >= comps.Length)
                throw new Exception($"'{go.name}' has {comps.Length} {t.Name}(s); index {index} is out of range");
            return comps[index];
        }

        /// <summary>Resolve an asset by "Assets/..."/"Packages/..." path or by GUID.</summary>
        public static UnityEngine.Object ResolveAsset(string pathOrGuid, Type type = null)
        {
            if (string.IsNullOrEmpty(pathOrGuid)) throw new Exception("missing asset path/guid");
            string path = pathOrGuid;
            // A 32-char hex string with no path separator is treated as a GUID.
            if (pathOrGuid.IndexOf('/') < 0 && pathOrGuid.Length == 32 && IsHex(pathOrGuid))
            {
                string p = AssetDatabase.GUIDToAssetPath(pathOrGuid);
                if (!string.IsNullOrEmpty(p)) path = p;
            }
            var obj = type != null
                ? AssetDatabase.LoadAssetAtPath(path, type)
                : AssetDatabase.LoadMainAssetAtPath(path);
            if (obj == null) throw new Exception($"no asset found at '{pathOrGuid}'");
            return obj;
        }

        /// <summary>"Parent/Child/Leaf" path of a transform within its scene.</summary>
        public static string PathOf(Transform tr)
        {
            var stack = new List<string>();
            for (Transform t = tr; t != null; t = t.parent) stack.Add(t.name);
            stack.Reverse();
            return string.Join("/", stack);
        }

        public static IEnumerable<GameObject> AllRoots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                foreach (GameObject root in s.GetRootGameObjects()) yield return root;
            }
        }

        private static void MatchPath(Transform tr, string[] parts, int depth, List<GameObject> matches)
        {
            if (tr.name != parts[depth]) return;
            if (depth == parts.Length - 1) { matches.Add(tr.gameObject); return; }
            foreach (Transform child in tr) MatchPath(child, parts, depth + 1, matches);
        }

        private static void MatchName(Transform tr, string name, List<GameObject> matches)
        {
            if (tr.name == name) matches.Add(tr.gameObject);
            foreach (Transform child in tr) MatchName(child, name, matches);
        }

        private static bool IsHex(string s)
        {
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }
    }
}
