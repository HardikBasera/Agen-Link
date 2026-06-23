using System;
using System.Linq;
using NUnit.Framework;
using AgenLink.History;

public class HistoryTests
{
    [Test]
    public void EncodeProjectDir_ReplacesNonAlnumWithDash()
    {
        Assert.AreEqual("C--Games-My-Project",
            TranscriptReader.EncodeProjectDir(@"C:\Games\My Project"));
    }

    [Test]
    public void Parse_ExtractsPromptsRepliesMarkers_DropsThinkingReadToolsAndToolResults()
    {
        var lines = new[]
        {
            "{\"type\":\"ai-title\",\"aiTitle\":\"Fix the blink\",\"sessionId\":\"s1\"}",
            "{\"type\":\"user\",\"timestamp\":\"2026-06-04T07:09:44.906Z\",\"message\":{\"role\":\"user\",\"content\":\"the terminal keeps blinking\"}}",
            "{\"type\":\"assistant\",\"timestamp\":\"2026-06-04T07:09:46.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[" +
                "{\"type\":\"thinking\",\"thinking\":\"secret reasoning\"}," +
                "{\"type\":\"text\",\"text\":\"Found it - an IMGUI resize loop.\"}," +
                "{\"type\":\"tool_use\",\"name\":\"Edit\",\"input\":{\"file_path\":\"D:/proj/Assets/TerminalView.cs\"}}," +
                "{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"git commit -m fix\"}}," +
                "{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"x.cs\"}}]}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"ok\"}]}}",
            "{\"type\":\"file-history-snapshot\"}"
        };

        var conv = TranscriptReader.ParseConversation(lines);

        Assert.AreEqual("Fix the blink", conv.Title);
        CollectionAssert.AreEqual(
            new[] { TurnKind.You, TurnKind.Claude, TurnKind.Action, TurnKind.Action },
            conv.Turns.Select(t => t.Kind).ToArray(),
            "thinking, the read-only Read tool, and the tool_result line must all be dropped");
        Assert.AreEqual("the terminal keeps blinking", conv.Turns[0].Text);
        Assert.AreEqual("Found it - an IMGUI resize loop.", conv.Turns[1].Text);
        Assert.AreEqual("✎ edited TerminalView.cs", conv.Turns[2].Text);
        Assert.AreEqual("▶ ran: git commit -m fix", conv.Turns[3].Text);
        Assert.AreNotEqual(DateTime.MinValue, conv.StartedAt);

        // Full, untruncated info is kept alongside the short markers for the expandable detail UI.
        Assert.AreEqual("D:/proj/Assets/TerminalView.cs", conv.Turns[2].Detail, "edit detail = full path");
        Assert.IsNull(conv.Turns[3].Detail, "short command fully visible in the marker -> no extra detail row");
    }

    [Test]
    public void Parse_LongBashCommand_KeepsFullDetail_AndDescription()
    {
        string longCmd = string.Join(" && ", Enumerable.Repeat("dotnet build --configuration Release", 4));
        var lines = new[]
        {
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[" +
                "{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"" + longCmd + "\",\"description\":\"Build all\"}}]}}"
        };
        var conv = TranscriptReader.ParseConversation(lines);
        Assert.AreEqual(1, conv.Turns.Count);
        StringAssert.StartsWith("▶ ran: ", conv.Turns[0].Text);
        StringAssert.EndsWith("…", conv.Turns[0].Text, "marker stays truncated for the list");
        Assert.AreEqual("Build all\n" + longCmd, conv.Turns[0].Detail, "detail keeps description + FULL command");
    }

    [Test]
    public void Parse_TitleFallsBackToFirstPrompt_WhenNoAiTitle()
    {
        var lines = new[]
        {
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"add VR teleport locomotion to the player\"}}"
        };
        Assert.AreEqual("add VR teleport locomotion to the player",
            TranscriptReader.ParseConversation(lines).Title);
    }

    [Test]
    public void Parse_SkipsMetaUserLines()
    {
        var lines = new[]
        {
            "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"<system reminder>\"}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"real prompt\"}}"
        };
        var conv = TranscriptReader.ParseConversation(lines);
        Assert.AreEqual(1, conv.Turns.Count);
        Assert.AreEqual("real prompt", conv.Turns[0].Text);
    }

    [Test]
    public void DateGroups_BucketsByRecency()
    {
        var now = new DateTime(2026, 6, 4, 12, 0, 0);
        Assert.AreEqual("Today",     DateGroups.Of(now, now));
        Assert.AreEqual("Yesterday", DateGroups.Of(now.AddDays(-1), now));
        Assert.AreEqual("This week", DateGroups.Of(now.AddDays(-3), now));
        Assert.AreEqual("Older",     DateGroups.Of(now.AddDays(-30), now));
    }

    [Test]
    public void DateGroups_RelativeTimes()
    {
        var now = new DateTime(2026, 6, 4, 12, 0, 0);
        Assert.AreEqual("just now",   DateGroups.Rel(now.AddSeconds(-20), now));
        Assert.AreEqual("5 min ago",  DateGroups.Rel(now.AddMinutes(-5), now));
        Assert.AreEqual("3 h ago",    DateGroups.Rel(now.AddHours(-3), now));
        Assert.AreEqual("yesterday",  DateGroups.Rel(now.AddDays(-1), now));
        Assert.AreEqual("3 days ago", DateGroups.Rel(now.AddDays(-3), now));
        Assert.AreEqual("May 4",      DateGroups.Rel(now.AddMonths(-1), now));
    }
}
