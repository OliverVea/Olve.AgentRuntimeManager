using Olve.Results.TUnit;
using Olve.AgentRuntimeManager.Messages;

namespace Olve.AgentRuntimeManager.UnitTests.Messages;

public class MessageRequestValidatorTests
{
    private readonly MessageRequestValidator _sut = new();

    [Test]
    public async Task Validate_NonEmptyText_Succeeds()
    {
        var result = _sut.Validate(new MessageRequest("hello"));

        await Assert.That(result).Succeeded();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Validate_EmptyOrWhitespace_Fails(string text)
    {
        var result = _sut.Validate(new MessageRequest(text));

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task Validate_TooLong_Fails()
    {
        var result = _sut.Validate(new MessageRequest(new string('a', 281)));

        await Assert.That(result).Failed();
    }
}
