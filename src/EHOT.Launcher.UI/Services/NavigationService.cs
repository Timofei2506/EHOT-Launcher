using EHOT.Launcher.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.UI.Services;

public sealed record NavigationEntry(string Key, string Title, string IconKey, Type ViewModelType, bool IsPrimary = true);

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
        new("home", "Главная", "IconHome", typeof(HomeViewModel)),
        new("play", "Играть", "IconPlay", typeof(PlayViewModel)),
        new("versions", "Версии", "IconLayers", typeof(VersionsViewModel)),
        new("mods", "Моды", "IconPuzzle", typeof(ModsViewModel)),
        new("modpacks", "Сборки", "IconBox", typeof(ModpacksViewModel)),
        new("resourcepacks", "Текстуры", "IconBrush", typeof(ResourcePacksViewModel)),
        new("shaders", "Шейдеры", "IconSparkle", typeof(ShadersViewModel)),
        new("worlds", "Карты", "IconMap", typeof(WorldsViewModel)),
        new("network", "Game Network", "IconNetwork", typeof(GameNetworkViewModel)),
        new("accounts", "Аккаунты", "IconUser", typeof(AccountsViewModel)),
        new("downloads", "Загрузки", "IconDownload", typeof(DownloadsViewModel)),
        new("settings", "Настройки", "IconSettings", typeof(SettingsViewModel)),
        new("about", "О программе", "IconInfo", typeof(AboutViewModel))
    ];

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
