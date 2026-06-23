using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using AgenLink.Neuron;

public class ProjectGraphTests
{
    // A(Script) -Inherits-> B; A -HasField-> C; Player(Prefab) -Component-> A; Player -AssetRef-> Mat(Asset)
    private static ProjectGraph Sample()
    {
        var g = new ProjectGraph();
        g.AddNode("A", NodeKind.Script, "A", null, null, "Game.A");
        g.AddNode("B", NodeKind.Script, "B", null, null, "Game.B");
        g.AddNode("C", NodeKind.Script, "C", null, null, "Game.C");
        g.AddNode("guidP", NodeKind.Prefab, "Player", "Assets/Player.prefab", "guidP");
        g.AddNode("guidM", NodeKind.Asset, "Mat", "Assets/Mat.mat", "guidM");
        g.AddEdge("A", "B", EdgeRelation.Inherits);
        g.AddEdge("A", "C", EdgeRelation.HasField);
        g.AddEdge("guidP", "A", EdgeRelation.Component);
        g.AddEdge("guidP", "guidM", EdgeRelation.AssetRef);
        return g;
    }

    [Test]
    public void AddEdge_DeDups_And_SkipsSelfAndMissingEndpoints()
    {
        var g = new ProjectGraph();
        g.AddNode("A", NodeKind.Script, "A");
        g.AddNode("B", NodeKind.Script, "B");
        g.AddEdge("A", "B", EdgeRelation.Inherits);
        g.AddEdge("A", "B", EdgeRelation.Inherits);   // duplicate
        g.AddEdge("A", "Z", EdgeRelation.HasField);   // Z missing
        g.AddEdge("A", "A", EdgeRelation.HasField);   // self
        Assert.AreEqual(1, g.EdgeCount);
    }

    [Test]
    public void Neighbors_Depth1_Out_OnlyDirectTargets()
    {
        var ids = Sample().Neighbors("A", 1, Direction.Out).Nodes.Select(n => n.Id).ToHashSet();
        Assert.IsTrue(ids.SetEquals(new[] { "A", "B", "C" }));
    }

    [Test]
    public void Neighbors_In_FindsDependents()
    {
        var ids = Sample().Neighbors("A", 1, Direction.In).Nodes.Select(n => n.Id).ToHashSet();
        Assert.IsTrue(ids.SetEquals(new[] { "A", "guidP" }), "who points AT A (the prefab's Component edge)");
    }

    [Test]
    public void Neighbors_KindAndRelationFilters_Apply()
    {
        var r = Sample().Neighbors("guidP", 1, Direction.Out,
            kindFilter: new HashSet<NodeKind> { NodeKind.Prefab, NodeKind.Script },
            relationFilter: new HashSet<EdgeRelation> { EdgeRelation.Component });
        var ids = r.Nodes.Select(n => n.Id).ToHashSet();
        Assert.IsTrue(ids.SetEquals(new[] { "guidP", "A" }), "AssetRef→Mat is filtered out");
        Assert.IsTrue(r.Edges.All(e => e.Relation == EdgeRelation.Component));
    }

    [Test]
    public void Neighbors_MaxNodes_Truncates()
    {
        var r = Sample().Neighbors("A", 5, Direction.Both, maxNodes: 2);
        Assert.LessOrEqual(r.Nodes.Count, 2);
        Assert.IsTrue(r.Truncated);
    }

    [Test]
    public void Result_Edges_OnlyBetweenIncludedNodes()
    {
        var r = Sample().Neighbors("A", 1, Direction.Out); // A,B,C — the guidP->A edge must be excluded
        Assert.IsTrue(r.Edges.All(e => r.Nodes.Any(n => n.Id == e.From) && r.Nodes.Any(n => n.Id == e.To)));
        Assert.IsFalse(r.Edges.Any(e => e.From == "guidP"));
    }

    [Test]
    public void ResolveEntityId_ByVariousKeys_And_AmbiguousReturnsNull()
    {
        var g = Sample();
        Assert.AreEqual("A", g.ResolveEntityId("A"));                          // exact id
        Assert.AreEqual("A", g.ResolveEntityId("Game.A"));                     // full type name
        Assert.AreEqual("guidP", g.ResolveEntityId("Assets/Player.prefab"));   // path
        Assert.AreEqual("guidP", g.ResolveEntityId("Player"));                 // unique display name
        Assert.IsNull(g.ResolveEntityId("does-not-exist"));                    // missing

        g.AddNode("dup1", NodeKind.Script, "Dup", null, null, "X.Dup");
        g.AddNode("dup2", NodeKind.Script, "Dup", null, null, "Y.Dup");
        Assert.IsNull(g.ResolveEntityId("Dup"));                               // ambiguous
    }
}
