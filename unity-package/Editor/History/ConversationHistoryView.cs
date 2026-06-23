using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AgenLink.History
{
    /// <summary>IMGUI browser of past AI sessions for the project, in the approved "editorial terminal"
    /// design: per-agent color coding (YOU teal / CLAUDE coral / ANTIGRAVITY violet / ANALYSIS amber),
    /// accent-bar turns instead of heavy bubbles, expandable tool-action details, "Show more" clamp on
    /// long replies, relative timestamps, and an agent filter. Loads off the GUI thread.</summary>
    internal sealed class ConversationHistoryView
    {
        private readonly string _projectRoot;
        private readonly Action _repaint;

        private List<Conversation> _convs;
        private volatile bool _loading;
        private Vector2 _scroll;
        private readonly HashSet<int> _expanded = new HashSet<int>();
        private readonly HashSet<long> _openReplies = new HashSet<long>();  // long replies un-clamped
        private readonly HashSet<long> _openActions = new HashSet<long>();  // action detail rows open
        private bool _olderExpanded;
        private int _filterIndex;
        private int _sortIndex;                                 // 0 = oldest first (newest at the bottom)
        private int _agentIndex;                                // 0 all, 1 claude, 2 antigravity, 3 analysis
        private bool _scrollToBottom;
        private static readonly string[] Filters = { "All", "Today", "Last 7 days", "Last 30 days" };
        private static readonly string[] SortModes = { "Oldest first", "Newest first" };
        private static readonly string[] Agents = { "All", "Claude", "Antigravity", "Analysis" };

        private const int ClampLines = 14;      // long replies fold beyond this ("Show more")
        private const float BodyMaxWidth = 640f; // readable text measure, ~76ch

        // ----- palette (from the approved HTML mock; all flat) -----
        private static readonly Color CardBg     = C(0x23, 0x24, 0x29);
        private static readonly Color CardOpenBg = C(0x26, 0x27, 0x2D);
        private static readonly Color InsetBg    = C(0x14, 0x15, 0x19);
        private static readonly Color Line       = C(0x34, 0x35, 0x3C);
        private static readonly Color TextCol    = C(0xD6, 0xD7, 0xDC);
        private static readonly Color Muted      = C(0x8B, 0x8D, 0x98);
        private static readonly Color Faint      = C(0x5F, 0x61, 0x6C);
        private static readonly Color YouCol     = C(0x4F, 0xC1, 0xB4);   // teal  — the human
        private static readonly Color YouBg      = C(0x1F, 0x2A, 0x28);
        private static readonly Color ClaudeCol  = C(0xE0, 0x8A, 0x66);   // coral — Claude
        private static readonly Color AgyCol     = C(0x8B, 0x9C, 0xF6);   // violet — Antigravity
        private static readonly Color AnaCol     = C(0xE2, 0xB1, 0x4E);   // amber — Analysis fixes
        private static readonly Color DetailText = C(0xB9, 0xBB, 0xC4);
        private static Color C(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

        private static Color AgentColor(Conversation c) =>
            c.Agent == "antigravity" ? AgyCol : c.Agent == "analysis" ? AnaCol : ClaudeCol;

        private static string AgentBadge(Conversation c) =>
            c.Agent == "antigravity" ? "● ANTIGRAVITY" : c.Agent == "analysis" ? "● ANALYSIS" : "● CLAUDE";

        private static string AgentName(Conversation c) =>
            c.Agent == "antigravity" ? "ANTIGRAVITY" : c.Agent == "analysis" ? "ANALYSIS" : "CLAUDE";

        private GUIStyle _card, _cardOpen, _youBlock, _claudeBlock, _inset, _metaInfo,
                         _whoYou, _badge, _body, _title, _arrow, _date, _rel, _meta, _group,
                         _mono, _monoBtn, _showMore;

        public ConversationHistoryView(string projectRoot, Action repaint)
        {
            _projectRoot = projectRoot;
            _repaint = repaint;
        }

        public void OnGUI()
        {
            EnsureStyles();
            if (_convs == null && !_loading) Reload();

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Date", EditorStyles.miniLabel, GUILayout.Width(30));
            _filterIndex = EditorGUILayout.Popup(_filterIndex, Filters, EditorStyles.toolbarPopup, GUILayout.Width(95));
            GUILayout.Space(6);
            GUILayout.Label("Sort", EditorStyles.miniLabel, GUILayout.Width(28));
            _sortIndex = EditorGUILayout.Popup(_sortIndex, SortModes, EditorStyles.toolbarPopup, GUILayout.Width(95));
            GUILayout.Space(6);
            GUILayout.Label("Agent", EditorStyles.miniLabel, GUILayout.Width(38));
            _agentIndex = EditorGUILayout.Popup(_agentIndex, Agents, EditorStyles.toolbarPopup, GUILayout.Width(95));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("⟳ Refresh", EditorStyles.toolbarButton, GUILayout.Width(80))) Reload();
            EditorGUILayout.EndHorizontal();

            if (_loading)
            {
                GUILayout.Space(10);
                GUILayout.Label("Loading conversations…", EditorStyles.centeredGreyMiniLabel);
                return;
            }
            if (_convs == null || _convs.Count == 0)
            {
                GUILayout.Space(10);
                EditorGUILayout.HelpBox("No conversations found for this project yet. Start a session in the Terminal tab.", MessageType.Info);
                return;
            }

            DateTime now = DateTime.Now;
            bool ascending = _sortIndex == 0;                   // oldest first -> newest at the bottom
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            string lastGroup = null;
            int n = _convs.Count;
            for (int k = 0; k < n; k++)
            {
                int idx = ascending ? n - 1 - k : k;            // _convs is stored newest-first
                Conversation c = _convs[idx];
                if (!PassesFilter(c, now)) continue;
                string group = DateGroups.Of(c.StartedAt, now);
                if (group != lastGroup)
                {
                    lastGroup = group;
                    DrawGroupHeader(group);
                }
                if (group == "Older" && !_olderExpanded) continue;
                DrawCard(idx, c, now);
            }
            GUILayout.Space(10);
            EditorGUILayout.EndScrollView();

            // Land on the newest conversation after a (re)load: bottom when ascending, top otherwise.
            if (_scrollToBottom && Event.current.type == EventType.Repaint)
            {
                _scroll.y = ascending ? float.MaxValue : 0f;
                _scrollToBottom = false;
                _repaint?.Invoke();
            }
        }

        // ----- structure -----

        private void DrawGroupHeader(string group)
        {
            GUILayout.Space(14);
            EditorGUILayout.BeginHorizontal();
            if (group == "Older")
            {
                _olderExpanded = EditorGUILayout.Foldout(_olderExpanded, "OLDER", true, _group);
            }
            else
            {
                GUILayout.Label(group.ToUpperInvariant(), _group, GUILayout.ExpandWidth(false));
            }
            Rect rule = GUILayoutUtility.GetRect(10f, 9f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(rule.x + 6, rule.y + 5, rule.width - 8, 1), Line);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        private void DrawCard(int i, Conversation c, DateTime now)
        {
            bool open = _expanded.Contains(i);
            EditorGUILayout.BeginVertical(open ? _cardOpen : _card);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(open ? "▾" : "▸", _arrow, GUILayout.Width(16)))
            {
                if (open) _expanded.Remove(i); else _expanded.Add(i);
            }
            var prevColor = GUI.contentColor;
            GUI.contentColor = AgentColor(c);
            GUILayout.Label(AgentBadge(c), _badge, GUILayout.ExpandWidth(false));
            GUI.contentColor = prevColor;
            if (GUILayout.Button(c.Title, _title)) { if (open) _expanded.Remove(i); else _expanded.Add(i); }
            GUILayout.FlexibleSpace();
            GUILayout.Label(MetaText(c), _meta);
            GUILayout.Label(DateGroups.Rel(c.StartedAt, now), _rel, GUILayout.Width(72));
            GUILayout.Label(c.StartedAt.ToString("MMM d  ·  h:mm tt"), _date, GUILayout.Width(106));
            EditorGUILayout.EndHorizontal();

            if (open)
            {
                Rect sep = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(sep, Line);
                GUILayout.Space(4);

                if (c.MetaOnly)
                {
                    GUILayout.Label("This session's content is stored by Antigravity itself. Reopen it with " +
                                    "“agy --continue” in the Terminal, or in the Antigravity app.", _metaInfo);
                }
                else
                {
                    for (int t = 0; t < c.Turns.Count; t++)
                        DrawTurn(i, t, c.Turns[t], c);
                    if (c.Agent == "antigravity")
                        GUILayout.Label("Antigravity keeps its replies in its own store — reopen this conversation " +
                                        "with “agy --continue” in the Terminal.", _metaInfo);
                }
                GUILayout.Space(6);
            }
            EditorGUILayout.EndVertical();

            if (Event.current.type == EventType.Repaint)
            {
                Rect r = GUILayoutUtility.GetLastRect();   // 1px card border
                EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), Line);
                EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), Line);
                EditorGUI.DrawRect(new Rect(r.x, r.y, 1, r.height), Line);
                EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), Line);
            }
            GUILayout.Space(8);
        }

        private static string MetaText(Conversation c)
        {
            if (c.Agent == "analysis")
                return c.Turns.Count + (c.Turns.Count == 1 ? " fix" : " fixes")
                     + (c.SourceNote != null ? " · " + c.SourceNote : "");
            if (c.MetaOnly) return "ran in the Terminal tab";
            int prompts = 0, actions = 0;
            foreach (ConvTurn t in c.Turns)
            {
                if (t.Kind == TurnKind.You) prompts++;
                else if (t.Kind == TurnKind.Action) actions++;
            }
            string p = prompts + (prompts == 1 ? " prompt" : " prompts");
            if (c.Agent == "antigravity") return p + " · replies in agy";
            return p + " · " + actions + (actions == 1 ? " action" : " actions");
        }

        // ----- turns -----

        private static long Key(int conv, int turn) => ((long)conv << 20) | (uint)turn;

        private void DrawTurn(int conv, int turnIdx, ConvTurn t, Conversation c)
        {
            switch (t.Kind)
            {
                case TurnKind.You:    DrawMessage(conv, turnIdx, t, c, you: true); break;
                case TurnKind.Claude: DrawMessage(conv, turnIdx, t, c, you: false); break;
                case TurnKind.Action: DrawAction(conv, turnIdx, t, c); break;
            }
        }

        private void DrawMessage(int conv, int turnIdx, ConvTurn t, Conversation c, bool you)
        {
            Color accent = you ? YouCol : AgentColor(c);
            string who = you ? "YOU" : AgentName(c);

            // "Show more": clamp long replies so a conversation stays scannable.
            string text = t.Text;
            bool clamped = false;
            if (!you)
            {
                string[] lines = text.Split('\n');
                if (lines.Length > ClampLines && !_openReplies.Contains(Key(conv, turnIdx)))
                {
                    text = string.Join("\n", lines, 0, ClampLines) + "\n…";
                    clamped = true;
                }
            }

            EditorGUILayout.BeginVertical(you ? _youBlock : _claudeBlock);
            var prev = GUI.contentColor;
            GUI.contentColor = accent;
            GUILayout.Label(who, _whoYou);
            GUI.contentColor = prev;

            string rich = Markdown.ToRichText(text);
            var content = new GUIContent(rich);
            Rect rect = GUILayoutUtility.GetRect(content, _body, GUILayout.MaxWidth(BodyMaxWidth));
            if (Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
            {
                var menu = new GenericMenu();
                string full = t.Text;
                menu.AddItem(new GUIContent("Copy message"), false, () => EditorGUIUtility.systemCopyBuffer = full);
                menu.AddItem(new GUIContent("Copy conversation"), false, () => EditorGUIUtility.systemCopyBuffer = ConversationText(c));
                menu.ShowAsContext();
                Event.current.Use();
            }
            EditorGUI.SelectableLabel(rect, rich, _body);

            bool isOpen = _openReplies.Contains(Key(conv, turnIdx));
            if (clamped || (!you && isOpen && t.Text.Split('\n').Length > ClampLines))
            {
                if (GUILayout.Button(clamped ? "Show more ▾" : "Show less ▴", _showMore, GUILayout.ExpandWidth(false)))
                {
                    if (clamped) _openReplies.Add(Key(conv, turnIdx));
                    else _openReplies.Remove(Key(conv, turnIdx));
                }
            }
            EditorGUILayout.EndVertical();

            if (Event.current.type == EventType.Repaint)
            {
                Rect r = GUILayoutUtility.GetLastRect();   // 2px accent bar
                EditorGUI.DrawRect(new Rect(r.x, r.y, 2, r.height), accent);
            }
            GUILayout.Space(3);
        }

        private void DrawAction(int conv, int turnIdx, ConvTurn t, Conversation c)
        {
            Color accent = AgentColor(c);
            if (string.IsNullOrEmpty(t.Detail))
            {
                GUILayout.Label("    " + t.Text, _mono);
                return;
            }

            long key = Key(conv, turnIdx);
            bool open = _openActions.Contains(key);
            if (GUILayout.Button((open ? "  ▾ " : "  ▸ ") + t.Text, _monoBtn))
            {
                if (open) _openActions.Remove(key); else _openActions.Add(key);
            }
            if (open)
            {
                EditorGUILayout.BeginVertical(_inset);
                var content = new GUIContent(t.Detail);
                Rect rect = GUILayoutUtility.GetRect(content, _mono, GUILayout.MaxWidth(BodyMaxWidth + 100f));
                EditorGUI.SelectableLabel(rect, t.Detail, _mono);
                EditorGUILayout.EndVertical();
                if (Event.current.type == EventType.Repaint)
                {
                    Rect r = GUILayoutUtility.GetLastRect();
                    EditorGUI.DrawRect(new Rect(r.x, r.y, 2, r.height), accent);
                }
            }
        }

        private static string ConversationText(Conversation c)
        {
            var sb = new StringBuilder();
            foreach (ConvTurn t in c.Turns)
            {
                switch (t.Kind)
                {
                    case TurnKind.You:    sb.Append("You:\n").Append(t.Text).Append("\n\n"); break;
                    case TurnKind.Claude: sb.Append("Claude:\n").Append(t.Text).Append("\n\n"); break;
                    case TurnKind.Action: sb.Append(t.Text).Append(string.IsNullOrEmpty(t.Detail) ? "" : "\n" + t.Detail).Append("\n\n"); break;
                }
            }
            return sb.ToString().TrimEnd();
        }

        private bool PassesFilter(Conversation c, DateTime now)
        {
            if (_agentIndex == 1 && c.Agent != "claude") return false;
            if (_agentIndex == 2 && c.Agent != "antigravity") return false;
            if (_agentIndex == 3 && c.Agent != "analysis") return false;
            switch (_filterIndex)
            {
                case 1: return c.StartedAt.Date == now.Date;
                case 2: return c.StartedAt > now.AddDays(-7);
                case 3: return c.StartedAt > now.AddDays(-30);
                default: return true;
            }
        }

        private void Reload()
        {
            _loading = true;
            string root = _projectRoot;
            new Thread(() =>
            {
                List<Conversation> result;
                try
                {
                    result = TranscriptReader.LoadAll(root);
                    result.AddRange(SessionLog.LoadAntigravity(root));
                    result.AddRange(Analysis.AnalysisLog.LoadConversations(root));
                    result.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));   // keep newest-first
                }
                catch { result = new List<Conversation>(); }
                MainThreadDispatcher.RunAsync(() =>
                {
                    _convs = result;
                    _loading = false;
                    _expanded.Clear();
                    _openReplies.Clear();
                    _openActions.Clear();
                    if (_convs.Count > 0) _expanded.Add(0);     // newest (stored first) expanded by default
                    _scrollToBottom = true;                     // and scroll to it
                    _repaint?.Invoke();
                });
            }) { IsBackground = true, Name = "AgenLink.History" }.Start();
        }

        // ----- styles -----

        private void EnsureStyles()
        {
            if (_card != null) return;

            _card = Block(CardBg, new RectOffset(10, 10, 7, 7), new RectOffset(4, 4, 0, 0));
            _cardOpen = Block(CardOpenBg, new RectOffset(10, 10, 7, 7), new RectOffset(4, 4, 0, 0));
            _youBlock = Block(YouBg, new RectOffset(12, 10, 6, 7), new RectOffset(6, 24, 4, 4));
            _claudeBlock = Block(Color.clear, new RectOffset(12, 10, 6, 7), new RectOffset(6, 24, 4, 4));
            _inset = Block(InsetBg, new RectOffset(12, 10, 6, 6), new RectOffset(34, 40, 2, 4));

            _title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12, wordWrap = false, alignment = TextAnchor.MiddleLeft };
            _title.normal.textColor = TextCol;
            _arrow = new GUIStyle(EditorStyles.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
            _arrow.normal.textColor = Faint;

            _badge = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = 9 };
            _badge.normal.textColor = Color.white;   // tinted per-agent via GUI.contentColor

            _meta = new GUIStyle(EditorStyles.miniLabel);
            _meta.normal.textColor = Faint;
            _rel = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            _rel.normal.textColor = Muted;
            _date = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            _date.normal.textColor = Faint;

            _group = new GUIStyle(EditorStyles.miniBoldLabel);
            _group.normal.textColor = Faint;

            _whoYou = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = 9, margin = new RectOffset(0, 0, 0, 2) };
            _whoYou.normal.textColor = Color.white;  // tinted per speaker via GUI.contentColor

            _body = new GUIStyle(EditorStyles.label) { richText = true, wordWrap = true, fontSize = 12 };
            _body.normal.textColor = TextCol;

            _metaInfo = new GUIStyle(EditorStyles.label) { wordWrap = true, fontSize = 12, padding = new RectOffset(12, 10, 8, 8) };
            _metaInfo.normal.textColor = Muted;

            var monoFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New", "Menlo", "monospace" }, 11);
            _mono = new GUIStyle(EditorStyles.label) { font = monoFont, fontSize = 11, wordWrap = true };
            _mono.normal.textColor = DetailText;
            _monoBtn = new GUIStyle(EditorStyles.label) { font = monoFont, fontSize = 11, wordWrap = false };
            _monoBtn.normal.textColor = Muted;
            _monoBtn.hover.textColor = TextCol;

            _showMore = new GUIStyle(EditorStyles.miniButton) { fontSize = 10 };
            _showMore.normal.textColor = Muted;
        }

        private static GUIStyle Block(Color bg, RectOffset padding, RectOffset margin)
        {
            var s = new GUIStyle { padding = padding, margin = margin };
            if (bg.a > 0f) s.normal.background = Tex(bg);
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
