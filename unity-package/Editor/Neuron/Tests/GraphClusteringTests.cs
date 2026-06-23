using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using AgenLink.Neuron;

public class GraphClusteringTests
{
    // Two scenes (S1,S2). S1: A,B,C,F densely linked (B is the hub). S2: D,E. M1,M2 used by BOTH (shared).
    // P belongs to no scene (project-level, isolated). SceneIds are set manually (membership is the builder's job).
    private static ProjectGraph Sample()
    {
        var g = new ProjectGraph();
        g.AddNode("S1", NodeKind.Scene, "Main", "Assets/Main.unity", "S1");
        g.AddNode("S2", NodeKind.Scene, "Level", "Assets/Level.unity", "S2");

        void Script(string id, params string[] scenes)
        {
            var n = g.AddNode(id, NodeKind.Script, id, null, null, "Game." + id);
            n.SceneIds = scenes.ToList();
        }
        Script("A", "S1"); Script("B", "S1"); Script("C", "S1"); Script("F", "S1");
        Script("D", "S2"); Script("E", "S2");
        Script("M1", "S1", "S2"); Script("M2", "S1", "S2");
        Script("P"); // no scene

        g.AddEdge("A", "B", EdgeRelation.HasField);
        g.AddEdge("B", "C", EdgeRelation.References);
        g.AddEdge("A", "C", EdgeRelation.References);
        g.AddEdge("B", "F", EdgeRelation.HasField);   // makes B the highest-degree (hub) of S1
        g.AddEdge("D", "E", EdgeRelation.Inherits);
        g.AddEdge("M1", "M2", EdgeRelation.HasField);
        return g;
    }

    [Test]
    public void Assign_EveryNonSceneNode_GetsASystem()
    {
        var g = Sample();
        GraphClustering.Assign(g);
        foreach (var n in g.AllNodes())
        {
            if (n.Kind == NodeKind.Scene) Assert.IsNull(n.SystemId, "scenes are containers, not members");
            else Assert.IsFalse(string.IsNullOrEmpty(n.SystemId), $"{n.Id} must get a system");
        }
    }

    [Test]
    public void Assign_ClustersStayWithinOwner_AndSceneScriptsGroup()
    {
        var g = Sample();
        GraphClustering.Assign(g);
        string Sys(string id) { g.TryGetNode(id, out var n); return n.SystemId; }

        // S1 scripts cluster together, separate from S2's
        Assert.AreEqual(Sys("A"), Sys("B"));
        Assert.AreEqual(Sys("B"), Sys("C"));
        Assert.AreEqual(Sys("C"), Sys("F"));
        Assert.AreEqual(Sys("D"), Sys("E"));
        Assert.AreNotEqual(Sys("A"), Sys("D"), "different scenes -> different systems");
        Assert.AreNotEqual(Sys("A"), Sys("M1"), "shared nodes are their own owner");
    }

    [Test]
    public void Assign_Owner_ReflectsSceneMembership()
    {
        var g = Sample();
        GraphClustering.Assign(g);
        g.TryGetNode("A", out var a); g.TryGetSystem(a.SystemId, out var sysA);
        g.TryGetNode("M1", out var m); g.TryGetSystem(m.SystemId, out var sysM);
        g.TryGetNode("P", out var p); g.TryGetSystem(p.SystemId, out var sysP);

        Assert.AreEqual("S1", sysA.Owner);
        Assert.AreEqual("shared", sysM.Owner);
        CollectionAssert.AreEquivalent(new[] { "S1", "S2" }, sysM.SceneIds);
        Assert.AreEqual("project", sysP.Owner);
    }

    [Test]
    public void Assign_Main_IsHighestDegreeMember()
    {
        var g = Sample();
        GraphClustering.Assign(g);
        g.TryGetNode("B", out var b);
        g.TryGetSystem(b.SystemId, out var sys);
        Assert.AreEqual("B", sys.MainId, "B has degree 3, the hub of S1");
        Assert.IsTrue(b.IsSystemMain);
    }

    [Test]
    public void Assign_IsDeterministic()
    {
        var map1 = SystemMap(Sample());
        var map2 = SystemMap(Sample());
        CollectionAssert.AreEquivalent(map1, map2);
    }

    private static Dictionary<string, string> SystemMap(ProjectGraph g)
    {
        GraphClustering.Assign(g);
        var m = new Dictionary<string, string>();
        foreach (var n in g.AllNodes()) if (n.Kind != NodeKind.Scene) m[n.Id] = n.SystemId;
        return m;
    }
}
