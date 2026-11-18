using EHOT.Launcher.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.UI.Services;

/// <summary>
/// Пункт меню. Group — заголовок группы в рельсе (null для пунктов без группы),
/// IsSubItem — пункт второй строки (каталог контента), в рельсе показывается свёрнутым.
/// </summary>
public sealed record NavigationEntry(
    string Key,
    string Title,
    string IconKey,
    Type ViewModelType,
    bool IsPrimary = true,
    string? Group = null,
    bool IsSubItem = false);

/// <summary>
/// Навигация по страницам. Экраны создаются лениво и кэшируются,
/// тяжёлые данные загружаются в OnNavigatedToAsync (никогда в конструкторе).
/// </summary>
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
    private readonly Dictionary<Type, string> _typeToKey = new();

    public static IReadOnlyList<NavigationEntry> DefaultEntries { get; } =
    [
        // Игра: главное, что делает лаунчер.
        new("home", "Главная", "IconHome", typeof(HomeViewModel), Group: "Игра"),
        new("play", "Играть", "IconPlay", typeof(PlayViewModel), Group: "Игра"),
        new("versions", "Версии", "IconLayers", typeof(VersionsViewModel), Group: "Игра"),

        // Каталог контента: пять почти одинаковых страниц не должны занимать весь рельс,
        // поэтому в меню один пункт «Каталог», а разделы открываются вкладками внутри.
        new("mods", "Моды", "IconPuzzle", typeof(ModsViewModel), Group: "Каталог", IsSubItem: true),
        new("modpacks", "Сборки", "IconBox", typeof(ModpacksViewModel), Group: "Каталог", IsSubItem: true),
        new("resourcepacks", "Текстуры", "IconBrush", typeof(ResourcePacksViewModel), Group: "Каталог", IsSubItem: true),
        new("shaders", "Шейдеры", "IconSparkle", typeof(ShadersViewModel), Group: "Каталог", IsSubItem: true),
        new("worlds", "Карты", "IconMap", typeof(WorldsViewModel), Group: "Каталог", IsSubItem: true),

        new("network", "Game Network", "IconNetwork", typeof(GameNetworkViewModel), Group: "Вместе"),
        new("accounts", "Аккаунты", "IconUser", typeof(AccountsViewModel), Group: "Вместе"),

        new("downloads", "Загрузки", "IconDownload", typeof(DownloadsViewModel), Group: "Система"),
        new("settings", "Настройки", "IconSettings", typeof(SettingsViewModel), Group: "Система"),
        new("about", "О программе", "IconInfo", typeof(AboutViewModel), Group: "Система"),

        new("import", "Импорт", "IconImport", typeof(ImportViewModel), Group: "Система")
    ];

    /// <summary>Пункт-контейнер каталога: в рельсе один, ведёт на первую вкладку.</summary>
    public static NavigationEntry CatalogEntry { get; } = new("mods", "Каталог", "IconPuzzle", typeof(ModsViewModel));

    /// <summary>Все подпункты каталога — для выпадающего списка и подсветки активного.</summary>
    public static IReadOnlyList<NavigationEntry> CatalogEntries { get; } =
        DefaultEntries.Where(e => e is { Group: "Каталог", IsSubItem: true }).ToArray();

    public IReadOnlyList<NavigationEntry> Entries => DefaultEntries;

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
        var entry = DefaultEntries.FirstOrDefault(e => e.Key == key);
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
