using EHOT.Launcher.Game.Install;
using EHOT.Launcher.Game.Launch;
using EHOT.Launcher.Game.Minecraft;
using EHOT.Launcher.Game.Profiles;
using EHOT.Launcher.Game.Runtimes;
using Microsoft.Extensions.DependencyInjection;

namespace EHOT.Launcher.Game;

/// <summary>Composition root игрового модуля.</summary>
public static class GameModule
{
    public static IServiceCollection AddEhotGame(this IServiceCollection services)
    {
        services.AddSingleton<IMinecraftVersionService, MinecraftVersionService>();
        services.AddSingleton<NativesExtractor>();
        services.AddSingleton<IGameInstaller, GameInstaller>();
        services.AddSingleton<ILoaderInstaller, LoaderInstaller>();
        services.AddSingleton<JavaRuntimeManager>();
        services.AddSingleton<ILaunchArgumentBuilder, LaunchArgumentBuilder>();
        services.AddSingleton<IGameLauncher, GameLauncher>();
        services.AddSingleton<IProfileManager, ProfileManager>();
        return services;
    }
}
