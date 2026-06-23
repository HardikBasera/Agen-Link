using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AgenLink.Analysis
{
    /// <summary>Scene-wide counters behind the audit verdicts. ToJson keys match the bridge's stats block.</summary>
    internal sealed class SceneStats
    {
        public long Triangles;
        public int Renderers, StaticRenderers, RealtimeLights, ShadowedRealtimeLights,
                   DistinctMaterials, DistinctShaders, TransparentMaterialSlots,
                   Rigidbodies, ParticleSystems;
        public bool LightmapsBaked, OcclusionBaked;

        public string ToJson() => new JObj()
            .N("sceneTriangles", Triangles)
            .N("renderers", Renderers)
            .N("staticRenderers", StaticRenderers)
            .N("realtimeLights", RealtimeLights)
            .N("shadowedRealtimeLights", ShadowedRealtimeLights)
            .N("distinctMaterials", DistinctMaterials)
            .N("distinctShaders", DistinctShaders)
            .N("transparentMaterialSlots", TransparentMaterialSlots)
            .N("rigidbodies", Rigidbodies)
            .N("particleSystems", ParticleSystems)
            .B("lightmapsBaked", LightmapsBaked)
            .B("occlusionBaked", OcclusionBaked)
            .Build();

        public static SceneStats FromJObject(JObject o) => new SceneStats
        {
            Triangles = (long?)o["sceneTriangles"] ?? 0,
            Renderers = (int?)o["renderers"] ?? 0,
            StaticRenderers = (int?)o["staticRenderers"] ?? 0,
            RealtimeLights = (int?)o["realtimeLights"] ?? 0,
            ShadowedRealtimeLights = (int?)o["shadowedRealtimeLights"] ?? 0,
            DistinctMaterials = (int?)o["distinctMaterials"] ?? 0,
            DistinctShaders = (int?)o["distinctShaders"] ?? 0,
            TransparentMaterialSlots = (int?)o["transparentMaterialSlots"] ?? 0,
            Rigidbodies = (int?)o["rigidbodies"] ?? 0,
            ParticleSystems = (int?)o["particleSystems"] ?? 0,
            LightmapsBaked = (bool?)o["lightmapsBaked"] ?? false,
            OcclusionBaked = (bool?)o["occlusionBaked"] ?? false,
        };
    }

    /// <summary>
    /// Rules-based optimization audit of the ACTIVE scene: geometry, lighting, rendering/batching,
    /// physics, UI, plus render-pipeline / quality settings. Returns structured findings the AI turns
    /// into a report (and can partly auto-fix via apply_fixes). Pipeline-agnostic: URP asset properties
    /// are probed via reflection so the package compiles without a URP assembly reference.
    /// </summary>
    internal static class SceneAuditor
    {
        public static string Run(int maxFindings)
        {
            int max = maxFindings > 0 ? maxFindings : 200;
            List<Finding> f = Collect(out SceneStats stats);
            return Finding.BuildReport(f, max, stats.ToJson());
        }

        /// <summary>Typed audit for direct callers (the Analysis tab); Run() wraps this for the bridge.</summary>
        internal static List<Finding> Collect(out SceneStats stats)
        {
            var f = new List<Finding>();

            var scene = SceneManager.GetActiveScene();
            long sceneTris = 0;
            int renderers = 0, staticRenderers = 0, realtimeLights = 0, shadowedRealtime = 0,
                transparentMats = 0, rigidbodies = 0, particleSystems = 0;
            var materials = new HashSet<Material>();
            var shaders = new HashSet<Shader>();

            foreach (GameObject root in scene.GetRootGameObjects())
                Walk(root.transform, f, ref sceneTris, ref renderers, ref staticRenderers,
                     ref realtimeLights, ref shadowedRealtime, ref transparentMats,
                     ref rigidbodies, ref particleSystems, materials, shaders);

            // ---- scene-level rules ----
            string sev = SceneAuditRules.Classify(sceneTris, SceneAuditRules.SceneTriWarn, SceneAuditRules.SceneTriCritical);
            if (sev != null) f.Add(new Finding
            {
                Id = "scene.triangle-budget", Severity = sev, Category = "geometry", Target = scene.name,
                Evidence = $"{sceneTris:n0} triangles in scene",
                Recommendation = "Reduce total scene polycount: decimate heavy meshes, add LODs/culling, remove hidden geometry.",
            });

            sev = SceneAuditRules.Classify(realtimeLights, SceneAuditRules.RealtimeLightsWarn, SceneAuditRules.RealtimeLightsCritical);
            if (sev != null) f.Add(new Finding
            {
                Id = "light.realtime-count", Severity = sev, Category = "lighting", Target = scene.name,
                Evidence = $"{realtimeLights} realtime lights ({shadowedRealtime} with shadows)",
                Recommendation = "Bake static lighting (set lights to Baked/Mixed) — realtime lights are expensive in mobile forward rendering.",
            });
            if (shadowedRealtime > SceneAuditRules.ShadowedRealtimeWarn) f.Add(new Finding
            {
                Id = "light.realtime-shadows", Severity = "warn", Category = "lighting", Target = scene.name,
                Evidence = $"{shadowedRealtime} realtime lights cast shadows",
                Recommendation = "Limit realtime shadow casters to one key light; bake or disable the rest.",
            });

            if (LightmapSettings.lightmaps.Length == 0 && staticRenderers > 10) f.Add(new Finding
            {
                Id = "light.no-lightmaps", Severity = "warn", Category = "lighting", Target = scene.name,
                Evidence = $"no baked lightmaps; {staticRenderers} static renderers present",
                Recommendation = "Bake lighting (Window ▸ Rendering ▸ Lighting ▸ Generate) so static geometry doesn't need realtime lights.",
            });

            foreach (LightmapData lm in LightmapSettings.lightmaps)
            {
                Texture2D lmTex = lm.lightmapColor;
                if (lmTex == null || Mathf.Max(lmTex.width, lmTex.height) <= SceneAuditRules.LightmapSizeWarn) continue;
                f.Add(new Finding
                {
                    Id = "light.large-lightmaps", Severity = "warn", Category = "lighting", Target = scene.name,
                    Evidence = $"{LightmapSettings.lightmaps.Length} lightmap(s), largest {lmTex.width}x{lmTex.height}",
                    Recommendation = $"Cap Max Lightmap Size at {SceneAuditRules.LightmapSizeWarn} for mobile — oversized lightmaps cost memory and bandwidth.",
                });
                break;   // one finding for the whole set
            }

            if (StaticOcclusionCulling.umbraDataSize == 0 && renderers > 50) f.Add(new Finding
            {
                Id = "scene.no-occlusion", Severity = "info", Category = "rendering", Target = scene.name,
                Evidence = $"no occlusion culling data; {renderers} renderers",
                Recommendation = "Bake occlusion culling (Window ▸ Rendering ▸ Occlusion Culling) for indoor/occluded scenes.",
            });

            if (transparentMats > SceneAuditRules.TransparentMaterialsWarn) f.Add(new Finding
            {
                Id = "render.transparent-overdraw", Severity = "warn", Category = "rendering", Target = scene.name,
                Evidence = $"{transparentMats} transparent material slots in scene",
                Recommendation = "Transparent surfaces cause overdraw (costly on tiled mobile GPUs). Convert to opaque/cutout where possible.",
            });

            if (rigidbodies > SceneAuditRules.RigidbodyWarn) f.Add(new Finding
            {
                Id = "physics.rigidbody-count", Severity = "warn", Category = "physics", Target = scene.name,
                Evidence = $"{rigidbodies} Rigidbodies",
                Recommendation = "Many active Rigidbodies are CPU-heavy; sleep, pool, or merge where possible.",
            });

            AuditPipelineAndQuality(f);

            stats = new SceneStats
            {
                Triangles = sceneTris,
                Renderers = renderers,
                StaticRenderers = staticRenderers,
                RealtimeLights = realtimeLights,
                ShadowedRealtimeLights = shadowedRealtime,
                DistinctMaterials = materials.Count,
                DistinctShaders = shaders.Count,
                TransparentMaterialSlots = transparentMats,
                Rigidbodies = rigidbodies,
                ParticleSystems = particleSystems,
                LightmapsBaked = LightmapSettings.lightmaps.Length > 0,
                OcclusionBaked = StaticOcclusionCulling.umbraDataSize > 0,
            };
            return f;
        }

        private static void Walk(Transform tr, List<Finding> f, ref long sceneTris, ref int renderers,
            ref int staticRenderers, ref int realtimeLights, ref int shadowedRealtime, ref int transparentMats,
            ref int rigidbodies, ref int particleSystems, HashSet<Material> materials, HashSet<Shader> shaders)
        {
            GameObject go = tr.gameObject;
            string path = HierarchyPath(tr);

            foreach (Component comp in go.GetComponents<Component>())
            {
                if (comp == null)
                {
                    f.Add(new Finding
                    {
                        Id = "scene.missing-script", Severity = "warn", Category = "settings", Target = path,
                        Evidence = "missing script reference",
                        Recommendation = "Remove the dead component or restore the script it referenced.",
                    });
                    continue;
                }

                switch (comp)
                {
                    case MeshRenderer mr when go.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null:
                        AuditRenderer(go, path, mf.sharedMesh, mr, f, ref sceneTris, ref staticRenderers, ref transparentMats, materials, shaders);
                        renderers++;
                        break;
                    case SkinnedMeshRenderer smr when smr.sharedMesh != null:
                        AuditRenderer(go, path, smr.sharedMesh, smr, f, ref sceneTris, ref staticRenderers, ref transparentMats, materials, shaders);
                        renderers++;
                        break;
                    case Light light:
                        if (light.lightmapBakeType == LightmapBakeType.Realtime)
                        {
                            realtimeLights++;
                            if (light.shadows != LightShadows.None) shadowedRealtime++;
                            f.Add(new Finding
                            {
                                Id = "light.realtime", Severity = "info", Category = "lighting", Target = path,
                                Evidence = $"{light.type} light, Realtime, shadows={light.shadows}",
                                Recommendation = "If this light never moves, set it to Baked (or Mixed) and re-bake lighting.",
                                FixType = "set_light_mode", FixValue = "baked",
                            });
                        }
                        break;
                    case Camera cam:
                        if (cam.farClipPlane > SceneAuditRules.FarClipWarn) f.Add(new Finding
                        {
                            Id = "camera.far-plane", Severity = "info", Category = "rendering", Target = path,
                            Evidence = $"far clip plane {cam.farClipPlane:n0}",
                            Recommendation = "A huge far plane hurts depth precision and culling; lower it to the real view distance.",
                            FixType = "set_camera_far", FixValue = "300",
                        });
                        if (cam.nearClipPlane < SceneAuditRules.NearClipTiny) f.Add(new Finding
                        {
                            Id = "camera.near-plane", Severity = "info", Category = "rendering", Target = path,
                            Evidence = $"near clip plane {cam.nearClipPlane}",
                            Recommendation = "A near plane this small causes z-fighting; raise it (e.g. 0.05).",
                        });
                        break;
                    case ParticleSystem ps:
                        particleSystems++;
                        int maxP = ps.main.maxParticles;
                        string psev = SceneAuditRules.Classify(maxP, SceneAuditRules.ParticlesWarn, SceneAuditRules.ParticlesCritical);
                        if (psev != null) f.Add(new Finding
                        {
                            Id = "particles.max", Severity = psev, Category = "rendering", Target = path,
                            Evidence = $"maxParticles {maxP:n0}",
                            Recommendation = "Cap maxParticles to what the effect actually needs.",
                            FixType = "set_particle_max", FixValue = "500",
                        });
                        break;
                    case ReflectionProbe probe when probe.mode == ReflectionProbeMode.Realtime:
                        f.Add(new Finding
                        {
                            Id = "render.realtime-probe", Severity = "warn", Category = "rendering", Target = path,
                            Evidence = "realtime reflection probe",
                            Recommendation = "Realtime probes re-render the scene; set to Baked unless reflections must move.",
                            FixType = "set_reflection_probe_mode", FixValue = "baked",
                        });
                        break;
                    case Rigidbody _:
                        rigidbodies++;
                        break;
                    case MeshCollider mc when mc.sharedMesh != null:
                        int colTris = TriCount(mc.sharedMesh);
                        if (colTris > SceneAuditRules.MeshColliderTriWarn) f.Add(new Finding
                        {
                            Id = "physics.heavy-meshcollider", Severity = "warn", Category = "physics", Target = path,
                            Evidence = $"MeshCollider over {colTris:n0}-triangle mesh",
                            Recommendation = "Replace with primitive colliders or a decimated collision mesh.",
                        });
                        break;
                }
            }

            foreach (Transform child in tr)
                Walk(child, f, ref sceneTris, ref renderers, ref staticRenderers, ref realtimeLights,
                     ref shadowedRealtime, ref transparentMats, ref rigidbodies, ref particleSystems, materials, shaders);
        }

        private static void AuditRenderer(GameObject go, string path, Mesh mesh, Renderer r, List<Finding> f,
            ref long sceneTris, ref int staticRenderers, ref int transparentMats,
            HashSet<Material> materials, HashSet<Shader> shaders)
        {
            int tris = TriCount(mesh);
            sceneTris += tris;
            if (go.isStatic) staticRenderers++;

            foreach (Material m in r.sharedMaterials)
            {
                if (m == null) continue;
                materials.Add(m);
                if (m.shader != null) shaders.Add(m.shader);
                if (m.renderQueue >= 3000) transparentMats++;
            }

            string sev = SceneAuditRules.Classify(tris, SceneAuditRules.TriWarn, SceneAuditRules.TriCritical);
            if (sev != null) f.Add(new Finding
            {
                Id = "mesh.high-poly", Severity = sev, Category = "geometry", Target = path,
                Evidence = $"{tris:n0} triangles ({mesh.name})",
                Recommendation = "Decimate this mesh or split/LOD it — it exceeds a sensible mobile/VR per-object budget.",
            });

            if (tris > SceneAuditRules.LodlessTriWarn && go.GetComponentInParent<LODGroup>() == null)
                f.Add(new Finding
                {
                    Id = "mesh.no-lod", Severity = "warn", Category = "geometry", Target = path,
                    Evidence = $"{tris:n0} triangles, no LODGroup in parents",
                    Recommendation = "Add a LODGroup (at minimum a cull LOD so it disappears when small on screen).",
                    FixType = "add_lod_group", FixValue = "0.02",
                });

            if (!go.isStatic && !(r is SkinnedMeshRenderer) &&
                go.GetComponentInParent<Rigidbody>() == null && go.GetComponentInParent<Animator>() == null)
                f.Add(new Finding
                {
                    Id = "mesh.not-static", Severity = "info", Category = "rendering", Target = path,
                    Evidence = "non-static renderer with no Rigidbody/Animator in parents",
                    Recommendation = "If this object never moves, mark it static for batching/lightmaps/occlusion.",
                    FixType = "set_static_flags", FixValue = "true",
                });
        }

        /// <summary>Allocation-free triangle count (GetIndexCount, not mesh.triangles).</summary>
        private static int TriCount(Mesh mesh)
        {
            long indices = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) indices += mesh.GetIndexCount(s);
            return (int)(indices / 3);
        }

        private static string HierarchyPath(Transform tr)
        {
            string path = tr.name;
            while (tr.parent != null) { tr = tr.parent; path = tr.name + "/" + path; }
            return path;
        }

        // ---- pipeline / quality (reflection — no URP assembly reference) ----

        private static void AuditPipelineAndQuality(List<Finding> f)
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp != null)
            {
                string target = rp.name + " (render pipeline asset)";
                if (Prop<bool>(rp, "supportsHDR") == true) f.Add(new Finding
                {
                    Id = "urp.hdr", Severity = "warn", Category = "settings", Target = target,
                    Evidence = "HDR enabled on the pipeline asset",
                    Recommendation = "HDR is expensive on mobile/VR tiled GPUs; disable it unless the look requires it.",
                });
                float? shadowDist = Prop<float>(rp, "shadowDistance");
                if (shadowDist > SceneAuditRules.ShadowDistanceWarn) f.Add(new Finding
                {
                    Id = "urp.shadow-distance", Severity = "warn", Category = "settings", Target = target,
                    Evidence = $"shadow distance {shadowDist:n0}",
                    Recommendation = "Lower the shadow distance to the gameplay-relevant range; distance drives shadow cost.",
                });
                int? cascades = Prop<int>(rp, "shadowCascadeCount");
                if (cascades > SceneAuditRules.ShadowCascadesWarn) f.Add(new Finding
                {
                    Id = "urp.cascades", Severity = "info", Category = "settings", Target = target,
                    Evidence = $"{cascades} shadow cascades",
                    Recommendation = "1-2 cascades is the usual mobile budget.",
                });
                if (Prop<bool>(rp, "supportsCameraDepthTexture") == true) f.Add(new Finding
                {
                    Id = "urp.depth-texture", Severity = "info", Category = "settings", Target = target,
                    Evidence = "camera depth texture enabled",
                    Recommendation = "Depth texture costs an extra pass on mobile; disable if no effect needs it.",
                });
                if (Prop<bool>(rp, "supportsCameraOpaqueTexture") == true) f.Add(new Finding
                {
                    Id = "urp.opaque-texture", Severity = "warn", Category = "settings", Target = target,
                    Evidence = "camera opaque texture enabled",
                    Recommendation = "Opaque texture forces a costly resolve each frame; disable unless refraction-style effects need it.",
                });
            }

            // Static/dynamic batching flags for the active build target (internal API — best-effort).
            try
            {
                MethodInfo mi = typeof(PlayerSettings).GetMethod("GetBatchingForPlatform",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (mi != null)
                {
                    object[] args = { EditorUserBuildSettings.activeBuildTarget, 0, 0 };
                    mi.Invoke(null, args);
                    if ((int)args[1] == 0) f.Add(new Finding
                    {
                        Id = "settings.static-batching-off", Severity = "warn", Category = "settings",
                        Target = "Player Settings",
                        Evidence = "static batching disabled for the active build target",
                        Recommendation = "Enable static batching (Player Settings ▸ Other) so static geometry batches.",
                    });
                }
            }
            catch { /* internal API moved — skip the rule */ }
        }

        private static T? Prop<T>(object obj, string name) where T : struct
        {
            try
            {
                PropertyInfo pi = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (pi != null && pi.PropertyType == typeof(T)) return (T)pi.GetValue(obj);
            }
            catch { /* property moved between URP versions */ }
            return null;
        }
    }
}
