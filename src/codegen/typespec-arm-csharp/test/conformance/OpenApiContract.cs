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
