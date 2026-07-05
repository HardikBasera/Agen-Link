using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AgenLink.Ops
{
    /// <summary>
    /// Asset-database operations that keep GUIDs and .meta files consistent — the CLI must use these instead
    /// of plain file moves/copies/deletes. Every result is permanent:true (delete goes to the OS trash, so it
    /// is recoverable). Nested params (material properties) arrive as the raw request line for Newtonsoft.
    /// </summary>
    internal static class AssetOps
    {
        public static string Handle(string requestLine)
        {
            var req = JObject.Parse(requestLine);
            if (!(req["params"] is JObject pr)) throw new Exception("manage_asset requires params");
            string action = ((string)pr["action"] ?? "").ToLowerInvariant();
            string path = (string)pr["path"];

            switch (action)
            {
                case "move":
                {
                    string dest = (string)pr["destination"];
                    RequirePaths(path, dest);
                    EnsureParentFolder(dest);
                    string err = AssetDatabase.MoveAsset(path, dest);
                    if (!string.IsNullOrEmpty(err)) throw new Exception("move failed: " + err);
                    return Done("move", dest, $"moved {path} -> {dest}");
                }
                case "copy":
                {
                    string dest = (string)pr["destination"];
                    RequirePaths(path, dest);
                    EnsureParentFolder(dest);
                    if (!AssetDatabase.CopyAsset(path, dest)) throw new Exception($"could not copy {path} -> {dest}");
                    return Done("copy", dest, $"copied {path} -> {dest}");
                }
                case "delete":
                {
                    if (string.IsNullOrEmpty(path)) throw new Exception("delete requires path");
                    if (!AssetDatabase.MoveAssetToTrash(path)) throw new Exception($"could not delete {path}");
                    return Done("delete", path, $"{path} moved to the OS trash (recoverable)");
                }
                case "create_prefab":
                {
                    string targetRef = (string)pr["target"];
                    if (string.IsNullOrEmpty(path)) throw new Exception("create_prefab requires path (e.g. 'Assets/Prefabs/Foo.prefab')");
                    GameObject go = ObjectResolver.ResolveGameObject(targetRef);
                    EnsureParentFolder(path);
                    GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.UserAction, out bool ok);
                    if (!ok || prefab == null) throw new Exception("failed to create prefab from " + go.name);
                    return Done("create_prefab", path, $"prefab '{prefab.name}' created and the scene object connected to it");
                }
                case "create_material":
                {
                    if (string.IsNullOrEmpty(path)) throw new Exception("create_material requires path (e.g. 'Assets/Materials/Foo.mat')");
                    string shaderName = (string)pr["shader"];
                    Shader shader = ResolveShader(shaderName);
                    var mat = new Material(shader);
                    if (pr["properties"] is JObject mp) ApplyMaterialProps(mat, mp);
                    EnsureParentFolder(path);
                    AssetDatabase.CreateAsset(mat, path);
                    return Done("create_material", path, $"material created with shader '{shader.name}'");
                }
                default:
                    throw new Exception("manage_asset action must be move, copy, delete, create_prefab, or create_material");
            }
        }

        private static void ApplyMaterialProps(Material mat, JObject props)
        {
            Shader shader = mat.shader;
            foreach (JProperty jp in props.Properties())
            {
                int idx = shader.FindPropertyIndex(jp.Name);
                if (idx < 0) throw new Exception($"shader '{shader.name}' has no property '{jp.Name}'");
                switch (shader.GetPropertyType(idx))
                {
                    case ShaderPropertyType.Color: mat.SetColor(jp.Name, ToColor(jp.Value)); break;
                    case ShaderPropertyType.Vector: mat.SetVector(jp.Name, ToVector(jp.Value)); break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range: mat.SetFloat(jp.Name, (float)jp.Value); break;
                    case ShaderPropertyType.Texture:
                        mat.SetTexture(jp.Name, ObjectResolver.ResolveAsset((string)jp.Value, typeof(Texture)) as Texture); break;
                    default: throw new Exception($"unsupported shader property type for '{jp.Name}'");
                }
            }
        }

        private static Shader ResolveShader(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                Shader s = Shader.Find(name);
                if (s == null) throw new Exception($"shader '{name}' not found");
                return s;
            }
            // No shader given: pick a sensible default for the active render pipeline.
            string[] candidates = GraphicsSettings.currentRenderPipeline != null
                ? new[] { "Universal Render Pipeline/Lit", "HDRP/Lit", "Standard" }
                : new[] { "Standard", "Universal Render Pipeline/Lit" };
            foreach (string c in candidates) { Shader s = Shader.Find(c); if (s != null) return s; }
            throw new Exception("could not resolve a default shader — pass 'shader' explicitly");
        }

        private static Color ToColor(JToken t)
        {
            if (t is JArray a) return new Color((float)a[0], (float)a[1], (float)a[2], a.Count > 3 ? (float)a[3] : 1f);
            return new Color((float)t["r"], (float)t["g"], (float)t["b"], t["a"] != null ? (float)t["a"] : 1f);
        }

        private static Vector4 ToVector(JToken t)
        {
            if (t is JArray a) return new Vector4((float)a[0], a.Count > 1 ? (float)a[1] : 0, a.Count > 2 ? (float)a[2] : 0, a.Count > 3 ? (float)a[3] : 0);
            return new Vector4((float)t["x"], t["y"] != null ? (float)t["y"] : 0, t["z"] != null ? (float)t["z"] : 0, t["w"] != null ? (float)t["w"] : 0);
        }

        private static void RequirePaths(string src, string dest)
        {
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dest)) throw new Exception("this action requires both path and destination");
        }

        /// <summary>Create any missing folders in an asset path's parent chain (AssetDatabase-aware).</summary>
        private static void EnsureParentFolder(string assetPath)
        {
            int slash = assetPath.LastIndexOf('/');
            if (slash < 0) return;
            string folder = assetPath.Substring(0, slash);
            if (AssetDatabase.IsValidFolder(folder)) return;

            string[] parts = folder.Split('/');
            string cur = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        private static string Done(string action, string path, string detail) =>
            new JObj().S("action", action).S("path", path).B("ok", true).B("permanent", true).S("result", detail).Build();
    }
}
