using System.Collections.Generic;

namespace AgenLink.Neuron
{
    internal enum NodeKind { Script, Prefab, Scene, Asset, GameObject }

    internal enum EdgeRelation
    {
        Inherits,      // Script -> Script (base class)
        Implements,    // Script -> Script (project-defined interface)
        HasField,      // Script -> Script (serialized field-type composition)
        Component,     // Prefab/Scene/GameObject -> Script (MonoBehaviour attached)
        AssetRef,      // Asset -> Asset (AssetDatabase dependency)
        Contains,      // Prefab/Scene -> GameObject (focused mode)
        PrefabSource,  // Prefab variant/instance -> source prefab
        References     // Script -> Script (code usage; from a lightweight source-token scan, not reflection)
    }

    internal enum Direction { Out, In, Both }

    internal sealed class GraphNode
    {
        public string Id;        // stable: asset GUID for assets; type FullName for scripts; "<guid>::<path>" for GameObjects
        public NodeKind Kind;
        public string Name;
        public string Path;      // asset path, where applicable
        public string Guid;      // asset GUID, where applicable
        public string TypeName;  // full type name, for Script nodes

        // ----- grouping (set by GraphBuilder scene-membership + GraphClustering) -----
        public List<string> SceneIds = new List<string>();  // scene node ids that (transitively) include this node; empty = project-level
        public string SystemId;                              // cluster id this node belongs to
        public bool IsSystemMain;                            // true if this node is the lead/hub of its system
    }

    internal sealed class GraphEdge
    {
        public string From;
        public string To;
        public EdgeRelation Relation;
    }

    /// <summary>A cluster of nodes that form a "system" (assigned by <see cref="GraphClustering"/>). Each system
    /// is nested under one owner: a scene id, "shared" (used by ≥2 scenes), or "project" (no scene). <see cref="Name"/>
    /// is a structural placeholder until the optional Claude naming pass replaces it.</summary>
    internal sealed class GraphSystem
    {
        public string Id;
        public string Name;
        public string MainId;                              // lead node (highest centrality within the cluster)
        public string Owner;                              // scene node id | "shared" | "project"
        public List<string> SceneIds = new List<string>();
        public List<string> MemberIds = new List<string>();

        /// <summary>Stable content key for this cluster (FNV-1a over sorted member ids). The Claude name cache is
        /// keyed by this: unchanged membership ⇒ same signature ⇒ reuse the cached name with zero LLM calls; any
        /// membership change ⇒ new signature ⇒ flagged for (re)naming.</summary>
        public string Signature()
        {
            var ids = new List<string>(MemberIds);
            ids.Sort(System.StringComparer.Ordinal);
            ulong h = 14695981039346656037UL;
            unchecked
            {
                foreach (var id in ids)
                {
                    foreach (char c in id) { h ^= c; h *= 1099511628211UL; }
                    h ^= '|'; h *= 1099511628211UL;
                }
            }
            return h.ToString("x");
        }
    }

    internal sealed class NeighborhoodResult
    {
        public List<GraphNode> Nodes = new List<GraphNode>();
        public List<GraphEdge> Edges = new List<GraphEdge>();   // only edges whose BOTH endpoints are in Nodes
        public bool Truncated;
        public string CenterId;
    }

    /// <summary>In-memory project dependency graph. Mutation is for the builder; the query API is pure and
    /// deterministic (EditMode-testable without any Unity asset APIs). Both the MCP handler and the IMGUI
    /// view consume the same query methods.</summary>
    internal sealed class ProjectGraph
    {
        private readonly Dictionary<string, GraphNode> _nodes = new Dictionary<string, GraphNode>();
        private readonly List<GraphEdge> _edges = new List<GraphEdge>();
        private readonly Dictionary<string, List<GraphEdge>> _out = new Dictionary<string, List<GraphEdge>>();
        private readonly Dictionary<string, List<GraphEdge>> _in = new Dictionary<string, List<GraphEdge>>();
        private readonly HashSet<string> _edgeKeys = new HashSet<string>();
        private readonly Dictionary<string, GraphSystem> _systems = new Dictionary<string, GraphSystem>();

        public long BuiltAtUnixMs;
        public string ProjectRoot;

        public int NodeCount => _nodes.Count;
        public int EdgeCount => _edges.Count;
        public int SystemCount => _systems.Count;

        /// <summary>Total degree (in + out) of a node — used for hub/centrality ranking.</summary>
        public int Degree(string id)
        {
            int d = 0;
            if (_out.TryGetValue(id, out var ol)) d += ol.Count;
            if (_in.TryGetValue(id, out var il)) d += il.Count;
            return d;
        }

        // ----- mutation (builder only) -----

        public GraphNode AddNode(string id, NodeKind kind, string name, string path = null, string guid = null, string typeName = null)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_nodes.TryGetValue(id, out var existing)) return existing;
            var n = new GraphNode { Id = id, Kind = kind, Name = name, Path = path, Guid = guid, TypeName = typeName };
            _nodes[id] = n;
            return n;
        }

        /// <summary>Add a directed edge. De-dups (from,to,relation); skips self-edges and edges with a missing endpoint.</summary>
        public void AddEdge(string from, string to, EdgeRelation relation)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return;
            if (!_nodes.ContainsKey(from) || !_nodes.ContainsKey(to)) return;
            string key = from + "|" + to + "|" + (int)relation;
            if (!_edgeKeys.Add(key)) return;

            var e = new GraphEdge { From = from, To = to, Relation = relation };
            _edges.Add(e);
            if (!_out.TryGetValue(from, out var ol)) { ol = new List<GraphEdge>(); _out[from] = ol; }
            ol.Add(e);
            if (!_in.TryGetValue(to, out var il)) { il = new List<GraphEdge>(); _in[to] = il; }
            il.Add(e);
        }

        // ----- pure query API -----

        public bool TryGetNode(string id, out GraphNode node) => _nodes.TryGetValue(id ?? "", out node);
        public IEnumerable<GraphNode> AllNodes() => _nodes.Values;
        public IEnumerable<GraphEdge> AllEdges() => _edges;

        // ----- systems (clusters) -----

        public IEnumerable<GraphSystem> AllSystems() => _systems.Values;
        public bool TryGetSystem(string id, out GraphSystem s) => _systems.TryGetValue(id ?? "", out s);
        public GraphSystem AddSystem(GraphSystem s) { if (s != null && !string.IsNullOrEmpty(s.Id)) _systems[s.Id] = s; return s; }
        public void ClearSystems() { _systems.Clear(); foreach (var n in _nodes.Values) { n.SystemId = null; n.IsSystemMain = false; } }

        /// <summary>Resolve an entity reference (id | GUID | asset path | type full name | unique display/short
        /// name) to a single node id. Returns null if missing or ambiguous.</summary>
        public string ResolveEntityId(string entity)
        {
            if (string.IsNullOrEmpty(entity)) return null;
            if (_nodes.ContainsKey(entity)) return entity;
            var matches = ResolveCandidates(entity, 2);
            return matches.Count == 1 ? matches[0].Id : null;
        }

        public List<GraphNode> ResolveCandidates(string entity, int max)
        {
            var result = new List<GraphNode>();
            if (string.IsNullOrEmpty(entity)) return result;
            if (_nodes.TryGetValue(entity, out var byId)) { result.Add(byId); return result; }

            foreach (var n in _nodes.Values)
            {
                bool hit = entity == n.Guid
                           || entity == n.Path
                           || entity == n.TypeName
                           || entity == n.Name
                           || (n.TypeName != null && ShortName(n.TypeName) == entity);
                if (hit)
                {
                    result.Add(n);
                    if (result.Count >= max) break;
                }
            }
            return result;
        }

        private static string ShortName(string fullName)
        {
            int i = fullName.LastIndexOf('.');
            return i >= 0 ? fullName.Substring(i + 1) : fullName;
        }

        /// <summary>Bounded BFS neighborhood around a center node, honoring direction + kind/relation filters.</summary>
        public NeighborhoodResult Neighbors(string id, int depth, Direction dir,
            ISet<NodeKind> kindFilter = null, ISet<EdgeRelation> relationFilter = null, int maxNodes = 300)
        {
            var res = new NeighborhoodResult { CenterId = id };
            if (!_nodes.ContainsKey(id ?? "")) return res;
            if (depth < 0) depth = 0;
            if (maxNodes < 1) maxNodes = 1;

            var visited = new HashSet<string> { id };
            var included = new HashSet<string> { id };
            var frontier = new List<string> { id };

            for (int d = 0; d < depth; d++)
            {
                var next = new List<string>();
                foreach (var cur in frontier)
                {
                    foreach (var e in EdgesOf(cur, dir))
                    {
                        if (relationFilter != null && !relationFilter.Contains(e.Relation)) continue;
                        string other = e.From == cur ? e.To : e.From;
                        if (!_nodes.TryGetValue(other, out var on)) continue;
                        if (kindFilter != null && !kindFilter.Contains(on.Kind)) continue;
                        if (!visited.Add(other)) continue;
                        if (included.Count < maxNodes) { included.Add(other); next.Add(other); }
                        else res.Truncated = true;
                    }
                }
                frontier = next;
                if (included.Count >= maxNodes) break;
            }

            BuildResult(res, included, relationFilter);
            return res;
        }

        /// <summary>Whole-graph projection filtered by kind, capped at maxNodes (edges further filtered by relation).</summary>
        public NeighborhoodResult Filtered(ISet<NodeKind> kindFilter, ISet<EdgeRelation> relationFilter, int maxNodes = 300)
        {
            var res = new NeighborhoodResult();
            var included = new HashSet<string>();
            foreach (var n in _nodes.Values)
            {
                if (kindFilter != null && !kindFilter.Contains(n.Kind)) continue;
                if (included.Count >= maxNodes) { res.Truncated = true; break; }
                included.Add(n.Id);
            }
            BuildResult(res, included, relationFilter);
            return res;
        }

        private IEnumerable<GraphEdge> EdgesOf(string id, Direction dir)
        {
            if ((dir == Direction.Out || dir == Direction.Both) && _out.TryGetValue(id, out var ol))
                foreach (var e in ol) yield return e;
            if ((dir == Direction.In || dir == Direction.Both) && _in.TryGetValue(id, out var il))
                foreach (var e in il) yield return e;
        }

        private void BuildResult(NeighborhoodResult res, HashSet<string> included, ISet<EdgeRelation> relationFilter)
        {
            foreach (var nid in included)
                if (_nodes.TryGetValue(nid, out var n)) res.Nodes.Add(n);
            foreach (var e in _edges)
            {
                if (!included.Contains(e.From) || !included.Contains(e.To)) continue;
                if (relationFilter != null && !relationFilter.Contains(e.Relation)) continue;
                res.Edges.Add(e);
            }
        }
    }
}
