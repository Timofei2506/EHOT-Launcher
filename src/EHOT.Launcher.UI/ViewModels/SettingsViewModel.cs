using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Game.Runtimes;
using EHOT.Launcher.Platform.Shared;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Настройки: папки, RAM, Java, загрузки, сеть, диагностика.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppState _state;
    private readonly IAppPaths _paths;
    private readonly IPlatformInfo _platform;
    private readonly ICapabilityRegistry _capabilities;
    private readonly JavaRuntimeManager _java;
    private readonly ILogService _log;
    private readonly IToastService _toasts;
    private readonly UpdateViewModel _updates;

    public SettingsViewModel(
        IAppState state,
        IAppPaths paths,
        IPlatformInfo platform,
        ICapabilityRegistry capabilities,
        JavaRuntimeManager java,
        ILogService log,
        IToastService toasts,
        UpdateViewModel updates)
    {
        _state = state;
        _paths = paths;
        _platform = platform;
        _capabilities = capabilities;
        _java = java;
        _log = log;
        _toasts = toasts;
        _updates = updates;

        var settings = state.Settings;
        MinecraftDirectory = settings.MinecraftDirectory ?? paths.DefaultMinecraftDirectory;
        DownloadsDirectory = settings.DownloadsDirectory ?? paths.DefaultDownloadsDirectory;
        MaxMemoryMb = settings.MaxMemoryMb;
        MaxParallelDownloads = settings.MaxParallelDownloads;
        AutoUpdateContent = settings.AutoUpdateContent;
        CheckLauncherUpdates = settings.CheckLauncherUpdates;
        NotificationsEnabled = settings.NotificationsEnabled;
        ReduceAnimations = settings.ReduceAnimations;
        ControlPlaneUrl = settings.ControlPlaneUrl;
        PreferDirect = settings.PreferDirectConnection;
        AllowRelay = settings.AllowRelay;
    }

    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = [];

    public ObservableCollection<Capability> Capabilities { get; } = [];

    [ObservableProperty]
    private string _minecraftDirectory = string.Empty;

    [ObservableProperty]
    private string _downloadsDirectory = string.Empty;

    [ObservableProperty]
    private int _maxMemoryMb = 4096;

    [ObservableProperty]
    private int _maxParallelDownloads = 4;

    [ObservableProperty]
    private bool _autoUpdateContent = true;

    [ObservableProperty]
    private bool _checkLauncherUpdates = true;

    [ObservableProperty]
    private bool _notificationsEnabled = true;

    [ObservableProperty]
    private bool _reduceAnimations;

    [ObservableProperty]
    private string _controlPlaneUrl = string.Empty;

    [ObservableProperty]
    private bool _preferDirect = true;

    [ObservableProperty]
    private bool _allowRelay = true;

    [ObservableProperty]
    private string _diagnosticsSummary = string.Empty;

    [ObservableProperty]
    private string _logPath = string.Empty;

    public string PlatformSummary => $"{_platform.OsName} · {_platform.Architecture} · .NET {_platform.RuntimeVersion} · RID {_platform.RuntimeIdentifier}";

    public string AppDataPath => _paths.AppDataDirectory;

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        Capabilities.Clear();
        foreach (var capability in _capabilities.All)
        {
            Capabilities.Add(capability);
        }

        LogPath = _log.CurrentLogFile;
        DiagnosticsSummary = BuildDiagnostics();

        var runtimes = await _java.DiscoverAsync(ct).ConfigureAwait(true);
        JavaRuntimes.Clear();
        foreach (var runtime in runtimes)
        {
            JavaRuntimes.Add(runtime);
        }
    }

    private string BuildDiagnostics() =>
        $"Платформа: {_platform.OsName} {_platform.OsVersion}\n" +
        $"Архитектура: {_platform.Architecture} (RID {_platform.RuntimeIdentifier})\n" +
        $"Ядер: {_platform.LogicalProcessorCount}, RAM: {Converters.BytesToHumanConverter.Format(_platform.TotalMemoryBytes)}\n" +
        $"Каталог данных: {_paths.AppDataDirectory}\n" +
        $"SQLite: {_paths.DatabasePath}\n" +
        $"Логи: {_paths.LogsDirectory}";

    [RelayCommand]
    private async Task SaveAsync()
    {
        var updated = _state.Settings with
        {
            MinecraftDirectory = MinecraftDirectory,
            DownloadsDirectory = DownloadsDirectory,
            MaxMemoryMb = MaxMemoryMb,
            MaxParallelDownloads = MaxParallelDownloads,
            AutoUpdateContent = AutoUpdateContent,
            CheckLauncherUpdates = CheckLauncherUpdates,
            NotificationsEnabled = NotificationsEnabled,
            ReduceAnimations = ReduceAnimations,
            ControlPlaneUrl = ControlPlaneUrl,
            PreferDirectConnection = PreferDirect,
            AllowRelay = AllowRelay
        };

        await _state.SaveSettingsAsync(updated).ConfigureAwait(true);
        _toasts.Success("Настройки сохранены", "Изменения применены.");
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try
        {
            var cache = _paths.CacheDirectory;
            if (Directory.Exists(cache))
            {
                foreach (var file in Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories))
                {
                    File.Delete(file);
                }
            }

            _toasts.Success("Кэш очищен", $"Освобождено место в {cache}");
        }
        catch (Exception ex)
        {
            _toasts.Error("Не удалось очистить кэш", ex.Message);
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RescanJavaAsync()
    {
        var runtimes = await _java.DiscoverAsync().ConfigureAwait(true);
        JavaRuntimes.Clear();
        foreach (var runtime in runtimes)
        {
            JavaRuntimes.Add(runtime);
        }

        _toasts.Show("Java", $"Найдено рантаймов: {runtimes.Count}", ToastKind.Info);
    }

    [RelayCommand]
    private Task OpenLogsAsync()
    {
        _toasts.Show("Логи", _log.CurrentLogFile, ToastKind.Info);
        return Task.CompletedTask;
    }

    // ----- «О лаунчере» (перенесено из отдельного раздела в настройки) -----

    public UpdateViewModel Updates => _updates;

    public string VersionLabel
    {
        get
        {
            var version = typeof(SettingsViewModel).Assembly.GetName().Version;
            return version is null ? "версия не определена" : $"Версия {version.Major}.{version.Minor}.{version.Build} (beta)";
        }
    }

    public string RuntimeLabel => $"Avalonia 12 · .NET {_platform.RuntimeVersion}";

    public string PlatformLabel => $"{_platform.OsName} {_platform.OsVersion} · {_platform.Architecture}";

    public string BuildInfo
    {
        get
        {
            var version = typeof(SettingsViewModel).Assembly.GetName().Version;
            return $"Сборка: {version} · {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}";
        }
    }

    public static string LegalNotice =>
        "EHOT Launcher не связан с Mojang Studios и Microsoft. Minecraft — торговая марка Mojang AB. " +
        "Лаунчер не обходит лицензионную защиту: для официальных серверов требуется приобретённая копия игры " +
        "и вход через Microsoft. Контент загружается из официальных API (Modrinth, CurseForge при наличии ключа) с " +
        "соблюдением их условий использования.";

    public static string Credits =>
        "Иконки — собственные SVG-контуры EHOT. Шрифт Inter (SIL Open Font License). " +
        "Спасибо сообществу Fabric, NeoForge, Modrinth за открытые форматы и API.";

    [RelayCommand]
    private async Task CheckUpdatesAsync() => await _updates.CheckCommand.ExecuteAsync(null).ConfigureAwait(true);

    [RelayCommand]
    private void OpenGitHub() => OpenUrl("https://github.com/Timofei2506/EHOT-Launcher");

    [RelayCommand]
    private void OpenReleases() => OpenUrl("https://github.com/Timofei2506/EHOT-Launcher-Releases/releases");

    [RelayCommand]
    private void OpenIssues() => OpenUrl("https://github.com/Timofei2506/EHOT-Launcher/issues/new");

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _toasts.Error("Ссылка не открылась", $"{url} ({ex.Message})");
        }
    }
}
