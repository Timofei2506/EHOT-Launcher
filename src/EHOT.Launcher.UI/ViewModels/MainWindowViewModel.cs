using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Оболочка: навигация, обновление лаунчера, тосты, шапка окна.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly IAppState _state;
    private readonly IToastService _toasts;
    private readonly ILogService _log;
    private readonly Core.Downloads.IDownloadManager _downloads;

    public MainWindowViewModel(
        INavigationService navigation,
        IAppState state,
        IToastService toasts,
        ILogService log,
        UpdateViewModel updates,
        Core.Downloads.IDownloadManager downloads)
    {
        _navigation = navigation;
        _state = state;
        _toasts = toasts;
        _log = log;
        _downloads = downloads;
        Updates = updates;

        NavigationItems = new ObservableCollection<NavigationItemViewModel>(
            _navigation.Entries.Select(e => new NavigationItemViewModel(e, () => _ = NavigateAsync(e.Key))));

        if (NavigationItems.Count > 0)
        {
            NavigationItems[0].IsSelected = true;
        }

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
    private string _accountInitials = "?";

    [ObservableProperty]
    private string? _accountSkinPath;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private bool _isSearchOpen;

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

            // Проверка обновлений — после первого показа окна, чтобы старт был мгновенным.
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
        AccountSkinPath = account?.LocalSkinPath;
    }

    [RelayCommand]
    private async Task NavigateAsync(string key)
    {
        foreach (var item in NavigationItems)
        {
            item.IsSelected = string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase);
        }

        await _navigation.NavigateAsync(key).ConfigureAwait(true);

        // Аккаунт не пункт рельса: подсветку не переносим, текущий раздел остаётся как есть.
        if (string.Equals(key, "accounts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "downloads", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "import", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "network", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in NavigationItems)
            {
                item.IsSelected = false;
            }
        }
    }

    /// <summary>Останавливает загрузки при закрытии, не удаляя недокачанные файлы.</summary>
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
        await _navigation.NavigateAsync("catalog", query).ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissToast(Toast toast) => Toasts.Remove(toast);

    [RelayCommand]
    private void OpenProfile() => _ = NavigateAsync("accounts");

    [RelayCommand]
    private void OpenNotifications()
        => _toasts.Show("Уведомления", "Новых системных уведомлений нет.", ToastKind.Info);

    [RelayCommand]
    private void OpenDownloads() => _ = NavigateAsync("downloads");
}

public sealed partial class NavigationItemViewModel(NavigationEntry entry, Action onSelected) : ObservableObject
{
    public NavigationEntry Entry { get; } = entry;

    public string Key => Entry.Key;

    public string Title => Entry.Title;

    public string IconKey => Entry.IconKey;

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select()
    {
        IsSelected = true;
        onSelected();
    }
}
