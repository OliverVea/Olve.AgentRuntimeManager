using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

public class CappedOutputTests
{
    [Test]
    public async Task Output_WithinTheCap_IsKeptWhole()
    {
        var output = new CappedOutput(5, 5);
        output.Append("0123");
        output.Append("456789");

        await Assert.That(output.ToString()).IsEqualTo("0123456789");
    }

    [Test]
    public async Task Output_OverTheCap_KeepsHeadAndTail_AndSaysWhatWasCut()
    {
        var output = new CappedOutput(3, 3);
        foreach (var piece in (string[])["abc", "defgh", "ijklmnop", "xyz"])
        {
            output.Append(piece);
        }

        await Assert.That(output.ToString()).IsEqualTo("abc\n\n[... 13 characters of output cut here, of 19: only the head and the tail are kept ...]\n\nxyz");
        await Assert.That(output.Total).IsEqualTo(19);
    }

    [Test]
    public async Task Output_NeverSplitsASurrogatePair()
    {
        var output = new CappedOutput(2, 2);
        output.Append("a\U0001F600bcdefg\U0001F600");

        var text = output.ToString();

        await Assert.That(text).StartsWith("a\n");
        await Assert.That(text).EndsWith("\n\U0001F600");
    }
}
