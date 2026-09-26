using Olve.Results.TUnit;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Messages;

namespace Olve.AgentRuntimeManager.UnitTests.Messages;

/// <summary>
/// The generated validator (from the spec's <c>@maxLength(280)</c> and required <c>text</c>) plus
/// the hand-written rule the spec can't express (non-blank text).
/// </summary>
public class MessageValidationTests
{
    private readonly MessageWritableValidator _sut = new();

    [Test]
    public async Task Validate_NonEmptyText_Succeeds() =>
        await Assert.That(_sut.Validate(new MessageWritable { Text = "hello" })).Succeeded();

    [Test]
    public async Task Validate_TooLong_Fails() =>
        await Assert.That(_sut.Validate(new MessageWritable { Text = new string('a', 281) })).Failed();

    [Test]
    public async Task Validate_MaxLength_Succeeds() =>
        await Assert.That(_sut.Validate(new MessageWritable { Text = new string('a', 280) })).Succeeded();

    [Test]
    public async Task Validate_NullText_Fails() =>
        await Assert.That(_sut.Validate(new MessageWritable { Text = null! })).Failed();

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task ValidateText_EmptyOrWhitespace_Fails(string text) =>
        await Assert.That(MessageMapping.ValidateText(text)).Failed();
}
