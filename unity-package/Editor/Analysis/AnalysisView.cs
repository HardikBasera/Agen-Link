using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgenLink.Analysis
{
    /// <summary>
    /// IMGUI scene-optimization tab: one-click audit (scene + assets), checkbox-selected auto-fixes
    /// (all checked by default), one-click apply (Undo-able scene fixes; PERMANENT-tagged asset
    /// reimports), and a play-mode profiling section. Audit state survives domain reloads via
    /// SessionState because the analyze → fix → profile flow crosses the play-mode reload. Applies
    /// are logged by FixApplier to AgenLink~/analysis.jsonl and surface as amber ANALYSIS cards in
    /// the History tab.
    /// </summary>
    internal sealed class AnalysisView
    {
        private const string KeyState = "AgenLink.Analysis.State";
        private const string KeyUnchecked = "AgenLink.Analysis.Unchecked";

        private static readonly string[] SeverityFilters = { "All", "Critical", "Warn+", "Info" };

        /// <summary>Human titles for rule groups; unknown ids fall back to the raw rule id.</summary>
        private static readonly Dictionary<string, string> RuleTitles = new Dictionary<string, string>
        {
            { "mesh.not-static", "Mark non-moving objects static" },
            { "mesh.no-lod", "Add LOD groups to heavy meshes" },
            { "mesh.high-poly", "High-poly meshes (decimate / LOD)" },
            { "mesh.readable", "Disable mesh Read/Write" },
            { "light.realtime", "Bake realtime lights" },
            { "light.realtime-count", "Too many realtime lights" },
            { "light.realtime-shadows", "Too many shadowed realtime lights" },
            { "light.no-lightmaps", "No baked lightmaps" },
            { "render.realtime-probe", "Bake reflection probes" },
            { "render.transparent-overdraw", "Transparent overdraw" },
            { "camera.far-plane", "Reduce camera far plane" },
            { "camera.near-plane", "Raise tiny camera near plane" },
            { "particles.max", "Cap particle system budgets" },
            { "physics.rigidbody-count", "Many active Rigidbodies" },
            { "physics.heavy-meshcollider", "Heavy MeshColliders" },
            { "scene.missing-script", "Missing script references" },
            { "scene.triangle-budget", "Scene over triangle budget" },
            { "scene.no-occlusion", "No occlusion culling data" },
            { "settings.static-batching-off", "Static batching disabled" },
            { "urp.hdr", "URP: HDR enabled" },
            { "urp.shadow-distance", "URP: long shadow distance" },
            { "urp.cascades", "URP: many shadow cascades" },
            { "urp.depth-texture", "URP: depth texture enabled" },
            { "urp.opaque-texture", "URP: opaque texture enabled" },
            { "tex.large", "Reduce texture max size" },
            { "tex.uncompressed", "Compress textures" },
            { "tex.no-android-override", "Add Android ASTC override" },
            { "tex.npot", "Non-power-of-two textures" },
            { "tex.readable", "Texture Read/Write enabled" },
            { "tex.no-mip-streaming", "Enable texture mipmap streaming" },
            { "mesh.no-compression", "Enable mesh compression" },
            { "light.large-lightmaps", "Lightmaps over mobile size budget" },
            { "audio.decompress-on-load", "Audio: avoid Decompress On Load" },
        };

        private static readonly Dictionary<string, string> PerfNames = new Dictionary<string, string>
        {
            { "mainThreadMs", "main thread (ms)" },
            { "batches", "batches" },
            { "setPassCalls", "SetPass calls" },
            { "drawCalls", "draw calls" },
            { "triangles", "triangles" },
            { "vertices", "vertices" },
            { "gcAllocPerFrameB", "GC alloc / frame (B)" },
            { "totalMemoryMB", "total memory (MB)" },
            { "gpuFrameMs", "GPU frame (ms)" },
        };

        private readonly Action _repaint;

        // audit state (persisted via SessionState across domain reloads)
        private List<Finding> _sceneFindings, _assetFindings;
        private SceneStats _sceneStats;
        private AssetStats _assetStats;
        private DateTime _ranAt;
        private readonly HashSet<string> _unchecked = new HashSet<string>();   // default = all checked

        /// <summary>Findings sharing one rule id, rendered as a collapsible group.</summary>
        private sealed class RuleGroup
        {
            public string Id;
            public readonly List<Finding> Items = new List<Finding>();
        }

        // derived from the findings lists; null = rebuild on next draw
        private List<RuleGroup> _sceneGroups, _assetGroups;
        private readonly HashSet<string> _openGroups = new HashSet<string>();

        // Project Auditor results (explicit run; not persisted — re-run after a reload if needed)
        private List<Finding> _codeFindings;
        private List<RuleGroup> _codeGroups;

        // apply state (in-memory only; the durable record lives in the History tab).
        // Successfully applied fixes are REMOVED from the lists; _results only keeps failures.
        private readonly Dictionary<string, FixResult> _results = new Dictionary<string, FixResult>();
        private bool _applyTouchedScene;
        private int _lastOk, _lastFailed;

        // perf assessment cache (rebuilt when the parked report changes)
        private string _assessedJson;
        private List<PerfVerdict> _verdicts;

        // UI state
        private int _severityFilter;
        private Vector2 _scroll;
        private bool _restored;

        // ----- palette (matches the History tab's editorial terminal design) -----
        private static readonly Color CardBg  = C(0x23, 0x24, 0x29);
        private static readonly Color LineCol = C(0x34, 0x35, 0x3C);
        private static readonly Color TextCol = C(0xD6, 0xD7, 0xDC);
        private static readonly Color Muted   = C(0x8B, 0x8D, 0x98);
        private static readonly Color Faint   = C(0x5F, 0x61, 0x6C);
        private static readonly Color Detail  = C(0xB9, 0xBB, 0xC4);
        private static readonly Color CritCol = C(0xE0, 0x6C, 0x6C);
        private static readonly Color WarnCol = C(0xE2, 0xB1, 0x4E);   // amber — also the ANALYSIS history color
        private static readonly Color OkCol   = C(0x7C, 0xC6, 0x8B);
        private static Color C(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

        private GUIStyle _section, _rowBlock, _targetBtn, _evidence, _reco, _tag, _dot,
                         _idLabel, _okLine, _errLine, _stats, _verdictHead, _arrowBtn, _groupTitle;

        public AnalysisView(Action repaint)
        {
            _repaint = repaint;
        }

        public void OnGUI()
        {
            EnsureStyles();
            if (!_restored) { _restored = true; TryRestoreSessionState(); }

            DrawToolbar();
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (_sceneFindings == null)
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox("Analyze the active scene to get optimization findings. Auto-fixable " +
                    "findings get a checkbox (all checked by default); Apply runs the checked ones. Scene fixes " +
                    "are Undo-able and never auto-saved; asset fixes reimport immediately (PERMANENT).", MessageType.Info);
            }
            else
            {
                if (_sceneGroups == null)
                {
                    _sceneGroups = BuildGroups(_sceneFindings);
                    _assetGroups = BuildGroups(_assetFindings);
                }
                DrawStatsStrip();
                DrawSection("SCENE", _sceneFindings, _sceneGroups);
                DrawSection("ASSETS", _assetFindings, _assetGroups);
            }
            GUILayout.Space(8);
            DrawCodeSection();
            GUILayout.Space(8);
            DrawPerfSection();
            GUILayout.Space(10);
            EditorGUILayout.EndScrollView();
            if (_sceneFindings != null) DrawFixBar();
        }

        // ----- toolbar + stats -----

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("▶ Analyze Scene", EditorStyles.toolbarButton, GUILayout.Width(110))) RunAnalysis();
            GUILayout.Space(6);
            GUILayout.Label("Severity", EditorStyles.miniLabel, GUILayout.Width(50));
            _severityFilter = EditorGUILayout.Popup(_severityFilter, SeverityFilters, EditorStyles.toolbarPopup, GUILayout.Width(75));
            using (new EditorGUI.DisabledScope(_sceneFindings == null))
            {
                if (GUILayout.Button("Select all", EditorStyles.toolbarButton, GUILayout.Width(70))) SetAll(true);
                if (GUILayout.Button("Select none", EditorStyles.toolbarButton, GUILayout.Width(82))) SetAll(false);
            }
            GUILayout.FlexibleSpace();
            if (_sceneFindings != null)
                GUILayout.Label("analyzed " + History.DateGroups.Rel(_ranAt, DateTime.Now), EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatsStrip()
        {
            // Guard a partial session restore that set findings but not stats (see TryRestoreSessionState).
            if (_sceneStats == null || _assetStats == null) return;
            string s = "△ " + _sceneStats.Triangles.ToString("n0") + " tris · "
                     + _sceneStats.Renderers + " renderers (" + _sceneStats.StaticRenderers + " static) · "
                     + _sceneStats.RealtimeLights + " realtime lights";
            s += _assetStats.Note != null
                ? " · assets: scene unsaved"
                : " · " + _assetStats.Textures + " tex / " + _assetStats.Models + " models / " + _assetStats.Audios + " audio";
            GUILayout.Label(s, _stats);
        }

        // ----- findings -----

        private void DrawSection(string title, List<Finding> findings, List<RuleGroup> groups)
        {
            GUILayout.Space(10);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(title + "  ·  " + findings.Count + (findings.Count == 1 ? " finding" : " findings"),
                _section, GUILayout.ExpandWidth(false));
            Rect rule = GUILayoutUtility.GetRect(10f, 9f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(rule.x + 6, rule.y + 5, rule.width - 8, 1), LineCol);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(2);

            int shown = 0;
            foreach (RuleGroup g in groups)
            {
                List<Finding> visible = Visible(g);
                if (visible.Count == 0) continue;
                shown += visible.Count;
                if (g.Items.Count == 1) DrawFindingRow(g.Items[0]);
                else DrawGroup(title, g, visible);
            }
            if (shown == 0)
                GUILayout.Label(findings.Count == 0 ? "nothing flagged" : "nothing at this severity",
                    EditorStyles.centeredGreyMiniLabel);
        }

        // ----- rule groups (one dropdown per rule, group checkbox checks/unchecks all inside) -----

        private static List<RuleGroup> BuildGroups(List<Finding> findings)
        {
            var by = new Dictionary<string, RuleGroup>();
            var groups = new List<RuleGroup>();
            foreach (Finding f in findings)   // findings are severity-sorted; first occurrence orders the group
            {
                if (!by.TryGetValue(f.Id, out RuleGroup g))
                {
                    g = new RuleGroup { Id = f.Id };
                    by[f.Id] = g;
                    groups.Add(g);
                }
                g.Items.Add(f);
            }
            return groups;
        }

        private List<Finding> Visible(RuleGroup g)
        {
            var list = new List<Finding>();
            foreach (Finding f in g.Items)
                if (PassesSeverity(f)) list.Add(f);
            return list;
        }

        private static string GroupTitle(RuleGroup g)
        {
            if (RuleTitles.TryGetValue(g.Id, out string t)) return t;
            // Project Auditor groups: the shared issue description beats a raw "pa.PAC0070" id.
            return g.Id.StartsWith("pa.", StringComparison.Ordinal) && !string.IsNullOrEmpty(g.Items[0].Recommendation)
                ? g.Items[0].Recommendation : g.Id;
        }

        private static string WorstSeverity(List<Finding> items)
        {
            string worst = "info";
            foreach (Finding f in items)
                if (Finding.Rank(f.Severity) < Finding.Rank(worst)) worst = f.Severity;
            return worst;
        }

        private void DrawGroup(string section, RuleGroup g, List<Finding> visible)
        {
            string gkey = section + "|" + g.Id;
            bool open = _openGroups.Contains(gkey);
            bool fixable = g.Items[0].FixType != null;   // one rule -> one fix op

            EditorGUILayout.BeginVertical(_rowBlock);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(open ? "▾" : "▸", _arrowBtn, GUILayout.Width(16)))
            {
                if (open) _openGroups.Remove(gkey); else _openGroups.Add(gkey);
                open = !open;
            }

            Color prev = GUI.contentColor;
            GUI.contentColor = SeverityColor(WorstSeverity(visible));
            GUILayout.Label("●", _dot, GUILayout.Width(12));
            GUI.contentColor = prev;

            if (fixable)
            {
                int on = 0;
                foreach (Finding f in visible)
                    if (!_unchecked.Contains(KeyOf(f))) on++;
                bool all = on == visible.Count;
                EditorGUI.showMixedValue = on > 0 && on < visible.Count;
                bool now = EditorGUILayout.Toggle(all, GUILayout.Width(16));
                EditorGUI.showMixedValue = false;
                if (now != all)
                {
                    foreach (Finding f in visible)
                    {
                        if (now) _unchecked.Remove(KeyOf(f));
                        else _unchecked.Add(KeyOf(f));
                    }
                    SaveUncheckedState();
                }
            }
            else
            {
                GUILayout.Space(20);
            }

            if (GUILayout.Button(GroupTitle(g) + "  (" + visible.Count + ")", _groupTitle))
            {
                if (open) _openGroups.Remove(gkey); else _openGroups.Add(gkey);
                open = !open;
            }
            if (fixable && FixApplier.IsPermanentFix(g.Items[0].FixType))
            {
                GUI.contentColor = WarnCol;
                GUILayout.Label("PERMANENT", _tag, GUILayout.ExpandWidth(false));
                GUI.contentColor = prev;
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(g.Id, _idLabel, GUILayout.ExpandWidth(false));
            EditorGUILayout.EndHorizontal();

            if (open)
            {
                GUILayout.Label(g.Items[0].Recommendation, _reco);   // shared rule text, shown once
                GUILayout.Space(2);
                foreach (Finding f in visible) DrawGroupItem(f);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawGroupItem(Finding f)
        {
            string key = KeyOf(f);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(30);
            if (f.FixType != null)
            {
                bool was = !_unchecked.Contains(key);
                bool now = EditorGUILayout.Toggle(was, GUILayout.Width(16));
                if (now != was)
                {
                    if (now) _unchecked.Remove(key); else _unchecked.Add(key);
                    SaveUncheckedState();
                }
            }
            else
            {
                GUILayout.Space(20);
            }
            if (GUILayout.Button(f.Target, _targetBtn, GUILayout.ExpandWidth(false))) Ping(f);
            GUILayout.Space(8);
            GUILayout.Label(f.Evidence, _evidence);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (_results.TryGetValue(key, out FixResult r) && !r.Ok)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(46);
                GUILayout.Label("✗ " + r.Error, _errLine);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawFindingRow(Finding f)
        {
            string key = KeyOf(f);
            EditorGUILayout.BeginVertical(_rowBlock);
            EditorGUILayout.BeginHorizontal();

            Color prev = GUI.contentColor;
            GUI.contentColor = SeverityColor(f.Severity);
            GUILayout.Label("●", _dot, GUILayout.Width(12));
            GUI.contentColor = prev;

            if (f.FixType != null)
            {
                bool was = !_unchecked.Contains(key);
                bool now = EditorGUILayout.Toggle(was, GUILayout.Width(16));
                if (now != was)
                {
                    if (now) _unchecked.Remove(key); else _unchecked.Add(key);
                    SaveUncheckedState();
                }
            }
            else
            {
                GUILayout.Space(20);   // keep targets aligned with fixable rows
            }

            if (GUILayout.Button(f.Target, _targetBtn, GUILayout.ExpandWidth(false))) Ping(f);
            if (f.FixType != null && FixApplier.IsPermanentFix(f.FixType))
            {
                GUI.contentColor = WarnCol;
                GUILayout.Label("PERMANENT", _tag, GUILayout.ExpandWidth(false));
                GUI.contentColor = prev;
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(f.Id, _idLabel, GUILayout.ExpandWidth(false));
            EditorGUILayout.EndHorizontal();

            GUILayout.Label(f.Evidence, _evidence);
            GUILayout.Label(f.Recommendation, _reco);

            if (_results.TryGetValue(key, out FixResult r))
                GUILayout.Label(r.Ok ? "✓ " + r.Detail : "✗ " + r.Error, r.Ok ? _okLine : _errLine);

            EditorGUILayout.EndVertical();
        }

        // ----- fix bar -----

        private void DrawFixBar()
        {
            if (_applyTouchedScene)
                EditorGUILayout.HelpBox("Scene fixes are NOT saved — Ctrl+Z reverts any fix; save the scene to keep them.",
                    MessageType.Warning);

            EditorGUILayout.BeginHorizontal();
            int n = CheckedCount();
            using (new EditorGUI.DisabledScope(n == 0))
            {
                if (GUILayout.Button("Apply " + n + (n == 1 ? " fix" : " fixes"), GUILayout.Height(26), GUILayout.Width(140)))
                    ApplyChecked();
            }
            if (_applyTouchedScene && GUILayout.Button("Save Scene", GUILayout.Height(26), GUILayout.Width(100)))
            {
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
                RunAnalysis();   // re-audit the saved truth: fixed findings stay gone, stats refresh
            }
            if (_lastOk + _lastFailed > 0)
                GUILayout.Label("last apply: ✓ " + _lastOk + (_lastFailed > 0 ? "  ·  ✗ " + _lastFailed : ""),
                    EditorStyles.miniLabel, GUILayout.Height(26));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        // ----- performance -----

        private void DrawPerfSection()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("PERFORMANCE  ·  PLAY MODE", _section, GUILayout.ExpandWidth(false));
            Rect rule = GUILayoutUtility.GetRect(10f, 9f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(rule.x + 6, rule.y + 5, rule.width - 8, 1), LineCol);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(2);

            bool armed = PerfRecorder.Armed;
            EditorGUILayout.BeginHorizontal();
            bool auto = PerfRecorder.AutoRecord;
            bool autoNow = GUILayout.Toggle(auto, " Record when playing", GUILayout.ExpandWidth(false));
            if (autoNow != auto) PerfRecorder.AutoRecord = autoNow;
            GUILayout.Space(10);
            GUILayout.Label(autoNow
                ? "enter play mode yourself to start recording; exit play mode to finish and get results"
                : "tick, then play the scene — recording follows your whole session, no frame limit",
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (armed)
            {
                if (PerfRecorder.FramesTarget > 0)
                {
                    // fixed-length run (terminal/MCP perf_start) keeps its progress bar
                    Rect r = GUILayoutUtility.GetRect(18, 18, GUILayout.ExpandWidth(true));
                    float t = PerfRecorder.FramesDone / (float)PerfRecorder.FramesTarget;
                    EditorGUI.ProgressBar(r, t, PerfRecorder.FramesDone + " / " + PerfRecorder.FramesTarget + " frames");
                }
                else
                {
                    Color prevRec = GUI.contentColor;
                    GUI.contentColor = CritCol;
                    GUILayout.Label("● recording — " + PerfRecorder.FramesDone + " frames · exit play mode to finish",
                        EditorStyles.miniBoldLabel);
                    GUI.contentColor = prevRec;
                }
                if (!EditorApplication.isPlaying)
                    GUILayout.Label("waiting for play mode…", EditorStyles.miniLabel);
                return;
            }

            List<PerfRecorder.PerfRow> rows = PerfRecorder.ReportRows();
            if (rows == null || rows.Count == 0) return;

            GUILayout.Label(PerfRecorder.ReportFramesSampled() + " frames sampled"
                + (PerfRecorder.Partial ? "  ·  PARTIAL — play mode ended early" : ""), EditorStyles.miniBoldLabel);
            DrawPerfRow("counter", "min", "avg", "p95", "max", EditorStyles.miniBoldLabel);
            foreach (PerfRecorder.PerfRow row in rows)
                DrawPerfRow(PerfNames.TryGetValue(row.Key, out string nice) ? nice : row.Key,
                            row.Min, row.Avg, row.P95, row.Max, EditorStyles.miniLabel);

            // where the frame goes: PlayerLoop stages, then user scripts under the ScriptRun* stages
            List<PerfRecorder.MarkerRow> stages = PerfRecorder.ReportMarkers("markers");
            if (stages != null)
            {
                GUILayout.Space(4);
                GUILayout.Label("TOP CPU COST  ·  avg ms per frame", _section);
                foreach (PerfRecorder.MarkerRow m in stages) DrawMarkerRow(m);
            }
            List<PerfRecorder.MarkerRow> scripts = PerfRecorder.ReportMarkers("scriptMarkers");
            if (scripts != null)
            {
                GUILayout.Space(2);
                GUILayout.Label("TOP SCRIPT COST", _section);
                foreach (PerfRecorder.MarkerRow m in scripts) DrawMarkerRow(m);
            }

            // what the numbers mean, against the same mobile/VR budgets the audit uses
            string statsJson = PerfRecorder.ReportStatsJson();
            if (statsJson != _assessedJson)
            {
                _assessedJson = statsJson;
                _verdicts = PerfAssessment.Build(statsJson);
            }
            if (_verdicts != null && _verdicts.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label("WHAT THIS MEANS", _section);
                GUILayout.Label(PerfAssessment.Summary(_verdicts), _verdictHead);
                foreach (PerfVerdict p in _verdicts)
                {
                    EditorGUILayout.BeginHorizontal();
                    Color prev = GUI.contentColor;
                    GUI.contentColor = VerdictColor(p.Severity);
                    GUILayout.Label("●", _dot, GUILayout.Width(12));
                    GUI.contentColor = prev;
                    GUILayout.Label(p.Text, _reco);
                    EditorGUILayout.EndHorizontal();
                }
            }
            GUILayout.Label("Editor numbers are indicative — profile on the target device for ground truth.", _evidence);
        }

        private void DrawMarkerRow(PerfRecorder.MarkerRow m)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);
            GUILayout.Label(m.Name, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(m.AvgMs + " ms", EditorStyles.miniLabel, GUILayout.Width(70));
            EditorGUILayout.EndHorizontal();
        }

        // ----- code diagnostics (optional Project Auditor package) -----

        private void DrawCodeSection()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("CODE  ·  PROJECT AUDITOR", _section, GUILayout.ExpandWidth(false));
            Rect rule = GUILayoutUtility.GetRect(10f, 9f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(rule.x + 6, rule.y + 5, rule.width - 8, 1), LineCol);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(2);

            if (!ProjectAuditorRunner.Installed)
            {
                GUILayout.Label("Unity's Project Auditor package adds script-level diagnostics this audit can't see: " +
                                "per-frame allocations, Camera.main in Update, and more.", _reco);
                if (GUILayout.Button("Install com.unity.project-auditor", GUILayout.Width(220)))
                {
                    UnityEditor.PackageManager.Client.Add("com.unity.project-auditor");
                    EditorUtility.DisplayDialog("Agen-Link",
                        "Install requested — watch the Package Manager progress, then come back to this tab.", "OK");
                }
                return;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Run code diagnostics", GUILayout.Width(160))) RunProjectAuditor();
            if (_codeFindings != null)
                GUILayout.Label(_codeFindings.Count + (_codeFindings.Count == 1 ? " issue" : " issues") + " · slow to run, results not kept across reloads",
                    EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (_codeFindings == null) return;
            if (_codeGroups == null) _codeGroups = BuildGroups(_codeFindings);
            int shown = 0;
            foreach (RuleGroup g in _codeGroups)
            {
                List<Finding> visible = Visible(g);
                if (visible.Count == 0) continue;
                shown += visible.Count;
                if (g.Items.Count == 1) DrawFindingRow(g.Items[0]);
                else DrawGroup("CODE", g, visible);
            }
            if (shown == 0)
                GUILayout.Label(_codeFindings.Count == 0 ? "no code issues found" : "nothing at this severity",
                    EditorStyles.centeredGreyMiniLabel);
        }

        private void RunProjectAuditor()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Project Auditor", "Analyzing scripts and settings — this can take a while…", 0.4f);
                _codeFindings = ProjectAuditorRunner.Run();
                _codeFindings.Sort(Finding.BySeverity);
                _codeGroups = null;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            _repaint?.Invoke();
        }

        private static void DrawPerfRow(string name, string min, string avg, string p95, string max, GUIStyle style)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(name, style, GUILayout.Width(150));
            GUILayout.Label(min ?? "—", style, GUILayout.Width(80));
            GUILayout.Label(avg ?? "—", style, GUILayout.Width(80));
            GUILayout.Label(p95 ?? "—", style, GUILayout.Width(80));
            GUILayout.Label(max ?? "—", style, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();
        }

        // ----- actions -----

        private void RunAnalysis()
        {
            _sceneFindings = SceneAuditor.Collect(out _sceneStats);
            _assetFindings = AssetAuditor.Collect(out _assetStats);
            _sceneFindings.Sort(Finding.BySeverity);
            _assetFindings.Sort(Finding.BySeverity);
            _sceneGroups = null;
            _assetGroups = null;
            _ranAt = DateTime.Now;
            _unchecked.Clear();
            _results.Clear();
            _lastOk = _lastFailed = 0;
            _applyTouchedScene = false;
            SaveSessionState();
            _repaint?.Invoke();
        }

        private void ApplyChecked()
        {
            var reqs = new List<FixRequest>();
            var keys = new List<string>();
            foreach (Finding f in AllFindings())
            {
                if (f.FixType == null) continue;
                string key = KeyOf(f);
                if (_unchecked.Contains(key)) continue;
                reqs.Add(new FixRequest { Type = f.FixType, Target = f.Target, Value = f.FixValue });
                keys.Add(key);
            }
            if (reqs.Count == 0) return;

            List<FixResult> results = FixApplier.ApplyFixes(reqs, "tab", out bool touched);
            _applyTouchedScene = touched;
            _lastOk = _lastFailed = 0;
            var appliedKeys = new HashSet<string>();
            for (int i = 0; i < results.Count && i < keys.Count; i++)
            {
                if (results[i].Ok)
                {
                    _lastOk++;
                    appliedKeys.Add(keys[i]);     // fixed -> drop the row from the tab
                    _unchecked.Remove(keys[i]);
                    _results.Remove(keys[i]);
                }
                else
                {
                    _lastFailed++;
                    _results[keys[i]] = results[i];   // failures stay visible with their error
                }
            }
            if (appliedKeys.Count > 0)
            {
                _sceneFindings.RemoveAll(f => appliedKeys.Contains(KeyOf(f)));
                _assetFindings.RemoveAll(f => appliedKeys.Contains(KeyOf(f)));
                _sceneGroups = null;
                _assetGroups = null;
                SaveSessionState();
            }
            _repaint?.Invoke();
        }

        private void SetAll(bool check)
        {
            if (check)
            {
                _unchecked.Clear();
            }
            else
            {
                foreach (Finding f in AllFindings())
                    if (f.FixType != null) _unchecked.Add(KeyOf(f));
            }
            SaveUncheckedState();
        }

        private static void Ping(Finding f)
        {
            try
            {
                if (f.Target != null && (f.Target.StartsWith("Assets/", StringComparison.Ordinal) || f.Category == "assets"))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(f.Target);
                    if (obj != null) EditorGUIUtility.PingObject(obj);
                }
                else
                {
                    EditorGUIUtility.PingObject(FixApplier.ResolveSceneObject(f.Target));
                }
            }
            catch { /* scene-level / settings targets aren't pingable — no-op */ }
        }

        // ----- helpers -----

        private IEnumerable<Finding> AllFindings()
        {
            if (_sceneFindings != null) foreach (Finding f in _sceneFindings) yield return f;
            if (_assetFindings != null) foreach (Finding f in _assetFindings) yield return f;
        }

        private int CheckedCount()
        {
            int n = 0;
            foreach (Finding f in AllFindings())
                if (f.FixType != null && !_unchecked.Contains(KeyOf(f))) n++;
            return n;
        }

        private bool PassesSeverity(Finding f) =>
            _severityFilter == 0
            || (_severityFilter == 1 && f.Severity == "critical")
            || (_severityFilter == 2 && f.Severity != "info")
            || (_severityFilter == 3 && f.Severity == "info");

        private static Color SeverityColor(string sev) =>
            sev == "critical" ? CritCol : sev == "warn" ? WarnCol : Muted;

        private static Color VerdictColor(string sev) =>
            sev == "critical" ? CritCol : sev == "warn" ? WarnCol : sev == "ok" ? OkCol : Muted;

        private static string KeyOf(Finding f) => f.Id + "|" + f.Target + "|" + f.FixType;

        // ----- SessionState persistence (survives the play-mode / recompile domain reload) -----

        private void SaveSessionState()
        {
            var sceneArr = new List<string>();
            foreach (Finding f in _sceneFindings) sceneArr.Add(f.ToJson());
            var assetArr = new List<string>();
            foreach (Finding f in _assetFindings) assetArr.Add(f.ToJson());
            SessionState.SetString(KeyState, new JObj()
                .S("ranAt", _ranAt.ToUniversalTime().ToString("o"))
                .Raw("scene", _sceneStats.ToJson())
                .Raw("assets", _assetStats.ToJson())
                .Raw("sceneFindings", Json.Arr(sceneArr))
                .Raw("assetFindings", Json.Arr(assetArr))
                .Build());
            SaveUncheckedState();
        }

        private void SaveUncheckedState() =>
            SessionState.SetString(KeyUnchecked, string.Join("\n", _unchecked));

        private void TryRestoreSessionState()
        {
            string blob = SessionState.GetString(KeyState, "");
            if (string.IsNullOrEmpty(blob)) return;
            try
            {
                var o = JObject.Parse(blob);
                _ranAt = DateTime.Parse((string)o["ranAt"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime();
                _sceneStats = SceneStats.FromJObject((JObject)o["scene"]);
                _assetStats = AssetStats.FromJObject((JObject)o["assets"]);
                _sceneFindings = ParseFindings(o["sceneFindings"] as JArray);
                _assetFindings = ParseFindings(o["assetFindings"] as JArray);
                _unchecked.Clear();
                foreach (string k in SessionState.GetString(KeyUnchecked, "").Split('\n'))
                    if (k.Length > 0) _unchecked.Add(k);
            }
            catch
            {
                _sceneFindings = null;
                _assetFindings = null;
            }
        }

        private static List<Finding> ParseFindings(JArray arr)
        {
            var list = new List<Finding>();
            if (arr != null)
                foreach (JToken t in arr)
                    if (t is JObject jo) list.Add(Finding.FromJObject(jo));
            return list;
        }

        // ----- styles -----

        private void EnsureStyles()
        {
            if (_section != null) return;

            _section = new GUIStyle(EditorStyles.miniBoldLabel);
            _section.normal.textColor = Faint;

            _rowBlock = Block(CardBg, new RectOffset(8, 8, 5, 6), new RectOffset(4, 4, 2, 2));

            _targetBtn = new GUIStyle(EditorStyles.label) { fontSize = 12, wordWrap = false };
            _targetBtn.normal.textColor = TextCol;
            _targetBtn.hover.textColor = Color.white;

            _evidence = new GUIStyle(EditorStyles.miniLabel);
            _evidence.normal.textColor = Muted;

            _reco = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            _reco.normal.textColor = Detail;

            _tag = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = 9 };
            _tag.normal.textColor = Color.white;   // tinted via GUI.contentColor

            _dot = new GUIStyle(EditorStyles.miniLabel);
            _dot.normal.textColor = Color.white;   // tinted via GUI.contentColor

            _idLabel = new GUIStyle(EditorStyles.miniLabel);
            _idLabel.normal.textColor = Faint;

            _okLine = new GUIStyle(EditorStyles.miniLabel);
            _okLine.normal.textColor = OkCol;
            _errLine = new GUIStyle(EditorStyles.miniLabel);
            _errLine.normal.textColor = CritCol;

            _stats = new GUIStyle(EditorStyles.miniLabel) { padding = new RectOffset(8, 8, 4, 2) };
            _stats.normal.textColor = Muted;

            _verdictHead = new GUIStyle(EditorStyles.miniBoldLabel) { wordWrap = true };
            _verdictHead.normal.textColor = TextCol;

            _arrowBtn = new GUIStyle(EditorStyles.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
            _arrowBtn.normal.textColor = Faint;

            _groupTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12, wordWrap = false, alignment = TextAnchor.MiddleLeft };
            _groupTitle.normal.textColor = TextCol;
            _groupTitle.hover.textColor = Color.white;
        }

        private static GUIStyle Block(Color bg, RectOffset padding, RectOffset margin)
        {
            var s = new GUIStyle { padding = padding, margin = margin };
            s.normal.background = Tex(bg);
            return s;
        }

        private static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }
    }
}
