using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Auth;
using EHOT.Launcher.Content;
using EHOT.Launcher.Content.Models;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Downloads;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Game.Install;
using EHOT.Launcher.Game.Launch;
using EHOT.Launcher.Game.Profiles;
using EHOT.Launcher.Network;
using EHOT.Launcher.Storage;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Главный экран: игровой профиль, кнопка ИГРАТЬ, новости, тренды, недавнее, Game Network.</summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly IAppState _state;
    private readonly IGameLauncher _launcher;
    private readonly IAccountSessionProvider _sessions;
    private readonly IMinecraftNewsService _news;
    private readonly IContentProviderRegistry _content;
    private readonly ILaunchHistoryRepository _history;
    private readonly IToastService _toasts;
    private readonly INavigationService _navigation;
    private readonly ICapabilityRegistry _capabilities;
    private readonly IRoomSession _room;
    private readonly ILogService _log;

    public HomeViewModel(
        IAppState state,
        IGameLauncher launcher,
        IAccountSessionProvider sessions,
        IMinecraftNewsService news,
        IContentProviderRegistry content,
        ILaunchHistoryRepository history,
        IToastService toasts,
        INavigationService navigation,
        ICapabilityRegistry capabilities,
        IRoomSession room,
        ILogService log)
    {
        _state = state;
        _launcher = launcher;
        _sessions = sessions;
        _news = news;
        _content = content;
        _history = history;
        _toasts = toasts;
        _navigation = navigation;
        _capabilities = capabilities;
        _room = room;
        _log = log;

        _launcher.SessionEnded += OnSessionEnded;
        _room.SnapshotChanged += snapshot => Dispatcher.UIThread.Post(() => ApplyRoom(snapshot));
        _state.Changed += () => Dispatcher.UIThread.Post(Refresh);

        ApplyRoom(_room.Snapshot);
        Refresh();
    }

    public ObservableCollection<NewsCardViewModel> News { get; } = [];

    public ObservableCollection<ContentCardViewModel> PopularMods { get; } = [];

    public ObservableCollection<ContentCardViewModel> PopularModpacks { get; } = [];

    public ObservableCollection<LaunchRecord> RecentLaunches { get; } = [];

    [ObservableProperty]
    private string _greeting = "Добро пожаловать";

    [ObservableProperty]
    private string _profileName = "Профиль не выбран";

    [ObservableProperty]
    private string _profileVersion = "—";

    [ObservableProperty]
    private string _accountName = "Без аккаунта";

    [ObservableProperty]
    private string _accountKindLabel = "Локальный";

    [ObservableProperty]
    private string _accountInitials = "?";

    [ObservableProperty]
    private bool _isMicrosoftAccount;

    [ObservableProperty]
    private string _playButtonText = "ИГРАТЬ";

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private string _installStatus = "Проверяем установку…";

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string _lastLaunchLabel = "Запусков ещё не было";

    [ObservableProperty]
    private string _networkStatus = "Game Network: не подключено";

    [ObservableProperty]
    private string _networkDetail = "Создайте комнату, чтобы играть с друзьями";

    [ObservableProperty]
    private string _networkAddress = "—";

    [ObservableProperty]
    private bool _isNetworkConnected;

    [ObservableProperty]
    private string _capabilityWarning = string.Empty;

    [ObservableProperty]
    private bool _hasCapabilityWarning;

    public string MinecraftRoot => (_state as AppState)?.MinecraftRoot ?? "—";

    private void Refresh()
    {
        var account = _state.CurrentAccount;
        var profile = _state.CurrentProfile;

        Greeting = DateTime.Now.Hour switch
        {
            < 5 => "Доброй ночи",
            < 12 => "Доброе утро",
            < 18 => "Добрый день",
            _ => "Добрый вечер"
        };

        AccountName = account?.Username ?? "Без аккаунта";
        AccountKindLabel = account?.Kind == AccountKind.Microsoft ? "Microsoft · лицензия" : "Локальный профиль";
        IsMicrosoftAccount = account?.Kind == AccountKind.Microsoft;
        AccountInitials = string.IsNullOrEmpty(account?.Username) ? "?" : account.Username[..1].ToUpperInvariant();

        ProfileName = profile?.Name ?? "Профиль не выбран";
        ProfileVersion = profile?.DisplayVersion ?? "Сначала создайте профиль";
        IsGameRunning = profile is not null && _launcher.IsRunning(profile.Id);
        PlayButtonText = IsGameRunning ? "ИДЁТ ИГРА" : "ИГРАТЬ";

        var tunnel = _capabilities.Get(CapabilityIds.GameNetworkTunnel);
        HasCapabilityWarning = tunnel.State != CapabilityState.Available;
        CapabilityWarning = tunnel.Detail ?? string.Empty;

        OnPropertyChanged(nameof(MinecraftRoot));
    }

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            Refresh();
            await LoadNewsAsync(ct).ConfigureAwait(true);
            await LoadPopularAsync(ct).ConfigureAwait(true);
            await LoadRecentAsync(ct).ConfigureAwait(true);
            await RefreshInstallStatusAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadNewsAsync(CancellationToken ct)
    {
        var items = await _news.GetLatestAsync(6, ct).ConfigureAwait(true);
        News.Clear();
        foreach (var item in items)
        {
            News.Add(new NewsCardViewModel(item));
        }
    }

    private async Task LoadPopularAsync(CancellationToken ct)
    {
        var provider = _content.Primary;
        var version = _state.CurrentProfile?.MinecraftVersion;

        var mods = await provider.SearchAsync(new ContentSearchQuery
        {
            Type = ContentType.Mod,
            Sort = ContentSort.Downloads,
            MinecraftVersion = version,
            Limit = 8
        }, ct).ConfigureAwait(true);

        PopularMods.Clear();
        foreach (var project in mods.Items)
        {
            PopularMods.Add(new ContentCardViewModel(project));
        }

        var packs = await provider.SearchAsync(new ContentSearchQuery
        {
            Type = ContentType.Modpack,
            Sort = ContentSort.Downloads,
            Limit = 6
        }, ct).ConfigureAwait(true);

        PopularModpacks.Clear();
        foreach (var project in packs.Items)
        {
            PopularModpacks.Add(new ContentCardViewModel(project));
        }
    }

    private async Task LoadRecentAsync(CancellationToken ct)
    {
        var recent = await _history.GetRecentAsync(5, ct).ConfigureAwait(true);
        RecentLaunches.Clear();
        foreach (var record in recent)
        {
            RecentLaunches.Add(record);
        }

        var last = recent.Count > 0 ? recent[0] : null;
        LastLaunchLabel = last is null
            ? "Запусков ещё не было"
            : $"Последний запуск: {last.ProfileName} · {last.StartedAt.ToLocalTime():dd.MM HH:mm} · {DescribeStatus(last.Status)}";
    }

    private static string DescribeStatus(LaunchStatus status) => status switch
    {
        LaunchStatus.Started => "запущена",
        LaunchStatus.Exited => "завершена",
        LaunchStatus.Failed => "ошибка",
        _ => "прервана"
    };

    private async Task RefreshInstallStatusAsync(CancellationToken ct)
    {
        var profile = _state.CurrentProfile;
        if (profile is null)
        {
            InstallStatus = "Профиль не создан";
            return;
        }

        var versionDir = Path.Combine(profile.InstanceDirectory, "versions", profile.MinecraftVersion);
        var installed = File.Exists(Path.Combine(versionDir, $"{profile.MinecraftVersion}.json"))
                        && (File.Exists(Path.Combine(versionDir, $"{profile.MinecraftVersion}.jar"))
                            || Directory.Exists(Path.Combine(versionDir, "natives")));

        InstallStatus = installed
            ? $"Версия {profile.MinecraftVersion} установлена"
            : $"Версия {profile.MinecraftVersion} не установлена";
        InstallProgress = installed ? 1 : 0;
        await Task.CompletedTask;
    }

    private void ApplyRoom(RoomSnapshot snapshot)
    {
        IsNetworkConnected = snapshot.State is RoomState.Connected or RoomState.WaitingForPeers;
        NetworkStatus = snapshot.State switch
        {
            RoomState.Offline => "Game Network: не подключено",
            RoomState.Creating => "Game Network: подключение…",
            RoomState.WaitingForPeers => $"Game Network: комната «{snapshot.Room?.Name}» (ожидание игроков)",
            RoomState.Connected => $"Game Network: комната «{snapshot.Room?.Name}» · {snapshot.Peers.Count} участн.",
            RoomState.Reconnecting => "Game Network: переподключение…",
            RoomState.Error => "Game Network: ошибка",
            _ => "Game Network: закрыто"
        };

        NetworkDetail = snapshot.Error
            ?? (snapshot.Transport switch
            {
                TransportKind.Direct => "Прямое соединение · шифрование WireGuard",
                TransportKind.NatTraversal => "Соединение через NAT traversal",
                TransportKind.Relay => "Соединение через relay",
                _ => "Создайте комнату, чтобы играть с друзьями"
            });

        NetworkAddress = snapshot.MinecraftAddress ?? "—";
    }

    private void OnSessionEnded(string profileId, LaunchOutcome outcome)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsGameRunning = false;
            PlayButtonText = "ИГРАТЬ";

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

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (IsGameRunning)
        {
            if (_state.CurrentProfile is { } running)
            {
                await _launcher.StopAsync(running.Id).ConfigureAwait(true);
                _toasts.Success("Игра остановлена", "Minecraft закрыт.");
                IsGameRunning = false;
                PlayButtonText = "ИГРАТЬ";
            }

            return;
        }

        var profile = _state.CurrentProfile;
        if (profile is null)
        {
            _toasts.Warning("Нужен профиль", "Создайте игровой профиль: раздел «Играть».");
            await _navigation.NavigateAsync("play").ConfigureAwait(true);
            return;
        }

        var account = _state.CurrentAccount;
        if (account is null)
        {
            _toasts.Warning("Нужен аккаунт", "Добавьте аккаунт в разделе «Аккаунты».");
            await _navigation.NavigateAsync("accounts").ConfigureAwait(true);
            return;
        }

        IsBusy = true;
        try
        {
            var session = await _sessions.GetSessionAsync(account).ConfigureAwait(true);
            if (!session.IsSuccess)
            {
                _toasts.Error("Нет доступа к аккаунту", session.Message ?? "Войдите заново.");
                await _navigation.NavigateAsync("accounts").ConfigureAwait(true);
                return;
            }

            if (session.Value!.IsOffline)
            {
                _toasts.Warning("Локальный профиль",
                    "Игра запустится в оффлайн-режиме: официальные серверы недоступны, скин не отправляется.");
            }

            var result = await _launcher.LaunchAsync(profile, account, session.Value.AccessToken).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                _toasts.Error("Не удалось запустить", result.Message ?? "Неизвестная ошибка.");
                return;
            }

            IsGameRunning = true;
            PlayButtonText = "ИДЁТ ИГРА";
            _toasts.Success("Minecraft запущен", $"{profile.Name} · {profile.DisplayVersion}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        var profile = _state.CurrentProfile;
        if (profile is null)
        {
            _toasts.Warning("Нужен профиль", "Сначала создайте профиль на странице «Играть».");
            return;
        }

        IsInstalling = true;
        InstallStatus = "Установка…";
        try
        {
            await _navigation.NavigateAsync("versions").ConfigureAwait(true);
            _toasts.Show("Установка", $"Выберите версию {profile.MinecraftVersion} на странице «Версии».");
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private Task OpenNetworkAsync() => _navigation.NavigateAsync("network");

    [RelayCommand]
    private Task OpenContentAsync(string? type) => _navigation.NavigateAsync(type switch
    {
        "mods" => "mods",
        "modpacks" => "modpacks",
        _ => "mods"
    });

    [RelayCommand]
    private async Task CopyNetworkAddressAsync()
    {
        if (string.IsNullOrEmpty(NetworkAddress) || NetworkAddress == "—")
        {
            _toasts.Warning("Адрес недоступен", "Сначала подключитесь к комнате.");
            return;
        }

        _toasts.Success("Адрес скопирован", NetworkAddress);
        await Task.CompletedTask;
    }
}

public sealed class NewsCardViewModel(NewsItem item)
{
    public string Title => item.Title;

    public string Version => item.Version;

    public string Type => item.Type switch
    {
        "snapshot" => "Снапшот",
        "release" => "Релиз",
        _ => item.Type
    };

    public string DateLabel => item.Date.ToLocalTime().ToString("dd.MM.yyyy");

    public string ImageUrl => item.ImageUrl;

    public bool HasImage => !string.IsNullOrEmpty(item.ImageUrl);
}

public sealed class ContentCardViewModel(ContentProject project)
{
    public string Title => project.Title;

    public string Description => project.Description;

    public string? IconUrl => project.IconUrl;

    public string Author => project.Author ?? "—";

    public string DownloadsLabel => project.Downloads switch
    {
        >= 1_000_000 => $"{project.Downloads / 1_000_000.0:0.#} млн",
        >= 1_000 => $"{project.Downloads / 1_000.0:0.#} тыс.",
        _ => project.Downloads.ToString()
    };

    public string CategoriesLabel => string.Join(" · ", project.Categories.Take(2));

    public ContentProject Project => project;
}
