using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary><c>GET /api/skills</c>: every registered skill, by name.</summary>
public sealed class ListSkillsHandler(ISkillStore store) : ISkillsListHandler
{
    public Task<SkillsListResponse> HandleAsync(SkillsListRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<SkillsListResponse>(new SkillsListResponse.Ok([.. store.List().Select(s => s.ToDto())]));
}
