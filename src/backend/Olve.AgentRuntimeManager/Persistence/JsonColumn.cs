using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>A value kept as JSON text in one column (compared by its JSON, so changes are seen).</summary>
public static class JsonColumn
{
    public static ValueConverter<T?, string?> Converter<T>() where T : class => new(
        value => value == null ? null : JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
        json => json == null ? null : JsonSerializer.Deserialize<T>(json, (JsonSerializerOptions?)null));

    public static ValueComparer<T?> Comparer<T>() where T : class => new(
        (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
        value => value == null ? 0 : JsonSerializer.Serialize(value, (JsonSerializerOptions?)null).GetHashCode(StringComparison.Ordinal),
        value => value);
}
