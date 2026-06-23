using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using AgenLink.Analysis;
using AgenLink.History;

public class AnalysisTests
{
    [Test]
    public void Classify_RespectsWarnAndCriticalThresholds()
    {
        Assert.IsNull(SceneAuditRules.Classify(10, 100, 200), "below warn -> null");
        Assert.AreEqual("warn", SceneAuditRules.Classify(100, 100, 200));
        Assert.AreEqual("warn", SceneAuditRules.Classify(199, 100, 200));
        Assert.AreEqual("critical", SceneAuditRules.Classify(200, 100, 200));
    }

    [Test]
    public void IsPowerOfTwo_Basics()
    {
        Assert.IsTrue(SceneAuditRules.IsPowerOfTwo(1024));
        Assert.IsTrue(SceneAuditRules.IsPowerOfTwo(1));
        Assert.IsFalse(SceneAuditRules.IsPowerOfTwo(0));
        Assert.IsFalse(SceneAuditRules.IsPowerOfTwo(1000));
    }

    [Test]
    public void Finding_SeverityRank_OrdersCriticalFirst()
    {
        Assert.Less(Finding.Rank("critical"), Finding.Rank("warn"));
        Assert.Less(Finding.Rank("warn"), Finding.Rank("info"));
    }

    // ----- FixApplier scene-target resolution (constructed objects; cleaned up per test) -----

    [Test]
    public void ResolveSceneObject_FindsByPath_AndReportsMissing()
    {
        var root = new GameObject("AgenLinkTestRoot");
        var child = new GameObject("Child");
        child.transform.SetParent(root.transform);
        try
        {
            Assert.AreSame(child, FixApplier.ResolveSceneObject("AgenLinkTestRoot/Child"));
            Assert.Throws<System.Exception>(() => FixApplier.ResolveSceneObject("AgenLinkTestRoot/Nope"));
        }
        finally { Object.DestroyImmediate(root); }
    }

    [Test]
    public void ResolveSceneObject_RejectsAmbiguousPaths()
    {
        var root = new GameObject("AgenLinkTestRoot2");
        var a = new GameObject("Twin"); a.transform.SetParent(root.transform);
        var b = new GameObject("Twin"); b.transform.SetParent(root.transform);
        try
        {
            Assert.Throws<System.Exception>(() => FixApplier.ResolveSceneObject("AgenLinkTestRoot2/Twin"));
        }
        finally { Object.DestroyImmediate(root); }
    }

    // ----- typed fix core (Analysis tab + bridge share these shapes) -----

    [Test]
    public void FixResult_ToJson_SuccessAndFailureShapes()
    {
        string ok = new FixResult { Type = "set_static_flags", Target = "A/B", Ok = true, Detail = "static=true", Permanent = false }.ToJson();
        StringAssert.Contains("\"ok\":true", ok);
        StringAssert.Contains("\"result\":\"static=true\"", ok);
        StringAssert.Contains("\"permanent\":false", ok);
        Assert.IsFalse(ok.Contains("\"error\""), "success omits error");

        string fail = new FixResult { Type = "nope", Target = "X", Ok = false, Error = "boom" }.ToJson();
        StringAssert.Contains("\"ok\":false", fail);
        StringAssert.Contains("\"error\":\"boom\"", fail);
        Assert.IsFalse(fail.Contains("\"result\""), "failure omits result");
        Assert.IsFalse(fail.Contains("\"permanent\""), "failure omits permanent");
    }

    [Test]
    public void ApplyFixes_UnknownType_ReturnsPerFixError()
    {
        List<FixResult> results = FixApplier.ApplyFixes(
            new[] { new FixRequest { Type = "nope", Target = "x" } }, source: null, out bool touched);
        Assert.AreEqual(1, results.Count);
        Assert.IsFalse(results[0].Ok);
        StringAssert.Contains("Unknown fix type", results[0].Error);
        Assert.IsFalse(touched);
    }

    [Test]
    public void Apply_RawLine_KeepsBridgeEnvelopeShape()
    {
        AnalysisLog.Disabled = true;
        try
        {
            string line = "{\"id\":\"t1\",\"command\":\"apply_fixes\",\"params\":{\"fixes\":[{\"type\":\"nope\",\"target\":\"x\"}]}}";
            string resp = FixApplier.Apply(line);
            StringAssert.Contains("\"applied\":1", resp);
            StringAssert.Contains("\"sceneDirty\":false", resp);
            StringAssert.Contains("\"ok\":false", resp);
            StringAssert.Contains("\"note\":null", resp);
        }
        finally { AnalysisLog.Disabled = false; }
    }

    // ----- AnalysisLog (History cards for fix applies) -----

    [Test]
    public void AnalysisLog_AppendAndLoad_RoundTrips()
    {
        string root = Path.Combine(Path.GetTempPath(), "agenlink-test-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var results = new List<FixResult>
            {
                new FixResult { Type = "set_static_flags", Target = "Temple/Prop", Ok = true, Detail = "static=true on 3 object(s)", Permanent = false },
                new FixResult { Type = "set_texture_max_size", Target = "Assets/t.png", Ok = true, Detail = "max 1024 (reimported)", Permanent = true },
                new FixResult { Type = "nope", Target = "x", Ok = false, Error = "Unknown fix type 'nope'" },
            };
            AnalysisLog.Append(root, "tab", results, sceneDirty: true);

            List<Conversation> convs = AnalysisLog.LoadConversations(root);
            Assert.AreEqual(1, convs.Count);
            Conversation c = convs[0];
            Assert.AreEqual("analysis", c.Agent);
            Assert.AreEqual("Applied 2 fixes · 1 failed", c.Title);
            Assert.AreEqual("Analysis tab", c.SourceNote);
            Assert.IsFalse(c.MetaOnly, "analysis cards must not trigger the agy MetaOnly message");
            Assert.AreEqual(3, c.Turns.Count);
            StringAssert.Contains("[permanent]", c.Turns[1].Text);
            StringAssert.StartsWith("✗", c.Turns[2].Text);
            Assert.AreEqual(TurnKind.Action, c.Turns[0].Kind);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public void IsPermanentFix_CoversAssetImportOps()
    {
        Assert.IsTrue(FixApplier.IsPermanentFix("set_texture_mip_streaming"));
        Assert.IsTrue(FixApplier.IsPermanentFix("set_mesh_compression"));
        Assert.IsTrue(FixApplier.IsPermanentFix("set_texture_max_size"));
        Assert.IsFalse(FixApplier.IsPermanentFix("set_static_flags"), "scene ops are Undo-able, not permanent");
    }

    // ----- perf assessment (numbers -> plain-language verdicts) -----

    [Test]
    public void PerfAssessment_FlagsBadSample_AsNotOptimized()
    {
        string bad = "{\"framesSampled\":300," +
            "\"mainThreadMs\":{\"min\":10,\"avg\":20,\"p95\":35,\"max\":50}," +
            "\"batches\":{\"min\":300,\"avg\":320,\"p95\":340,\"max\":350}," +
            "\"setPassCalls\":{\"min\":110,\"avg\":120,\"p95\":130,\"max\":140}," +
            "\"triangles\":{\"min\":1300000,\"avg\":1400000,\"p95\":1500000,\"max\":1600000}," +
            "\"gcAllocPerFrameB\":{\"min\":0,\"avg\":20000,\"p95\":30000,\"max\":40000}}";
        List<PerfVerdict> v = PerfAssessment.Build(bad);
        Assert.IsTrue(v.Exists(p => p.Severity == "critical"), "bad sample yields critical verdicts");
        Assert.IsTrue(v.Exists(p => p.Text.Contains("spiky")), "p95 35 vs avg 20 flags stutter");
        StringAssert.Contains("NOT optimized", PerfAssessment.Summary(v));
    }

    [Test]
    public void PerfAssessment_CleanSample_ReadsOptimized()
    {
        string good = "{\"framesSampled\":300," +
            "\"mainThreadMs\":{\"min\":3,\"avg\":4,\"p95\":5,\"max\":6}," +
            "\"batches\":{\"min\":80,\"avg\":90,\"p95\":100,\"max\":110}," +
            "\"setPassCalls\":{\"min\":10,\"avg\":13,\"p95\":15,\"max\":18}," +
            "\"triangles\":{\"min\":300000,\"avg\":350000,\"p95\":400000,\"max\":450000}," +
            "\"gcAllocPerFrameB\":{\"min\":0,\"avg\":0,\"p95\":0,\"max\":0}}";
        List<PerfVerdict> v = PerfAssessment.Build(good);
        Assert.IsFalse(v.Exists(p => p.Severity == "critical" || p.Severity == "warn"),
            "clean sample has no warnings: " + string.Join(" | ", v.ConvertAll(p => p.Severity + ":" + p.Text)));
        StringAssert.Contains("well optimized", PerfAssessment.Summary(v));
    }

    [Test]
    public void PerfAssessment_EmptyOrMalformed_YieldsNoVerdicts()
    {
        Assert.AreEqual(0, PerfAssessment.Build("{}").Count);
        Assert.AreEqual(0, PerfAssessment.Build("not json").Count);
    }

    // ----- typed audit core wraps to identical bridge JSON -----

    [Test]
    public void SceneAuditor_Run_WrapsCollect()
    {
        List<Finding> findings = SceneAuditor.Collect(out SceneStats stats);
        JObject o = JObject.Parse(SceneAuditor.Run(100000));
        Assert.AreEqual(findings.Count, (int)o["total"]);
        Assert.AreEqual(stats.Renderers, (int)o["stats"]["renderers"]);
        Assert.AreEqual(stats.Triangles, (long)o["stats"]["sceneTriangles"]);
        Assert.AreEqual(stats.LightmapsBaked, (bool)o["stats"]["lightmapsBaked"]);
    }

    [Test]
    public void Finding_ToJson_FromJObject_RoundTrips()
    {
        var f = new Finding
        {
            Id = "mesh.high-poly", Severity = "warn", Category = "geometry", Target = "A/B",
            Evidence = "45,000 tris", Recommendation = "Decimate \"it\"",
        };
        Finding back = Finding.FromJObject(JObject.Parse(f.ToJson()));
        Assert.AreEqual(f.Id, back.Id);
        Assert.AreEqual(f.Severity, back.Severity);
        Assert.AreEqual(f.Category, back.Category);
        Assert.AreEqual(f.Target, back.Target);
        Assert.AreEqual(f.Evidence, back.Evidence);
        Assert.AreEqual(f.Recommendation, back.Recommendation);
        Assert.IsNull(back.FixType, "null FixType survives the round-trip");
        Assert.IsNull(back.FixValue);

        var g = new Finding
        {
            Id = "tex.large", Severity = "critical", Category = "assets", Target = "Assets/t.png",
            FixType = "set_texture_max_size", FixValue = "1024",
        };
        Finding gback = Finding.FromJObject(JObject.Parse(g.ToJson()));
        Assert.AreEqual("set_texture_max_size", gback.FixType);
        Assert.AreEqual("1024", gback.FixValue);
    }
}
