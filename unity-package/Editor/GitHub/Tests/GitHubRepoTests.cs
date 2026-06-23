using NUnit.Framework;
using AgenLink.GitHub;

public class GitHubRepoTests
{
    [Test]
    public void Parse_Reads_Array_Of_Repos()
    {
        string json = "[{\"nameWithOwner\":\"me/alpha\",\"visibility\":\"PRIVATE\",\"updatedAt\":\"2026-01-01T00:00:00Z\"}," +
                      "{\"nameWithOwner\":\"org/beta\",\"visibility\":\"PUBLIC\",\"updatedAt\":\"2026-02-02T00:00:00Z\"}]";
        var repos = GitHubRepo.ParseList(json);
        Assert.AreEqual(2, repos.Count);
        Assert.AreEqual("me/alpha", repos[0].nameWithOwner);
        Assert.AreEqual("PRIVATE", repos[0].visibility);
        Assert.AreEqual("org/beta", repos[1].nameWithOwner);
    }

    [Test]
    public void Parse_Empty_Or_Null_Returns_Empty()
    {
        Assert.AreEqual(0, GitHubRepo.ParseList("").Count);
        Assert.AreEqual(0, GitHubRepo.ParseList("[]").Count);
    }
}
