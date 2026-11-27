using EHOT.Launcher.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.UI.Services;

/// <summary>
/// Пункт навигации. IsPrimary=false — скрытый раздел: его можно открыть командой,
/// но он не занимает место в боковой панели.
/// </summary>
public sealed record NavigationEntry(
    string Key,
    string Title,
    string IconKey,
    Type ViewModelType,
    bool IsPrimary = true,
    string? Group = null,
    bool IsSubItem = false);

/// <summary>Навигация по страницам. Экраны создаются лениво и кэшируются.</summary>
public interface INavigationService
{
    ViewModelBase? Current { get; }

    NavigationEntry? CurrentEntry { get; }

    IReadOnlyList<NavigationEntry> Entries { get; }

    event Action<ViewModelBase>? Navigated;

    Task NavigateAsync(string key, object? parameter = null, CancellationToken ct = default);

    Task NavigateAsync<TViewModel>(object? parameter = null, CancellationToken ct = default)
        where TViewModel : ViewModelBase;
}

public sealed class NavigationService(IServiceProvider services, ILogger<NavigationService> logger) : INavigationService
{
    private readonly Dictionary<string, ViewModelBase> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Рельс концепта: только реальные разделы, без дублирования и «beta»-пунктов.</summary>
    public static IReadOnlyList<NavigationEntry> DefaultEntries { get; } =
    [
        new("home", "Играть", "IconPlay", typeof(HomeViewModel)),
        new("catalog", "Моды и ресурспаки", "IconPuzzle", typeof(CatalogViewModel)),
        new("gallery", "Галерея", "IconGallery", typeof(GalleryViewModel)),
        new("news", "Новости", "IconNews", typeof(NewsViewModel)),
        new("servers", "Серверы", "IconServer", typeof(ServersViewModel)),
        new("builds", "Сборки", "IconBox", typeof(BuildsViewModel)),
        new("settings", "Настройки", "IconSettings", typeof(SettingsViewModel)),

        // Скрытые, но рабочие разделы: открываются из настроек/аккаунта/обновления.
        new("accounts", "Аккаунт", "IconUser", typeof(AccountsViewModel), IsPrimary: false),
        new("downloads", "Загрузки", "IconDownload", typeof(DownloadsViewModel), IsPrimary: false),
        new("network", "Game Network", "IconNetwork", typeof(GameNetworkViewModel), IsPrimary: false),
        new("import", "Импорт", "IconImport", typeof(ImportViewModel), IsPrimary: false)
    ];

    public IReadOnlyList<NavigationEntry> Entries { get; } = DefaultEntries.Where(e => e.IsPrimary).ToArray();

    public ViewModelBase? Current { get; private set; }

    public NavigationEntry? CurrentEntry { get; private set; }

    public event Action<ViewModelBase>? Navigated;

    public Task NavigateAsync<TViewModel>(object? parameter = null, CancellationToken ct = default)
        where TViewModel : ViewModelBase
    {
        var entry = DefaultEntries.FirstOrDefault(e => e.ViewModelType == typeof(TViewModel));
        return entry is null
            ? NavigateCoreAsync(typeof(TViewModel), null, parameter, ct)
            : NavigateCoreAsync(typeof(TViewModel), entry, parameter, ct);
    }

    public Task NavigateAsync(string key, object? parameter = null, CancellationToken ct = default)
    {
        var entry = DefaultEntries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            logger.LogWarning("Неизвестный раздел навигации: {Key}", key);
            return Task.CompletedTask;
        }

        return NavigateCoreAsync(entry.ViewModelType, entry, parameter, ct);
    }

    private async Task NavigateCoreAsync(Type viewModelType, NavigationEntry? entry, object? parameter, CancellationToken ct)
    {
        if (Current is not null && Current.GetType() == viewModelType && parameter is null)
        {
            return;
        }

        if (Current is not null)
        {
            await Current.OnNavigatedFromAsync().ConfigureAwait(true);
        }

        if (!_cache.TryGetValue(viewModelType.FullName!, out var vm))
        {
            vm = (ViewModelBase)ActivatorUtilities.CreateInstance(services, viewModelType);
            _cache[viewModelType.FullName!] = vm;
            logger.LogDebug("Создана ViewModel {Type}", viewModelType.Name);
        }

        Current = vm;
        CurrentEntry = entry ?? DefaultEntries.FirstOrDefault(e => e.ViewModelType == viewModelType);

        if (vm is INavigationAware aware)
        {
            await aware.OnNavigatedToAsync(parameter, ct).ConfigureAwait(true);
        }
        else if (!vm.IsLoaded)
        {
            await vm.OnNavigatedToAsync(ct).ConfigureAwait(true);
            vm.IsLoaded = true;
        }

        Navigated?.Invoke(vm);
    }
}
