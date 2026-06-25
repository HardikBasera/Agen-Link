using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;
using AgenLink.Terminal;
using AgenLink.History;

namespace AgenLink
{
    /// <summary>
    /// The Agen-Link editor window: runs the real `claude` CLI in an embedded Terminal next to your
    /// project, with a Unity MCP bridge giving Claude a live view of the Editor.
    /// Tabs: Terminal | History | Analysis | GitHub | Neuron | Settings.
    /// </summary>
    public class AgenLinkWindow : EditorWindow
    {
        private static readonly string[] Tabs = { "Terminal", "History", "Analysis", "GitHub", "Neuron", "Settings" };

        // ----- persisted across domain reloads -----
        [SerializeField] private int _tab;

        // ----- transient -----
        private Vector2 _tabScroll;

        // terminal tab
        private TerminalClient _termClient;
        private ScreenBuffer _termBuffer;
        private VtParser _termParser;
        private TerminalView _termView;
        private string _termStatus = "idle";
        private bool _termExited;

        // history tab
        private ConversationHistoryView _historyView;

        // analysis tab
        private Analysis.AnalysisView _analysisView;

        // neuron tab
        private AgenLink.Neuron.NeuronGraphView _neuronView;

        // github tab (all cached — never call git/gh from OnGUI)
        private bool _gitBusy;
        private string _gitStatus = "";
        private string _ghActiveAccount = "";
        private string[] _ghAccounts = new string[0];
        private int _ghAccountIndex;
        private bool _gitChecked;
        private bool _ghInstalled;
        private bool _gitIsRepo;
        private string _gitBranch = "";
        private string _gitRemote = "";
        private string[] _gitCommits = new string[0];

        // github sign-in + repo flow
        private AgenLink.GitHub.GitHubAuthFlow _ghAuth;
        private string _ghCodePopup = "";
        private bool _ghSigningIn;
        private int _repoMode; // 0 = link existing, 1 = create new
        private List<AgenLink.GitHub.GitHubRepo> _repoList;
        private int _repoPick;
        private string _newRepoName = "", _newRepoDesc = "";
        private bool _newRepoPrivate = true, _newRepoPush = true;
        private bool _showAdvanced;

        [MenuItem("Window/Agen-Link")]
        public static void ShowWindow()
        {
            var w = GetWindow<AgenLinkWindow>("Agen-Link");
            w.minSize = new Vector2(420, 360);
            w.Show();
        }

        private void OnEnable()
        {
            TryReconnectTerminal();
        }

        private void OnDisable()
        {
            // Drop the terminal socket but DO NOT stop the host — it must survive reloads/closes.
            _termClient?.Disconnect();
        }

        private void OnInspectorUpdate()
        {
            // The bridge marshals every request onto the main thread via MainThreadDispatcher, normally
            // drained from EditorApplication.update. That pump parks when the editor is unfocused or just
            // after a successful-compile domain reload, stranding bridge requests until you click into Unity.
            // OnInspectorUpdate fires ~10x/sec regardless of focus, so draining here keeps the bridge alive.
            MainThreadDispatcher.Pump();

            // OnOutput already calls Repaint(), but that paint also waits for a tick the parked editor isn't
            // giving it — so nudge a repaint here too while a session is live, to un-stall terminal output.
            if (PtyHostLauncher.HostAlive() && _termClient != null && _termClient.Connected)
                Repaint();

            // Drive the Analysis tab's recording progress bar; checked from SessionState every tick, so
            // it keeps working after the play-mode domain reload with no subscriptions to leak.
            if (Analysis.PerfRecorder.Armed)
                Repaint();
        }

        private void OnGUI()
        {
            _tab = GUILayout.Toolbar(_tab, Tabs);
            EditorGUILayout.Space(2);
            switch (_tab)
            {
                case 0: DrawTerminal(); break;
                case 1: DrawHistory(); break;
                case 2: DrawAnalysis(); break;
                case 3: DrawGitHub(); break;
                case 4: DrawNeuron(); break;
                case 5: DrawSettings(); break;
            }
        }

        // ===================== Analysis =====================

        private void DrawAnalysis()
        {
            if (_analysisView == null)
                _analysisView = new Analysis.AnalysisView(Repaint);
            _analysisView.OnGUI();
        }

        // ===================== Terminal =====================

        private void DrawTerminal()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            bool alive = PtyHostLauncher.HostAlive() && _termClient != null && _termClient.Connected;
            using (new EditorGUI.DisabledScope(alive))
                if (GUILayout.Button(alive ? "Running" : "▶ Start session", EditorStyles.toolbarButton, GUILayout.Width(110)))
                    StartTerminal();
            if (GUILayout.Button("⟳ Restart", EditorStyles.toolbarButton, GUILayout.Width(80))) RestartTerminal();
            using (new EditorGUI.DisabledScope(!PtyHostLauncher.HostAlive()))
                if (GUILayout.Button("■ Stop", EditorStyles.toolbarButton, GUILayout.Width(70))) StopTerminal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("status: " + _termStatus, EditorStyles.miniLabel, GUILayout.Width(180));
            EditorGUILayout.EndHorizontal();

            if (PtyHostLauncher.ResolvePtyHostEntry() == null)
            {
                EditorGUILayout.HelpBox("pty-host is not built. Run install/setup.cmd (installs node-pty), then restart Unity.", MessageType.Warning);
                return;
            }

            if (_termView == null && !PtyHostLauncher.HostAlive())
            {
                EditorGUILayout.HelpBox("Press ▶ Start session to launch the real Claude CLI here. Your login, skills, " +
                                        "plugins and commands all apply; the Agen-Link bridge is added automatically.", MessageType.Info);
                return;
            }

            EnsureTerminalObjects();
            var rect = GUILayoutUtility.GetRect(100, 4000, 100, 4000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            _termView.OnGUI(rect);
        }

        private void StartTerminal()
        {
            EnsureTerminalObjects();
            _termExited = false; _termStatus = "starting…"; Repaint();
            string err;
            if (!PtyHostLauncher.Start(_termBuffer.Cols, _termBuffer.Rows, out err))
            {
                _termStatus = "failed";
                EditorUtility.DisplayDialog("Terminal", "Could not start session:\n" + err, "OK");
                return;
            }
            if (!_termClient.Connect(BridgeSettings.TerminalHostPort, BridgeSettings.TerminalHostToken, out err))
            {
                _termStatus = "failed";
                EditorUtility.DisplayDialog("Terminal", "Started host but failed to connect:\n" + err, "OK");
            }
        }

        private void RestartTerminal() { StopTerminal(); StartTerminal(); }

        private void StopTerminal()
        {
            _termClient?.Disconnect();
            PtyHostLauncher.Stop();
            _termStatus = "idle"; _termExited = false;
            Repaint();
        }

        private void TryReconnectTerminal()
        {
            if (!PtyHostLauncher.HostAlive()) return;
            EnsureTerminalObjects();
            string err;
            _termStatus = _termClient.Connect(BridgeSettings.TerminalHostPort, BridgeSettings.TerminalHostToken, out err)
                ? "reconnecting…" : "disconnected";
        }

        private void EnsureTerminalObjects()
        {
            if (_termBuffer == null) _termBuffer = new ScreenBuffer(100, 30);
            if (_termParser == null) _termParser = new VtParser(_termBuffer);
            if (_termClient == null)
            {
                _termClient = new TerminalClient();
                _termClient.OnOutput = b => { _termParser.Feed(b); Repaint(); };
                _termClient.OnExit = code => { _termStatus = "exited (" + code + ")"; _termExited = true; Repaint(); };
                _termClient.OnConnected = () => { _termStatus = "connected"; Repaint(); };
                _termClient.OnDisconnected = () => { if (!_termExited) _termStatus = "disconnected"; Repaint(); };
            }
            if (_termView == null)
            {
                _termView = new TerminalView(_termBuffer, _termClient);
                _termView.OnResizeRequest = (c, r) => _termClient.Resize(c, r);
                _termView.OnRepaintRequest = Repaint; // selection drag / scroll need immediate repaints
            }
        }

        // ===================== History =====================

        private void DrawHistory()
        {
            if (_historyView == null)
                _historyView = new ConversationHistoryView(ConfigBuilder.ProjectRoot(), Repaint);
            _historyView.OnGUI();
        }

        // ===================== Neuron =====================

        private void DrawNeuron()
        {
            wantsMouseMove = true; // the graph's hover-highlight needs MouseMove events
            if (_neuronView == null)
                _neuronView = new AgenLink.Neuron.NeuronGraphView(ConfigBuilder.ProjectRoot(), Repaint);
            _neuronView.OnGUI();
        }

        // ===================== GitHub =====================

        private void DrawGitHub()
        {
            if (!_gitChecked) { RefreshGitStatus(); _gitChecked = true; }

            _tabScroll = EditorGUILayout.BeginScrollView(_tabScroll);

            // ----- Account -----
            EditorGUILayout.LabelField("Account", EditorStyles.boldLabel);
            if (!_ghInstalled)
            {
                EditorGUILayout.HelpBox("GitHub CLI (gh) not found. Run install/setup.cmd to install it, then restart Unity.", MessageType.Warning);
                if (GUILayout.Button("Re-check", GUILayout.Width(80))) RefreshGitStatus();
            }
            else if (string.IsNullOrEmpty(_ghActiveAccount))
            {
                EditorGUILayout.HelpBox("Not signed in. Click below to sign in through your browser — no password is typed into Unity.", MessageType.Info);
                using (new EditorGUI.DisabledScope(_ghSigningIn))
                    if (GUILayout.Button(_ghSigningIn ? "Waiting for browser…" : "Sign in to GitHub", GUILayout.Height(26)))
                        BeginGitHubSignIn();
                if (!string.IsNullOrEmpty(_ghCodePopup))
                    EditorGUILayout.HelpBox("Your one-time code: " + _ghCodePopup + "\nEnter it in the browser window that opened.", MessageType.None);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Signed in as:", _ghActiveAccount);
                if (_ghAccounts.Length > 1)
                {
                    _ghAccountIndex = EditorGUILayout.Popup(_ghAccountIndex, _ghAccounts, GUILayout.Width(140));
                    using (new EditorGUI.DisabledScope(_gitBusy))
                        if (GUILayout.Button("Switch", GUILayout.Width(60))) SwitchAccount();
                }
                if (GUILayout.Button("Sign out", GUILayout.Width(70))) SignOutGitHub();
                EditorGUILayout.EndHorizontal();
            }

            // ----- Repository -----
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Repository", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Local git repo:", _gitIsRepo ? $"yes (branch {_gitBranch})" : "no");
            if (!string.IsNullOrEmpty(_gitRemote)) EditorGUILayout.LabelField("Remote:", _gitRemote, EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_ghActiveAccount)))
            {
                _repoMode = GUILayout.Toolbar(_repoMode, new[] { "Link existing", "Create new" });

                if (_repoMode == 0)
                {
                    if (GUILayout.Button("Load my repositories", GUILayout.Width(160)))
                    {
                        _gitStatus = "Loading repositories…"; _gitBusy = true; Repaint();
                        GitIntegration.GhListReposAsync(200, list =>
                        {
                            _repoList = list; _gitBusy = false;
                            _gitStatus = $"Loaded {list.Count} repositories."; Repaint();
                        });
                    }

                    if (_repoList != null && _repoList.Count > 0)
                    {
                        var names = new string[_repoList.Count];
                        for (int i = 0; i < _repoList.Count; i++)
                            names[i] = $"{_repoList[i].nameWithOwner}  ({_repoList[i].visibility.ToLower()})";
                        _repoPick = EditorGUILayout.Popup("Repository", _repoPick, names);
                        using (new EditorGUI.DisabledScope(_gitBusy))
                            if (GUILayout.Button("Link this repository", GUILayout.Height(24)))
                                LinkSelectedRepo();
                    }
                    else if (_repoList != null)
                        EditorGUILayout.LabelField("No repositories found for this account.", EditorStyles.miniLabel);
                }
                else
                {
                    if (string.IsNullOrEmpty(_newRepoName))
                        _newRepoName = SanitizeRepoName(System.IO.Path.GetFileName(ConfigBuilder.ProjectRoot().TrimEnd('\\', '/')));
                    _newRepoName = EditorGUILayout.TextField("Name", _newRepoName);
                    _newRepoDesc = EditorGUILayout.TextField("Description", _newRepoDesc);
                    _newRepoPrivate = EditorGUILayout.ToggleLeft("Private", _newRepoPrivate);
                    _newRepoPush = EditorGUILayout.ToggleLeft("Push current project after creating", _newRepoPush);
                    using (new EditorGUI.DisabledScope(_gitBusy || string.IsNullOrWhiteSpace(_newRepoName)))
                        if (GUILayout.Button("Create repository", GUILayout.Height(24)))
                            CreateNewRepo();
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_gitBusy || !_gitIsRepo))
                if (GUILayout.Button("Commit now")) CommitNow();
            using (new EditorGUI.DisabledScope(_gitBusy || !_gitIsRepo || string.IsNullOrEmpty(_gitRemote)))
                if (GUILayout.Button("Push")) PushCurrent();
            EditorGUILayout.EndHorizontal();

            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced (manual owner/repo/branch)");
            if (_showAdvanced)
            {
                BridgeSettings.GitHubOwner = EditorGUILayout.TextField("Owner", BridgeSettings.GitHubOwner);
                BridgeSettings.GitHubRepo = EditorGUILayout.TextField("Repository", BridgeSettings.GitHubRepo);
                BridgeSettings.GitHubBranch = EditorGUILayout.TextField("Branch", BridgeSettings.GitHubBranch);
                using (new EditorGUI.DisabledScope(_gitBusy || _gitIsRepo))
                    if (GUILayout.Button("Initialize repo + .gitignore")) InitRepo();
            }

            if (!string.IsNullOrEmpty(_gitStatus))
                EditorGUILayout.HelpBox(_gitStatus, MessageType.None);

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox("Whole-project backup. Files larger than 100 MB need Git LFS (`git lfs track \"*.ext\"`); " +
                                    "GitHub rejects large binaries otherwise.", MessageType.Info);

            if (_gitIsRepo && _gitCommits.Length > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Recent commits", EditorStyles.boldLabel);
                foreach (var c in _gitCommits)
                    EditorGUILayout.LabelField(c, EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>Runs git/gh once and caches everything the GitHub tab needs (never call these from OnGUI).</summary>
        private void RefreshGitStatus()
        {
            _ghInstalled = GitIntegration.GhInstalled();
            if (_ghInstalled)
            {
                _ghActiveAccount = GitIntegration.GhActiveAccount() ?? "";
                var accounts = GitIntegration.GhAccounts();
                _ghAccounts = accounts.ToArray();
                int idx = accounts.IndexOf(_ghActiveAccount);
                _ghAccountIndex = idx >= 0 ? idx : 0;
            }
            _gitIsRepo = GitIntegration.IsGitRepo();
            if (_gitIsRepo)
            {
                _gitBranch = GitIntegration.CurrentBranch();
                _gitRemote = GitIntegration.RemoteUrl();
                _gitCommits = GitIntegration.RecentCommits(8).ToArray();
            }
            else { _gitBranch = ""; _gitRemote = ""; _gitCommits = new string[0]; }
            Repaint();
        }

        private void SwitchAccount()
        {
            if (_ghAccountIndex < 0 || _ghAccountIndex >= _ghAccounts.Length) return;
            string user = _ghAccounts[_ghAccountIndex];
            _gitBusy = true; _gitStatus = $"Switching to {user}…";
            var r = GitIntegration.GhSwitch(user);
            GitIntegration.GhSetupGit();
            _gitBusy = false;
            _gitStatus = r.ok ? $"Now using GitHub account: {user}" : "Switch failed: " + r.Message;
            RefreshGitStatus();
        }

        private void InitRepo()
        {
            _gitBusy = true;
            var r = GitIntegration.InitRepo(BridgeSettings.GitHubBranch);
            _gitBusy = false;
            _gitStatus = r.ok ? r.Message : "Init failed: " + r.Message;
            RefreshGitStatus();
        }

        private void CommitNow()
        {
            _gitBusy = true;
            var r = GitIntegration.AutoCommit("Manual checkpoint (Agen-Link)");
            _gitBusy = false;
            _gitStatus = r.ok ? r.Message : "Commit failed: " + r.Message;
            RefreshGitStatus();
        }

        private void BeginGitHubSignIn()
        {
            _ghSigningIn = true; _ghCodePopup = ""; _gitStatus = "Starting GitHub sign-in…";
            _ghAuth = new AgenLink.GitHub.GitHubAuthFlow();
            _ghAuth.Begin(
                code => MainThreadDispatcher.RunAsync(() =>
                {
                    _ghCodePopup = code;
                    EditorGUIUtility.systemCopyBuffer = code;
                    EditorUtility.DisplayDialog("GitHub sign-in",
                        "A browser window is opening.\n\nEnter this one-time code (already copied to your clipboard):\n\n    " + code,
                        "OK");
                    Repaint();
                }),
                (ok, err) => MainThreadDispatcher.RunAsync(() =>
                {
                    _ghSigningIn = false; _ghCodePopup = "";
                    _gitStatus = ok ? "Signed in to GitHub." : "Sign-in failed: " + err;
                    RefreshGitStatus();
                }));
        }

        private void SignOutGitHub()
        {
            if (!EditorUtility.DisplayDialog("Sign out", "Sign out of GitHub on this machine (gh auth logout)?", "Sign out", "Cancel")) return;
            GitIntegration.GhSignOut();
            _gitStatus = "Signed out.";
            RefreshGitStatus();
        }

        private void LinkSelectedRepo()
        {
            if (_repoList == null || _repoPick < 0 || _repoPick >= _repoList.Count) return;
            string nwo = _repoList[_repoPick].nameWithOwner;
            string branch = string.IsNullOrEmpty(BridgeSettings.GitHubBranch) ? "main" : BridgeSettings.GitHubBranch;
            _gitBusy = true; _gitStatus = "Linking " + nwo + "…"; Repaint();
            GitIntegration.LinkExistingAsync(nwo, branch, res =>
            {
                _gitBusy = false; _gitStatus = res.message;
                int slash = nwo.IndexOf('/');
                if (slash > 0) { BridgeSettings.GitHubOwner = nwo.Substring(0, slash); BridgeSettings.GitHubRepo = nwo.Substring(slash + 1); }
                if (res.outcome == GitIntegration.LinkOutcome.LinkedDiverged)
                    EditorUtility.DisplayDialog("Histories diverged", res.message, "OK");
                RefreshGitStatus();
            });
        }

        private void CreateNewRepo()
        {
            string owner = string.IsNullOrEmpty(_ghActiveAccount) ? "" : _ghActiveAccount;
            string repo = SanitizeRepoName(_newRepoName);
            if (!EditorUtility.DisplayDialog("Create GitHub repo",
                    $"Create {(_newRepoPrivate ? "PRIVATE" : "PUBLIC")} repo github.com/{owner}/{repo} as '{_ghActiveAccount}'" +
                    (_newRepoPush ? " and push this project?" : "?"), "Create", "Cancel")) return;
            _gitBusy = true; _gitStatus = "Creating repo…"; Repaint();
            GitIntegration.CreateRepoAsync(owner, repo, _newRepoPrivate, _newRepoDesc, _newRepoPush, r =>
            {
                _gitBusy = false;
                _gitStatus = r.ok ? $"Created github.com/{owner}/{repo}." : "Create failed: " + r.Message;
                if (r.ok) { BridgeSettings.GitHubOwner = owner; BridgeSettings.GitHubRepo = repo; }
                RefreshGitStatus();
            });
        }

        private void PushCurrent()
        {
            string owner = BridgeSettings.GitHubOwner.Trim();
            string repo = BridgeSettings.GitHubRepo.Trim();
            string branch = string.IsNullOrEmpty(BridgeSettings.GitHubBranch) ? "main" : BridgeSettings.GitHubBranch;
            if (owner.Length == 0 || repo.Length == 0) { _gitStatus = "Link or create a repository first."; return; }
            if (!EditorUtility.DisplayDialog("Confirm push",
                    $"Push to github.com/{owner}/{repo} (branch {branch}) as account '{_ghActiveAccount}'?", "Push", "Cancel")) return;
            _gitBusy = true; _gitStatus = "Pushing…"; Repaint();
            GitIntegration.PushAsync(owner, repo, branch, r =>
            {
                _gitBusy = false;
                _gitStatus = r.ok ? $"Pushed to github.com/{owner}/{repo} ({branch})." : "Push failed: " + r.Message;
                RefreshGitStatus();
            });
        }

        private static string SanitizeRepoName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "my-unity-project";
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' ? ch : '-');
            return sb.ToString();
        }

        // ===================== Settings =====================

        private void DrawSettings()
        {
            _tabScroll = EditorGUILayout.BeginScrollView(_tabScroll);

            EditorGUILayout.LabelField("Terminal", EditorStyles.boldLabel);

            string[] cliLabels = { "Claude", "Antigravity" };
            int cliSel = BridgeSettings.TerminalCli == "antigravity" ? 1 : 0;
            int cliNext = EditorGUILayout.Popup("CLI", cliSel, cliLabels);
            if (cliNext != cliSel) BridgeSettings.TerminalCli = cliNext == 1 ? "antigravity" : "claude";
            EditorGUILayout.LabelField("Restart the terminal session to apply a new CLI.", EditorStyles.miniLabel);

            int font = EditorGUILayout.IntSlider("Font size", BridgeSettings.TerminalFontSize, 8, 28);
            if (font != BridgeSettings.TerminalFontSize) BridgeSettings.TerminalFontSize = font;
            EditorGUILayout.LabelField("Restart the terminal session to apply a new font size.", EditorStyles.miniLabel);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Bridge server", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Status:", BridgeServer.IsRunning ? $"listening on 127.0.0.1:{BridgeServer.ActivePort}" : "stopped");
            int port = EditorGUILayout.IntField("Port", BridgeSettings.Port);
            if (port != BridgeSettings.Port && port > 0 && port < 65536) BridgeSettings.Port = port;
            if (GUILayout.Button("Restart bridge", GUILayout.Width(120))) BridgeServer.Restart();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("MCP server", EditorStyles.boldLabel);
            string resolved = ConfigBuilder.ResolveMcpServerPath();
            EditorGUILayout.LabelField("Resolved path:", resolved ?? "(not found — build mcp-server)", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            BridgeSettings.McpServerPath = EditorGUILayout.TextField("Override", BridgeSettings.McpServerPath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string p = EditorUtility.OpenFilePanel("Select mcp-server/build/index.js", "", "js");
                if (!string.IsNullOrEmpty(p)) BridgeSettings.McpServerPath = p;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Claude CLI", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Resolved:", ClaudeCli.ResolveDisplay(), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            BridgeSettings.ClaudePath = EditorGUILayout.TextField("Override", BridgeSettings.ClaudePath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string cp = EditorUtility.OpenFilePanel("Select claude.exe", "", "exe");
                if (!string.IsNullOrEmpty(cp)) BridgeSettings.ClaudePath = cp;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Auto-detected from npm global / PATH. Set only if Claude isn't found.", EditorStyles.miniLabel);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Antigravity CLI (agy)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Resolved:", AntigravityCli.ResolveDisplay(), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            BridgeSettings.AntigravityPath = EditorGUILayout.TextField("Override", BridgeSettings.AntigravityPath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string gp = EditorUtility.OpenFilePanel("Select agy.exe", "", "exe");
                if (!string.IsNullOrEmpty(gp)) BridgeSettings.AntigravityPath = gp;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Auto-detected from %LOCALAPPDATA%\\agy. Set only if agy isn't found.", EditorStyles.miniLabel);

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox("The Terminal tab runs your selected CLI (Claude or Antigravity) with your own config, " +
                                    "skills and plugins. The bridge + MCP server above let it read live editor state " +
                                    "(console, compile errors, scene), edit files, and share project memory across both " +
                                    "CLIs (agen_memory_* tools + a local AGENTS.md).", MessageType.Info);

            EditorGUILayout.EndScrollView();
        }

    }
}
