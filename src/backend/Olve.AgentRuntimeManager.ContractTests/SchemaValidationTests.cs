namespace Olve.AgentRuntimeManager.ContractTests;

/// <summary>Proves the validator actually rejects bodies that break the contract.</summary>
public class SchemaValidationTests
{
    private const string Id = "0b6f7f0e-4a52-4d0e-9d7a-5b1f0f6c2a11";

    [Test]
    public async Task ValidMessage_Passes() =>
        await Assert.That(Validate("Messages_get", 200, $$"""{"id":"{{Id}}","text":"hi"}""")).IsEmpty();

    [Test]
    [Arguments("""{"id":"0b6f7f0e-4a52-4d0e-9d7a-5b1f0f6c2a11"}""")]
    [Arguments("""{"id":"0b6f7f0e-4a52-4d0e-9d7a-5b1f0f6c2a11","text":"hi","extra":1}""")]
    [Arguments("""{"id":"0b6f7f0e-4a52-4d0e-9d7a-5b1f0f6c2a11","text":42}""")]
    [Arguments("[]")]
    public async Task InvalidMessage_Fails(string body) =>
        await Assert.That(Validate("Messages_get", 200, body)).IsNotEmpty();

    [Test]
    public async Task ProblemArray_ForNotFound_Passes() =>
        await Assert.That(Validate("Messages_get", 404,
            """[{"message":"nope","tags":null,"severity":0,"source":null,"exceptionSummary":null}]""")).IsEmpty();

    [Test]
    public async Task BodyOnBodylessResponse_Fails() =>
        await Assert.That(Validate("Messages_delete", 200, "{}")).IsNotEmpty();

    private static IReadOnlyList<string> Validate(string operationId, int status, string body) =>
        OpenApiContract.Validate(OpenApiContract.Operation(operationId), status, body);
}
