using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using AgenLink.Neuron;

public class GraphLayoutTests
{
    private static GraphNode N(string id) => new GraphNode { Id = id, Kind = NodeKind.Script, Name = id };

    [Test]
    public void Compute_IsDeterministic()
    {
        var nodes = new List<GraphNode> { N("a"), N("b"), N("c") };
        var edges = new List<GraphEdge> { new GraphEdge { From = "a", To = "b", Relation = EdgeRelation.Inherits } };
        var p1 = GraphLayout.Compute(nodes, edges, null);
        var p2 = GraphLayout.Compute(nodes, edges, null);
        foreach (var id in new[] { "a", "b", "c" }) Assert.AreEqual(p1[id], p2[id]);
    }

    [Test]
    public void Focused_LayersByDistanceFromCenter()
    {
        var nodes = new List<GraphNode> { N("center"), N("mid"), N("far") };
        var edges = new List<GraphEdge>
        {
            new GraphEdge { From = "center", To = "mid", Relation = EdgeRelation.HasField },
            new GraphEdge { From = "mid", To = "far", Relation = EdgeRelation.HasField },
        };
        var pos = GraphLayout.Compute(nodes, edges, "center");
        Assert.Less(pos["center"].x, pos["mid"].x);
        Assert.Less(pos["mid"].x, pos["far"].x);
    }

    [Test]
    public void ComputeForce_Deterministic_Finite_CenterPinned()
    {
        var nodes = new List<GraphNode>();
        for (int i = 0; i < 12; i++) nodes.Add(N("n" + i));
        var edges = new List<GraphEdge>
        {
            new GraphEdge { From = "n0", To = "n1", Relation = EdgeRelation.Inherits },
            new GraphEdge { From = "n1", To = "n2", Relation = EdgeRelation.Component },
        };
        var a = GraphLayout.ComputeForce(nodes, edges, "n0");
        var b = GraphLayout.ComputeForce(nodes, edges, "n0");
        Assert.AreEqual(12, a.Count);
        foreach (var id in a.Keys)
        {
            Assert.AreEqual(a[id], b[id]);
            Assert.IsFalse(float.IsNaN(a[id].x) || float.IsInfinity(a[id].x));
            Assert.IsFalse(float.IsNaN(a[id].y) || float.IsInfinity(a[id].y));
        }
        Assert.AreEqual(Vector2.zero, a["n0"]); // focus pinned at origin
    }

    [Test]
    public void Compute_PlacesAllNodes_WithFiniteCoords()
    {
        var nodes = new List<GraphNode>();
        for (int i = 0; i < 25; i++) nodes.Add(N("n" + i));
        var pos = GraphLayout.Compute(nodes, null, null);
        Assert.AreEqual(25, pos.Count);
        foreach (var kv in pos)
        {
            Assert.IsFalse(float.IsNaN(kv.Value.x) || float.IsInfinity(kv.Value.x));
            Assert.IsFalse(float.IsNaN(kv.Value.y) || float.IsInfinity(kv.Value.y));
        }
    }

    // ---- nested-cluster (Overview) layout ----

    private static ProjectGraph Clustered()
    {
        var g = new ProjectGraph();
        g.AddNode("S1", NodeKind.Scene, "Main");
        g.AddNode("S2", NodeKind.Scene, "Level");
        void Script(string id, params string[] scenes)
        { var n = g.AddNode(id, NodeKind.Script, id, null, null, "Game." + id); n.SceneIds = new List<string>(scenes); }
        Script("A", "S1"); Script("B", "S1"); Script("C", "S1"); Script("F", "S1");
        Script("D", "S2"); Script("E", "S2");
        Script("M1", "S1", "S2"); Script("M2", "S1", "S2");
        Script("P");
        g.AddEdge("A", "B", EdgeRelation.HasField);
        g.AddEdge("B", "C", EdgeRelation.References);
        g.AddEdge("B", "F", EdgeRelation.HasField);
        g.AddEdge("D", "E", EdgeRelation.Inherits);
        g.AddEdge("M1", "M2", EdgeRelation.HasField);
        GraphClustering.Assign(g);
        return g;
    }

    [Test]
    public void ComputeNestedClusters_PlacesEveryNonSceneNode_Finite_Deterministic()
    {
        var g = Clustered();
        var p1 = GraphLayout.ComputeNestedClusters(g);
        var p2 = GraphLayout.ComputeNestedClusters(g);

        int expected = 0;
        foreach (var n in g.AllNodes()) if (n.Kind != NodeKind.Scene) expected++;
        Assert.AreEqual(expected, p1.Count);
        foreach (var kv in p1)
        {
            Assert.IsFalse(float.IsNaN(kv.Value.x) || float.IsInfinity(kv.Value.x));
            Assert.AreEqual(kv.Value, p2[kv.Key]); // deterministic
        }
    }

    [Test]
    public void ComputeNestedClusters_NoNodeShapesOverlap()
    {
        var g = Clustered();
        var pos = GraphLayout.ComputeNestedClusters(g);
        var ids = new List<string>(pos.Keys);
        for (int i = 0; i < ids.Count; i++)
            for (int j = i + 1; j < ids.Count; j++)
            {
                g.TryGetNode(ids[i], out var a); g.TryGetNode(ids[j], out var b);
                float ra = GraphLayout.NodeRadius(a.Kind, g.Degree(a.Id), a.IsSystemMain);
                float rb = GraphLayout.NodeRadius(b.Kind, g.Degree(b.Id), b.IsSystemMain);
                float dist = (pos[ids[i]] - pos[ids[j]]).magnitude;
                Assert.GreaterOrEqual(dist, ra + rb - 0.5f, $"{ids[i]} and {ids[j]} overlap");
            }
    }
}
