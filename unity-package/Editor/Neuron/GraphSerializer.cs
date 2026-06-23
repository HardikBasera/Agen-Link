using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AgenLink.Neuron
{
    /// <summary>Persists the project graph to <c>&lt;ProjectRoot&gt;/Library/AgenLink/neuron-graph.json</c> so it
    /// survives domain reloads. Stores nodes (incl. scene-membership + system assignment), edges, the system
    /// clusters, and the Claude name cache (signature → name). Uses Newtonsoft (a package dependency) for this
    /// internal cache — the bridge wire protocol stays on the hand-rolled JObj.</summary>
    internal static class GraphSerializer
    {
        private static string CacheFile()
            => Path.Combine(ConfigBuilder.ProjectRoot(), "Library", "AgenLink", "neuron-graph.json");

        private class Dto
        {
            public long builtAtUnixMs;
            public string projectRoot;
            public List<GraphNode> nodes;
            public List<EdgeDto> edges;
            public List<GraphSystem> systems;
            public Dictionary<string, string> systemNames;   // cluster signature -> Claude name
        }

        private class EdgeDto { public string from; public string to; public int relation; }

        public static void Save(ProjectGraph g, Dictionary<string, string> systemNames = null)
        {
            var dto = new Dto
            {
                builtAtUnixMs = g.BuiltAtUnixMs,
                projectRoot = g.ProjectRoot,
                nodes = new List<GraphNode>(),
                edges = new List<EdgeDto>(),
                systems = new List<GraphSystem>(),
                systemNames = systemNames ?? new Dictionary<string, string>(),
            };
            foreach (var n in g.AllNodes()) dto.nodes.Add(n);
            foreach (var e in g.AllEdges()) dto.edges.Add(new EdgeDto { from = e.From, to = e.To, relation = (int)e.Relation });
            foreach (var s in g.AllSystems()) dto.systems.Add(s);

            string file = CacheFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, JsonConvert.SerializeObject(dto));
        }

        public static ProjectGraph Load(out Dictionary<string, string> systemNames)
        {
            systemNames = new Dictionary<string, string>();
            string file = CacheFile();
            if (!File.Exists(file)) return null;
            var dto = JsonConvert.DeserializeObject<Dto>(File.ReadAllText(file));
            if (dto == null) return null;

            var g = new ProjectGraph { BuiltAtUnixMs = dto.builtAtUnixMs, ProjectRoot = dto.projectRoot };
            if (dto.nodes != null)
                foreach (var n in dto.nodes)
                {
                    var nn = g.AddNode(n.Id, n.Kind, n.Name, n.Path, n.Guid, n.TypeName);
                    if (nn != null)
                    {
                        nn.SceneIds = n.SceneIds ?? new List<string>();
                        nn.SystemId = n.SystemId;
                        nn.IsSystemMain = n.IsSystemMain;
                    }
                }
            if (dto.edges != null)
                foreach (var e in dto.edges)
                    g.AddEdge(e.from, e.to, (EdgeRelation)e.relation);
            if (dto.systems != null)
                foreach (var s in dto.systems)
                    g.AddSystem(s);
            if (dto.systemNames != null) systemNames = dto.systemNames;
            return g;
        }
    }
}
