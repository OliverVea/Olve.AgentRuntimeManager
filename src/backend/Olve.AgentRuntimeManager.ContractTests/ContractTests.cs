using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Olve.AgentRuntimeManager.ContractTests;

/// <summary>
/// One exercised response: an operation, the scenario, the status it must produce, and how to
/// produce it over raw HTTP (no generated client, so wire-format mistakes aren't masked).
/// </summary>
public sealed record ContractCase(
    string OperationId,
    string Scenario,
    int ExpectedStatus,
    Func<ApiFactory, Task<HttpResponseMessage>> Send)
{
    public override string ToString() => $"{OperationId} {ExpectedStatus} ({Scenario})";
}

/// <summary>
/// Exercises each operation's happy path and documented error paths against the in-process app,
/// and checks that the status is one the contract declares and the body validates against the
/// contract's schema for that status.
/// </summary>
[ClassDataSource<ApiFactory>(Shared = SharedType.PerAssembly)]
public class ContractTests(ApiFactory factory)
{
    private static readonly string MissingId = Guid.NewGuid().ToString();

    public static IEnumerable<Func<ContractCase>> Cases()
    {
        // GET /api/messages
        yield return () => new("Messages_list", "default page", 200,
            f => f.CreateClient().GetAsync("/api/messages"));
        yield return () => new("Messages_list", "explicit page and size", 200,
            f => f.CreateClient().GetAsync("/api/messages?page=2&pageSize=1"));
        yield return () => new("Messages_list", "page with a next page", 200,
            async f =>
            {
                await CreateMessageAsync(f);
                await CreateMessageAsync(f);
                return await f.CreateClient().GetAsync("/api/messages?page=1&pageSize=1");
            });

        // POST /api/messages
        yield return () => new("Messages_create", "valid text", 200,
            f => f.CreateAuthenticatedClient().PostAsync("/api/messages", Json(new { text = "hello" })));
        yield return () => new("Messages_create", "empty text", 400,
            f => f.CreateAuthenticatedClient().PostAsync("/api/messages", Json(new { text = "" })));
        yield return () => new("Messages_create", "text over 280 chars", 400,
            f => f.CreateAuthenticatedClient().PostAsync("/api/messages", Json(new { text = new string('x', 281) })));
        yield return () => new("Messages_create", "no bearer token", 401,
            f => f.CreateClient().PostAsync("/api/messages", Json(new { text = "hello" })));

        // GET /api/messages/{id}
        yield return () => new("Messages_get", "existing message", 200,
            async f => await f.CreateClient().GetAsync($"/api/messages/{await CreateMessageAsync(f)}"));
        yield return () => new("Messages_get", "missing message", 404,
            f => f.CreateClient().GetAsync($"/api/messages/{MissingId}"));

        // PUT /api/messages/{id}
        yield return () => new("Messages_update", "existing message", 200,
            async f => await f.CreateAuthenticatedClient().PutAsync($"/api/messages/{await CreateMessageAsync(f)}", Json(new { text = "after" })));
        yield return () => new("Messages_update", "empty text", 400,
            async f => await f.CreateAuthenticatedClient().PutAsync($"/api/messages/{await CreateMessageAsync(f)}", Json(new { text = "" })));
        yield return () => new("Messages_update", "missing message", 400,
            f => f.CreateAuthenticatedClient().PutAsync($"/api/messages/{MissingId}", Json(new { text = "after" })));
        yield return () => new("Messages_update", "no bearer token", 401,
            f => f.CreateClient().PutAsync($"/api/messages/{MissingId}", Json(new { text = "after" })));

        // DELETE /api/messages/{id}
        yield return () => new("Messages_delete", "existing message", 200,
            async f => await f.CreateAuthenticatedClient().DeleteAsync($"/api/messages/{await CreateMessageAsync(f)}"));
        yield return () => new("Messages_delete", "missing message", 400,
            f => f.CreateAuthenticatedClient().DeleteAsync($"/api/messages/{MissingId}"));
        yield return () => new("Messages_delete", "no bearer token", 401,
            f => f.CreateClient().DeleteAsync($"/api/messages/{MissingId}"));

        // GET /api/auth-config
        yield return () => new("AuthConfig_get", "anonymous", 200,
            f => f.CreateClient().GetAsync("/api/auth-config"));
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Response_MatchesContract(ContractCase contractCase)
    {
        var operation = OpenApiContract.Operation(contractCase.OperationId);

        using var response = await contractCase.Send(factory);
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(status).IsEqualTo(contractCase.ExpectedStatus);
        await Assert.That(operation.DeclaredStatuses).Contains(status);
        await Assert.That(OpenApiContract.Validate(operation, status, body)).IsEmpty();
    }

    /// <summary>Every status the contract declares is exercised by at least one case above.</summary>
    [Test]
    public async Task EveryDeclaredResponse_IsExercised()
    {
        var exercised = Cases().Select(c => c()).Select(c => $"{c.OperationId} {c.ExpectedStatus}").ToHashSet();
        var declared = OpenApiContract.Operations
            .SelectMany(o => o.DeclaredStatuses.Select(s => $"{o.OperationId} {s}"))
            .ToHashSet();

        await Assert.That(declared).IsNotEmpty();
        await Assert.That(declared.Except(exercised).Order().ToList()).IsEmpty();
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<string> CreateMessageAsync(ApiFactory f)
    {
        var response = await f.CreateAuthenticatedClient().PostAsync("/api/messages", Json(new { text = "seed" }));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetString()!;
    }
}
