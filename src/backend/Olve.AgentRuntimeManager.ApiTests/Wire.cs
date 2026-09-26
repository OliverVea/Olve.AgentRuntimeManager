using System.Net.Http.Json;
using System.Text;

namespace Olve.AgentRuntimeManager.ApiTests;

// The wire shapes the tests read are declared here on purpose (SessionBody, ErrorEnvelopeBody, …):
// raw HTTP, no generated client, so the tests see what a client sees.

public static class Wire
{
    /// <summary>The API's JSON conventions (camelCase), for reading nested event data.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public static StringContent JsonContent<T>(T value) =>
        new(System.Text.Json.JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

    /// <summary>POST /api/sessions, then reads the new session back; fails unless it was created (201 or 202).</summary>
    public static Task<SessionBody> CreateSessionAsync(this HttpClient client, string prompt, string caller = "api-tests", int? timeoutSeconds = null) =>
        client.CreateSessionAsync(new CreateSessionBody(prompt, caller, timeoutSeconds));

    public static async Task<SessionBody> CreateSessionAsync(this HttpClient client, CreateSessionBody body)
    {
        using var response = await client.PostAsync("/api/sessions", JsonContent(body));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<CreatedSessionBody>(JsonOptions))!;
        return await client.GetSessionAsync(created.Id);
    }

    /// <summary>A session whose (fake) agent runs until killed (or its timeout).</summary>
    public static Task<SessionBody> CreateHangingSessionAsync(this HttpClient client, string caller = "api-tests") =>
        client.CreateSessionAsync("Wait. fake:hang", caller);

    public static async Task<SessionBody> GetSessionAsync(this HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<SessionBody>($"/api/sessions/{id}", JsonOptions))!;

    public static async Task<HttpResponseMessage> KillSessionAsync(this HttpClient client, Guid id, string? reason = null, string caller = "api-tests") =>
        await client.PostAsync($"/api/sessions/{id}/kill", JsonContent(new { caller, reason }));

    /// <summary>Polls the session until <paramref name="condition"/> holds (agents end asynchronously).</summary>
    public static async Task<SessionBody> WaitForSessionAsync(this HttpClient client, Guid id, Func<SessionBody, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var session = await client.GetSessionAsync(id);
            if (condition(session))
            {
                return session;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    public static async Task<ErrorBody> ErrorAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorEnvelopeBody>(JsonOptions))!.Error;
}
