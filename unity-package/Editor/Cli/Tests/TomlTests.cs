using System.Collections.Generic;
using NUnit.Framework;
using AgenLink.Cli;

public class TomlTests
{
    [Test]
    public void Str_WrapsInQuotes()
    {
        Assert.AreEqual("\"node\"", Toml.Str("node"));
    }

    [Test]
    public void Str_EscapesBackslashAndQuote()
    {
        Assert.AreEqual("\"C:\\\\a\\\\b\"", Toml.Str("C:\\a\\b"));
        Assert.AreEqual("\"say \\\"hi\\\"\"", Toml.Str("say \"hi\""));
    }

    [Test]
    public void Str_EscapesNewlineTabAndCarriageReturn()
    {
        Assert.AreEqual("\"a\\nb\\tc\\rd\"", Toml.Str("a\nb\tc\rd"));
    }

    // Unity logs through a native char*, and a NUL truncates everything after it. Never let one
    // reach a value we may also log. Other C0 controls have no TOML basic-string escape either.
    [Test]
    public void Str_DropsNulAndOtherControlCharacters()
    {
        Assert.AreEqual("\"ab\"", Toml.Str("a\0\u0001\u0007b"));
        Assert.AreEqual("\"ab\"", Toml.Str("a\u007fb"));   // DEL is forbidden in a basic string too
    }

    [Test]
    public void StrArray_EmitsBracketedCommaSeparatedList()
    {
        Assert.AreEqual("[\"a\",\"b\"]", Toml.StrArray("a", "b"));
        Assert.AreEqual("[]", Toml.StrArray());
    }

    [Test]
    public void InlineTable_EmitsBracedQuotedPairs()
    {
        string t = Toml.InlineTable(
            new KeyValuePair<string, string>("A", "1"),
            new KeyValuePair<string, string>("B", "x y"));
        Assert.AreEqual("{A=\"1\",B=\"x y\"}", t);
    }

    [Test]
    public void InlineTable_Empty_IsEmptyBraces()
    {
        Assert.AreEqual("{}", Toml.InlineTable());
    }
}
