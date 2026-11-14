using EHOT.Launcher.Content.Install;
using EHOT.Launcher.Content.Models;
using EHOT.Launcher.Content.Modpacks;
using EHOT.Launcher.Content.Resolution;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EHOT.Launcher.Content;

public static class ContentModule
{
    public static IServiceCollection AddEhotContent(this IServiceCollection services, Func<string?> curseForgeKeyAccessor)
    {
        services.AddSingleton<IContentProviderRegistry>(sp => new ContentProviderRegistry(
            sp.GetRequiredService<IHttpService>(),
            sp.GetRequiredService<ILogService>(),
            curseForgeKeyAccessor()));

        services.AddSingleton<IDependencyResolver, DependencyResolver>();
        services.AddSingleton<IContentInstallService, ContentInstallService>();
        services.AddSingleton<IGameInstallerBridge, GameInstallerBridge>();
        services.AddSingleton<IModpackService, ModpackService>();
        services.AddSingleton<IInstalledContentRepository, InstalledContentRepository>();
        services.AddSingleton<IMinecraftNewsService, MinecraftNewsService>();
        services.AddSingleton<Optimization.OptimizationAdvisor>();
        return services;
    }
}
