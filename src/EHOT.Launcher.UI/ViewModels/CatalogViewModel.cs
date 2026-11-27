using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Единый раздел каталога: моды, ресурспаки, шейдеры и датапаки на одной странице.</summary>
public sealed partial class CatalogViewModel : ViewModelBase, INavigationAware
{
    private readonly ModsViewModel _mods;
    private readonly ModpacksViewModel _modpacks;
    private readonly ResourcePacksViewModel _resourcePacks;
    private readonly ShadersViewModel _shaders;
    private readonly WorldsViewModel _dataPacks;

    public CatalogViewModel(
        ModsViewModel mods,
        ModpacksViewModel modpacks,
        ResourcePacksViewModel resourcePacks,
        ShadersViewModel shaders,
        WorldsViewModel dataPacks)
    {
        _mods = mods;
        _modpacks = modpacks;
        _resourcePacks = resourcePacks;
        _shaders = shaders;
        _dataPacks = dataPacks;
        _currentSection = _mods;
    }

    [ObservableProperty]
    private ContentBrowseViewModel _currentSection;

    [ObservableProperty]
    private string _selectedSection = "mods";

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken ct = default)
    {
        var query = parameter as string;
        if (SelectedSection != "mods")
        {
            await SelectSectionAsync("mods", query, ct).ConfigureAwait(true);
            return;
        }

        await _mods.OnNavigatedToAsync(query, ct).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task ShowModsAsync() => SelectSectionAsync("mods", null, CancellationToken.None);

    [RelayCommand]
    private Task ShowModpacksAsync() => SelectSectionAsync("modpacks", null, CancellationToken.None);

    [RelayCommand]
    private Task ShowResourcePacksAsync() => SelectSectionAsync("resourcepacks", null, CancellationToken.None);

    [RelayCommand]
    private Task ShowShadersAsync() => SelectSectionAsync("shaders", null, CancellationToken.None);

    [RelayCommand]
    private Task ShowDataPacksAsync() => SelectSectionAsync("datapacks", null, CancellationToken.None);

    public async Task SelectSectionAsync(string section, object? parameter, CancellationToken ct)
    {
        ContentBrowseViewModel next = section switch
        {
            "modpacks" => _modpacks,
            "resourcepacks" => _resourcePacks,
            "shaders" => _shaders,
            "datapacks" => _dataPacks,
            _ => _mods
        };

        SelectedSection = section switch
        {
            "modpacks" => "modpacks",
            "resourcepacks" => "resourcepacks",
            "shaders" => "shaders",
            "datapacks" => "datapacks",
            _ => "mods"
        };
        CurrentSection = next;
        await next.OnNavigatedToAsync(parameter, ct).ConfigureAwait(true);
    }
}
