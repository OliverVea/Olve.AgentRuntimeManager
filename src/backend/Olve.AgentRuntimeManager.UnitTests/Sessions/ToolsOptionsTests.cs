using Olve.AgentRuntimeManager.Sessions;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

/// <summary>Which tools a new session gets (M10): what it named, else the server's defaults, else the built-ins.</summary>
public class ToolsOptionsTests
{
    private static readonly ToolsOptions WithDefaults = new() { Default = ["arm_bash", "arm_file_read", "arm_bash"] };

    [Test]
    public async Task Named_AreExactlyThose_DefaultsOrNot()
    {
        await Assert.That(WithDefaults.For(["arm_file_edit"])).IsEquivalentTo(["arm_file_edit"]);
        await Assert.That(new ToolsOptions().For(["arm_file_edit"])).IsEquivalentTo(["arm_file_edit"]);
    }

    [Test]
    public async Task AnEmptyList_StaysEmpty_DefaultsOrNot()
    {
        await Assert.That(WithDefaults.For([])).IsEmpty();
        await Assert.That(new ToolsOptions().For([])).IsEmpty();
    }

    [Test]
    public async Task NoneNamed_AreTheDefaults_EachOnce_InOrder() =>
        await Assert.That(WithDefaults.For(null)).IsEquivalentTo(["arm_bash", "arm_file_read"], TUnit.Assertions.Enums.CollectionOrdering.Matching);

    [Test]
    public async Task NoneNamed_WithoutDefaults_IsUnset_TheBuiltIns() =>
        await Assert.That(new ToolsOptions().For(null)).IsNull();
}
