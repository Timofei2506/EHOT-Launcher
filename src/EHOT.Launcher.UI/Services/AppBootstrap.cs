using EHOT.Launcher.Auth;
using EHOT.Launcher.Content;
using EHOT.Launcher.Core.Archives;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Downloads;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Game;
using EHOT.Launcher.Network;
using EHOT.Launcher.Network.Adapters;
using EHOT.Launcher.Platform.Linux;
using EHOT.Launcher.Platform.MacOS;
using EHOT.Launcher.Platform.Shared;
using EHOT.Launcher.Platform.Windows;
using EHOT.Launcher.Storage;
using EHOT.Launcher.UI.ViewModels;
using EHOT.Launcher.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.UI.Services;

/// <summary>
/// Composition root лаунчера. Платформенные реализации выбираются здесь и только здесь —
/// остальной код работает через интерфейсы.
/// </summary>
public static class AppBootstrap
{
    public static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        // --- Платформа ---------------------------------------------------
        if (OperatingSystem.IsWindows())
        {
            services.AddWindowsPlatform();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddMacPlatform();
        }
        else
        {
            services.AddLinuxPlatform();
        }

        // --- Инфраструктура ---------------------------------------------
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<EHOT.Launcher.Platform.Shared.IGraphicsDetector, EHOT.Launcher.Platform.Shared.GraphicsDetector>();
        services.AddSingleton<ICapabilityRegistry>(sp =>
        {
            var registry = new CapabilityRegistry();
            registry.Set(Capability.Available(CapabilityIds.LocalAuth, "Локальные профили"));
            registry.Set(Capability.Available(CapabilityIds.GameLaunch, "Запуск Minecraft"));
            registry.Set(Capability.Available(CapabilityIds.ContentModrinth, "Каталог Modrinth"));

            var privilege = sp.GetRequiredService<INetworkPrivileges>();
            registry.Set(privilege.HasTunnelPrivileges
                ? Capability.Available(CapabilityIds.GameNetworkTunnel, "Туннель Game Network")
                : Capability.Degraded(CapabilityIds.GameNetworkTunnel, "Туннель Game Network",
                    privilege.Describe(TunnelPrivilegeState.NotSupported)));

            registry.Set(Capability.Available(CapabilityIds.GameNetworkControl, "Сервер комнат (при настройке адреса)"));
            registry.Set(Capability.Available(CapabilityIds.AutoUpdate, "Обновление лаунчера", "Проверка манифеста обновлений"));
            return registry;
        });

        services.AddSingleton<ILogService>(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            paths.EnsureCreated();
            return new LogService(paths.LogsDirectory);
        });

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.Services.AddSingleton<ILoggerProvider>(sp =>
                new LogServiceLoggerProvider(sp.GetRequiredService<ILogService>()));
        });

        services.AddSingleton<HttpSettings>(_ => HttpSettings.Default);
        services.AddSingleton<IHttpService>(sp =>
            new HttpService(sp.GetRequiredService<HttpSettings>(), sp.GetRequiredService<ILogger<HttpService>>()));

        services.AddSingleton<IArchiveExtractor>(sp =>
            new SafeZipExtractor(sp.GetRequiredService<ILogger<SafeZipExtractor>>()));

        // --- Хранилище ---------------------------------------------------
        services.AddEhotStorage();

        services.AddSingleton<DownloadManagerOptions>(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            return new DownloadManagerOptions { MaxParallelDownloads = 4 };
        });

        services.AddSingleton<IDownloadManager>(sp => new DownloadManager(
            sp.GetRequiredService<IHttpService>(),
            sp.GetRequiredService<IDownloadHistoryStore>(),
            sp.GetRequiredService<DownloadManagerOptions>(),
            sp.GetRequiredService<ILogService>()));

        // --- Домены ------------------------------------------------------
        services.AddEhotGame();
        services.AddEhotAuth();
        services.AddEhotContent(() => Environment.GetEnvironmentVariable("EHOT_CURSEFORGE_KEY"));

        services.AddSingleton<INetworkAdapter>(sp => CreateNetworkAdapter(sp));
        services.AddEhotNetworkFactory();
        services.AddSingleton<ControlPlaneOptions>(sp => new ControlPlaneOptions
        {
            BaseUrl = Environment.GetEnvironmentVariable("EHOT_CONTROL_PLANE")
                       ?? "https://api.ehot.example"
        });

        // --- UI-сервисы --------------------------------------------------
        services.AddSingleton<IToastService, ToastService>();

        // --- Автообновление -----------------------------------------------
        // Репозиторий релизов зашит в сборку (AssemblyMetadata "UpdateRepository"),
        // поэтому «сборка из исходников» честно сообщит, что канал не настроен.
        services.AddSingleton(sp => new UpdateOptions
        {
            Repository = ReadUpdateRepository(),
            StagingRoot = Path.Combine(sp.GetRequiredService<IAppPaths>().AppDataDirectory, "updates"),
            ExecutablePath = Environment.ProcessPath,
            IncludePrerelease = true
        });
        services.AddSingleton<IUpdateService, GitHubUpdateService>();
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ISkinFilePicker, SkinFilePicker>();
        services.AddSingleton<DeepLinkRouter>(sp => new DeepLinkRouter(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("DeepLink"),
            sp.GetRequiredService<INavigationService>(),
            () => (Avalonia.Application.Current?.ApplicationLifetime
                   as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow));
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IAppState, AppState>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<PlayViewModel>();
        services.AddSingleton<VersionsViewModel>();
        services.AddSingleton<ModsViewModel>();
        services.AddSingleton<ModpacksViewModel>();
        services.AddSingleton<ResourcePacksViewModel>();
        services.AddSingleton<ShadersViewModel>();
        services.AddSingleton<WorldsViewModel>();
        services.AddSingleton<GameNetworkViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false
        });
    }

    /// <summary>
    /// Платформенный сетевой адаптер. Создаётся через контейнер, чтобы права/логи были настоящими,
    /// а не заглушками: если адаптер недоступен, он сам сообщает причину.
    /// </summary>
    private static INetworkAdapter CreateNetworkAdapter(IServiceProvider sp)
    {
        var log = sp.GetRequiredService<ILogService>();

        if (OperatingSystem.IsWindows())
        {
            return new WindowsWintunAdapter(sp.GetRequiredService<INetworkPrivileges>(), sp.GetRequiredService<IProcessRunner>(), log);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacNetworkAdapter(sp.GetRequiredService<INetworkPrivileges>(), log);
        }

        return new NullNetworkAdapter(log);
    }

    /// <summary>Репозиторий релизов из метаданных сборки (owner/name). Пусто — канал не настроен.</summary>
    private static string ReadUpdateRepository()
        => typeof(AppBootstrap).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")
            ?.Value ?? string.Empty;
}
