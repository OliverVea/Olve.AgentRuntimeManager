namespace Arm.Conformance;

/// <summary>Proves the validator actually rejects bodies that break the fixture contract.</summary>
public class SchemaValidationTests
{
    private const string Id = "0b6f7f0e-4a52-4d0e-9d7a-5b1f0f6c2a11";

    private const string ValidWidget =
        $$$"""{"id":"{{{Id}}}","serial":"SN","name":"w","priority":1,"description":null,"createdAt":"2026-01-01T00:00:00Z","shape":{"kind":"circle","radius":1}}""";

    [Test]
    public async Task ValidWidget_Passes() =>
        await Assert.That(Validate("Widgets_create", 200, ValidWidget)).IsEmpty();

    [Test]
    public async Task ValidShapes_Pass()
    {
        await Assert.That(Validate("Shapes_get", 200, """{"kind":"circle","radius":1}""")).IsEmpty();
        await Assert.That(Validate("Shapes_get", 200, """{"kind":"square","side":2}""")).IsEmpty();
    }

    [Test]
    // A required property missing.
    [Arguments($$$"""{"id":"{{{Id}}}","serial":"SN","priority":1,"description":null,"createdAt":"2026-01-01T00:00:00Z","shape":{"kind":"circle","radius":1}}""")]
    // A required nullable property missing (null is fine, absent is not).
    [Arguments($$$"""{"id":"{{{Id}}}","serial":"SN","name":"w","priority":1,"createdAt":"2026-01-01T00:00:00Z","shape":{"kind":"circle","radius":1}}""")]
    // A property the contract doesn't have.
    [Arguments($$$"""{"id":"{{{Id}}}","serial":"SN","name":"w","priority":1,"description":null,"createdAt":"2026-01-01T00:00:00Z","shape":{"kind":"circle","radius":1},"extra":1}""")]
    // A wrong type.
    [Arguments($$$"""{"id":"{{{Id}}}","serial":"SN","name":42,"priority":1,"description":null,"createdAt":"2026-01-01T00:00:00Z","shape":{"kind":"circle","radius":1}}""")]
    // A value outside an enum.
    [Arguments($$$"""{"id":"{{{Id}}}","serial":"SN","name":"w","priority":3,"description":null,"createdAt":"2026-01-01T00:00:00Z","shape":{"kind":"circle","radius":1}}""")]
    // A constraint (maxLength 50) broken.
    [Arguments($$$"""{"id":"{{{Id}}}","serial":"SN","name":"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx","priority":1,"description":null,"createdAt":"2026-01-01T00:00:00Z","shape":{"kind":"circle","radius":1}}""")]
    // A union member that doesn't exist.
    [Arguments($$$"""{"id":"{{{Id}}}","serial":"SN","name":"w","priority":1,"description":null,"createdAt":"2026-01-01T00:00:00Z","shape":{"kind":"triangle","radius":1}}""")]
    [Arguments("[]")]
    public async Task InvalidWidget_Fails(string body) =>
        await Assert.That(Validate("Widgets_create", 200, body)).IsNotEmpty();

    [Test]
    public async Task ProblemArray_Passes() =>
        await Assert.That(Validate("Widgets_update", 404,
            """[{"message":"nope","tags":null,"severity":0,"source":null,"exceptionSummary":null}]""")).IsEmpty();

    [Test]
    public async Task BodyOnBodylessResponse_Fails() =>
        await Assert.That(Validate("Widgets_delete", 204, "{}")).IsNotEmpty();

    [Test]
    public async Task MissingBodyOnJsonResponse_Fails() =>
        await Assert.That(Validate("Widgets_list", 200, "")).IsNotEmpty();

    private const string ValidChanged =
        $$$"""{"type":"widget.changed","at":"2026-01-01T00:00:00Z","widgetId":"{{{Id}}}","name":"w","shape":{"kind":"circle","radius":1}}""";

    [Test]
    public async Task ValidEventStream_Passes() =>
        await Assert.That(Validate("WidgetEvents_stream", 200,
            $"event: ping\ndata: {{\"type\":\"ping\",\"at\":\"2026-01-01T00:00:00Z\"}}\n\nevent: widget.changed\nid: 1\ndata: {ValidChanged}\n\n")).IsEmpty();

    [Test]
    // An event the contract doesn't declare.
    [Arguments("event: widget.exploded\ndata: {\"type\":\"widget.exploded\",\"at\":\"2026-01-01T00:00:00Z\"}\n\n")]
    // Data invalid for its event's schema (a required property missing).
    [Arguments("event: widget.removed\ndata: {\"type\":\"widget.removed\",\"at\":\"2026-01-01T00:00:00Z\"}\n\n")]
    // Data whose type isn't the event name (and so doesn't match that event's schema either).
    [Arguments("event: ping\ndata: {\"type\":\"widget.removed\",\"at\":\"2026-01-01T00:00:00Z\",\"widgetId\":\"" + Id + "\"}\n\n")]
    // No type at all.
    [Arguments("event: ping\ndata: {\"at\":\"2026-01-01T00:00:00Z\"}\n\n")]
    // Not JSON.
    [Arguments("event: ping\ndata: pong\n\n")]
    public async Task InvalidEventStream_Fails(string body) =>
        await Assert.That(Validate("WidgetEvents_stream", 200, body)).IsNotEmpty();

    private static IReadOnlyList<string> Validate(string operationId, int status, string body) =>
        OpenApiContract.Validate(OpenApiContract.Operation(operationId), status, body);
}
