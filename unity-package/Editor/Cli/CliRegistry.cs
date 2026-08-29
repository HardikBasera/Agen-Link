using System.Collections.Generic;

namespace AgenLink.Cli
{
    /// <summary>The CLIs the terminal can launch, in Settings-dropdown order.</summary>
    internal static class CliRegistry
    {
        private static readonly CliProvider[] Providers =
        {
            new ClaudeProvider(),
            new AntigravityProvider(),
            new CodexProvider(),
        };

        public static IReadOnlyList<CliProvider> All => Providers;

        public static CliProvider Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var p in Providers) if (p.Id == id) return p;
            return null;
        }

        /// <summary>
        /// The selected CLI. Falls back to the first provider when the stored id is unknown — a stale
        /// or hand-edited EditorPref must never be able to break a launch.
        /// </summary>
        public static CliProvider Current => Find(BridgeSettings.TerminalCli) ?? Providers[0];

        public static int IndexOf(string id)
        {
            for (int i = 0; i < Providers.Length; i++) if (Providers[i].Id == id) return i;
            return -1;
        }

        public static string[] DisplayNames
        {
            get
            {
                var names = new string[Providers.Length];
                for (int i = 0; i < Providers.Length; i++) names[i] = Providers[i].DisplayName;
                return names;
            }
        }
    }
}
