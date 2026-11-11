using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Platform.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.Platform.Linux;

/// <summary>
/// Linux-реализация — используется для CI/разработки. Продукт официально не поддерживает Linux,
/// но архитектура готова: платформенный код изолирован.
/// </summary>
public sealed class LinuxAppPaths : BaseAppPaths
{
    protected override string ResolveAppDataDirectory()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(xdg, "ehot-launcher");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "ehot-launcher");
    }
}

public sealed class LinuxNotificationService(IProcessRunner runner, ILogger<LinuxNotificationService> logger) : INotificationService
{
    public bool IsSupported => OperatingSystem.IsLinux() && File.Exists("/usr/bin/notify-send");

    public async Task NotifyAsync(string title, string message, CancellationToken ct = default)
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            await runner.RunAsync(new ProcessRequest("/usr/bin/notify-send", [title, message]), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug("notify-send не сработал: {Message}", ex.Message);
        }
    }
}

public sealed class LinuxNetworkPrivileges : INetworkPrivileges
{
    public bool HasTunnelPrivileges => false;

    public bool CanRequestElevation => false;

    public string Describe(TunnelPrivilegeState state) =>
        "Linux не поддерживается официально: доступны прямое соединение и relay без TUN-адаптера.";
}

public static class LinuxPlatformModule
{
    public static IServiceCollection AddLinuxPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IAppPaths, LinuxAppPaths>();
        services.AddSingleton<IPlatformInfo, BasePlatformInfo>();
        services.AddSingleton<INotificationService, LinuxNotificationService>();
        services.AddSingleton<INetworkPrivileges, LinuxNetworkPrivileges>();
        services.AddSingleton<IJavaRuntimeService, JavaRuntimeService>();
        services.AddSingleton<ISecretStore>(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            return new FileSecretStore(
                Path.Combine(paths.AppDataDirectory, "secrets.json"),
                new AesGcmFileKeyProtector(Path.Combine(paths.AppDataDirectory, "secrets.key")));
        });

        return services;
    }
}
