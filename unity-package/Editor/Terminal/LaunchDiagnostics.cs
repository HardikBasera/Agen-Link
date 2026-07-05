namespace AgenLink.Terminal
{
    /// <summary>
    /// Carries a one-line reason when the last terminal launch could not wire up the Unity MCP tools
    /// (the mcp-server build was missing, or the config could not be written). The launch still proceeds so
    /// the CLI runs, but without any agen_* tools — a state that is otherwise invisible. The Terminal tab
    /// reads this to show a loud banner instead of letting the CLI silently fall back to asking the user /
    /// writing scripts. Null / empty means the tools were wired up successfully.
    /// </summary>
    internal static class LaunchDiagnostics
    {
        public static string McpFailure;
    }
}
