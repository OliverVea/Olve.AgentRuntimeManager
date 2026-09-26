using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Olve.AgentRuntimeManager.Persistence;

/// <summary>Stores a <see cref="DateTimeOffset"/> as its UTC <see cref="DateTime"/>; it reads back with offset zero.</summary>
public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTime>(
    value => value.UtcDateTime,
    value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
