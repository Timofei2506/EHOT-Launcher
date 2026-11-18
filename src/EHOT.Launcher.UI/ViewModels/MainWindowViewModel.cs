using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Оболочка: навигация, глобальный поиск, тосты, заголовок окна.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly IAppState _state;
    private readonly IToastService _toasts;
    private readonly ILogService _log;
    private readonly IPlatformInfo _platform;
    private readonly ICapabilityRegistry _capabilities;
    private readonly Core.Downloads.IDownloadManager _downloads;

    public MainWindowViewModel(
        INavigationService navigation,
        IAppState state,
        IToastService toasts,
        ILogService log,
        IPlatformInfo platform,
        ICapabilityRegistry capabilities,
        UpdateViewModel updates,
        Core.Downloads.IDownloadManager downloads)
    {
        _navigation = navigation;
        _state = state;
        _toasts = toasts;
        _log = log;
        _platform = platform;
        _capabilities = capabilities;
        _downloads = downloads;
        Updates = updates;

        // Рельс: 9 пунктов в 4 группах вместо 13 подряд. Каталог — один пункт с вкладками.
        var entries = _navigation.Entries.Where(e => !e.IsSubItem).Append(NavigationService.CatalogEntry);
        NavigationItems = new ObservableCollection<NavigationItemViewModel>(
            entries.Select(e =>
            {
                var item = new NavigationItemViewModel(
                    e,
                    () => _ = NavigateAsync(e.Key),
                    e.Key == "mods" ? CatalogEntries : []);
                // Подпункт открывается через то же окно навигации, что и обычный пункт.
                item.OpenRequested = key => _ = NavigateAsync(key);
                return item;
            }));

        // Заголовок группы рисуется один раз — у первого пункта этой группы.
        foreach (var group in NavigationItems.GroupBy(i => i.Group))
        {
            var first = true;
            foreach (var item in group)
            {
                item.IsGroupStart = first && group.Key is not null;
                first = false;
            }
        }

        NavigationItems[0].IsSelected = true;
        _navigation.Navigated += vm => CurrentPage = vm;
        Toasts = _toasts.Items;

        _state.Changed += OnStateChanged;
        _ = InitializeAsync();
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public ObservableCollection<Toast> Toasts { get; }

    [ObservableProperty]
    private ViewModelBase? _currentPage;

    [ObservableProperty]
    private string _accountName = "Локальный профиль";

    [ObservableProperty]
    private string _accountKindLabel = "Локальный";

    [ObservableProperty]
    private string _profileName = "Профиль не выбран";

    [ObservableProperty]
    private string _profileVersion = "—";

    [ObservableProperty]
    private string _accountInitials = "?";

    [ObservableProperty]
    private string _accountUuid = "—";

    [ObservableProperty]
    private string _accountUuidSource = "—";

    [ObservableProperty]
    private string? _accountSkinPath;

    [ObservableProperty]
    private string _accountSkinLabel = "Скин не выбран";

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private string _platformLabel = string.Empty;

    [ObservableProperty]
    private string _networkIndicator = "Game Network: не подключено";

    public static bool CanClose => true;

    /// <summary>Правая панель показывает данные аккаунта только когда он выбран.</summary>
    public bool IsAccountSelected => AccountUuid != "—";

    public ObservableCollection<CapabilityRow> CapabilityRows { get; } = [];

    /// <summary>Баннер автообновления в шапке.</summary>
    public UpdateViewModel Updates { get; }

    private async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            await _state.InitializeAsync().ConfigureAwait(true);
            ApplyState();
            await _navigation.NavigateAsync("home").ConfigureAwait(true);

            // Проверка обновлений — после того как окно уже показано, чтобы старт был мгновенным.
            // Если пользователь выключил авто-проверку в настройках, в сеть не ходим.
            if (_state.Settings.CheckLauncherUpdates && Updates.IsSupported)
            {
                await Updates.CheckCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _log.Error("UI", "Ошибка старта лаунчера", ex);
            ErrorMessage = "Не удалось загрузить настройки: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnStateChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyState();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplyState);
        }
    }

    private void ApplyState()
    {
        var account = _state.CurrentAccount;
        AccountName = account?.Username ?? "Без аккаунта";
        AccountKindLabel = account switch
        {
            { Kind: AccountKind.Microsoft } => "Microsoft · лицензия",
            { Kind: AccountKind.Local } => "Локальный (оффлайн)",
            _ => "Не выбран"
        };

        AccountInitials = string.IsNullOrEmpty(account?.Username)
            ? "?"
            : account!.Username[..1].ToUpperInvariant();

        // Карточка «как я захожу в игру»: ник, откуда UUID, есть ли скин.
        AccountUuid = account?.Uuid.ToString("D") ?? "—";
        OnPropertyChanged(nameof(IsAccountSelected));
        AccountUuidSource = account switch
        {
            null => "—",
            { UuidFromOfficialProfile: true } => "UUID официального игрока",
            _ => "UUID оффлайн (online-mode сервер не пустит)"
        };
        AccountSkinPath = account?.LocalSkinPath;
        AccountSkinLabel = account switch
        {
            null => "Скин не выбран",
            { SkinSource: "mojang" } => "Скин с официального аккаунта",
            { SkinSource: "local" } => "Свой скин (виден только вам)",
            _ => "Скин не задан — стандартный персонаж"
        };

        var profile = _state.CurrentProfile;
        ProfileName = profile?.Name ?? "Профиль не выбран";
        ProfileVersion = profile?.DisplayVersion ?? "—";

        PlatformLabel = $"{_platform.OsName} {_platform.Architecture}";
        NetworkIndicator = _capabilities.Get(CapabilityIds.GameNetworkTunnel).State == CapabilityState.Available
            ? "Game Network: готово"
            : "Game Network: ограничено";

        CapabilityRows.Clear();
        foreach (var capability in _capabilities.All)
        {
            CapabilityRows.Add(new CapabilityRow(capability));
        }
    }

    /// <summary>Подпункты каталога: моды, сборки, текстуры, шейдеры, карты.</summary>
    public IReadOnlyList<NavigationEntry> CatalogEntries { get; } = NavigationService.CatalogEntries;

    [RelayCommand]
    private async Task NavigateAsync(string key)
    {
        // Подпункт каталога подсвечивает пункт «Каталог» и запоминает вкладку.
        var isCatalog = CatalogEntries.Any(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
        foreach (var item in NavigationItems)
        {
            item.IsSelected = isCatalog
                ? item.Key == "mods"
                : string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase);

            if (isCatalog && item.Key == "mods")
            {
                item.SubTitle = CatalogEntries.First(e => e.Key == key).Title;
            }
        }

        await _navigation.NavigateAsync(key).ConfigureAwait(true);
    }

    /// <summary>
    /// Вызывается при закрытии окна: останавливает загрузки, не удаляя недокачанные файлы,
    /// чтобы следующий запуск продолжил установку, а не начал её заново.
    /// </summary>
    public void PrepareForExit() => _downloads.ShutdownForExit();

    [RelayCommand]
    private void ToggleSearch() => IsSearchOpen = !IsSearchOpen;

    [RelayCommand]
    private async Task SubmitSearchAsync()
    {
        var query = SearchText?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        IsSearchOpen = false;
        await _navigation.NavigateAsync("mods", query).ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissToast(Toast toast) => Toasts.Remove(toast);

    [RelayCommand]
    private void OpenProfile()
    {
        _ = NavigateAsync("accounts");
    }

    [RelayCommand]
    private void OpenNotifications()
    {
        _toasts.Show("Уведомления", "Новых системных уведомлений нет.", ToastKind.Info);
    }
}

public sealed partial class NavigationItemViewModel : ObservableObject
{
    private readonly Action _onSelected;

    public NavigationItemViewModel(NavigationEntry entry, Action onSelected, IReadOnlyList<NavigationEntry> subItems)
    {
        Entry = entry;
        _onSelected = onSelected;
        SubItems = subItems;
        _subTitle = entry.Title;
        IsGroupStart = entry.Group is not null;
    }

    /// <summary>Первый пункт группы — у него рисуется заголовок группы.</summary>
    public bool IsGroupStart { get; set; }

    public NavigationEntry Entry { get; }

    public string Key => Entry.Key;

    public string Title => Entry.Title;

    public string IconKey => Entry.IconKey;

    /// <summary>Заголовок группы; показывается только у первого пункта группы.</summary>
    public string? Group => Entry.Group;

    /// <summary>Подпункты (только у «Каталога»): моды, сборки, текстуры, шейдеры, карты.</summary>
    public IReadOnlyList<NavigationEntry> SubItems { get; }

    public bool HasSubItems => SubItems.Count > 0;

    /// <summary>Что открыто внутри пункта: «Каталог · Моды».</summary>
    [ObservableProperty]
    private string _subTitle;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Раскрыт ли список подпунктов (только для каталога).</summary>
    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void Select()
    {
        IsSelected = true;
        if (HasSubItems)
        {
            IsExpanded = !IsExpanded;
        }

        _onSelected();
    }

    [RelayCommand]
    private void OpenSubItem(NavigationEntry? sub)
    {
        if (sub is null)
        {
            return;
        }

        SubTitle = sub.Title;
        IsExpanded = false;
        _onSelected?.Invoke();
        OpenRequested?.Invoke(sub.Key);
    }

    /// <summary>Навигация по подпункту: обрабатывается окном, чтобы не плодить зависимости.</summary>
    public Action<string>? OpenRequested { get; set; }
}

/// <summary>Строка панели «Состояние систем»: возможность платформы и её честный статус.</summary>
public sealed class CapabilityRow(Capability capability)
{
    public string Title { get; } = capability.Title;

    public string StateLabel { get; } = capability.State switch
    {
        CapabilityState.Available => "доступно",
        CapabilityState.Degraded => "ограничено",
        _ => "недоступно"
    };

    public string Detail { get; } = capability.Detail ?? string.Empty;

    public IBrush Brush { get; } = capability.State switch
    {
        CapabilityState.Available => Brushes.Success,
        CapabilityState.Degraded => Brushes.Warning,
        _ => Brushes.Danger
    };

    private static class Brushes
    {
        public static readonly IBrush Success = new SolidColorBrush(Color.Parse("#34D399"));
        public static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#FBBF24"));
        public static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#F87171"));
    }
}
