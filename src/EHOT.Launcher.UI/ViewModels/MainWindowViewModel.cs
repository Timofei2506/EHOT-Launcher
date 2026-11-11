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

    public MainWindowViewModel(
        INavigationService navigation,
        IAppState state,
        IToastService toasts,
        ILogService log,
        IPlatformInfo platform,
        ICapabilityRegistry capabilities,
        UpdateViewModel updates)
    {
        _navigation = navigation;
        _state = state;
        _toasts = toasts;
        _log = log;
        _platform = platform;
        _capabilities = capabilities;
        Updates = updates;

        NavigationItems = new ObservableCollection<NavigationItemViewModel>(
            _navigation.Entries.Select(e => new NavigationItemViewModel(e, () => _ = NavigateAsync(e.Key))));

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
    private string? _searchText;

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private string _platformLabel = string.Empty;

    [ObservableProperty]
    private string _networkIndicator = "Game Network: не подключено";

    public static bool CanClose => true;

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

    [RelayCommand]
    private async Task NavigateAsync(string key)
    {
        foreach (var item in NavigationItems)
        {
            item.IsSelected = string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase);
        }

        await _navigation.NavigateAsync(key).ConfigureAwait(true);
    }

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

public sealed partial class NavigationItemViewModel(NavigationEntry entry, Action onSelected) : ObservableObject
{
    public string Key => entry.Key;

    public string Title => entry.Title;

    public string IconKey => entry.IconKey;

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select()
    {
        IsSelected = true;
        onSelected();
    }
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
