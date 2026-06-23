using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgenLink.Analysis
{
    /// <summary>Asset-audit counters. ToJson preserves both bridge stats shapes: the counts block,
    /// or {"note": ...} alone when the scene is unsaved and has no dependencies to audit.</summary>
    internal sealed class AssetStats
    {
        public int Textures, Models, Audios;
        public string BuildTarget;
        public string Note;   // unsaved-scene case: the only emitted field

        public string ToJson() => Note != null
            ? new JObj().S("note", Note).Build()
            : new JObj()
                .N("texturesAudited", Textures)
                .N("modelsAudited", Models)
                .N("audioClipsAudited", Audios)
                .S("buildTarget", BuildTarget)
                .Build();

        public static AssetStats FromJObject(JObject o) => new AssetStats
        {
            Textures = (int?)o["texturesAudited"] ?? 0,
            Models = (int?)o["modelsAudited"] ?? 0,
            Audios = (int?)o["audioClipsAudited"] ?? 0,
            BuildTarget = (string)o["buildTarget"],
            Note = (string)o["note"],
        };
    }

    /// <summary>
    /// Import-settings audit of every asset the ACTIVE scene depends on: texture sizes/compression
    /// (incl. the Android/ASTC override that matters for Quest), mesh read/write, audio load types.
    /// Cheap by design — reads importers and metadata, never texture pixels.
    /// </summary>
    internal static class AssetAuditor
    {
        public static string Run(int maxFindings)
        {
            int max = maxFindings > 0 ? maxFindings : 200;
            List<Finding> f = Collect(out AssetStats stats);
            return Finding.BuildReport(f, max, stats.ToJson());
        }

        /// <summary>Typed audit for direct callers (the Analysis tab); Run() wraps this for the bridge.</summary>
        internal static List<Finding> Collect(out AssetStats stats)
        {
            var f = new List<Finding>();
            int textures = 0, models = 0, audios = 0;

            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                stats = new AssetStats { Note = "scene has no path (unsaved) — no dependencies to audit" };
                return f;
            }

            foreach (string path in AssetDatabase.GetDependencies(scene.path, true))
            {
                AssetImporter importer = AssetImporter.GetAtPath(path);
                switch (importer)
                {
                    case TextureImporter ti: textures++; AuditTexture(path, ti, f); break;
                    case ModelImporter mi:   models++;   AuditModel(path, mi, f);   break;
                    case AudioImporter ai:   audios++;   AuditAudio(path, ai, f);   break;
                }
            }

            stats = new AssetStats
            {
                Textures = textures,
                Models = models,
                Audios = audios,
                BuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
            };
            return f;
        }

        private static void AuditTexture(string path, TextureImporter ti, List<Finding> f)
        {
            string sev = SceneAuditRules.Classify(ti.maxTextureSize, SceneAuditRules.TexSizeWarn, SceneAuditRules.TexSizeCritical);
            if (sev != null) f.Add(new Finding
            {
                Id = "tex.large", Severity = sev, Category = "assets", Target = path,
                Evidence = $"max texture size {ti.maxTextureSize}",
                Recommendation = "Lower the max size — most mobile/VR surfaces don't need more than 1024-2048.",
                FixType = "set_texture_max_size", FixValue = "1024",
            });

            if (ti.textureCompression == TextureImporterCompression.Uncompressed) f.Add(new Finding
            {
                Id = "tex.uncompressed", Severity = "warn", Category = "assets", Target = path,
                Evidence = "compression: Uncompressed",
                Recommendation = "Use compressed formats (ASTC on Android/Quest) — uncompressed textures blow up memory and bandwidth.",
                FixType = "set_texture_compression", FixValue = "compressed",
            });

            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android &&
                !ti.GetPlatformTextureSettings("Android").overridden)
                f.Add(new Finding
                {
                    Id = "tex.no-android-override", Severity = "info", Category = "assets", Target = path,
                    Evidence = "no Android platform override",
                    Recommendation = "Add an Android override with an ASTC format for predictable quality/size on Quest.",
                    FixType = "set_texture_compression", FixValue = "astc",
                });

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex != null &&
                (!SceneAuditRules.IsPowerOfTwo(tex.width) || !SceneAuditRules.IsPowerOfTwo(tex.height)) &&
                ti.npotScale == TextureImporterNPOTScale.None && ti.textureType != TextureImporterType.Sprite)
                f.Add(new Finding
                {
                    Id = "tex.npot", Severity = "info", Category = "assets", Target = path,
                    Evidence = $"{tex.width}x{tex.height} (non-power-of-two, NPOT scale None)",
                    Recommendation = "NPOT textures can't compress/mipmap optimally; resize to POT or set an NPOT scale mode.",
                });

            if (ti.isReadable) f.Add(new Finding
            {
                Id = "tex.readable", Severity = "info", Category = "assets", Target = path,
                Evidence = "Read/Write enabled",
                Recommendation = "Read/Write keeps a CPU copy (double memory); disable unless scripts read pixels.",
            });

            if (ti.mipmapEnabled && !ti.streamingMipmaps) f.Add(new Finding
            {
                Id = "tex.no-mip-streaming", Severity = "info", Category = "assets", Target = path,
                Evidence = "mipmaps enabled, streaming off",
                Recommendation = "Enable Mipmap Streaming so only the mip levels in use stay in memory.",
                FixType = "set_texture_mip_streaming", FixValue = "true",
            });
        }

        private static void AuditModel(string path, ModelImporter mi, List<Finding> f)
        {
            if (mi.isReadable) f.Add(new Finding
            {
                Id = "mesh.readable", Severity = "warn", Category = "assets", Target = path,
                Evidence = "Read/Write enabled",
                Recommendation = "Read/Write keeps a CPU copy of the mesh (double memory); disable unless scripts access vertices.",
                FixType = "set_mesh_readwrite", FixValue = "false",
            });

            if (mi.meshCompression == ModelImporterMeshCompression.Off) f.Add(new Finding
            {
                Id = "mesh.no-compression", Severity = "info", Category = "assets", Target = path,
                Evidence = "mesh compression Off",
                Recommendation = "Mesh compression shrinks build size with little visual cost on props; Medium is a safe start.",
                FixType = "set_mesh_compression", FixValue = "medium",
            });
        }

        private static void AuditAudio(string path, AudioImporter ai, List<Finding> f)
        {
            try
            {
                if (ai.defaultSampleSettings.loadType == AudioClipLoadType.DecompressOnLoad)
                {
                    long bytes = new FileInfo(path).Length;
                    if (bytes > SceneAuditRules.AudioDecompressWarnBytes) f.Add(new Finding
                    {
                        Id = "audio.decompress-on-load", Severity = "warn", Category = "assets", Target = path,
                        Evidence = $"Decompress On Load, source {bytes / 1024:n0} KB",
                        Recommendation = "Long clips should stream (music/ambience) or stay Compressed In Memory.",
                        FixType = "set_audio_load_type", FixValue = "streaming",
                    });
                }
            }
            catch { /* file metadata unavailable — skip */ }
        }
    }
}
