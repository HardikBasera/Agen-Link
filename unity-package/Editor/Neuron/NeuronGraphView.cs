using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgenLink.Neuron
{
    /// <summary>IMGUI canvas for the project graph — an "Aerospace HUD" map. Overview draws nested clusters
    /// (scene › system › main/sub), circle-packed with no overlaps: scenes are soft cluster glows, systems are
    /// inner sub-rings, the system main script is the largest node at the centre, and multi-scene "shared" nodes
    /// sit in a dedicated cluster ringed in white with dashed tethers to the scenes that use them. Focused mode
    /// shows the radial neighbourhood of one node. Reads the cached <see cref="GraphStore"/>; never builds in OnGUI.</summary>
    internal sealed class NeuronGraphView
    {
        private readonly string _projectRoot;
        private readonly Action _repaint;

        private int _mode = 1;                 // 0 focused, 1 overview
        private static readonly string[] Modes = { "Focused", "Overview" };
        private string _search = "";
        private string _centerId;
        private int _depth = 1;
        private string _status = "";

        // toolbar toggles
        private bool _refsAlways = true;       // References shown by default
        private bool _showTethers = true;
        private bool _showSystems = true;
        private bool _labelsAll = true;        // labels shown by default

        private static readonly NodeKind[] FilterKinds = { NodeKind.Script, NodeKind.Prefab, NodeKind.Scene, NodeKind.Asset };
        private readonly Dictionary<NodeKind, bool> _kinds = new Dictionary<NodeKind, bool>
        {
            { NodeKind.Script, true }, { NodeKind.Prefab, true }, { NodeKind.Scene, true },
            { NodeKind.Asset, true }, { NodeKind.GameObject, true },
        };

        private Vector2 _pan;
        private float _zoom = 1f;
        private NeighborhoodResult _view;
        private Dictionary<string, Vector2> _pos;
        private Dictionary<string, int> _degree;
        private int _hubMin = 6;
        private long _viewKey = -1, _degKey = -1;
        private string _viewSig;
        private string _hover, _selected;

        // overview cluster geometry (world space), rebuilt with the view
        private sealed class OwnerGeo { public string Owner; public Vector2 Center; public float Radius; public Color Tint; public string Label; public bool IsScene; }
        private sealed class SysGeo { public Vector2 Center; public float Radius; public string Name; }
        private List<OwnerGeo> _ownerGeo = new List<OwnerGeo>();
        private List<SysGeo> _sysGeo = new List<SysGeo>();
        private List<(Vector2 a, float ra, Vector2 b, float rb, int count)> _sceneLinks = new List<(Vector2, float, Vector2, float, int)>();

        private GUIStyle _label, _labelHub, _legend, _center, _centerBold;

        // ---- Aerospace HUD palette ----
        private static readonly Color BG = new Color(0.055f, 0.067f, 0.086f);
        private static readonly Color Accent = new Color(0.478f, 0.824f, 0.92f);
        private static readonly Color SharedTint = new Color(0.43f, 0.80f, 0.84f);
        private static readonly Color ProjectTint = new Color(0.55f, 0.58f, 0.62f);
        private static readonly Color[] SceneTints =
        {
            new Color(0.59f, 0.55f, 0.84f), new Color(0.84f, 0.64f, 0.41f),
            new Color(0.45f, 0.70f, 0.60f), new Color(0.80f, 0.50f, 0.55f), new Color(0.50f, 0.62f, 0.82f),
        };

        public NeuronGraphView(string projectRoot, Action repaint) { _projectRoot = projectRoot; _repaint = repaint; }

        public void OnGUI()
        {
            EnsureStyles();
            var g = GraphStore.EnsureLoaded();
            DrawToolbar(g);

            if (GraphStore.Building) { Centered("Building graph…"); return; }
            if (g == null)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.HelpBox("No graph cached yet. Build it to map this project's scripts, prefabs and their wiring.", MessageType.Info);
                if (GUILayout.Button("Build graph", GUILayout.Width(120))) { GraphStore.RequestRebuild(); _repaint?.Invoke(); }
                return;
            }

            EnsureDegree(g);
            EnsureView(g);

            var canvas = GUILayoutUtility.GetRect(100, 4000, 100, 4000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            HandleInput(canvas);
            if (Event.current.type == EventType.Repaint) Draw(canvas, g);
        }

        // ---------------- toolbar ----------------

        private void DrawToolbar(ProjectGraph g)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Folders ▾", EditorStyles.toolbarButton, GUILayout.Width(70)))
                PopupWindow.Show(GUILayoutUtility.GetLastRect(), new FolderFilterPopup(_repaint));
            int m = GUILayout.Toolbar(_mode, Modes, EditorStyles.toolbarButton, GUILayout.Width(150));
            if (m != _mode) { _mode = m; Invalidate(); }

            if (_mode == 0)
            {
                _search = GUILayout.TextField(_search, EditorStyles.toolbarTextField, GUILayout.Width(140));
                if (GUILayout.Button("Focus", EditorStyles.toolbarButton, GUILayout.Width(46)) && g != null)
                {
                    string id = g.ResolveEntityId(_search);
                    _status = id != null ? "" : "No unique match for '" + _search + "'";
                    if (id != null) { _centerId = id; _selected = id; ResetView(); Invalidate(); }
                }
                GUILayout.Label("Depth", EditorStyles.miniLabel, GUILayout.Width(40));
                if (GUILayout.Button("−", EditorStyles.toolbarButton, GUILayout.Width(24)) && _depth > 1) { _depth--; Invalidate(); }
                GUILayout.Label(_depth.ToString(), EditorStyles.miniLabel, GUILayout.Width(14));
                if (GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(24)) && _depth < 4) { _depth++; Invalidate(); }
            }
            else
            {
                bool refs = GUILayout.Toggle(_refsAlways, "Refs", EditorStyles.toolbarButton, GUILayout.Width(48));
                if (refs != _refsAlways) { _refsAlways = refs; _repaint?.Invoke(); }
                bool teth = GUILayout.Toggle(_showTethers, "Shared", EditorStyles.toolbarButton, GUILayout.Width(56));
                if (teth != _showTethers) { _showTethers = teth; _repaint?.Invoke(); }
                bool sys = GUILayout.Toggle(_showSystems, "Systems", EditorStyles.toolbarButton, GUILayout.Width(62));
                if (sys != _showSystems) { _showSystems = sys; _repaint?.Invoke(); }
                bool lab = GUILayout.Toggle(_labelsAll, "Labels", EditorStyles.toolbarButton, GUILayout.Width(54));
                if (lab != _labelsAll) { _labelsAll = lab; _repaint?.Invoke(); }
            }

            GUILayout.FlexibleSpace();
            var prevBg = GUI.backgroundColor;
            foreach (var kind in FilterKinds)
            {
                Color c = NodeColor(kind);
                GUI.backgroundColor = _kinds[kind] ? c : new Color(c.r, c.g, c.b, 0.25f);
                bool v = GUILayout.Toggle(_kinds[kind], "  " + kind, EditorStyles.toolbarButton, GUILayout.Width(58));
                if (v != _kinds[kind]) { _kinds[kind] = v; Invalidate(); }
            }
            GUI.backgroundColor = prevBg;
            if (GUILayout.Button("⟳ Rebuild", EditorStyles.toolbarButton, GUILayout.Width(76))) { GraphStore.RequestRebuild(); _repaint?.Invoke(); }
            EditorGUILayout.EndHorizontal();

            int unnamed = 0;
            foreach (var s in g.AllSystems())
            {
                if (s.Id != null && s.Id.EndsWith("#scattered")) continue;
                if (GraphStore.SystemNames == null || !GraphStore.SystemNames.ContainsKey(s.Signature())) unnamed++;
            }
            string info = $"{g.NodeCount} nodes · {g.EdgeCount} edges · {g.SystemCount} systems";
            if (unnamed > 0) info += $"  ·  {unnamed} unnamed (ask Claude to analyze)";
            if (_view != null) info += $"  ·  showing {_view.Nodes.Count}" + (_view.Truncated ? " (capped)" : "");
            if (!string.IsNullOrEmpty(_status)) info += "  ·  " + _status;
            EditorGUILayout.LabelField(info, EditorStyles.miniLabel);
        }

        // ---------------- model / view ----------------

        private void Invalidate() => _viewSig = null;
        private void ResetView() { _pan = Vector2.zero; _zoom = 1f; }

        private ISet<NodeKind> KindFilter()
        {
            var set = new HashSet<NodeKind>();
            foreach (var kv in _kinds) if (kv.Value) set.Add(kv.Key);
            return set.Count == _kinds.Count ? null : set;
        }

        private string Sig()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(_mode).Append('|').Append(_centerId).Append('|').Append(_depth).Append('|');
            foreach (var kv in _kinds) if (kv.Value) sb.Append((int)kv.Key);
            return sb.ToString();
        }

        private void EnsureDegree(ProjectGraph g)
        {
            if (_degree != null && _degKey == g.BuiltAtUnixMs) return;
            _degKey = g.BuiltAtUnixMs;
            _degree = new Dictionary<string, int>();
            foreach (var e in g.AllEdges())
            {
                _degree[e.From] = (_degree.TryGetValue(e.From, out var a) ? a : 0) + 1;
                _degree[e.To] = (_degree.TryGetValue(e.To, out var b) ? b : 0) + 1;
            }
            int max = 0;
            foreach (var kv in _degree) if (kv.Value > max) max = kv.Value;
            _hubMin = Mathf.Max(5, Mathf.RoundToInt(max * 0.5f));
        }

        private void EnsureView(ProjectGraph g)
        {
            string sig = Sig();
            if (_view != null && _viewKey == g.BuiltAtUnixMs && _viewSig == sig) return;
            _viewKey = g.BuiltAtUnixMs;
            _viewSig = sig;
            var kinds = KindFilter();

            if (_mode == 0 && !string.IsNullOrEmpty(_centerId) && g.TryGetNode(_centerId, out _))
            {
                _view = g.Neighbors(_centerId, _depth, Direction.Both, kinds, null, 400);
                _pos = _view.Nodes.Count > 1
                    ? GraphLayout.ComputeRadial(_view.Nodes, _view.Edges, _centerId)
                    : GraphLayout.ComputeForce(_view.Nodes, _view.Edges, _centerId);
                _ownerGeo.Clear(); _sysGeo.Clear(); _sceneLinks.Clear();
            }
            else
            {
                _view = g.Filtered(kinds, null, 2000);
                _pos = GraphLayout.ComputeNestedClusters(g);
                BuildClusterGeometry(g);
            }
        }

        /// <summary>Compute owner (scene/shared/project) + system blob centres/radii and the scene↔scene shared
        /// links from the laid-out node positions, for Overview drawing.</summary>
        private void BuildClusterGeometry(ProjectGraph g)
        {
            _ownerGeo = new List<OwnerGeo>();
            _sysGeo = new List<SysGeo>();
            _sceneLinks = new List<(Vector2, float, Vector2, float, int)>();
            if (_pos == null) return;

            // scene tint assignment, deterministic by sorted scene id
            var sceneIds = new List<string>();
            foreach (var n in g.AllNodes()) if (n.Kind == NodeKind.Scene) sceneIds.Add(n.Id);
            sceneIds.Sort(StringComparer.Ordinal);
            var sceneTint = new Dictionary<string, Color>();
            for (int i = 0; i < sceneIds.Count; i++) sceneTint[sceneIds[i]] = SceneTints[i % SceneTints.Length];

            // group nodes by owner + by system from the systems registry
            var ownerCenter = new Dictionary<string, OwnerGeo>();
            foreach (var sys in g.AllSystems())
            {
                // system blob
                Vector2 sc = Vector2.zero; int sn = 0; float sr = 0f;
                var mpos = new List<(Vector2 p, float r)>();
                foreach (var id in sys.MemberIds)
                {
                    if (!_pos.TryGetValue(id, out var p) || !g.TryGetNode(id, out var nd)) continue;
                    float r = GraphLayout.NodeRadius(nd.Kind, Deg(id), nd.IsSystemMain);
                    mpos.Add((p, r)); sc += p; sn++;
                }
                if (sn == 0) continue;
                sc /= sn;
                foreach (var mp in mpos) sr = Mathf.Max(sr, (mp.p - sc).magnitude + mp.r);
                if (_showSystems && sys.MemberIds.Count > 1)
                    _sysGeo.Add(new SysGeo { Center = sc, Radius = sr + 12f, Name = sys.Name });

                // accumulate into owner geo
                if (!ownerCenter.TryGetValue(sys.Owner, out var og))
                {
                    og = new OwnerGeo { Owner = sys.Owner };
                    if (sys.Owner == "shared") { og.Tint = SharedTint; og.Label = "⬡ Shared · Core"; }
                    else if (sys.Owner == "project") { og.Tint = ProjectTint; og.Label = "Project · no scene"; }
                    else
                    {
                        og.IsScene = true;
                        og.Tint = sceneTint.TryGetValue(sys.Owner, out var tt) ? tt : SceneTints[0];
                        og.Label = "◆ SCENE · " + (g.TryGetNode(sys.Owner, out var snode) ? snode.Name : "Scene");
                    }
                    ownerCenter[sys.Owner] = og;
                }
            }

            // owner centre/radius from all member positions of that owner
            foreach (var kv in ownerCenter)
            {
                Vector2 c = Vector2.zero; int cnt = 0; float rad = 0f;
                var pts = new List<(Vector2 p, float r)>();
                foreach (var sys in g.AllSystems())
                {
                    if (sys.Owner != kv.Key) continue;
                    foreach (var id in sys.MemberIds)
                    {
                        if (!_pos.TryGetValue(id, out var p) || !g.TryGetNode(id, out var nd)) continue;
                        pts.Add((p, GraphLayout.NodeRadius(nd.Kind, Deg(id), nd.IsSystemMain))); c += p; cnt++;
                    }
                }
                if (cnt == 0) continue;
                c /= cnt;
                foreach (var pt in pts) rad = Mathf.Max(rad, (pt.p - c).magnitude + pt.r);
                kv.Value.Center = c; kv.Value.Radius = rad + 26f;
                _ownerGeo.Add(kv.Value);
            }

            // scene↔scene shared links (pairs that share ≥1 node)
            var pairCount = new Dictionary<string, int>();
            foreach (var n in g.AllNodes())
            {
                if (n.SceneIds == null || n.SceneIds.Count < 2) continue;
                var s = new List<string>(n.SceneIds); s.Sort(StringComparer.Ordinal);
                for (int i = 0; i < s.Count; i++)
                    for (int j = i + 1; j < s.Count; j++)
                    {
                        string key = s[i] + "|" + s[j];
                        pairCount[key] = (pairCount.TryGetValue(key, out var c) ? c : 0) + 1;
                    }
            }
            foreach (var kv in pairCount)
            {
                var parts = kv.Key.Split('|');
                var a = _ownerGeo.Find(o => o.Owner == parts[0]);
                var b = _ownerGeo.Find(o => o.Owner == parts[1]);
                if (a != null && b != null) _sceneLinks.Add((a.Center, a.Radius, b.Center, b.Radius, kv.Value));
            }
        }

        private int Deg(string id) => _degree != null && _degree.TryGetValue(id, out var d) ? d : 0;

        // ---------------- input ----------------

        private void HandleInput(Rect canvas)
        {
            var e = Event.current;
            if (!canvas.Contains(e.mousePosition))
            {
                if (_hover != null) { _hover = null; _repaint?.Invoke(); }
                return;
            }

            switch (e.type)
            {
                case EventType.ScrollWheel:
                {
                    // Zoom toward the cursor: keep the world point under the mouse fixed across the zoom step.
                    float oldZoom = _zoom;
                    float newZoom = Mathf.Clamp(_zoom * (1f - e.delta.y * 0.05f), 0.2f, 3f);
                    if (!Mathf.Approximately(newZoom, oldZoom))
                    {
                        Vector2 m = e.mousePosition - new Vector2(canvas.x, canvas.y); // window → canvas-local
                        Vector2 origin = Origin(canvas);                               // uses current _pan
                        _pan += (m - origin) * (1f - newZoom / oldZoom);
                        _zoom = newZoom;
                    }
                    e.Use(); _repaint?.Invoke();
                    break;
                }
                case EventType.MouseDrag when e.button == 2 || e.button == 0:
                    _pan += e.delta; e.Use(); _repaint?.Invoke();
                    break;
                case EventType.MouseMove:
                    string h = NodeAt(canvas, e.mousePosition);
                    if (h != _hover) { _hover = h; _repaint?.Invoke(); }
                    break;
                case EventType.MouseDown when e.button == 0:
                    string hit = NodeAt(canvas, e.mousePosition);
                    if (hit != null)
                    {
                        if (e.clickCount >= 2) { _centerId = hit; _mode = 0; ResetView(); Invalidate(); }
                        _selected = hit;
                    }
                    else _selected = null;
                    e.Use(); _repaint?.Invoke();
                    break;
            }
        }

        private string NodeAt(Rect canvas, Vector2 mouse)
        {
            if (_pos == null || _view == null) return null;
            Vector2 origin = Origin(canvas);
            // drawing happens inside GUI.BeginClip(canvas) → node centres are canvas-local; the event mouse is
            // in window space, so shift it into canvas-local before hit-testing (otherwise hover/click is offset
            // by the toolbar height).
            Vector2 m = mouse - new Vector2(canvas.x, canvas.y);
            for (int i = _view.Nodes.Count - 1; i >= 0; i--)
            {
                var n = _view.Nodes[i];
                if (n.Kind == NodeKind.Scene) continue;
                if (!_pos.TryGetValue(n.Id, out var wp)) continue;
                Vector2 c = origin + wp * _zoom;
                if ((m - c).sqrMagnitude <= Sqr(Radius(n) * _zoom + 3f)) return n.Id;
            }
            return null;
        }

        private Vector2 Origin(Rect canvas) => new Vector2(canvas.width * 0.5f, canvas.height * 0.5f) + _pan;

        // ---------------- draw ----------------

        private void Draw(Rect canvas, ProjectGraph g)
        {
            EditorGUI.DrawRect(canvas, BG);
            GUI.BeginClip(canvas);
            Vector2 origin = Origin(canvas);
            DrawGrid(canvas, origin);

            string focus = _hover ?? _selected;
            if (_mode == 0 && !string.IsNullOrEmpty(_centerId)) focus = _hover ?? _selected ?? _centerId;
            HashSet<string> active = null;
            if (focus != null)
            {
                active = new HashSet<string> { focus };
                foreach (var e in _view.Edges) { if (e.From == focus) active.Add(e.To); if (e.To == focus) active.Add(e.From); }
            }

            bool showScenes = _kinds[NodeKind.Scene];
            if (_mode == 1)
            {
                // scene↔scene shared links (behind everything), anchored at each circle's edge
                if (showScenes)
                    foreach (var sl in _sceneLinks)
                    {
                        Vector2 dir = (sl.b - sl.a); float l = dir.magnitude; if (l < 1f) continue; dir /= l;
                        Vector2 a = origin + (sl.a + dir * sl.ra) * _zoom;
                        Vector2 b = origin + (sl.b - dir * sl.rb) * _zoom;
                        DrawDashed(a, b, new Color(Accent.r, Accent.g, Accent.b, 0.25f));
                        Vector2 mid = (a + b) * 0.5f;
                        GUI.Label(new Rect(mid.x - 30f, mid.y - 7f, 60f, 14f), sl.count + " shared", _center);
                    }
                // owner (scene/shared/project) blobs
                foreach (var og in _ownerGeo)
                {
                    if (og.IsScene && !showScenes) continue;
                    DrawBlob(origin + og.Center * _zoom, og.Radius * _zoom, og.Tint, og.IsScene ? 0.030f : 0.022f);
                }
                // system sub-blobs
                if (_showSystems) foreach (var sg in _sysGeo) DrawCircleOutline(origin + sg.Center * _zoom, sg.Radius * _zoom, new Color(0.7f, 0.75f, 0.78f, 0.18f), 1f);
                // shared tethers
                if (_showTethers && showScenes) DrawTethers(origin, focus, active);
            }

            // edges (lines)
            foreach (var e in _view.Edges)
            {
                if (!_pos.TryGetValue(e.From, out var a) || !_pos.TryGetValue(e.To, out var b)) continue;
                bool lit = active != null && (e.From == focus || e.To == focus);
                if (e.Relation == EdgeRelation.References && !_refsAlways && _mode == 1 && !lit) continue;
                Color col = EdgeColor(e.Relation);
                float w;
                if (active == null) { w = 1.4f; }                                          // nothing focused
                else if (lit) { col = new Color(col.r, col.g, col.b, Mathf.Max(col.a, 0.96f)); w = 2.6f; } // highlighted
                else { col.a *= (_mode == 1 ? 0.08f : 0.35f); w = 1.1f; }                  // de-emphasized
                DrawEdgeLine(origin + a * _zoom, origin + b * _zoom, col, w);
            }

            // glow under focus
            if (focus != null && _pos.TryGetValue(focus, out var fp) && g.TryGetNode(focus, out var fn))
                DrawGlow(origin + fp * _zoom, Radius(fn) * _zoom, IsHub(fn) ? Accent : NodeColor(fn.Kind));

            // nodes
            foreach (var n in _view.Nodes)
            {
                if (n.Kind == NodeKind.Scene) continue;
                if (!_pos.TryGetValue(n.Id, out var wp)) continue;
                Vector2 c = origin + wp * _zoom; float r = Radius(n) * _zoom;
                if (c.x + r < 0 || c.y + r < 0 || c.x - r > canvas.width || c.y - r > canvas.height) continue;

                bool dim = active != null && !active.Contains(n.Id);
                bool hub = IsHub(n);
                Color fill = NodeColor(n.Kind);
                if (dim) fill.a *= 0.15f;
                DrawShape(n.Kind, c, r, fill);

                if (n.IsSystemMain && !dim)
                    DrawShapeOutline(n.Kind, c, r + 2.5f, new Color(1f, 1f, 1f, 0.45f), 1.5f);
                if (hub && !dim)
                    DrawShapeOutline(n.Kind, c, r + 4.5f, new Color(Accent.r, Accent.g, Accent.b, 0.85f), 1.5f);
                if ((n.SceneIds != null && n.SceneIds.Count > 1) && !dim)   // shared
                {
                    DrawShapeOutline(n.Kind, c, r + 6f, new Color(1f, 1f, 1f, 0.92f), 2f);
                    DrawShapeOutline(n.Kind, c, r + 9f, new Color(1f, 1f, 1f, 0.30f), 1f);
                }
                if (n.Id == _hover || n.Id == _selected || n.Id == _centerId)
                    DrawShapeOutline(n.Kind, c, r + (hub ? 10f : 7f), Accent, 1.6f);
            }

            // arrows ON TOP of nodes
            foreach (var e in _view.Edges)
            {
                if (!_pos.TryGetValue(e.From, out var a) || !_pos.TryGetValue(e.To, out var b)) continue;
                bool lit = active != null && (e.From == focus || e.To == focus);
                if (e.Relation == EdgeRelation.References && !_refsAlways && _mode == 1 && !lit) continue;
                if (active != null && !lit && _mode == 1) continue;   // in Overview, arrows only on lit edges
                if (!g.TryGetNode(e.To, out var tn)) continue;
                Color col = EdgeColor(e.Relation);
                DrawArrow(origin + a * _zoom, origin + b * _zoom, tn, lit ? new Color(col.r, col.g, col.b, 0.97f) : col);
            }

            // labels (collision-aware) + owner labels
            if (_mode == 1)
            {
                foreach (var og in _ownerGeo)
                {
                    if (og.IsScene && !showScenes) continue;
                    GUI.Label(LabelRect(origin + og.Center * _zoom, og.Radius * _zoom), og.Label, _centerBold);
                }
                if (_showSystems) foreach (var sg in _sysGeo) if (sg.Radius * _zoom > 28f) GUI.Label(LabelRect(origin + sg.Center * _zoom, sg.Radius * _zoom), sg.Name, _center);
            }
            DrawNodeLabels(origin, g, focus, active);

            GUI.EndClip();
            DrawLegend(canvas);
        }

        private Rect LabelRect(Vector2 center, float radius) => new Rect(center.x - 80f, center.y - radius - 16f, 160f, 14f);

        private void DrawTethers(Vector2 origin, string focus, HashSet<string> active)
        {
            foreach (var n in _view.Nodes)
            {
                if (n.SceneIds == null || n.SceneIds.Count < 2) continue;
                if (!_pos.TryGetValue(n.Id, out var wp)) continue;
                if (active != null && !active.Contains(n.Id)) continue;
                Vector2 np = origin + wp * _zoom; float nr = Radius(n) * _zoom;
                foreach (var sid in n.SceneIds)
                {
                    var og = _ownerGeo.Find(o => o.Owner == sid);
                    if (og == null) continue;
                    Vector2 gc = origin + og.Center * _zoom;
                    Vector2 d = gc - np; float len = d.magnitude; if (len < 1f) continue;
                    Vector2 dir = d / len;
                    Vector2 start = np + dir * nr;
                    Vector2 end = gc - dir * (og.Radius * _zoom);
                    DrawDashed(start, end, new Color(og.Tint.r, og.Tint.g, og.Tint.b, 0.5f));
                }
            }
        }

        private void DrawGrid(Rect canvas, Vector2 origin)
        {
            Handles.color = new Color(0.47f, 0.67f, 0.63f, 0.04f);
            float step = 46f * _zoom;
            if (step < 16f) return;
            for (float x = origin.x % step; x < canvas.width; x += step) Handles.DrawLine(new Vector2(x, 0), new Vector2(x, canvas.height));
            for (float y = origin.y % step; y < canvas.height; y += step) Handles.DrawLine(new Vector2(0, y), new Vector2(canvas.width, y));
        }

        private void DrawBlob(Vector2 c, float r, Color tint, float baseAlpha)
        {
            const int rings = 5;
            for (int i = rings; i >= 1; i--)
            {
                float t = (float)i / rings;
                DrawPoly(c, r * t, r * t, 36, 0f, new Color(tint.r, tint.g, tint.b, baseAlpha));
            }
            DrawCircleOutline(c, r, new Color(tint.r, tint.g, tint.b, 0.30f), 1.3f);
        }

        private void DrawGlow(Vector2 c, float r, Color col)
        {
            DrawPoly(c, r * 2.7f, r * 2.7f, 28, 0f, new Color(col.r, col.g, col.b, 0.05f));
            DrawPoly(c, r * 1.9f, r * 1.9f, 28, 0f, new Color(col.r, col.g, col.b, 0.10f));
        }

        private static void DrawEdgeLine(Vector2 a, Vector2 b, Color col, float width)
        {
            Vector2 d = b - a; float len = d.magnitude;
            if (len < 1f) return;
            Vector2 perp = new Vector2(-d.y, d.x) / len;
            float bend = Mathf.Min(30f, len * 0.12f);
            Vector2 ctrl = (a + b) * 0.5f + perp * bend;       // quadratic control point
            const int seg = 14;
            var pts = new Vector3[seg + 1];
            for (int i = 0; i <= seg; i++)
            {
                float t = (float)i / seg, u = 1f - t;
                Vector2 p = u * u * a + 2f * u * t * ctrl + t * t * b;
                pts[i] = new Vector3(p.x, p.y, 0f);
            }
            Handles.color = col;
            Handles.DrawAAPolyLine(width, pts);            // reliable (same primitive as node rings)
        }

        private void DrawArrow(Vector2 a, Vector2 b, GraphNode target, Color col)
        {
            Vector2 d = b - a; float len = d.magnitude; if (len < 1f) return;
            Vector2 perp = new Vector2(-d.y, d.x) / len;
            float bend = Mathf.Min(30f, len * 0.12f);
            Vector2 ctrl = (a + b) * 0.5f + perp * bend;
            Vector2 adir = (b - ctrl); float al = adir.magnitude; if (al < 0.01f) adir = d / len; else adir /= al;
            float rb = Boundary(target, adir) * _zoom + 1.5f;
            Vector2 tip = b - adir * rb;
            float size = Mathf.Max(2.5f, 8f * _zoom);
            Vector2 ap = new Vector2(-adir.y, adir.x);
            Vector2 bp = tip - adir * size;
            Handles.color = col;
            Handles.DrawAAConvexPolygon(tip, bp + ap * size * 0.5f, bp - ap * size * 0.5f);
        }

        private float Boundary(GraphNode n, Vector2 dir)
        {
            float r = Radius(n);
            if (n.Kind == NodeKind.Script)
            {
                float rx = r * 1.42f, ry = r * 0.8f;
                return 1f / Mathf.Sqrt(Sqr(dir.x / rx) + Sqr(dir.y / ry));
            }
            return r;
        }

        private void DrawShape(NodeKind kind, Vector2 c, float r, Color col)
        {
            switch (kind)
            {
                case NodeKind.Prefab: DrawPoly(c, r, r, 28, 0f, col); break;
                case NodeKind.Script: DrawPoly(c, r * 1.42f, r * 0.8f, 30, 0f, col); break;
                case NodeKind.Asset: DrawPoly(c, r * 1.04f, r * 1.04f, 6, 30f, col); break;
                default: DrawPoly(c, r * 0.92f, r * 0.92f, 22, 0f, col); break;
            }
        }

        private void DrawShapeOutline(NodeKind kind, Vector2 c, float r, Color col, float width)
        {
            // ring matches the node's shape (ellipse for scripts) so it doesn't read as a circle around an ellipse
            if (kind == NodeKind.Script) DrawEllipseOutline(c, r * 1.42f, r * 0.8f, col, width);
            else DrawCircleOutline(c, r, col, width);
        }

        private static void DrawPoly(Vector2 c, float rx, float ry, int sides, float rotDeg, Color col)
        {
            var pts = new Vector3[sides];
            float rot = rotDeg * Mathf.Deg2Rad;
            for (int i = 0; i < sides; i++)
            {
                float a = rot + 2f * Mathf.PI * i / sides;
                pts[i] = new Vector3(c.x + Mathf.Cos(a) * rx, c.y + Mathf.Sin(a) * ry, 0f);
            }
            Handles.color = col;
            Handles.DrawAAConvexPolygon(pts);
        }

        private static void DrawCircleOutline(Vector2 c, float r, Color col, float width)
            => DrawEllipseOutline(c, r, r, col, width);

        private static void DrawEllipseOutline(Vector2 c, float rx, float ry, Color col, float width)
        {
            const int seg = 36;
            var pts = new Vector3[seg + 1];
            for (int i = 0; i <= seg; i++)
            {
                float a = 2f * Mathf.PI * i / seg;
                pts[i] = new Vector3(c.x + Mathf.Cos(a) * rx, c.y + Mathf.Sin(a) * ry, 0f);
            }
            Handles.color = col;
            Handles.DrawAAPolyLine(width, pts);
        }

        // smooth, evenly-spaced dotted line (avoids the chunky look of Handles.DrawDottedLine)
        private static void DrawDashed(Vector2 a, Vector2 b, Color col)
        {
            float len = (b - a).magnitude;
            if (len < 1f) return;
            int n = Mathf.Max(1, Mathf.RoundToInt(len / 7f));
            for (int i = 0; i <= n; i++)
            {
                Vector2 p = Vector2.Lerp(a, b, (float)i / n);
                DrawPoly(p, 1.3f, 1.3f, 8, 0f, col);
            }
        }

        // ---------------- node labels (collision-aware) ----------------

        private void DrawNodeLabels(Vector2 origin, ProjectGraph g, string focus, HashSet<string> active)
        {
            var screen = new List<(GraphNode n, Vector2 c, float r)>();
            foreach (var n in _view.Nodes)
            {
                if (n.Kind == NodeKind.Scene) continue;
                if (!_pos.TryGetValue(n.Id, out var wp)) continue;
                screen.Add((n, origin + wp * _zoom, Radius(n) * _zoom));
            }

            var cands = new List<(GraphNode n, Vector2 c, float r)>();
            foreach (var s in screen)
            {
                if (active != null && !active.Contains(s.n.Id)) continue;
                bool shared = s.n.SceneIds != null && s.n.SceneIds.Count > 1;
                bool show = _labelsAll || _mode == 0 || IsHub(s.n) || shared || s.n.IsSystemMain
                            || s.n.Id == _hover || s.n.Id == _selected || _zoom >= 1.25f;
                if (show) cands.Add(s);
            }
            cands.Sort((x, y) => Priority(y.n) - Priority(x.n));

            var placed = new List<Rect>();
            foreach (var s in cands)
            {
                bool hub = IsHub(s.n) || s.n.IsSystemMain;
                var style = hub ? _labelHub : _label;
                Vector2 sz = style.CalcSize(new GUIContent(s.n.Name));
                float w = sz.x + 4f, h = 14f, R = s.r;
                Vector2[] tries =
                {
                    new Vector2(s.c.x - w / 2f, s.c.y + R + 4f),
                    new Vector2(s.c.x - w / 2f, s.c.y - R - 4f - h),
                    new Vector2(s.c.x + R + 6f, s.c.y - h / 2f),
                    new Vector2(s.c.x - R - 6f - w, s.c.y - h / 2f),
                };
                Rect? put = null;
                foreach (var t in tries)
                {
                    var rect = new Rect(t.x, t.y, w, h);
                    bool ok = true;
                    foreach (var o in screen) { if (o.n == s.n) continue; if (RectHitsCircle(rect, o.c, o.r + 1f)) { ok = false; break; } }
                    if (ok) foreach (var p in placed) if (p.Overlaps(rect)) { ok = false; break; }
                    if (ok) { put = rect; break; }
                }
                if (put == null) continue;
                GUI.Label(put.Value, s.n.Name, style);
                placed.Add(put.Value);
            }
        }

        private int Priority(GraphNode n)
        {
            if (n.Id == _selected || n.Id == _hover || n.Id == _centerId) return 4;
            if (n.SceneIds != null && n.SceneIds.Count > 1) return 3;
            if (n.IsSystemMain) return 2;
            if (IsHub(n)) return 1;
            return 0;
        }

        private static bool RectHitsCircle(Rect r, Vector2 c, float radius)
        {
            float nx = Mathf.Clamp(c.x, r.xMin, r.xMax), ny = Mathf.Clamp(c.y, r.yMin, r.yMax);
            return Sqr(c.x - nx) + Sqr(c.y - ny) < radius * radius;
        }

        // ---------------- legend ----------------

        private void DrawLegend(Rect canvas)
        {
            string[] labels = { "Script", "Prefab", "Asset" };
            NodeKind[] kinds = { NodeKind.Script, NodeKind.Prefab, NodeKind.Asset };
            float h = 16f + (labels.Length + 1) * 15f, w = 170f;
            var box = new Rect(canvas.x + 8f, canvas.yMax - h - 8f, w, h);
            EditorGUI.DrawRect(box, new Color(0f, 0f, 0f, 0.5f));
            GUI.Label(new Rect(box.x + 6f, box.y + 1f, w, 14f), "scene › system › main › sub", _legend);
            for (int i = 0; i < kinds.Length; i++)
            {
                float y = box.y + 16f + i * 15f;
                EditorGUI.DrawRect(new Rect(box.x + 8f, y + 3f, 9f, 9f), NodeColor(kinds[i]));
                GUI.Label(new Rect(box.x + 24f, y, w, 14f), labels[i], _legend);
            }
            float ys = box.y + 16f + kinds.Length * 15f;
            GUI.Label(new Rect(box.x + 24f, ys, w, 14f), "○ shared (multi-scene)", _legend);
        }

        /// <summary>Folder show/hide filter as a persistent popup: unlike a GenericMenu it stays open while you
        /// toggle several folders and only dismisses when you click outside it. Reads/writes the same
        /// <see cref="GraphStore.IncludeFolders"/> state the toolbar rebuild consumes.</summary>
        private sealed class FolderFilterPopup : PopupWindowContent
        {
            private readonly Action _repaint;
            private Vector2 _scroll;
            private string[] _folders;
            public FolderFilterPopup(Action repaint) { _repaint = repaint; }

            public override Vector2 GetWindowSize()
            {
                _folders = _folders ?? AssetDatabase.GetSubFolders("Assets");
                int rows = _folders.Length + 1;                 // + "All folders"
                return new Vector2(240f, Mathf.Min(24f + rows * 18f, 360f));
            }

            public override void OnGUI(Rect rect)
            {
                _folders = _folders ?? AssetDatabase.GetSubFolders("Assets");
                var inc = GraphStore.IncludeFolders;
                bool all = inc == null || inc.Count == 0;

                EditorGUILayout.Space(2f);
                bool newAll = EditorGUILayout.ToggleLeft("All folders", all);
                if (newAll && !all) { GraphStore.IncludeFolders = null; GraphStore.RequestRebuild(); _repaint?.Invoke(); }
                EditorGUILayout.Space(2f);

                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var f in _folders)
                {
                    bool on = all || inc.Contains(f);
                    bool now = EditorGUILayout.ToggleLeft(f.Substring("Assets/".Length), on);
                    if (now != on) ToggleFolder(f);
                }
                EditorGUILayout.EndScrollView();
            }

            private void ToggleFolder(string folder)
            {
                var inc = GraphStore.IncludeFolders;
                if (inc == null)
                {
                    inc = new HashSet<string>(AssetDatabase.GetSubFolders("Assets"));
                    inc.Remove(folder);
                }
                else if (!inc.Remove(folder)) inc.Add(folder);
                GraphStore.IncludeFolders = inc.Count == 0 ? null : inc;
                GraphStore.RequestRebuild();
                _repaint?.Invoke();
            }
        }

        // ---------------- helpers ----------------

        private float Radius(GraphNode n)
            => n == null ? 12f : GraphLayout.NodeRadius(n.Kind, Deg(n.Id), n.IsSystemMain);

        private bool IsHub(GraphNode n)
            => n != null && _degree != null && _degree.TryGetValue(n.Id, out var c) && c >= _hubMin && _hubMin > 0;

        private void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperLeft, clipping = TextClipping.Overflow };
            _label.normal.textColor = new Color(0.79f, 0.82f, 0.85f);
            _labelHub = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
            _labelHub.normal.textColor = Color.white;
            _legend = new GUIStyle(EditorStyles.miniLabel);
            _legend.normal.textColor = new Color(0.79f, 0.82f, 0.85f);
            _center = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperCenter };
            _center.normal.textColor = new Color(0.72f, 0.76f, 0.80f);
            _centerBold = new GUIStyle(_center) { fontStyle = FontStyle.Bold };
            _centerBold.normal.textColor = Color.white;
        }

        private static void Centered(string msg)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(msg, EditorStyles.centeredGreyMiniLabel);
        }

        private static float Sqr(float x) => x * x;

        private static Color NodeColor(NodeKind k)
        {
            switch (k)
            {
                case NodeKind.Script: return new Color(0.42f, 0.66f, 0.86f);
                case NodeKind.Prefab: return new Color(0.47f, 0.78f, 0.59f);
                case NodeKind.Scene: return new Color(0.82f, 0.64f, 0.41f);
                case NodeKind.GameObject: return new Color(0.63f, 0.55f, 0.78f);
                default: return new Color(0.59f, 0.62f, 0.66f);
            }
        }

        private static Color EdgeColor(EdgeRelation r)
        {
            switch (r)
            {
                case EdgeRelation.Inherits: return new Color(0.59f, 0.77f, 0.96f, 0.9f);
                case EdgeRelation.Implements: return new Color(0.50f, 0.86f, 0.86f, 0.85f);
                case EdgeRelation.HasField: return new Color(0.82f, 0.80f, 0.55f, 0.8f);
                case EdgeRelation.Component: return new Color(0.55f, 0.86f, 0.65f, 0.85f);
                case EdgeRelation.AssetRef: return new Color(0.59f, 0.63f, 0.70f, 0.7f);
                case EdgeRelation.References: return new Color(0.55f, 0.71f, 0.61f, 0.6f);
                default: return new Color(0.78f, 0.66f, 0.90f, 0.8f);
            }
        }
    }
}
