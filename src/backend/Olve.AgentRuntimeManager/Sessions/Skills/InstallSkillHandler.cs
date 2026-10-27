using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.Variables;

namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary>
/// <c>PUT /api/skills/{name}</c>: installs a skill from a folder of a git repository, or replaces
/// it. The supervisor's <c>skill-install</c> does the work as the agents' user, with the registered
/// default environment variables for git (so a <c>GIT_SSH_COMMAND</c> reaches it), bounded by
/// <see cref="SkillsOptions.InstallTimeout"/>: it fetches the source, puts the folder at
/// <c>&lt;Skills:Root&gt;/&lt;name&gt;</c> and prints its <c>SKILL.md</c>, whose description is recorded
/// with the source. A fetch that fails is a 400 <c>SKILL_SOURCE_FAILED</c> with git's message, a folder
/// without a readable <c>SKILL.md</c> a 400 <c>INVALID_SKILL</c>. Sessions that exist keep their copies.
/// </summary>
public sealed class InstallSkillHandler(
    ISkillStore store, IEnvStore env, Supervisors supervisors, IOptions<SkillsOptions> options, TimeProvider time, ILogger<InstallSkillHandler> logger)
    : ISkillsInstallHandler
{
    public async Task<SkillsInstallResponse> HandleAsync(SkillsInstallRequest request, CancellationToken cancellationToken)
    {
        if (!SkillNames.IsValid(request.Name))
        {
            return new SkillsInstallResponse.BadRequest(SkillErrors.InvalidName(request.Name));
        }

        var settings = options.Value;
        var source = request.Body.Source;
        var install = new SupervisorSkillInstall
        {
            Root = settings.ResolvedRoot(),
            Name = request.Name,
            Url = source.Url.Trim(),
            Ref = string.IsNullOrWhiteSpace(source.Ref) ? null : source.Ref.Trim(),
            Path = string.IsNullOrWhiteSpace(source.Path) ? null : source.Path.Trim(),
            Environment = env.List().Where(v => v.IsDefault).ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal),
            GitTimeout = settings.InstallTimeout,
        };

        var result = await supervisors.InstallSkillAsync(install, settings.InstallTimeout, cancellationToken);
        switch (result.ExitCode)
        {
            case 0:
                break;
            case Supervisors.SkillInstallInvalid:
                logger.LogInformation("Skill {Skill}: {Url} has no valid SKILL.md at {Path}: {Error}", request.Name, install.Url, install.Path ?? "its root", result.Error);
                return new SkillsInstallResponse.BadRequest(SkillErrors.Invalid(result.Error is { Length: > 0 } ? result.Error : "The source has no readable SKILL.md."));
            default:
                logger.LogInformation("Skill {Skill}: fetching {Url} failed ({ExitCode}): {Error}", request.Name, install.Url, result.ExitCode, result.Error);
                return new SkillsInstallResponse.BadRequest(SkillErrors.SourceFailed(install.Url, result.Error is { Length: > 0 } ? result.Error : $"exit code {result.ExitCode}"));
        }

        if (SkillFile.Parse(result.Output, out var problem) is not { } file)
        {
            logger.LogWarning("Skill {Skill}: the supervisor installed it, but its SKILL.md doesn't parse: {Error}", request.Name, problem);
            return new SkillsInstallResponse.BadRequest(SkillErrors.Invalid(problem ?? "The SKILL.md can't be read."));
        }

        var skill = new SkillRecord
        {
            Name = request.Name,
            Description = file.Description!,
            IsDefault = request.Body.Default ?? false,
            SourceUrl = install.Url,
            SourceRef = install.Ref,
            SourcePath = install.Path,
            UpdatedAt = time.GetUtcNow(),
        };
        store.Set(skill);
        logger.LogInformation("Skill {Skill}: installed from {Url} ({Ref}, {Path})", skill.Name, skill.SourceUrl, skill.SourceRef ?? "default branch", skill.SourcePath ?? "root");
        return new SkillsInstallResponse.Ok(skill.ToDto());
    }
}
