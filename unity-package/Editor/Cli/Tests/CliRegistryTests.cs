using System.Collections.Generic;
using NUnit.Framework;
using AgenLink;
using AgenLink.Cli;

public class CliRegistryTests
{
    private string _savedCli;

    [SetUp]
    public void SaveSettings() { _savedCli = BridgeSettings.TerminalCli; }

    [TearDown]
    public void RestoreSettings() { BridgeSettings.TerminalCli = _savedCli; }

    // Task 3 extends this to include "codex" once CodexProvider is registered.
    [Test]
    public void All_StartsWithClaudeThenAntigravity()
    {
        var ids = new List<string>();
        foreach (var p in CliRegistry.All) ids.Add(p.Id);
        Assert.AreEqual("claude", ids[0]);
        Assert.AreEqual("antigravity", ids[1]);
    }

    [Test]
    public void Ids_AreUnique_AndFindRoundTripsEveryProvider()
    {
        var seen = new HashSet<string>();
        foreach (var p in CliRegistry.All)
        {
            Assert.IsTrue(seen.Add(p.Id), "duplicate provider id: " + p.Id);
            Assert.AreSame(p, CliRegistry.Find(p.Id));
        }
    }

    [Test]
    public void Find_ReturnsNull_ForAnUnknownId()
    {
        Assert.IsNull(CliRegistry.Find("gemini"));
        Assert.IsNull(CliRegistry.Find(""));
        Assert.IsNull(CliRegistry.Find(null));
    }

    // A stale EditorPref from an older install (or a hand-edited one) must never break launch.
    [Test]
    public void Current_FallsBackToClaude_ForAnUnknownStoredId()
    {
        BridgeSettings.TerminalCli = "not-a-cli";
        Assert.AreEqual("claude", CliRegistry.Current.Id);
    }

    [Test]
    public void Current_ReturnsTheStoredProvider()
    {
        BridgeSettings.TerminalCli = "antigravity";
        Assert.AreEqual("antigravity", CliRegistry.Current.Id);
    }

    [Test]
    public void DisplayNames_MatchAllInOrder()
    {
        string[] names = CliRegistry.DisplayNames;
        Assert.AreEqual(CliRegistry.All.Count, names.Length);
        for (int i = 0; i < names.Length; i++) Assert.AreEqual(CliRegistry.All[i].DisplayName, names[i]);
    }

    [Test]
    public void IndexOf_MatchesAllOrder_AndIsMinusOneWhenUnknown()
    {
        for (int i = 0; i < CliRegistry.All.Count; i++)
            Assert.AreEqual(i, CliRegistry.IndexOf(CliRegistry.All[i].Id));
        Assert.AreEqual(-1, CliRegistry.IndexOf("nope"));
    }
}
