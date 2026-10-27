using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Supervision;

namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary>
/// <c>DELETE /api/skills/{name}</c>: unregisters a skill and removes its folder (as the agents'
/// user, through the supervisor's <c>skill-remove</c>). Sessions that took a copy keep it. A folder
/// that couldn't be removed is logged; the skill is gone from the registry anyway.
/// </summary>
public sealed class DeleteSkillHandler(ISkillStore store, Supervisors supervisors, IOptions<SkillsOptions> options, ILogger<DeleteSkillHandler> logger)
    : ISkillsDeleteHandler
{
    public Task<SkillsDeleteResponse> HandleAsync(SkillsDeleteRequest request, CancellationToken cancellationToken)
    {
        if (!SkillNames.IsValid(request.Name) || !store.Delete(request.Name))
        {
            return Task.FromResult<SkillsDeleteResponse>(new SkillsDeleteResponse.NotFound(SkillErrors.NotFound(request.Name)));
        }

        if (supervisors.RemoveSkill(options.Value.ResolvedRoot(), request.Name) is { } error)
        {
            logger.LogWarning("Skill {Skill}: unregistered, but its folder could not be removed: {Error}", request.Name, error);
        }
        else
        {
            logger.LogInformation("Skill {Skill}: unregistered and its folder removed", request.Name);
        }

        return Task.FromResult<SkillsDeleteResponse>(new SkillsDeleteResponse.NoContent());
    }
}
