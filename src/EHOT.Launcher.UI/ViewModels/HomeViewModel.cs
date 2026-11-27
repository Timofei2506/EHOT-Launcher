using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Auth;
using EHOT.Launcher.Content;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Game.Install;
using EHOT.Launcher.Game.Launch;
using EHOT.Launcher.Game.Minecraft;
using EHOT.Launcher.Game.Profiles;
using EHOT.Launcher.Storage;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Главный экран: выбор версии/загрузчика, запуск и 3D-превью персонажа.</summary>
public sealed partial class HomeViewModel : ViewModelBase, INavigationAware
{
    private readonly IAppState _state;
    private readonly IGameLauncher _launcher;
    private readonly IAccountSessionProvider _sessions;
    private readonly IMinecraftNewsService _news;
    private readonly ILaunchHistoryRepository _history;
    private readonly IToastService _toasts;
    private readonly INavigationService _navigation;
    private readonly ILogService _log;
    private readonly IMinecraftVersionService _versions;
    private readonly IGameInstaller _installer;
    private readonly ILoaderInstaller _loaders;
    private readonly IProfileManager _profiles;
    private readonly OptiFineInstaller _optifine;
    private IReadOnlyList<VersionSummary> _allVersions = [];
    private string? _latestRelease;

    public HomeViewModel(
        IAppState state,
        IGameLauncher launcher,
        IAccountSessionProvider sessions,
        IMinecraftNewsService news,
        ILaunchHistoryRepository history,
        IToastService toasts,
        INavigationService navigation,
        ILogService log,
        IMinecraftVersionService versions,
        IGameInstaller installer,
        ILoaderInstaller loaders,
        IProfileManager profiles,
        OptiFineInstaller optifine)
    {
        _state = state;
        _launcher = launcher;
        _sessions = sessions;
        _news = news;
        _history = history;
        _toasts = toasts;
        _navigation = navigation;
        _log = log;
        _versions = versions;
        _installer = installer;
        _loaders = loaders;
        _profiles = profiles;
        _optifine = optifine;

        _launcher.SessionEnded += OnSessionEnded;
        _state.Changed += () => Dispatcher.UIThread.Post(Refresh);

        Refresh();
    }

    public ObservableCollection<NewsItem> News { get; } = [];
    public ObservableCollection<VersionChoice> Versions { get; } = [];
    public ObservableCollection<LoaderOption> LoaderOptions { get; } = [];
    public ObservableCollection<string> Channels { get; } = ["Релизы", "Снапшоты"];

    [ObservableProperty]
    private string _greeting = "Добро пожаловать!";

    [ObservableProperty]
    private string _selectedChannel = "Релизы";

    [ObservableProperty]
    private VersionChoice? _selectedVersion;

    [ObservableProperty]
    private LoaderKind _selectedLoader = LoaderKind.Vanilla;

    [ObservableProperty]
    private bool _selectedOptiFine;

    [ObservableProperty]
    private string _hint = "Выберите версию и отправляйтесь в игру.";

    [ObservableProperty]
    private string _profileName = "Профиль по умолчанию";

    [ObservableProperty]
    private string _profileVersion = "—";

    [ObservableProperty]
    private string _accountName = "Без аккаунта";

    [ObservableProperty]
    private string _accountKindLabel = "Локальный";

    [ObservableProperty]
    private string _accountInitials = "?";

    [ObservableProperty]
    private string? _skinPath;

    [ObservableProperty]
    private string _playButtonText = "Играть";

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private string _installStatus = "Проверяем игру…";

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string _lastLaunchLabel = "Запусков ещё не было";

    [ObservableProperty]
    private string _whatsNewTitle = "Minecraft";

    [ObservableProperty]
    private string _whatsNewSummary = "Новости появятся после загрузки с официального источника Mojang.";

    [ObservableProperty]
    private bool _canPickOptiFine;

    private void Refresh()
    {
        var account = _state.CurrentAccount;
        var profile = _state.CurrentProfile;

        Greeting = "Добро пожаловать!";
        AccountName = account?.Username ?? "Без аккаунта";
        AccountKindLabel = account?.Kind == AccountKind.Microsoft ? "Microsoft · лицензия" : "Локальный профиль";
        AccountInitials = string.IsNullOrEmpty(account?.Username) ? "?" : account.Username[..1].ToUpperInvariant();
        SkinPath = account?.LocalSkinPath;

        ProfileName = profile?.Name ?? "Автопрофиль";
        ProfileVersion = profile?.DisplayVersion ?? SelectedVersion?.Id ?? "—";
        IsGameRunning = profile is not null && _launcher.IsRunning(profile.Id);
        PlayButtonText = IsGameRunning ? "Остановить игру" : "Играть";
    }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default) => await LoadHomeAsync(ct).ConfigureAwait(true);

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken ct = default)
    {
        _ = parameter;
        await LoadHomeAsync(ct).ConfigureAwait(true);
    }

    private async Task LoadHomeAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            Refresh();
            await LoadNewsAsync(ct).ConfigureAwait(true);
            await LoadVersionsAsync(ct).ConfigureAwait(true);
            await LoadRecentAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadNewsAsync(CancellationToken ct)
    {
        try
        {
            var items = await _news.GetLatestAsync(6, ct).ConfigureAwait(true);
            News.Clear();
            foreach (var item in items)
            {
                News.Add(item);
            }

            if (items.Count > 0)
            {
                WhatsNewTitle = items[0].Version.Length > 0 ? $"Minecraft {items[0].Version}" : items[0].Title;
                WhatsNewSummary = items[0].Title;
            }
        }
        catch (Exception ex)
        {
            _log.Warn("UI", "Новости не загрузились: " + ex.Message);
        }
    }

    private async Task LoadVersionsAsync(CancellationToken ct)
    {
        var result = await _versions.GetManifestAsync(false, ct).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            ErrorMessage = result.Message ?? "Не удалось загрузить список версий.";
            InstallStatus = "Список версий недоступен — проверьте сеть.";
            return;
        }

        _allVersions = result.Value!.Versions;
        _latestRelease = result.Value.Latest?.Release;
        ApplyVersionChannel();
        await RefreshInstallStatusAsync().ConfigureAwait(true);
        await RefreshLoaderAvailabilityAsync(ct).ConfigureAwait(true);
    }

    private void ApplyVersionChannel()
    {
        var wantSnapshots = string.Equals(SelectedChannel, "Снапшоты", StringComparison.OrdinalIgnoreCase);
        var current = SelectedVersion?.Id;

        Versions.Clear();
        foreach (var version in _allVersions.Where(v => wantSnapshots ? v.Type == "snapshot" : v.Type == "release").Take(150))
        {
            Versions.Add(new VersionChoice(version));
        }

        SelectedVersion = Versions.FirstOrDefault(v => v.Id == current)
            ?? Versions.FirstOrDefault(v => v.Id == _latestRelease)
            ?? Versions.FirstOrDefault();
    }

    partial void OnSelectedChannelChanged(string value)
    {
        _ = value;
        ApplyVersionChannel();
        _ = RefreshInstallStatusAsync();
        _ = RefreshLoaderAvailabilityAsync(CancellationToken.None);
    }

    partial void OnSelectedVersionChanged(VersionChoice? value)
    {
        _ = value;
        _ = RefreshInstallStatusAsync();
        _ = RefreshLoaderAvailabilityAsync(CancellationToken.None);
    }

    private async Task RefreshLoaderAvailabilityAsync(CancellationToken ct)
    {
        LoaderOptions.Clear();
        var id = SelectedVersion?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        LoaderOptions.Add(new LoaderOption("Vanilla", LoaderKind.Vanilla, true, "Официальная сборка без модов"));

        foreach (var loader in new[] { LoaderKind.Fabric, LoaderKind.NeoForge, LoaderKind.Forge, LoaderKind.Quilt })
        {
            try
            {
                var options = await _loaders.GetLoaderVersionsAsync(loader, id, ct).ConfigureAwait(true);
                var ok = options.IsSuccess && options.Value?.Count > 0;
                LoaderOptions.Add(new LoaderOption(loader.ToString(), loader, ok,
                    ok ? $"Доступно сборок: {options.Value!.Count}" : options.Message ?? "Для этой версии сборка недоступна"));
            }
            catch (Exception ex)
            {
                LoaderOptions.Add(new LoaderOption(loader.ToString(), loader, false, ex.Message));
            }
        }

        try
        {
            var opti = await _optifine.GetVersionsAsync(ct).ConfigureAwait(true);
            var available = opti.IsSuccess && opti.Value?.Any(v => v.MinecraftVersion == id) == true;
            CanPickOptiFine = available;
            LoaderOptions.Add(new LoaderOption("OptiFine", LoaderKind.Vanilla, available,
                available ? "Патч поверх установленного Minecraft" : opti.Message ?? "Для этой версии OptiFine недоступен")
            {
                IsOptiFine = true
            });
        }
        catch (Exception ex)
        {
            CanPickOptiFine = false;
            LoaderOptions.Add(new LoaderOption("OptiFine", LoaderKind.Vanilla, false, ex.Message) { IsOptiFine = true });
        }
    }

    private async Task RefreshInstallStatusAsync()
    {
        var id = SelectedVersion?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            InstallStatus = "Версия не выбрана";
            InstallProgress = 0;
            return;
        }

        var root = _state.MinecraftRoot;
        bool installed;
        try
        {
            installed = await _installer.IsInstalledAsync(root, id).ConfigureAwait(true);
        }
        catch
        {
            installed = File.Exists(Path.Combine(root, "versions", id, $"{id}.json"));
        }

        InstallStatus = installed
            ? $"Minecraft {id} установлен"
            : $"Minecraft {id} скачается перед запуском";
        InstallProgress = installed ? 1 : 0;
    }

    private async Task LoadRecentAsync(CancellationToken ct)
    {
        try
        {
            var recent = await _history.GetRecentAsync(1, ct).ConfigureAwait(true);
            var last = recent.Count > 0 ? recent[0] : null;
            LastLaunchLabel = last is null
                ? "Запусков ещё не было"
                : $"Последний запуск: {last.ProfileName} · {last.StartedAt.ToLocalTime():dd.MM HH:mm}";
        }
        catch (Exception ex)
        {
            _log.Warn("UI", "История запусков недоступна: " + ex.Message);
        }
    }

    [RelayCommand]
    private void SelectLoader(LoaderOption? option)
    {
        if (option is null || !option.IsAvailable)
        {
            return;
        }

        SelectedOptiFine = option.IsOptiFine;
        SelectedLoader = option.Loader;
    }

    [RelayCommand]
    private async Task OpenNewsAsync() => await _navigation.NavigateAsync("news").ConfigureAwait(true);

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (IsGameRunning)
        {
            var runningProfile = _state.CurrentProfile;
            if (runningProfile is not null)
            {
                await _launcher.StopAsync(runningProfile.Id).ConfigureAwait(true);
                IsGameRunning = false;
                PlayButtonText = "Играть";
                _toasts.Success("Игра остановлена", "Minecraft закрыт.");
            }

            return;
        }

        var account = _state.CurrentAccount;
        if (account is null)
        {
            _toasts.Warning("Нужен аккаунт", "Добавьте аккаунт: карточка профиля внизу слева.");
            await _navigation.NavigateAsync("accounts").ConfigureAwait(true);
            return;
        }

        var version = SelectedVersion;
        if (version is null)
        {
            _toasts.Warning("Версия", "Выберите версию Minecraft.");
            return;
        }

        IsBusy = true;
        IsInstalling = true;
        InstallStatus = "Готовлю игру…";
        try
        {
            var root = _state.MinecraftRoot;
            var progress = new Progress<InstallProgress>(p =>
            {
                InstallProgress = p.Fraction;
                InstallStatus = p.Detail is null ? p.Stage : $"{p.Stage} · {p.Detail}";
            });

            string launchVersionId;
            var launchLoader = SelectedLoader;
            string? loaderVersion = null;

            if (SelectedOptiFine)
            {
                InstallStatus = "Проверяю Minecraft…";
                if (!await _installer.IsInstalledAsync(root, version.Id).ConfigureAwait(true))
                {
                    var baseInstall = await _installer.InstallAsync(root, version.Id, new InstallOptions(), progress).ConfigureAwait(true);
                    if (!baseInstall.IsSuccess)
                    {
                        _toasts.Error("Установка не удалась", baseInstall.Message ?? "Смотрите лог.");
                        return;
                    }
                }

                InstallStatus = "Устанавливаю OptiFine…";
                var list = await _optifine.GetVersionsAsync().ConfigureAwait(true);
                var opti = list.Value?.FirstOrDefault(v => v.MinecraftVersion == version.Id);
                if (opti is null)
                {
                    _toasts.Error("OptiFine недоступен", list.Message ?? $"Для {version.Id} нет сборки OptiFine.");
                    return;
                }

                var installedOpti = await _optifine.InstallAsync(root, opti, progress).ConfigureAwait(true);
                if (!installedOpti.IsSuccess)
                {
                    _toasts.Error("OptiFine не установился", installedOpti.Message ?? "Смотрите лог.");
                    return;
                }

                launchVersionId = installedOpti.Value!;
                launchLoader = LoaderKind.Vanilla;
                loaderVersion = opti.Label;
            }
            else if (SelectedLoader == LoaderKind.Vanilla)
            {
                InstallStatus = "Проверяю Minecraft…";
                if (!await _installer.IsInstalledAsync(root, version.Id).ConfigureAwait(true))
                {
                    var installed = await _installer.InstallAsync(root, version.Id, new InstallOptions(), progress).ConfigureAwait(true);
                    if (!installed.IsSuccess)
                    {
                        _toasts.Error("Установка не удалась", installed.Message ?? "Смотрите лог.");
                        return;
                    }
                }

                launchVersionId = version.Id;
            }
            else
            {
                InstallStatus = $"Устанавливаю {SelectedLoader}…";
                var installed = await _loaders.InstallLoaderAsync(root, version.Id, SelectedLoader, null, progress).ConfigureAwait(true);
                if (!installed.IsSuccess)
                {
                    _toasts.Error("Лоадер не установился", installed.Message ?? "Смотрите лог.");
                    return;
                }

                launchVersionId = installed.Value!;
            }

            InstallProgress = 1;
            InstallStatus = "Запускаю игру…";

            var profile = await FindOrCreateProfileAsync(account, version.Id, launchVersionId, launchLoader, loaderVersion).ConfigureAwait(true);
            if (profile is null)
            {
                return;
            }

            await _state.SetProfileAsync(profile).ConfigureAwait(true);
            await _state.SetAccountAsync(account).ConfigureAwait(true);

            var session = await _sessions.GetSessionAsync(account).ConfigureAwait(true);
            if (!session.IsSuccess)
            {
                _toasts.Error("Аккаунт недоступен", session.Message ?? "Войдите заново.");
                return;
            }

            if (session.Value!.IsOffline)
            {
                _toasts.Warning("Локальный профиль", "Официальные серверы недоступны: игра запустится оффлайн.");
            }

            var result = await _launcher.LaunchAsync(profile, account, session.Value.AccessToken).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                _toasts.Error("Запуск не удался", result.Message ?? "Смотрите лог.");
                return;
            }

            IsGameRunning = true;
            PlayButtonText = "Остановить игру";
            ProfileName = profile.Name;
            ProfileVersion = profile.DisplayVersion;
            _toasts.Success("Minecraft запущен", $"{profile.DisplayVersion} · {account.Username}");
        }
        finally
        {
            IsInstalling = false;
            IsBusy = false;
        }
    }

    private async Task<GameProfile?> FindOrCreateProfileAsync(AccountInfo account, string baseVersion, string launchVersionId, LoaderKind loader, string? loaderVersion)
    {
        var existing = _state.Profiles.FirstOrDefault(p => string.Equals(p.MinecraftVersion, launchVersionId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var name = loader == LoaderKind.Vanilla
            ? SelectedOptiFine ? $"OptiFine {baseVersion}" : baseVersion
            : $"{baseVersion} · {loader}";

        var created = await _profiles.CreateAsync(new CreateProfileRequest
        {
            Name = name,
            MinecraftVersion = launchVersionId,
            Loader = loader,
            LoaderVersion = loaderVersion,
            AccountId = account.Id,
            MemoryMb = 4096
        }).ConfigureAwait(true);

        if (!created.IsSuccess)
        {
            _toasts.Error("Профиль не создан", created.Message ?? "Ошибка.");
            return null;
        }

        await _state.RefreshProfilesAsync().ConfigureAwait(true);
        return created.Value;
    }

    private void OnSessionEnded(string profileId, LaunchOutcome outcome)
    {
        _ = profileId;
        Dispatcher.UIThread.Post(() =>
        {
            IsGameRunning = false;
            PlayButtonText = "Играть";

            if (outcome.Status == LaunchStatus.Failed)
            {
                _toasts.Error("Minecraft завершился с ошибкой", outcome.Error ?? "Подробности в логе запуска.");
            }
            else
            {
                _toasts.Success("Игра завершена", $"Длительность: {outcome.Duration:hh\\:mm\\:ss}");
            }

            _ = LoadRecentAsync(CancellationToken.None);
        });
    }
}

public sealed record VersionChoice(VersionSummary Summary)
{
    public string Id => Summary.Id;
    public string TypeLabel => Summary.Type == "snapshot" ? "снапшот" : "релиз";
    public override string ToString() => Id;
}

public sealed class LoaderOption
{
    public LoaderOption(string label, LoaderKind loader, bool isAvailable, string hint)
    {
        Label = label;
        Loader = loader;
        IsAvailable = isAvailable;
        Hint = hint;
    }

    public string Label { get; }
    public LoaderKind Loader { get; }
    public bool IsAvailable { get; }
    public string Hint { get; }
    public bool IsOptiFine { get; init; }
}
