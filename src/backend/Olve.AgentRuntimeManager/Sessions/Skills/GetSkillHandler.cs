using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary><c>GET /api/skills/{name}</c>: one registered skill (a name no skill can have is a 404 too).</summary>
public sealed class GetSkillHandler(ISkillStore store) : ISkillsGetHandler
{
    public Task<SkillsGetResponse> HandleAsync(SkillsGetRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<SkillsGetResponse>(
            SkillNames.IsValid(request.Name) && store.Find(request.Name) is { } skill
                ? new SkillsGetResponse.Ok(skill.ToDto())
                : new SkillsGetResponse.NotFound(SkillErrors.NotFound(request.Name)));
}
