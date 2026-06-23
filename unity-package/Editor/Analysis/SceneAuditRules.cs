namespace AgenLink.Analysis
{
    /// <summary>
    /// Pure threshold logic for the scene/asset audit — mobile/VR-biased numbers (the typical Agen-Link
    /// project targets Android/Quest). Kept free of Unity API so every classification is unit-testable,
    /// and all tuning lives in one place.
    /// </summary>
    internal static class SceneAuditRules
    {
        // Per-renderer triangle budgets.
        public const int TriWarn = 30_000;
        public const int TriCritical = 100_000;
        // Whole-scene triangle budgets.
        public const long SceneTriWarn = 500_000;
        public const long SceneTriCritical = 1_200_000;
        // A renderer above this with no LODGroup should get one.
        public const int LodlessTriWarn = 20_000;
        // MeshCollider sharing a mesh above this is expensive for physics.
        public const int MeshColliderTriWarn = 10_000;
        // Realtime light counts (forward mobile).
        public const int RealtimeLightsWarn = 3;
        public const int RealtimeLightsCritical = 6;
        public const int ShadowedRealtimeWarn = 2;
        // Camera planes.
        public const float FarClipWarn = 1000f;
        public const float NearClipTiny = 0.01f;
        // Particles.
        public const int ParticlesWarn = 1000;
        public const int ParticlesCritical = 5000;
        // Textures.
        public const int TexSizeWarn = 2048;
        public const int TexSizeCritical = 4096;
        // Audio clips bigger than this should not Decompress-On-Load.
        public const long AudioDecompressWarnBytes = 1_000_000;
        // Rendering variety (batching pressure).
        public const int TransparentMaterialsWarn = 20;
        // Physics.
        public const int RigidbodyWarn = 50;
        // URP, mobile-biased.
        public const float ShadowDistanceWarn = 60f;
        public const int ShadowCascadesWarn = 3;   // >2 cascades is heavy on mobile
        // Lighting.
        public const int LightmapSizeWarn = 2048;       // per-lightmap texture size on mobile
        // Play-mode perf budgets (PerfAssessment; editor numbers are indicative).
        public const double FrameMsBudget72 = 13.9;     // 72 Hz Quest frame budget
        public const double FrameMsBudget60 = 16.7;     // 60 fps frame budget
        public const double FrameSpikeRatio = 1.5;      // p95 > avg * this -> stutter signature
        public const double GcWarnBytes = 1024;         // per-frame GC alloc
        public const double GcCriticalBytes = 16_384;
        public const int BatchesWarn = 150;
        public const int BatchesCritical = 300;
        public const int SetPassWarn = 50;
        public const int SetPassCritical = 100;

        /// <summary>"critical" | "warn" | null for a value against warn/critical thresholds.</summary>
        public static string Classify(long value, long warn, long critical) =>
            value >= critical ? "critical" : value >= warn ? "warn" : null;

        public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;
    }
}
