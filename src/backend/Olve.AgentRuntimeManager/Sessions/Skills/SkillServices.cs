using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions.Skills;

/// <summary>The skills registry's settings and the <c>Skills_*</c> handlers (the store is registered with the persistence).</summary>
public static class SkillServices
{
    public static void AddSkillServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SkillsOptions>(configuration.GetSection(SkillsOptions.Section));
        services.AddSingleton<ISkillsListHandler, ListSkillsHandler>();
        services.AddSingleton<ISkillsGetHandler, GetSkillHandler>();
        services.AddSingleton<ISkillsInstallHandler, InstallSkillHandler>();
        services.AddSingleton<ISkillsDeleteHandler, DeleteSkillHandler>();
    }
}
