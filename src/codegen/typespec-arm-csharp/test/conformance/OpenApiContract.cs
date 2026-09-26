using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;

namespace Arm.Conformance;

/// <summary>One operation in the contract: an HTTP method on a path template.</summary>
public sealed record SpecOperation(string Method, string Path, string OperationId, JsonObject Responses)
{
    public IReadOnlyCollection<int> DeclaredStatuses =>
        Responses.Select(r => int.Parse(r.Key, System.Globalization.CultureInfo.InvariantCulture)).ToList();

    public override string ToString() => $"{Method} {Path} ({OperationId})";
}

/// <summary>
/// The fixture's compiled contract (<c>artifacts/codegen/conformance/spec/openapi.json</c>, copied
/// next to the test assembly by the csproj), with helpers to look up operations and validate
/// response bodies against it.
/// </summary>
public static partial class OpenApiContract
{
    private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "patch", "head", "options", "trace", "query"];

    private static readonly Lazy<JsonObject> LazyDocument = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "openapi.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Compiled fixture contract not found. The Conformance.csproj build produces it (emit-fixture.js).",
                path);
        }

        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    });

    public static JsonObject Document => LazyDocument.Value;

    public static IReadOnlyList<SpecOperation> Operations =>
    [
        .. from path in Document["paths"]!.AsObject()
           from op in path.Value!.AsObject()
           where HttpMethods.Contains(op.Key)
           select new SpecOperation(
               op.Key.ToUpperInvariant(),
               path.Key,
               op.Value!["operationId"]!.GetValue<string>(),
               op.Value!["responses"]!.AsObject()),
    ];

    public static SpecOperation Operation(string operationId) =>
        Operations.SingleOrDefault(o => o.OperationId == operationId)
        ?? throw new InvalidOperationException($"Operation '{operationId}' is not in the contract.");

    /// <summary>Route templates compared by shape: every <c>{param[:constraint][?]}</c> becomes <c>{}</c>.</summary>
    public static string NormalizePath(string template)
    {
        var normalized = RouteParameter().Replace(template, "{}").TrimEnd('/');
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized.ToLowerInvariant();
    }

    /// <summary>
    /// Validates <paramref name="body"/> against the contract's schema for <paramref name="status"/>
    /// on <paramref name="operation"/>. Returns the validation errors; empty means valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(SpecOperation operation, int status, string body)
    {
        var response = operation.Responses[status.ToString(System.Globalization.CultureInfo.InvariantCulture)]
            ?? throw new InvalidOperationException($"{operation} does not declare status {status}.");

        if (response["content"]?["text/event-stream"]?["itemSchema"] is { } itemSchema)
        {
            return ValidateEventStream(itemSchema, body);
        }

        var schemaNode = response["content"]?["application/json"]?["schema"];
        if (schemaNode is null)
        {
            // No content declared: the response must have no body.
            return string.IsNullOrEmpty(body) ? [] : [$"expected an empty body, got: {body}"];
        }

        if (string.IsNullOrEmpty(body))
        {
            return ["expected a JSON body, got an empty response"];
        }

        return ValidateJson(schemaNode, body);
    }

    /// <summary>The events of a <c>text/event-stream</c> body, parsed as a client would.</summary>
    public static IReadOnlyList<SseItem<string>> ParseEvents(string body) =>
        [.. SseParser.Create(new MemoryStream(Encoding.UTF8.GetBytes(body))).Enumerate()];

    /// <summary>
    /// Validates every event of an SSE body against the response's <c>itemSchema</c>: its name is
    /// one the contract declares (a <c>oneOf</c> branch's <c>event.const</c>), its data is JSON
    /// valid for that branch's <c>contentSchema</c> (an annotation JSON Schema validators skip, so
    /// it's checked explicitly), and the data's <c>type</c> is the event name.
    /// </summary>
    private static IReadOnlyList<string> ValidateEventStream(JsonNode itemSchema, string body)
    {
        var branches = itemSchema["oneOf"]?.AsArray()
            .Where(b => b?["properties"]?["event"]?["const"] is not null)
            .ToDictionary(b => b!["properties"]!["event"]!["const"]!.GetValue<string>(), b => b!["properties"]!["data"]!)
            ?? [];

        var errors = new List<string>();
        foreach (var (item, index) in ParseEvents(body).Select((item, index) => (item, index)))
        {
            var at = $"event #{index} ('{item.EventType}')";
            if (!branches.TryGetValue(item.EventType, out var data))
            {
                errors.Add($"{at}: not an event the contract declares");
                continue;
            }

            if (data["contentMediaType"]?.GetValue<string>() != "application/json" || data["contentSchema"] is not { } schema)
            {
                errors.Add($"{at}: the contract declares no JSON data schema for it");
                continue;
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(item.Data);
            }
            catch (JsonException exception)
            {
                errors.Add($"{at}: data is not JSON ({exception.Message})");
                continue;
            }

            if (parsed?["type"]?.GetValueKind() != JsonValueKind.String || parsed["type"]!.GetValue<string>() != item.EventType)
            {
                errors.Add($"{at}: data.type is {parsed?["type"]?.ToJsonString() ?? "missing"}, not the event name");
            }

            errors.AddRange(ValidateJson(schema, item.Data).Select(e => $"{at}: {e}"));
        }

        return errors;
    }

    private static IReadOnlyList<string> ValidateJson(JsonNode schemaNode, string body)
    {
        using var instance = JsonDocument.Parse(body);
        var results = BuildSchema(schemaNode).Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return [];
        }

        return
        [
            .. from detail in results.Details ?? []
               where detail.Errors is not null
               from error in detail.Errors!
               select $"{detail.InstanceLocation} ({detail.EvaluationPath}): {error.Key} — {error.Value}",
        ];
    }

    /// <summary>
    /// Wraps a response schema in a standalone JSON Schema (2020-12) document that carries
    /// <c>components.schemas</c> as <c>$defs</c>, with <c>#/components/schemas/X</c> refs rewritten.
    /// Object schemas are closed (<c>additionalProperties: false</c>) so a field the server adds
    /// without the contract fails the test.
    /// </summary>
    private static JsonSchema BuildSchema(JsonNode responseSchema)
    {
        var defs = Document["components"]?["schemas"]?.DeepClone().AsObject() ?? [];
        foreach (var (_, def) in defs)
        {
            Prepare(def);
        }

        var root = responseSchema.DeepClone().AsObject();
        Prepare(root);
        root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        root["$defs"] = defs;

        // A fresh base URI per build keeps each document out of the others' way in the registry.
        var baseUri = new Uri($"https://contract.test/{Guid.NewGuid():N}");
        return JsonSchema.FromText(root.ToJsonString(), baseUri: baseUri);
    }

    private static void Prepare(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue refValue && refValue.GetValue<string>() is var target &&
                    target.StartsWith("#/components/schemas/", StringComparison.Ordinal))
                {
                    obj["$ref"] = "#/$defs/" + target["#/components/schemas/".Length..];
                }

                if (obj["type"]?.ToString() == "object" && obj["properties"] is not null && !obj.ContainsKey("additionalProperties"))
                {
                    obj["additionalProperties"] = false;
                }

                foreach (var (_, child) in obj.ToList())
                {
                    Prepare(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Prepare(child);
                }

                break;
        }
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteParameter();
}
