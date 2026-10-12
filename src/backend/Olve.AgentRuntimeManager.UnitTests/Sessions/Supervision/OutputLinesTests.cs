using System.Runtime.Versioning;
using System.Text;
using Olve.AgentRuntimeManager.Sessions.Supervision;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Supervision;

/// <summary>A run's output read from its end, a chunk at a time.</summary>
[UnsupportedOSPlatform("windows")]
public class OutputLinesTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("arm-output-lines-").FullName;

    [After(Test)]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private string Write(string content)
    {
        var path = Path.Combine(_root, "output.jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(64 * 1024)]
    public async Task CompleteLines_LastFirst_WhateverTheChunks(int chunkSize)
    {
        // Lines longer and shorter than a chunk, an empty one, and characters of 2–4 bytes across chunk ends.
        var path = Write("first\n\nnaïve ünïcödé 😀 line\nx\nthe last whole line\nstill being writ");

        var lines = OutputLines.Backwards(path, chunkSize).ToList();

        await Assert.That(lines).IsEquivalentTo(
            ["the last whole line", "x", "naïve ünïcödé 😀 line", "", "first"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task EndingWithALineBreak_HasNothingUnfinished()
    {
        await Assert.That(OutputLines.Backwards(Write("a\nb\n"), 2).ToList()).IsEquivalentTo(["b", "a"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task EmptyMissingOrUnfinished_HasNoLines()
    {
        await Assert.That(OutputLines.Backwards(Write("")).ToList()).IsEmpty();
        await Assert.That(OutputLines.Backwards(Write("no line break yet")).ToList()).IsEmpty();
        await Assert.That(OutputLines.Backwards(Path.Combine(_root, "missing.jsonl")).ToList()).IsEmpty();
    }

    [Test]
    public async Task ALink_IsNotFollowed()
    {
        var target = Write("secret\n");
        var link = Path.Combine(_root, "link.jsonl");
        File.CreateSymbolicLink(link, target);

        await Assert.That(OutputLines.Backwards(link).ToList()).IsEmpty();
    }

    [Test]
    public async Task ReadsOnlyAsFarAsItsEnumerated()
    {
        // Ends with two short lines, after a megabyte that is never needed.
        var path = Write(new string('x', 1024 * 1024) + "\nnext to last\nlast\n");
        using var lines = OutputLines.Backwards(path, chunkSize: 16).GetEnumerator();

        await Assert.That(lines.MoveNext()).IsTrue();
        await Assert.That(lines.Current).IsEqualTo("last");
        await Assert.That(lines.MoveNext()).IsTrue();
        await Assert.That(lines.Current).IsEqualTo("next to last");
    }
}
