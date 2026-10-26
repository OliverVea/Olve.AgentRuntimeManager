using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olve.AgentRuntimeManager.Tools.Calls;

/// <summary>
/// A call's arguments, read by name: each wrong or missing one is noted in <see cref="Problems"/>
/// (a tool checks them before doing anything) rather than thrown. Names the tool doesn't know are
/// ignored, and a null is as if it were left out. Numbers and flags given as strings (<c>"120"</c>,
/// <c>"true"</c>) are taken too: models sometimes send them so.
/// </summary>
internal sealed class ToolArguments(JsonObject? arguments)
{
    private readonly JsonObject _arguments = arguments ?? [];

    public List<string> Problems { get; } = [];

    /// <summary>All the problems in one message; null if there are none.</summary>
    public ToolResult? Failure => Problems.Count == 0 ? null : ToolResult.Error(string.Join(" ", Problems));

    public string RequiredString(string name, bool allowEmpty = false)
    {
        if (!Has(name))
        {
            Problems.Add($"`{name}` is required.");
            return "";
        }

        var value = OptionalString(name);
        if (value is "" && !allowEmpty)
        {
            Problems.Add($"`{name}` must not be empty.");
        }

        return value ?? "";
    }

    public string? OptionalString(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        if (_arguments[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            return value.GetValue<string>();
        }

        Problems.Add($"`{name}` must be a string.");
        return null;
    }

    public long? OptionalInteger(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        if (_arguments[name] is JsonValue value)
        {
            switch (value.GetValueKind())
            {
                case JsonValueKind.Number when value.TryGetValue<long>(out var integer):
                    return integer;
                case JsonValueKind.Number when value.TryGetValue<double>(out var number) && number == Math.Floor(number) && Math.Abs(number) < 1e15:
                    return (long)number;
                case JsonValueKind.String when long.TryParse(value.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
            }
        }

        Problems.Add($"`{name}` must be a whole number.");
        return null;
    }

    public bool Flag(string name)
    {
        if (!Has(name))
        {
            return false;
        }

        if (_arguments[name] is JsonValue value)
        {
            switch (value.GetValueKind())
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.String when bool.TryParse(value.GetValue<string>(), out var parsed):
                    return parsed;
            }
        }

        Problems.Add($"`{name}` must be true or false.");
        return false;
    }

    private bool Has(string name) => _arguments.TryGetPropertyValue(name, out var node) && node is not null;
}
