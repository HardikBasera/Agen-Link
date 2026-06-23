using System.Collections.Generic;

namespace AgenLink.Neuron
{
    /// <summary>Assigns nodes to "systems" (clusters) using deterministic, offline community detection over the
    /// structural edges — no LLM, no Unity asset APIs (EditMode-testable). Clustering runs WITHIN each owner
    /// partition (a scene id, "shared" = used by ≥2 scenes, or "project" = no scene) so a system never crosses
    /// scenes — that gives the scene › system nesting directly. The highest-degree member of each system is
    /// marked as its main; isolated nodes fall into a per-owner "Scattered" system. Names assigned here are
    /// structural placeholders (the system main's name); the optional Claude pass renames them later.</summary>
    internal static class GraphClustering
    {
        private const int MaxIterations = 20;

        private static int Weight(EdgeRelation r)
        {
            switch (r)
            {
                case EdgeRelation.Inherits:
                case EdgeRelation.Implements:
                case EdgeRelation.HasField:
                case EdgeRelation.Component: return 3;   // strong structural composition
                case EdgeRelation.References: return 2;  // code usage
                default: return 1;                       // AssetRef, Contains, PrefabSource
            }
        }

        private static string OwnerOf(GraphNode n)
        {
            int c = n.SceneIds != null ? n.SceneIds.Count : 0;
            if (c == 0) return "project";
            if (c == 1) return n.SceneIds[0];
            return "shared";
        }

        public static void Assign(ProjectGraph g)
        {
            if (g == null) return;
            g.ClearSystems();

            // clusterable nodes = everything except scenes (scenes are containers, not members)
            var nodes = new List<GraphNode>();
            foreach (var n in g.AllNodes()) if (n.Kind != NodeKind.Scene) nodes.Add(n);
            nodes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));   // determinism
            if (nodes.Count == 0) return;

            var owner = new Dictionary<string, string>();
            foreach (var n in nodes) owner[n.Id] = OwnerOf(n);

            // weighted adjacency, restricted to same-owner edges (clusters stay within a scene/owner)
            var adj = new Dictionary<string, Dictionary<string, int>>();
            foreach (var n in nodes) adj[n.Id] = new Dictionary<string, int>();
            foreach (var e in g.AllEdges())
            {
                if (!owner.TryGetValue(e.From, out var of)) continue;
                if (!owner.TryGetValue(e.To, out var ot)) continue;
                if (of != ot) continue;
                int w = Weight(e.Relation);
                Bump(adj[e.From], e.To, w);
                Bump(adj[e.To], e.From, w);
            }

            // deterministic asynchronous label propagation
            var label = new Dictionary<string, string>();
            foreach (var n in nodes) label[n.Id] = n.Id;
            for (int it = 0; it < MaxIterations; it++)
            {
                bool changed = false;
                foreach (var n in nodes)   // sorted order -> deterministic
                {
                    string best = BestLabel(adj[n.Id], label, n.Id);
                    if (best != label[n.Id]) { label[n.Id] = best; changed = true; }
                }
                if (!changed) break;
            }

            // group by (owner, label); singletons fold into a per-owner "Scattered" system
            var groups = new Dictionary<string, List<GraphNode>>();
            foreach (var n in nodes)
            {
                string key = owner[n.Id] + "#" + label[n.Id];
                if (!groups.TryGetValue(key, out var list)) { list = new List<GraphNode>(); groups[key] = list; }
                list.Add(n);
            }

            var scattered = new Dictionary<string, List<GraphNode>>();
            var realGroups = new List<KeyValuePair<string, List<GraphNode>>>();
            foreach (var kv in groups)
            {
                if (kv.Value.Count >= 2) realGroups.Add(kv);
                else
                {
                    var n = kv.Value[0];
                    string ow = owner[n.Id];
                    if (!scattered.TryGetValue(ow, out var l)) { l = new List<GraphNode>(); scattered[ow] = l; }
                    l.Add(n);
                }
            }
            realGroups.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            foreach (var kv in realGroups) MakeSystem(g, kv.Key, kv.Value, false);
            var scatterKeys = new List<string>(scattered.Keys);
            scatterKeys.Sort(System.StringComparer.Ordinal);
            foreach (var ow in scatterKeys) MakeSystem(g, ow + "#scattered", scattered[ow], true);
        }

        private static void MakeSystem(ProjectGraph g, string sysId, List<GraphNode> members, bool scattered)
        {
            if (members.Count == 0) return;
            members.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            GraphNode main = members[0];
            int bestDeg = g.Degree(main.Id);
            foreach (var m in members)
            {
                int d = g.Degree(m.Id);
                if (d > bestDeg) { bestDeg = d; main = m; }   // ties keep the lower id (sorted)
            }

            string owner = sysId.Substring(0, sysId.IndexOf('#'));
            var sys = new GraphSystem { Id = sysId, MainId = main.Id, Owner = owner };
            var sceneIds = new List<string>();
            foreach (var m in members)
            {
                m.SystemId = sysId;
                sys.MemberIds.Add(m.Id);
                if (m.SceneIds != null)
                    foreach (var s in m.SceneIds) if (!sceneIds.Contains(s)) sceneIds.Add(s);
            }
            sceneIds.Sort(System.StringComparer.Ordinal);
            sys.SceneIds = sceneIds;

            if (!scattered) { main.IsSystemMain = true; sys.Name = main.Name; }
            else sys.Name = "Scattered";
            g.AddSystem(sys);
        }

        private static void Bump(Dictionary<string, int> map, string key, int w)
        {
            map.TryGetValue(key, out var cur);
            map[key] = cur + w;
        }

        private static string BestLabel(Dictionary<string, int> neighbors, Dictionary<string, string> label, string selfId)
        {
            if (neighbors.Count == 0) return label[selfId];
            var score = new Dictionary<string, int>();
            foreach (var kv in neighbors)
            {
                string lab = label[kv.Key];
                score.TryGetValue(lab, out var s);
                score[lab] = s + kv.Value;
            }
            string best = label[selfId];
            int bestScore = -1;
            foreach (var kv in score)
                if (kv.Value > bestScore || (kv.Value == bestScore && string.CompareOrdinal(kv.Key, best) < 0))
                { bestScore = kv.Value; best = kv.Key; }
            return best;
        }
    }
}
