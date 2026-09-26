using System.Net.Http.Json;
using System.Text;

namespace Olve.AgentRuntimeManager.ApiTests;

// The wire shapes the tests read, declared here on purpose: raw HTTP, no generated client, so
// the tests see what a client sees.

public sealed record MessageBody(Guid Id, string Text);

public sealed record PaginationBody(int Page, int PageSize, int Offset);

public sealed record PageBody(
    List<MessageBody> Items,
    int PageNumber,
    int PageSize,
    int TotalCount,
    int? TotalPages,
    bool? HasNextPage,
    PaginationBody? Next);

public sealed record ProblemBody(string Message, List<string>? Tags);

public sealed record AuthConfigBody(string? Authority, string? ClientId, string Scopes);

public static class Wire
{
    /// <summary>The API's JSON conventions (camelCase), for reading nested event data.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public static StringContent TextBody(string text) => JsonContent(new { text });

    public static StringContent JsonContent<T>(T value) =>
        new(System.Text.Json.JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public static async Task<MessageBody> CreateMessageAsync(this HttpClient client, string text)
    {
        using var response = await client.PostAsync("/api/messages", TextBody(text));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MessageBody>())!;
    }

    public static async Task<List<string>> ProblemMessagesAsync(this HttpResponseMessage response) =>
        [.. (await response.Content.ReadFromJsonAsync<List<ProblemBody>>())!.Select(p => p.Message)];

    /// <summary>Every message, following the pages (the store is shared, so never assume a count).</summary>
    public static async Task<List<MessageBody>> ListAllMessagesAsync(this HttpClient client)
    {
        var all = new List<MessageBody>();
        for (var page = 1; ; page++)
        {
            var body = (await client.GetFromJsonAsync<PageBody>($"/api/messages?page={page}&pageSize=100"))!;
            all.AddRange(body.Items);
            if (body.HasNextPage != true)
            {
                return all;
            }
        }
    }
}
