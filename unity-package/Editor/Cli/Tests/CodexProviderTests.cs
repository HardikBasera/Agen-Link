using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using AgenLink;
using AgenLink.Cli;
using AgenLink.Terminal;

public class CodexProviderTests
{
    private string _savedMcpPath, _savedCodexPath;
    private int _savedPort;
    private string _tempMcp;

    [SetUp]
    public void SetUp()
    {
        _savedMcpPath = BridgeSettings.McpServerPath;
        _savedCodexPath = BridgeSettings.CodexPath;
        _savedPort = BridgeSettings.Port;

        _tempMcp = Path.Combine(Path.GetTempPath(), "agenlink-test-mcp-index.js");
        File.WriteAllText(_tempMcp, "// test stub");
        BridgeSettings.McpServerPath = _tempMcp;
        BridgeSettings.Port = 6577;
    }

    [TearDown]
    public void TearDown()
    {
        BridgeSettings.McpServerPath = _savedMcpPath;
        BridgeSettings.CodexPath = _savedCodexPath;
        BridgeSettings.Port = _savedPort;
        try { if (File.Exists(_tempMcp)) File.Delete(_tempMcp); } catch { }
        LaunchDiagnostics.McpFailure = null;
    }

    private static CodexProvider Codex() => (CodexProvider)CliRegistry.Find("codex");

    /// <summary>Every -c must be its own argv entry, immediately followed by its key=value entry.</summary>
    private static Dictionary<string, string> Overrides(List<string> args)
    {
        var map = new Dictionary<string, string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] != "-c") continue;
            Assert.Less(i + 1, args.Count, "-c with no value at index " + i);
            string kv = args[i + 1];
            int eq = kv.IndexOf('=');
            Assert.Greater(eq, 0, "override is not key=value: " + kv);
            map[kv.Substring(0, eq)] = kv.Substring(eq + 1);
            i++;
        }
        return map;
    }

    [Test]
    public void BuildArgs_EmitsTheMcpServerOverrides()
    {
        var map = Overrides(Codex().BuildArgs());

        Assert.AreEqual("\"node\"", map["mcp_servers.agenlink.command"]);
        Assert.AreEqual("\"auto\"", map["mcp_servers.agenlink.default_tools_approval_mode"]);
        Assert.AreEqual("30", map["mcp_servers.agenlink.startup_timeout_sec"]);
    }

    [Test]
    public void BuildArgs_PassesTheMcpServerPathForwardSlashed()
    {
        var map = Overrides(Codex().BuildArgs());
        string argsValue = map["mcp_servers.agenlink.args"];

        Assert.IsFalse(argsValue.Contains("\\\\"), "path should be forward-slashed, got " + argsValue);
        StringAssert.StartsWith("[\"", argsValue);
        StringAssert.EndsWith("\"]", argsValue);
        StringAssert.Contains("agenlink-test-mcp-index.js", argsValue);
    }

    [Test]
    public void BuildArgs_PassesAllThreeBridgeEnvironmentVariables()
    {
        var map = Overrides(Codex().BuildArgs());
        string env = map["mcp_servers.agenlink.env"];

        StringAssert.StartsWith("{", env);
        StringAssert.EndsWith("}", env);
        StringAssert.Contains("AGEN_LINK_PORT=\"6577\"", env);
        StringAssert.Contains("AGEN_LINK_PROJECT_ROOT=\"", env);
        StringAssert.Contains("AGEN_LINK_CLI=\"codex\"", env);
    }

    [Test]
    public void BuildArgs_ClearsTheMcpFailureWhenWiringSucceeds()
    {
        LaunchDiagnostics.McpFailure = "stale";
        Codex().BuildArgs();
        Assert.IsNull(LaunchDiagnostics.McpFailure);
    }

    // Without the MCP server there are no agen_* tools at all. Codex must still launch, but the
    // failure has to be recorded so the Terminal tab can show its banner.
    [Test]
    public void BuildArgs_WithNoMcpServer_ReportsFailureAndEmitsNoOverrides()
    {
        BridgeSettings.McpServerPath = Path.Combine(Path.GetTempPath(), "definitely-not-here.js");
        LaunchDiagnostics.McpFailure = null;

        List<string> args = Codex().BuildArgs();

        Assert.IsNotNull(LaunchDiagnostics.McpFailure);
        CollectionAssert.DoesNotContain(args, "-c");
    }

    [Test]
    public void Identity_IsStableAndDistinct()
    {
        var codex = Codex();
        Assert.AreEqual("codex", codex.Id);
        Assert.AreEqual("Codex", codex.DisplayName);
        Assert.AreEqual("codex.exe", codex.ExeFileName);
    }

    [Test]
    public void Registry_ListsCodexThirdAfterClaudeAndAntigravity()
    {
        var ids = new List<string>();
        foreach (var p in CliRegistry.All) ids.Add(p.Id);
        CollectionAssert.AreEqual(new[] { "claude", "antigravity", "codex" }, ids);
    }

    // BuildArgs must not require an installed Codex — it builds config text, nothing more.
    [Test]
    public void BuildArgs_WorksWithNoCodexInstalled()
    {
        BridgeSettings.CodexPath = Path.Combine(Path.GetTempPath(), "no-such-codex.exe");
        Assert.DoesNotThrow(() => Codex().BuildArgs());
        CollectionAssert.Contains(Codex().BuildArgs(), "-c");
    }

    [Test]
    public void PathOverride_RoundTripsThroughBridgeSettings()
    {
        Codex().PathOverride = @"C:\tools\codex.exe";
        Assert.AreEqual(@"C:\tools\codex.exe", BridgeSettings.CodexPath);
        Assert.AreEqual(@"C:\tools\codex.exe", Codex().PathOverride);
    }
}
