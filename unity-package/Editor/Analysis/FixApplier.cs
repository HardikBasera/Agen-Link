using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AgenLink.Analysis
{
    /// <summary>
    /// Applies WHITELISTED optimization fixes from the audit. Scene-object fixes register Undo (Ctrl+Z
    /// reverts each one) and mark the scene dirty but NEVER save — the user reviews, then saves. Asset
    /// import fixes reimport immediately and are flagged permanent:true in the response so the AI warns.
    /// Targets: scene hierarchy path ("Temple/Props/Diya_01") or asset path ("Assets/Tex/wall.png").
    /// </summary>
    internal sealed class FixRequest
    {
        public string Type;
        public string Target;
        public string Value;
    }

    /// <summary>Outcome of one fix. ToJson matches the bridge's per-fix result shapes exactly.</summary>
    internal sealed class FixResult
    {
        public string Type;
        public string Target;
        public string Detail;     // success message (Ok only)
        public string Error;      // failure message (!Ok only)
        public bool Ok;
        public bool Permanent;    // asset reimport (no Undo) vs Undo-able scene change

        public string ToJson() => Ok
            ? new JObj().S("type", Type).S("target", Target).B("ok", true)
                .S("result", Detail).B("permanent", Permanent).Build()
            : new JObj().S("type", Type).S("target", Target).B("ok", false)
                .S("error", Error).Build();
    }

    internal static class FixApplier
    {
        private const string UndoLabel = "Agen-Link fix";

        /// <summary>Parses the raw request line (the fixes array is too nested for JsonUtility).</summary>
        public static string Apply(string requestLine)
        {
            var req = JObject.Parse(requestLine);
            if (!(req["params"]?["fixes"] is JArray fixes) || fixes.Count == 0)
                throw new Exception("apply_fixes requires params.fixes: [{type, target, value?}]");

            var list = new List<FixRequest>();
            foreach (JToken fix in fixes)
                list.Add(new FixRequest
                {
                    Type = (string)fix["type"],
                    Target = (string)fix["target"],
                    Value = fix["value"]?.ToString(),
                });

            List<FixResult> results = ApplyFixes(list, "bridge", out bool touchedScene);
            var elems = new List<string>();
            foreach (FixResult r in results) elems.Add(r.ToJson());
            return new JObj()
                .N("applied", results.Count)
                .B("sceneDirty", touchedScene)
                .S("note", touchedScene
                    ? "Scene fixes are NOT saved — review in the editor (Ctrl+Z reverts any fix), then save the scene."
                    : null)
                .Raw("results", Json.Arr(elems))
                .Build();
        }

        /// <summary>
        /// Typed core shared by the bridge (Apply) and the Analysis tab. Marks the scene dirty when any
        /// scene fix landed, and logs the apply event to AnalysisLog so it shows in the History tab —
        /// from BOTH paths. source: "tab" | "bridge" | null (null skips history logging; used by tests).
        /// </summary>
        internal static List<FixResult> ApplyFixes(IList<FixRequest> fixes, string source, out bool touchedScene)
        {
            var results = new List<FixResult>();
            touchedScene = false;
            foreach (FixRequest fix in fixes)
            {
                try
                {
                    bool sceneChange = ApplyOne(fix.Type, fix.Target, fix.Value, out string detail, out bool permanent);
                    touchedScene |= sceneChange;
                    results.Add(new FixResult { Type = fix.Type, Target = fix.Target, Ok = true, Detail = detail, Permanent = permanent });
                }
                catch (Exception e)
                {
                    results.Add(new FixResult { Type = fix.Type, Target = fix.Target, Ok = false, Error = e.Message });
                }
            }

            if (touchedScene) EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            if (source != null)
            {
                try { AnalysisLog.Append(ConfigBuilder.ProjectRoot(), source, results, touchedScene); }
                catch { /* history is best-effort */ }
            }
            return results;
        }

        /// <summary>True for asset-import ops (reimport immediately, no Undo) — the UI tags these PERMANENT.</summary>
        internal static bool IsPermanentFix(string type) =>
            type == "set_texture_max_size" || type == "set_texture_compression" ||
            type == "set_audio_load_type" || type == "set_mesh_readwrite" ||
            type == "set_texture_mip_streaming" || type == "set_mesh_compression";

        /// <summary>Returns true when the fix mutated the open scene (vs an asset import setting).</summary>
        private static bool ApplyOne(string type, string target, string value, out string detail, out bool permanent)
        {
            permanent = false;
            switch (type)
            {
                // ---------- scene-object fixes (Undo-able, not saved) ----------
                case "set_static_flags":
                {
                    GameObject go = ResolveSceneObject(target);
                    bool on = ParseBool(value, true);
                    int count = 0;
                    foreach (Transform tr in go.GetComponentsInChildren<Transform>(true))
                    {
                        Undo.RegisterCompleteObjectUndo(tr.gameObject, UndoLabel);
                        GameObjectUtility.SetStaticEditorFlags(tr.gameObject, on ? (StaticEditorFlags)~0 : 0);
                        count++;
                    }
                    detail = $"static={on} on {count} object(s) incl. children";
                    return true;
                }
                case "set_light_mode":
                {
                    var light = ResolveComponent<Light>(target);
                    Undo.RecordObject(light, UndoLabel);
                    light.lightmapBakeType = ParseEnum(value, LightmapBakeType.Baked);
                    detail = "light mode -> " + light.lightmapBakeType + " (re-bake lighting to take effect)";
                    return true;
                }
                case "set_light_shadows":
                {
                    var light = ResolveComponent<Light>(target);
                    Undo.RecordObject(light, UndoLabel);
                    light.shadows = ParseEnum(value, LightShadows.None);
                    detail = "light shadows -> " + light.shadows;
                    return true;
                }
                case "set_shadow_casting":
                {
                    var renderer = ResolveComponent<Renderer>(target);
                    Undo.RecordObject(renderer, UndoLabel);
                    renderer.shadowCastingMode = ParseBool(value, false) ? ShadowCastingMode.On : ShadowCastingMode.Off;
                    detail = "shadow casting -> " + renderer.shadowCastingMode;
                    return true;
                }
                case "set_camera_far":
                {
                    var cam = ResolveComponent<Camera>(target);
                    Undo.RecordObject(cam, UndoLabel);
                    cam.farClipPlane = ParseFloat(value, 300f);
                    detail = "far clip plane -> " + cam.farClipPlane;
                    return true;
                }
                case "set_particle_max":
                {
                    var ps = ResolveComponent<ParticleSystem>(target);
                    Undo.RecordObject(ps, UndoLabel);
                    ParticleSystem.MainModule main = ps.main;
                    main.maxParticles = (int)ParseFloat(value, 500);
                    detail = "maxParticles -> " + main.maxParticles;
                    return true;
                }
                case "set_reflection_probe_mode":
                {
                    var probe = ResolveComponent<ReflectionProbe>(target);
                    Undo.RecordObject(probe, UndoLabel);
                    probe.mode = ParseEnum(value, ReflectionProbeMode.Baked);
                    detail = "probe mode -> " + probe.mode + " (re-bake to take effect)";
                    return true;
                }
                case "add_lod_group":
                {
                    GameObject go = ResolveSceneObject(target);
                    if (go.GetComponent<LODGroup>() != null) throw new Exception("LODGroup already present");
                    float cull = Mathf.Clamp(ParseFloat(value, 0.02f), 0.001f, 0.5f);
                    var lodGroup = Undo.AddComponent<LODGroup>(go);
                    Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
                    lodGroup.SetLODs(new[] { new LOD(cull, renderers) });   // single LOD: culls when small
                    detail = $"LODGroup added: {renderers.Length} renderer(s) cull below {cull:P0} screen height";
                    return true;
                }

                // ---------- asset import fixes (reimport; permanent) ----------
                case "set_texture_max_size":
                {
                    var ti = ResolveImporter<TextureImporter>(target);
                    ti.maxTextureSize = (int)ParseFloat(value, 1024);
                    ti.SaveAndReimport();
                    permanent = true;
                    detail = "max texture size -> " + ti.maxTextureSize + " (reimported)";
                    return false;
                }
                case "set_texture_compression":
                {
                    var ti = ResolveImporter<TextureImporter>(target);
                    if (string.Equals(value, "astc", StringComparison.OrdinalIgnoreCase))
                    {
                        TextureImporterPlatformSettings android = ti.GetPlatformTextureSettings("Android");
                        android.overridden = true;
                        android.format = TextureImporterFormat.ASTC_6x6;
                        android.maxTextureSize = ti.maxTextureSize;
                        ti.SetPlatformTextureSettings(android);
                        detail = "Android override -> ASTC 6x6 (reimported)";
                    }
                    else
                    {
                        ti.textureCompression = TextureImporterCompression.Compressed;
                        detail = "compression -> Compressed (reimported)";
                    }
                    ti.SaveAndReimport();
                    permanent = true;
                    return false;
                }
                case "set_audio_load_type":
                {
                    var ai = ResolveImporter<AudioImporter>(target);
                    AudioImporterSampleSettings s = ai.defaultSampleSettings;
                    s.loadType = string.Equals(value, "streaming", StringComparison.OrdinalIgnoreCase)
                        ? AudioClipLoadType.Streaming
                        : AudioClipLoadType.CompressedInMemory;
                    ai.defaultSampleSettings = s;
                    ai.SaveAndReimport();
                    permanent = true;
                    detail = "audio load type -> " + s.loadType + " (reimported)";
                    return false;
                }
                case "set_mesh_readwrite":
                {
                    var mi = ResolveImporter<ModelImporter>(target);
                    mi.isReadable = ParseBool(value, false);
                    mi.SaveAndReimport();
                    permanent = true;
                    detail = "mesh Read/Write -> " + mi.isReadable + " (reimported)";
                    return false;
                }
                case "set_texture_mip_streaming":
                {
                    var ti = ResolveImporter<TextureImporter>(target);
                    ti.streamingMipmaps = ParseBool(value, true);
                    ti.SaveAndReimport();
                    permanent = true;
                    detail = "mipmap streaming -> " + ti.streamingMipmaps + " (reimported)";
                    return false;
                }
                case "set_mesh_compression":
                {
                    var mi = ResolveImporter<ModelImporter>(target);
                    mi.meshCompression = ParseEnum(value, ModelImporterMeshCompression.Medium);
                    mi.SaveAndReimport();
                    permanent = true;
                    detail = "mesh compression -> " + mi.meshCompression + " (reimported)";
                    return false;
                }

                default:
                    throw new Exception($"Unknown fix type '{type}'. Allowed: set_static_flags, set_light_mode, " +
                        "set_light_shadows, set_shadow_casting, set_camera_far, set_particle_max, " +
                        "set_reflection_probe_mode, add_lod_group, set_texture_max_size, set_texture_compression, " +
                        "set_audio_load_type, set_mesh_readwrite, set_texture_mip_streaming, set_mesh_compression");
            }
        }

        // ---------- target resolution ----------

        /// <summary>Resolve a scene hierarchy path ("Root/Child/Leaf"). Errors on missing or ambiguous.</summary>
        internal static GameObject ResolveSceneObject(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new Exception("missing target");
            var matches = new List<GameObject>();
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
                Match(root.transform, path.Split('/'), 0, matches);
            if (matches.Count == 0) throw new Exception($"no scene object at path '{path}'");
            if (matches.Count > 1) throw new Exception($"ambiguous: {matches.Count} objects match '{path}'");
            return matches[0];
        }

        private static void Match(Transform tr, string[] parts, int depth, List<GameObject> matches)
        {
            if (tr.name != parts[depth]) return;
            if (depth == parts.Length - 1) { matches.Add(tr.gameObject); return; }
            foreach (Transform child in tr) Match(child, parts, depth + 1, matches);
        }

        private static T ResolveComponent<T>(string path) where T : Component
        {
            GameObject go = ResolveSceneObject(path);
            var comp = go.GetComponent<T>();
            if (comp == null) throw new Exception($"'{path}' has no {typeof(T).Name}");
            return comp;
        }

        private static T ResolveImporter<T>(string assetPath) where T : AssetImporter
        {
            var importer = AssetImporter.GetAtPath(assetPath) as T;
            if (importer == null) throw new Exception($"'{assetPath}' is not a {typeof(T).Name} asset");
            return importer;
        }

        // ---------- value parsing ----------

        private static bool ParseBool(string v, bool def) =>
            string.IsNullOrEmpty(v) ? def : v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";

        private static float ParseFloat(string v, float def) =>
            float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float r) ? r : def;

        private static T ParseEnum<T>(string v, T def) where T : struct =>
            Enum.TryParse(v, true, out T r) ? r : def;
    }
}
