using UnityEditor;

namespace AgenLink
{
    /// <summary>Editor-persisted settings for the bridge + embedded terminal (stored in EditorPrefs).</summary>
    internal static class BridgeSettings
    {
        public const int DefaultPort = 6577;

        private const string PortKey = "AgenLink.Port";
        private const string McpPathKey = "AgenLink.McpServerPath";
        private const string GitHubOwnerKey = "AgenLink.GitHubOwner";
        private const string GitHubRepoKey = "AgenLink.GitHubRepo";
        private const string GitHubBranchKey = "AgenLink.GitHubBranch";

        public static int Port
        {
            get => EditorPrefs.GetInt(PortKey, DefaultPort);
            set => EditorPrefs.SetInt(PortKey, value);
        }

        /// <summary>Absolute path to mcp-server/build/index.js. Empty = auto-detect from the package folder.</summary>
        public static string McpServerPath
        {
            get => EditorPrefs.GetString(McpPathKey, "");
            set => EditorPrefs.SetString(McpPathKey, value);
        }

        // ----- GitHub backup (per-machine; repo identity is shown back to the user before every push) -----
        public static string GitHubOwner
        {
            get => EditorPrefs.GetString(GitHubOwnerKey, "");
            set => EditorPrefs.SetString(GitHubOwnerKey, value);
        }

        public static string GitHubRepo
        {
            get => EditorPrefs.GetString(GitHubRepoKey, "");
            set => EditorPrefs.SetString(GitHubRepoKey, value);
        }

        public static string GitHubBranch
        {
            get => EditorPrefs.GetString(GitHubBranchKey, "main");
            set => EditorPrefs.SetString(GitHubBranchKey, value);
        }

        // ----- Embedded terminal -----
        // Host pid/port/token are transient but must survive a domain reload so the panel can
        // reconnect to the still-running pty-host; SessionState persists across reloads, clears on quit.
        public static int TerminalHostPid
        {
            get => SessionState.GetInt("AgenLink.Term.Pid", 0);
            set => SessionState.SetInt("AgenLink.Term.Pid", value);
        }

        public static int TerminalHostPort
        {
            get => SessionState.GetInt("AgenLink.Term.Port", 0);
            set => SessionState.SetInt("AgenLink.Term.Port", value);
        }

        public static string TerminalHostToken
        {
            get => SessionState.GetString("AgenLink.Term.Token", "");
            set => SessionState.SetString("AgenLink.Term.Token", value);
        }

        public static int TerminalFontSize
        {
            get => EditorPrefs.GetInt("AgenLink.Term.Font", 13);
            set => EditorPrefs.SetInt("AgenLink.Term.Font", value);
        }

        /// <summary>Which CLI the terminal launches: "claude" (default) or "antigravity". Applied on restart.</summary>
        public static string TerminalCli
        {
            get => EditorPrefs.GetString("AgenLink.Term.Cli", "claude");
            set => EditorPrefs.SetString("AgenLink.Term.Cli", value);
        }

        /// <summary>Absolute path to claude.exe. Empty = auto-detect (npm global / PATH).</summary>
        public static string ClaudePath
        {
            get => EditorPrefs.GetString("AgenLink.ClaudePath", "");
            set => EditorPrefs.SetString("AgenLink.ClaudePath", value);
        }

        /// <summary>Absolute path to the Antigravity CLI (agy.exe). Empty = auto-detect.</summary>
        public static string AntigravityPath
        {
            get => EditorPrefs.GetString("AgenLink.AntigravityPath", "");
            set => EditorPrefs.SetString("AgenLink.AntigravityPath", value);
        }
    }
}
