using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgenLink.Ops
{
    /// <summary>
    /// Create / modify / delete / find / inspect GameObjects in the loaded scenes. All mutations register Undo
    /// and mark the scene dirty but never save. This is the tool path the CLI uses instead of writing and
    /// compiling a throwaway editor script.
    /// </summary>
    internal static class GameObjectOps
    {
        private const string UndoLabel = "Agen-Link";

        // ---------- create ----------

        public static string Create(CommandHandlers.RequestParams p)
        {
            GameObject go;
            string kind;

            if (!string.IsNullOrEmpty(p.prefab))
            {
                var asset = ObjectResolver.ResolveAsset(p.prefab, typeof(GameObject)) as GameObject;
                if (asset == null) throw new Exception($"'{p.prefab}' is not a prefab/GameObject asset");
                go = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                kind = "prefab instance";
            }
            else if (!string.IsNullOrEmpty(p.copyFrom))
            {
                GameObject src = ObjectResolver.ResolveGameObject(p.copyFrom);
                go = UnityEngine.Object.Instantiate(src);
                go.name = src.name; // Instantiate appends "(Clone)"
                kind = "copy";
            }
            else if (!string.IsNullOrEmpty(p.primitive))
            {
                go = GameObject.CreatePrimitive(ParsePrimitive(p.primitive));
                kind = p.primitive.ToLowerInvariant();
            }
            else
            {
                go = new GameObject(string.IsNullOrEmpty(p.name) ? "GameObject" : p.name);
                kind = "empty";
            }

            Undo.RegisterCreatedObjectUndo(go, UndoLabel + " create");

            if (!string.IsNullOrEmpty(p.parent))
            {
                GameObject parent = ObjectResolver.ResolveGameObject(p.parent);
                Undo.SetTransformParent(go.transform, parent.transform, UndoLabel + " create");
            }

            // Named last, once the object sits under its final parent, so sibling uniqueness is judged
            // against the right set. A copy carries no requested name of its own but must still not collide
            // with the source it was duplicated from.
            if (!string.IsNullOrEmpty(p.name)) SetUniqueName(go, p.name);
            else if (kind == "copy") SetUniqueName(go, go.name);

            ApplyTransform(go.transform, p.position, p.rotation, p.scale, p.worldSpace);

            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            return Summary(go, "created " + kind).Build();
        }

        /// <summary>
        /// Give <paramref name="go"/> its requested name, uniquified against its siblings but never against
        /// itself. GameObjectUtility.GetUniqueNameForSibling counts the object being named as one of the
        /// siblings it checks, so naming an object that already holds that name returns "Name (1)" — which is
        /// why every empty GameObject created through the bridge came out mis-named.
        /// </summary>
        private static void SetUniqueName(GameObject go, string name)
        {
            var taken = new HashSet<string>();
            Transform parent = go.transform.parent;
            if (parent == null)
            {
                foreach (GameObject root in go.scene.GetRootGameObjects())
                    if (root != go) taken.Add(root.name);
            }
            else
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    GameObject sibling = parent.GetChild(i).gameObject;
                    if (sibling != go) taken.Add(sibling.name);
                }
            }

            if (!taken.Contains(name)) { go.name = name; return; }
            for (int n = 1; ; n++)
            {
                string candidate = $"{name} ({n})";
                if (!taken.Contains(candidate)) { go.name = candidate; return; }
            }
        }

        // ---------- modify (nested params -> Newtonsoft) ----------

        public static string Modify(string requestLine)
        {
            var req = JObject.Parse(requestLine);
            if (!(req["params"] is JObject pr)) throw new Exception("modify_gameobject requires params");
            GameObject go = ObjectResolver.ResolveGameObject((string)pr["target"]);
            var changed = new List<string>();

            if (pr["name"] != null)
            {
                Undo.RecordObject(go, UndoLabel + " rename");
                SetUniqueName(go, (string)pr["name"]);
                changed.Add("name");
            }
            if (pr["parent"] != null)
            {
                string parentRef = (string)pr["parent"];
                Transform newParent = string.IsNullOrEmpty(parentRef) ? null : ObjectResolver.ResolveGameObject(parentRef).transform;
                Undo.SetTransformParent(go.transform, newParent, UndoLabel + " reparent");
                changed.Add("parent");
            }
            if (pr["active"] != null)
            {
                Undo.RecordObject(go, UndoLabel + " setActive");
                go.SetActive((bool)pr["active"]);
                changed.Add("active");
            }
            if (pr["tag"] != null)
            {
                Undo.RecordObject(go, UndoLabel + " tag");
                try { go.tag = (string)pr["tag"]; }
                catch { throw new Exception($"tag '{pr["tag"]}' is not defined. Add it in Project Settings ▸ Tags, or use an existing tag."); }
                changed.Add("tag");
            }
            if (pr["layer"] != null)
            {
                Undo.RecordObject(go, UndoLabel + " layer");
                go.layer = ToLayer(pr["layer"]);
                changed.Add("layer");
            }
            if (pr["isStatic"] != null)
            {
                Undo.RegisterCompleteObjectUndo(go, UndoLabel + " static");
                GameObjectUtility.SetStaticEditorFlags(go, (bool)pr["isStatic"] ? (StaticEditorFlags)~0 : 0);
                changed.Add("isStatic");
            }
            if (pr["position"] != null || pr["rotation"] != null || pr["scale"] != null)
            {
                Undo.RecordObject(go.transform, UndoLabel + " transform");
                ApplyTransform(go.transform, ToFloats(pr["position"]), ToFloats(pr["rotation"]), ToFloats(pr["scale"]), pr["worldSpace"] != null && (bool)pr["worldSpace"]);
                changed.Add("transform");
            }

            if (changed.Count == 0) throw new Exception("nothing to change — pass at least one of name/parent/active/tag/layer/isStatic/position/rotation/scale");
            EditorSceneManager.MarkSceneDirty(go.scene);
            return Summary(go, "modified: " + string.Join(", ", changed)).Build();
        }

        // ---------- delete ----------

        public static string Delete(CommandHandlers.RequestParams p)
        {
            if (p.targets == null || p.targets.Length == 0) throw new Exception("delete_gameobjects requires targets: [ref, ...]");
            var results = new List<string>();
            bool any = false;
            Scene scene = default;
            foreach (string t in p.targets)
            {
                try
                {
                    GameObject go = ObjectResolver.ResolveGameObject(t);
                    scene = go.scene; any = true;
                    string path = ObjectResolver.PathOf(go.transform);
                    Undo.DestroyObjectImmediate(go);
                    results.Add(new JObj().S("target", t).S("path", path).B("ok", true).Build());
                }
                catch (Exception e) { results.Add(new JObj().S("target", t).B("ok", false).S("error", e.Message).Build()); }
            }
            if (any) EditorSceneManager.MarkSceneDirty(scene);
            return new JObj().N("count", results.Count).B("sceneDirty", any).Raw("results", Json.Arr(results)).Build();
        }

        // ---------- find ----------

        public static string Find(CommandHandlers.RequestParams p)
        {
            int max = p.max > 0 ? p.max : 100;
            Type compType = string.IsNullOrEmpty(p.component) ? null : TypeResolver.RequireComponentType(p.component);
            var matches = new List<string>();
            int total = 0;

            foreach (GameObject root in ObjectResolver.AllRoots())
                foreach (Transform tr in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = tr.gameObject;
                    if (!string.IsNullOrEmpty(p.gname) && go.name.IndexOf(p.gname, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!string.IsNullOrEmpty(p.gpath) && ObjectResolver.PathOf(tr) != p.gpath) continue;
                    if (compType != null && go.GetComponent(compType) == null) continue;
                    if (!string.IsNullOrEmpty(p.tag) && !go.CompareTag(p.tag)) continue;
                    total++;
                    if (matches.Count < max) matches.Add(NodeSummary(go));
                }

            return new JObj()
                .N("total", total)
                .B("truncated", total > matches.Count)
                .Raw("matches", Json.Arr(matches))
                .Build();
        }

        // ---------- get ----------

        public static string Get(CommandHandlers.RequestParams p)
        {
            GameObject go = ObjectResolver.ResolveGameObject(p.target);
            int depth = p.maxDepth > 0 ? p.maxDepth : 2;
            var comps = new List<string>();
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null) { comps.Add(new JObj().S("type", "MissingScript").Build()); continue; }
                var job = new JObj().S("type", c.GetType().Name).N("instanceID", c.GetInstanceID());
                if (p.includeProperties) job.Raw("properties", PropertyReader.ReadComponentProperties(c, depth));
                comps.Add(job.Build());
            }
            return Summary(go, null).Raw("components", Json.Arr(comps)).Build();
        }

        // ---------- shared ----------

        private static JObj Summary(GameObject go, string result)
        {
            var job = new JObj()
                .N("instanceID", go.GetInstanceID())
                .S("name", go.name)
                .S("path", ObjectResolver.PathOf(go.transform))
                .B("activeSelf", go.activeSelf)
                .S("tag", go.tag)
                .S("layer", LayerMask.LayerToName(go.layer))
                .S("scene", go.scene.name);
            if (result != null) job.S("result", result);
            return job;
        }

        private static string NodeSummary(GameObject go)
        {
            var types = new List<string>();
            foreach (Component c in go.GetComponents<Component>()) types.Add(Json.Str(c == null ? "MissingScript" : c.GetType().Name));
            return new JObj()
                .N("instanceID", go.GetInstanceID())
                .S("name", go.name)
                .S("path", ObjectResolver.PathOf(go.transform))
                .B("activeInHierarchy", go.activeInHierarchy)
                .S("tag", go.tag)
                .Raw("components", Json.Arr(types))
                .Build();
        }

        private static void ApplyTransform(Transform tr, float[] pos, float[] rot, float[] scale, bool world)
        {
            if (pos != null && pos.Length >= 3)
            {
                var v = new Vector3(pos[0], pos[1], pos[2]);
                if (world) tr.position = v; else tr.localPosition = v;
            }
            if (rot != null && rot.Length >= 3)
            {
                var q = Quaternion.Euler(rot[0], rot[1], rot[2]);
                if (world) tr.rotation = q; else tr.localRotation = q;
            }
            if (scale != null && scale.Length >= 3)
                tr.localScale = new Vector3(scale[0], scale[1], scale[2]); // scale is always local
        }

        private static float[] ToFloats(JToken t)
        {
            if (!(t is JArray a)) return null;
            var f = new float[a.Count];
            for (int i = 0; i < a.Count; i++) f[i] = (float)a[i];
            return f;
        }

        private static int ToLayer(JToken t)
        {
            if (t.Type == JTokenType.Integer) return (int)t;
            int l = LayerMask.NameToLayer((string)t);
            if (l < 0) throw new Exception($"unknown layer '{t}'");
            return l;
        }

        private static PrimitiveType ParsePrimitive(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "cube": return PrimitiveType.Cube;
                case "sphere": return PrimitiveType.Sphere;
                case "capsule": return PrimitiveType.Capsule;
                case "cylinder": return PrimitiveType.Cylinder;
                case "plane": return PrimitiveType.Plane;
                case "quad": return PrimitiveType.Quad;
                default: throw new Exception($"unknown primitive '{s}' (cube, sphere, capsule, cylinder, plane, quad)");
            }
        }
    }
}
