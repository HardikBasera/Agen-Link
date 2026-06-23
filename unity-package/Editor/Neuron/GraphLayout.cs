using System.Collections.Generic;
using UnityEngine;

namespace AgenLink.Neuron
{
    /// <summary>Pure, deterministic 2D placement for a node set. Focused (centerId present) = BFS layers
    /// left→right from the center; whole-project = a square grid. Sorted by id so the same input always
    /// yields the same layout (EditMode-testable, no Unity asset APIs).</summary>
    internal static class GraphLayout
    {
        public const float NodeW = 150f;
        public const float NodeH = 30f;
        private const float ColGap = 220f;
        private const float RowGap = 46f;

        public static Dictionary<string, Vector2> Compute(IList<GraphNode> nodes, IList<GraphEdge> edges, string centerId)
        {
            var pos = new Dictionary<string, Vector2>();
            if (nodes == null || nodes.Count == 0) return pos;

            if (!string.IsNullOrEmpty(centerId) && Contains(nodes, centerId))
                LayerLayout(nodes, edges, centerId, pos);
            else
                GridLayout(nodes, pos);
            return pos;
        }

        /// <summary>Deterministic force-directed layout that settles into a COMPACT cluster (not a ring):
        /// phyllotaxis seed, repulsion with a distance cutoff (prevents a perimeter shell), edge springs, and
        /// strong gravity. The focus node is pinned at the origin. Fixed iterations → reproducible.</summary>
        public static Dictionary<string, Vector2> ComputeForce(IList<GraphNode> nodes, IList<GraphEdge> edges, string centerId)
        {
            var pos = new Dictionary<string, Vector2>();
            int n = nodes != null ? nodes.Count : 0;
            if (n == 0) return pos;

            var ids = new List<string>(n);
            foreach (var nd in nodes) ids.Add(nd.Id);
            ids.Sort((a, b) => string.CompareOrdinal(a, b));
            var index = new Dictionary<string, int>(n);
            for (int i = 0; i < n; i++) index[ids[i]] = i;

            // phyllotaxis (golden-angle) seed in a tight disc → even spread, no ring artifact
            float seedR = 10f * Mathf.Sqrt(n);
            const float golden = 2.39996323f;
            var p = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * golden;
                float rad = seedR * Mathf.Sqrt((i + 0.5f) / n);
                p[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad;
            }

            var ea = new List<int>(); var eb = new List<int>(); var rest = new List<float>();
            if (edges != null)
                foreach (var e in edges)
                    if (index.TryGetValue(e.From, out var ia) && index.TryGetValue(e.To, out var ib) && ia != ib)
                    {
                        ea.Add(ia); eb.Add(ib);
                        rest.Add(e.Relation == EdgeRelation.Inherits ? 55f : 95f);
                    }

            int center = (centerId != null && index.TryGetValue(centerId, out var ci)) ? ci : -1;
            float k = 55f;
            float repCut = 3.5f * k;            // far nodes don't repel → no perimeter shell/donut
            var disp = new Vector2[n];
            const int iters = 240;

            for (int it = 0; it < iters; it++)
            {
                for (int i = 0; i < n; i++) disp[i] = Vector2.zero;

                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++)
                    {
                        Vector2 d = p[i] - p[j];
                        float dist = Mathf.Max(0.05f, d.magnitude);
                        if (dist > repCut) continue;
                        Vector2 dir = d / dist;
                        float rep = k * k / dist;
                        disp[i] += dir * rep; disp[j] -= dir * rep;
                    }

                for (int e = 0; e < ea.Count; e++)
                {
                    int i = ea[e], j = eb[e];
                    Vector2 d = p[i] - p[j];
                    float dist = Mathf.Max(0.05f, d.magnitude);
                    Vector2 dir = d / dist;
                    float force = (dist - rest[e]) * 0.06f;
                    disp[i] -= dir * force; disp[j] += dir * force;
                }

                for (int i = 0; i < n; i++) disp[i] -= p[i] * 0.09f; // strong gravity → compact blob

                float cool = Mathf.Lerp(12f, 0.6f, (float)it / iters);
                for (int i = 0; i < n; i++)
                {
                    if (i == center) { p[i] = Vector2.zero; continue; }
                    float dl = disp[i].magnitude;
                    if (dl > 0.0001f) p[i] += disp[i] / dl * Mathf.Min(dl, cool);
                }
            }

            for (int i = 0; i < n; i++) pos[ids[i]] = p[i];
            return pos;
        }

        /// <summary>Clean radial layout for focused exploration: the center node at the origin, direct
        /// neighbours on the first ring, 2-hop on the next, etc. Deterministic and very readable.</summary>
        public static Dictionary<string, Vector2> ComputeRadial(IList<GraphNode> nodes, IList<GraphEdge> edges, string centerId)
        {
            var pos = new Dictionary<string, Vector2>();
            if (nodes == null || nodes.Count == 0 || string.IsNullOrEmpty(centerId)) return pos;

            var adj = new Dictionary<string, List<string>>();
            foreach (var nd in nodes) adj[nd.Id] = new List<string>();
            if (!adj.ContainsKey(centerId)) return ComputeForce(nodes, edges, centerId);
            if (edges != null)
                foreach (var e in edges)
                    if (adj.ContainsKey(e.From) && adj.ContainsKey(e.To)) { adj[e.From].Add(e.To); adj[e.To].Add(e.From); }

            var level = new Dictionary<string, int> { { centerId, 0 } };
            var q = new Queue<string>(); q.Enqueue(centerId);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                foreach (var nb in adj[cur]) if (!level.ContainsKey(nb)) { level[nb] = level[cur] + 1; q.Enqueue(nb); }
            }
            int maxL = 0; foreach (var kv in level) if (kv.Value > maxL) maxL = kv.Value;

            var byLevel = new SortedDictionary<int, List<string>>();
            var sorted = new List<GraphNode>(nodes);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            foreach (var nd in sorted)
            {
                int l = level.TryGetValue(nd.Id, out var lv) ? lv : maxL + 1;
                if (!byLevel.TryGetValue(l, out var list)) { list = new List<string>(); byLevel[l] = list; }
                list.Add(nd.Id);
            }

            const float ringGap = 135f;
            foreach (var kv in byLevel)
            {
                if (kv.Key == 0) { pos[kv.Value[0]] = Vector2.zero; continue; }
                float radius = kv.Key * ringGap;
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    float a = 2f * Mathf.PI * i / kv.Value.Count + kv.Key * 0.5f;
                    pos[kv.Value[i]] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
                }
            }
            return pos;
        }

        private static void LayerLayout(IList<GraphNode> nodes, IList<GraphEdge> edges, string centerId, Dictionary<string, Vector2> pos)
        {
            var adj = new Dictionary<string, List<string>>();
            foreach (var n in nodes) adj[n.Id] = new List<string>();
            if (edges != null)
                foreach (var e in edges)
                    if (adj.ContainsKey(e.From) && adj.ContainsKey(e.To))
                    {
                        adj[e.From].Add(e.To);
                        adj[e.To].Add(e.From);
                    }

            var layer = new Dictionary<string, int> { { centerId, 0 } };
            var queue = new Queue<string>();
            queue.Enqueue(centerId);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var nb in adj[cur])
                    if (!layer.ContainsKey(nb)) { layer[nb] = layer[cur] + 1; queue.Enqueue(nb); }
            }

            int maxLayer = 0;
            foreach (var kv in layer) if (kv.Value > maxLayer) maxLayer = kv.Value;
            foreach (var n in nodes) if (!layer.ContainsKey(n.Id)) layer[n.Id] = maxLayer + 1; // disconnected → trailing column

            var byLayer = new SortedDictionary<int, List<string>>();
            foreach (var n in SortedById(nodes))
            {
                int l = layer[n.Id];
                if (!byLayer.TryGetValue(l, out var list)) { list = new List<string>(); byLayer[l] = list; }
                list.Add(n.Id);
            }
            foreach (var kv in byLayer)
                for (int i = 0; i < kv.Value.Count; i++)
                    pos[kv.Value[i]] = new Vector2(kv.Key * ColGap, i * RowGap - (kv.Value.Count - 1) * RowGap * 0.5f);
        }

        private static void GridLayout(IList<GraphNode> nodes, Dictionary<string, Vector2> pos)
        {
            var sorted = SortedById(nodes);
            int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(sorted.Count)));
            for (int i = 0; i < sorted.Count; i++)
                pos[sorted[i].Id] = new Vector2((i % cols) * ColGap, (i / cols) * RowGap);
        }

        private static List<GraphNode> SortedById(IList<GraphNode> nodes)
        {
            var list = new List<GraphNode>(nodes);
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        private static bool Contains(IList<GraphNode> nodes, string id)
        {
            foreach (var n in nodes) if (n.Id == id) return true;
            return false;
        }

        // ---------------- nested-cluster (Overview) layout ----------------

        /// <summary>Hierarchy-aware node radius (world units): high-degree / main scripts grow, assets stay small.
        /// Shared by layout (packing) and the view (drawing) so geometry and visuals always agree.</summary>
        public static float NodeRadius(NodeKind kind, int degree, bool isMain)
        {
            float b = Mathf.Sqrt(Mathf.Max(0, degree));
            float r;
            switch (kind)
            {
                case NodeKind.Prefab: r = Mathf.Min(24f, 14f + 3f * b); break;
                case NodeKind.Script: r = Mathf.Min(26f, 10f + 5.2f * b); break;
                case NodeKind.Asset:  r = Mathf.Min(14f, 8f + 1.6f * b); break;
                default:              r = Mathf.Min(17f, 9f + 2f * b); break;
            }
            return r * (isMain ? 1.32f : 1f);
        }

        /// <summary>Effective collision radius — scripts draw as wide ellipses, so reserve more room.</summary>
        private static float CollisionRadius(NodeKind kind, float r)
        {
            if (kind == NodeKind.Script) return r * 1.42f;
            if (kind == NodeKind.Asset) return r * 1.05f;
            return r;
        }

        /// <summary>3-level circle packing: nodes within each system, systems within each owner (scene / shared /
        /// project), owners in the world. The system main is pinned to its system centre. Deterministic (sorted
        /// ids). Returns absolute positions for every non-scene node; scenes are drawn as cluster glows by the
        /// view, computed from these positions.</summary>
        public static Dictionary<string, Vector2> ComputeNestedClusters(ProjectGraph g)
        {
            var pos = new Dictionary<string, Vector2>();
            if (g == null) return pos;

            var systems = new List<GraphSystem>(g.AllSystems());
            systems.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            if (systems.Count == 0) return pos;

            // level 1: pack member nodes inside each system, pin main to centre
            var sysNodeRel = new Dictionary<string, Dictionary<string, Vector2>>();
            var sysRadius = new Dictionary<string, float>();
            foreach (var sys in systems)
            {
                var members = new List<GraphNode>();
                foreach (var id in sys.MemberIds)
                    if (g.TryGetNode(id, out var nd) && nd.Kind != NodeKind.Scene) members.Add(nd);
                members.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                var rel = new Dictionary<string, Vector2>();
                int m = members.Count;
                if (m == 0) { sysNodeRel[sys.Id] = rel; sysRadius[sys.Id] = 20f; continue; }

                var radii = new float[m];
                for (int i = 0; i < m; i++)
                {
                    var nd = members[i];
                    float r = NodeRadius(nd.Kind, g.Degree(nd.Id), nd.IsSystemMain);
                    radii[i] = CollisionRadius(nd.Kind, r);
                }
                var p = PackAround(radii, 9f, out _);
                int mi = -1;
                for (int i = 0; i < m; i++) if (members[i].Id == sys.MainId) { mi = i; break; }
                Vector2 off = mi >= 0 ? p[mi] : Vector2.zero;
                float enc = 0f;
                for (int i = 0; i < m; i++) { Vector2 q = p[i] - off; rel[members[i].Id] = q; enc = Mathf.Max(enc, q.magnitude + radii[i]); }
                sysNodeRel[sys.Id] = rel;
                sysRadius[sys.Id] = enc + 18f;
            }

            // group systems by owner
            var owners = new List<string>();
            var byOwner = new Dictionary<string, List<GraphSystem>>();
            foreach (var sys in systems)
            {
                if (!byOwner.TryGetValue(sys.Owner, out var l)) { l = new List<GraphSystem>(); byOwner[sys.Owner] = l; owners.Add(sys.Owner); }
                l.Add(sys);
            }
            owners.Sort(System.StringComparer.Ordinal);

            // level 2: pack systems inside each owner
            var sysCenterRel = new Dictionary<string, Vector2>();
            var ownerRadius = new Dictionary<string, float>();
            foreach (var ow in owners)
            {
                var list = byOwner[ow];
                list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                var radii = new float[list.Count];
                for (int i = 0; i < list.Count; i++) radii[i] = sysRadius[list[i].Id];
                var p = PackAround(radii, 26f, out float enc);
                for (int i = 0; i < list.Count; i++) sysCenterRel[list[i].Id] = p[i];
                ownerRadius[ow] = enc + 30f;
            }

            // level 3: pack owners in the world
            var oradii = new float[owners.Count];
            for (int i = 0; i < owners.Count; i++) oradii[i] = ownerRadius[owners[i]];
            var ownerPos = PackAround(oradii, 90f, out _);
            var ownerCenter = new Dictionary<string, Vector2>();
            for (int i = 0; i < owners.Count; i++) ownerCenter[owners[i]] = ownerPos[i];

            // compose absolute positions
            foreach (var sys in systems)
            {
                Vector2 oc = ownerCenter.TryGetValue(sys.Owner, out var o) ? o : Vector2.zero;
                Vector2 sc = oc + (sysCenterRel.TryGetValue(sys.Id, out var sr) ? sr : Vector2.zero);
                foreach (var kv in sysNodeRel[sys.Id]) pos[kv.Key] = sc + kv.Value;
            }
            return pos;
        }

        /// <summary>Greedy circle packing around the origin: places the largest circles first, then each next on
        /// an expanding spiral at the first non-overlapping slot. Deterministic. Returns parallel positions and
        /// the enclosing radius.</summary>
        private static Vector2[] PackAround(float[] radii, float pad, out float enclosing)
        {
            int n = radii.Length;
            var pos = new Vector2[n];
            enclosing = 0f;
            if (n == 0) return pos;

            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            System.Array.Sort(order, (a, b) => radii[b].CompareTo(radii[a]));

            var placed = new List<Vector3>(); // x, y, r
            foreach (int i in order)
            {
                float ri = radii[i];
                if (placed.Count == 0) { pos[i] = Vector2.zero; placed.Add(new Vector3(0, 0, ri)); continue; }
                Vector2 best = Vector2.zero; bool found = false;
                for (int ring = 1; ring < 80 && !found; ring++)
                {
                    float rad = ring * (pad * 0.6f + 6f);
                    for (float a = 0f; a < 6.2832f; a += 0.32f)
                    {
                        float x = Mathf.Cos(a) * rad, y = Mathf.Sin(a) * rad;
                        bool ok = true;
                        foreach (var p in placed)
                        {
                            float dx = x - p.x, dy = y - p.y, min = ri + p.z + pad;
                            if (dx * dx + dy * dy < min * min) { ok = false; break; }
                        }
                        if (ok) { best = new Vector2(x, y); found = true; break; }
                    }
                }
                pos[i] = best; placed.Add(new Vector3(best.x, best.y, ri));
            }
            for (int i = 0; i < n; i++) enclosing = Mathf.Max(enclosing, pos[i].magnitude + radii[i]);
            return pos;
        }
    }
}
