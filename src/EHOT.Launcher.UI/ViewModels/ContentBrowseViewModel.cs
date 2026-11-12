using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Content.Install;
using EHOT.Launcher.Content.Models;
using EHOT.Launcher.Content.Modpacks;
using EHOT.Launcher.Content.Resolution;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Game.Install;
using EHOT.Launcher.Storage;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>
/// Каталог контента: один класс на все категории (моды, сборки, текстуры, шейдеры, карты)
/// с разным <see cref="ContentType"/> — без дублирования логики.
/// </summary>
public partial class ContentBrowseViewModel : ViewModelBase, INavigationAware
{
    private readonly IContentProviderRegistry _providers;
    private readonly IDependencyResolver _resolver;
    private readonly IContentInstallService _installer;
    private readonly IModpackService _modpacks;
    private readonly IGameInstaller _gameInstaller;
    private readonly IAppState _state;
    private readonly IProfileRepository _profiles;
    private readonly IInstalledContentRepository _installed;
    private readonly IToastService _toasts;
    private int _offset;

    public ContentBrowseViewModel(
        IContentProviderRegistry providers,
        IDependencyResolver resolver,
        IContentInstallService installer,
        IModpackService modpacks,
        IGameInstaller gameInstaller,
        IAppState state,
        IProfileRepository profiles,
        IInstalledContentRepository installed,
        IToastService toasts,
        ContentType type,
        string title,
        string subtitle)
    {
        _providers = providers;
        _resolver = resolver;
        _installer = installer;
        _modpacks = modpacks;
        _gameInstaller = gameInstaller;
        _state = state;
        _profiles = profiles;
        _installed = installed;
        _toasts = toasts;

        Type = type;
        Title = title;
        Subtitle = subtitle;
    }

    public ContentType Type { get; }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>Результаты поиска в виде готовых карточек (см. <see cref="ContentCard"/>).</summary>
    public ObservableCollection<ContentCard> Items { get; } = [];

    public ObservableCollection<string> SortOptions { get; } = ["По релевантности", "По загрузкам", "По подпискам", "Новые", "Обновлённые"];

    public ObservableCollection<ContentVersion> Versions { get; } = [];

    public ObservableCollection<InstalledContent> InstalledItems { get; } = [];

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private int _sortIndex;

    [ObservableProperty]
    private string? _minecraftVersion;

    [ObservableProperty]
    private ContentProject? _selectedProject;

    [ObservableProperty]
    private ContentVersion? _selectedVersion;

    [ObservableProperty]
    private string _providerNotice = string.Empty;

    [ObservableProperty]
    private bool _hasProviderNotice;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private string _installStage = string.Empty;

    [ObservableProperty]
    private int _totalHits;

    [ObservableProperty]
    private bool _showInstalled;

    public string EmptyStateTitle => Items.Count == 0 && !IsBusy
        ? "Ничего не найдено"
        : string.Empty;

    public static string EmptyStateHint => "Измените запрос, версию игры или фильтр сортировки.";

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken ct = default)
    {
        if (parameter is string search && !string.IsNullOrWhiteSpace(search))
        {
            Query = search;
        }

        var cf = _providers.Get("curseforge");
        if (cf is { IsAvailable: false })
        {
            HasProviderNotice = true;
            ProviderNotice = cf.UnavailableReason ?? "CurseForge недоступен.";
        }

        await SearchAsync(reset: true, ct).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SearchAsync(bool reset = true, CancellationToken ct = default)
    {
        if (reset)
        {
            _offset = 0;
            Items.Clear();
        }

        IsBusy = true;
        try
        {
            var sort = SortIndex switch
            {
                1 => ContentSort.Downloads,
                2 => ContentSort.Follows,
                3 => ContentSort.Newest,
                4 => ContentSort.Updated,
                _ => ContentSort.Relevance
            };

            var page = await _providers.Primary.SearchAsync(new ContentSearchQuery
            {
                Text = string.IsNullOrWhiteSpace(Query) ? null : Query.Trim(),
                Type = Type,
                Sort = sort,
                MinecraftVersion = MinecraftVersion ?? _state.CurrentProfile?.MinecraftVersion,
                Offset = _offset,
                Limit = 24
            }, ct).ConfigureAwait(true);

            foreach (var item in page.Items)
            {
                Items.Add(new ContentCard(item));
            }

            TotalHits = page.TotalHits;
            _offset += page.Items.Count;
        }
        catch (Exception ex)
        {
            ErrorMessage = "Каталог недоступен: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (Items.Count >= TotalHits)
        {
            return;
        }

        await SearchAsync(reset: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenProjectAsync(ContentProject project)
    {
        SelectedProject = project;
        Versions.Clear();
        IsBusy = true;
        try
        {
            var versions = await _providers.Primary.GetVersionsAsync(
                project.Id,
                MinecraftVersion ?? _state.CurrentProfile?.MinecraftVersion,
                _state.CurrentProfile?.Loader,
                CancellationToken.None).ConfigureAwait(true);

            foreach (var version in versions)
            {
                Versions.Add(version);
            }

            SelectedVersion = Versions.FirstOrDefault();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        var project = SelectedProject;
        var version = SelectedVersion;
        var profile = _state.CurrentProfile;

        if (project is null || version is null)
        {
            return;
        }

        if (profile is null)
        {
            _toasts.Warning("Нужен профиль", "Создайте игровой профиль в разделе «Играть».");
            return;
        }

        IsInstalling = true;
        InstallStage = "Проверка совместимости";
        try
        {
            // Проверяем совместимость и зависимости именно для этого профиля.
            var resolved = await _resolver.ResolveAsync(
                _providers.Primary, version, profile.MinecraftVersion, profile.Loader).ConfigureAwait(true);

            if (!resolved.IsSuccess)
            {
                var message = resolved.Issues.Count > 0 ? resolved.Issues[0].Message : "Версия несовместима с профилем.";
                _toasts.Warning("Несовместимо", message);
                return;
            }

            foreach (var dep in resolved.Versions)
            {
                InstallStage = $"Установка {dep.ProjectTitle}";
                var progress = new Progress<ContentInstallProgress>(p =>
                {
                    InstallProgress = p.Fraction;
                    InstallStage = p.Stage;
                });

                var result = await _installer.InstallAsync(dep, Type, profile.InstanceDirectory, profile.Id, false, progress).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    _toasts.Error("Установка прервана", result.Message ?? "Ошибка загрузки.");
                    return;
                }
            }

            _toasts.Success("Установлено", $"{project.Title} · {version.VersionNumber} ({resolved.Versions.Count} файл(ов))");
            await LoadInstalledAsync(profile).ConfigureAwait(true);
        }
        finally
        {
            IsInstalling = false;
            InstallStage = string.Empty;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(InstalledContent item)
    {
        var profile = _state.CurrentProfile;
        if (profile is null)
        {
            return;
        }

        var result = await _installer.RemoveAsync(item, profile.InstanceDirectory).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            _toasts.Show("Удалено", item.ProjectTitle, ToastKind.Info);
            await LoadInstalledAsync(profile).ConfigureAwait(true);
        }
        else
        {
            _toasts.Error("Не удалось удалить", result.Message ?? "Ошибка.");
        }
    }

    [RelayCommand]
    private async Task ToggleInstalledAsync()
    {
        ShowInstalled = !ShowInstalled;
        if (_state.CurrentProfile is { } profile)
        {
            await LoadInstalledAsync(profile).ConfigureAwait(true);
        }
    }

    private async Task LoadInstalledAsync(GameProfile profile)
    {
        var items = await _installed.GetForProfileAsync(profile.Id).ConfigureAwait(true);
        InstalledItems.Clear();
        foreach (var item in items.Where(i => i.Type == Type))
        {
            InstalledItems.Add(item);
        }
    }
}

/// <summary>
/// Карточка каталога для разметки: исходный проект плюс готовые к показу поля
/// (число загрузок, инициал, ссылка на иконку). Логика не дублируется в XAML.
/// </summary>
public sealed class ContentCard(ContentProject project)
{
    public ContentProject Project { get; } = project;

    public string Title => Project.Title;

    public string Author => string.IsNullOrWhiteSpace(Project.Author) ? "автор не указан" : Project.Author!;

    public string Description => Project.Description;

    public string? IconUrl => Project.IconUrl;

    public bool HasIcon => !string.IsNullOrWhiteSpace(Project.IconUrl);

    public string Initial => string.IsNullOrWhiteSpace(Project.Title) ? "?" : Project.Title[..1].ToUpperInvariant();

    public string DownloadsLabel => FormatCount(Project.Downloads);

    /// <summary>Тип проекта по-русски — чтобы в карточке было видно, мод это или сборка.</summary>
    public string TypeLabel => Project.Type switch
    {
        ContentType.Mod => "мод",
        ContentType.Modpack => "сборка",
        ContentType.ResourcePack => "текстуры",
        ContentType.Shader => "шейдер",
        ContentType.World => "карта",
        _ => "проект"
    };

    public string? CategoriesLabel => Project.Categories.Count > 0
        ? string.Join(" · ", Project.Categories.Take(2))
        : null;

    public string? UpdatedLabel => Project.UpdatedAt is { } updated
        ? "обновлён " + updated.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
        : null;

    public bool HasUpdated => Project.UpdatedAt is not null;

    /// <summary>12 345 678 → «12,3 млн»; 1 234 → «1,2 тыс.»</summary>
    internal static string FormatCount(long value) => value switch
    {
        >= 1_000_000 => $"{(value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture)} млн",
        >= 1_000 => $"{(value / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture)} тыс.",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };
}

public sealed class ModsViewModel : ContentBrowseViewModel
{
    public ModsViewModel(
        IContentProviderRegistry providers, IDependencyResolver resolver, IContentInstallService installer,
        IModpackService modpacks, IGameInstaller gameInstaller, IAppState state, IProfileRepository profiles,
        IInstalledContentRepository installed, IToastService toasts)
        : base(providers, resolver, installer, modpacks, gameInstaller, state, profiles, installed, toasts,
            ContentType.Mod, "Моды", "Моды с Modrinth: фильтры по версии, загрузчику, совместимости")
    {
    }
}

public sealed class ModpacksViewModel : ContentBrowseViewModel
{
    public ModpacksViewModel(
        IContentProviderRegistry providers, IDependencyResolver resolver, IContentInstallService installer,
        IModpackService modpacks, IGameInstaller gameInstaller, IAppState state, IProfileRepository profiles,
        IInstalledContentRepository installed, IToastService toasts)
        : base(providers, resolver, installer, modpacks, gameInstaller, state, profiles, installed, toasts,
            ContentType.Modpack, "Сборки", "Готовые сборки: установка в изолированный профиль")
    {
    }
}

public sealed class ResourcePacksViewModel : ContentBrowseViewModel
{
    public ResourcePacksViewModel(
        IContentProviderRegistry providers, IDependencyResolver resolver, IContentInstallService installer,
        IModpackService modpacks, IGameInstaller gameInstaller, IAppState state, IProfileRepository profiles,
        IInstalledContentRepository installed, IToastService toasts)
        : base(providers, resolver, installer, modpacks, gameInstaller, state, profiles, installed, toasts,
            ContentType.ResourcePack, "Текстуры", "Ресурс-паки: установка в resourcepacks выбранного профиля")
    {
    }
}

public sealed class ShadersViewModel : ContentBrowseViewModel
{
    public ShadersViewModel(
        IContentProviderRegistry providers, IDependencyResolver resolver, IContentInstallService installer,
        IModpackService modpacks, IGameInstaller gameInstaller, IAppState state, IProfileRepository profiles,
        IInstalledContentRepository installed, IToastService toasts)
        : base(providers, resolver, installer, modpacks, gameInstaller, state, profiles, installed, toasts,
            ContentType.Shader, "Шейдеры", "Шейдеры: требуют Iris/OptiFine в профиле")
    {
    }
}

public sealed class WorldsViewModel : ContentBrowseViewModel
{
    public WorldsViewModel(
        IContentProviderRegistry providers, IDependencyResolver resolver, IContentInstallService installer,
        IModpackService modpacks, IGameInstaller gameInstaller, IAppState state, IProfileRepository profiles,
        IInstalledContentRepository installed, IToastService toasts)
        : base(providers, resolver, installer, modpacks, gameInstaller, state, profiles, installed, toasts,
            ContentType.World, "Карты", "Карты и датапаки: распаковка в папку saves профиля")
    {
    }
}
