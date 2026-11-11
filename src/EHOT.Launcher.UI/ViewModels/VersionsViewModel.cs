using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Game.Install;
using EHOT.Launcher.Game.Minecraft;
using EHOT.Launcher.Game.Profiles;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

public sealed partial class VersionsViewModel : ViewModelBase
{
    private readonly IMinecraftVersionService _versions;
    private readonly IGameInstaller _installer;
    private readonly ILoaderInstaller _loaders;
    private readonly IProfileManager _profiles;
    private readonly IAppState _state;
    private readonly IToastService _toasts;

    public VersionsViewModel(
        IMinecraftVersionService versions,
        IGameInstaller installer,
        ILoaderInstaller loaders,
        IProfileManager profiles,
        IAppState state,
        IToastService toasts)
    {
        _versions = versions;
        _installer = installer;
        _loaders = loaders;
        _profiles = profiles;
        _state = state;
        _toasts = toasts;
    }

    public ObservableCollection<VersionRowViewModel> Releases { get; } = [];

    public ObservableCollection<VersionRowViewModel> Snapshots { get; } = [];

    [ObservableProperty]
    private VersionRowViewModel? _latestReleaseRow;

    public ObservableCollection<LoaderVersionOption> LoaderVersions { get; } = [];

    public LoaderKind[] Loaders { get; } = [LoaderKind.Vanilla, LoaderKind.Fabric, LoaderKind.NeoForge, LoaderKind.Forge, LoaderKind.Quilt];

    [ObservableProperty]
    private LoaderKind _selectedLoader = LoaderKind.Vanilla;

    [ObservableProperty]
    private LoaderVersionOption? _selectedLoaderVersion;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private int _memoryMb = 4096;

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private string _installStage = string.Empty;

    [ObservableProperty]
    private bool _showSnapshots;

    [ObservableProperty]
    private string _latestRelease = "—";

    [ObservableProperty]
    private string _latestSnapshot = "—";

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        if (IsLoaded)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var manifest = await _versions.GetManifestAsync(forceRefresh: true, ct).ConfigureAwait(true);
            if (!manifest.IsSuccess)
            {
                ErrorMessage = manifest.Message;
                _toasts.Error("Версии недоступны", manifest.Message ?? "Сеть.");
                return;
            }

            Releases.Clear();
            Snapshots.Clear();
            foreach (var version in manifest.Value!.Versions)
            {
                var row = new VersionRowViewModel(version);
                if (version.Type == "release")
                {
                    Releases.Add(row);
                }
                else if (version.Type == "snapshot")
                {
                    Snapshots.Add(row);
                }
            }

            LatestRelease = manifest.Value.Latest?.Release ?? "—";
            LatestSnapshot = manifest.Value.Latest?.Snapshot ?? "—";

            LatestReleaseRow = Releases.FirstOrDefault();
            if (LatestReleaseRow is not null)
            {
                LatestReleaseRow.IsLatest = true;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync(VersionRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var profile = _state.CurrentProfile;
        if (profile is null)
        {
            _toasts.Warning("Нужен профиль", "Сначала создайте профиль в разделе «Играть».");
            return;
        }

        IsBusy = true;
        try
        {
            var root = profile.InstanceDirectory;
            var progress = new Progress<InstallProgress>(p =>
            {
                InstallProgress = p.Fraction;
                InstallStage = p.Stage;
            });

            OperationResult result;
            if (SelectedLoader == LoaderKind.Vanilla)
            {
                var installed = await _installer.InstallAsync(root, row.Id, new InstallOptions(), progress).ConfigureAwait(true);
                result = installed.ToOperationResult();
            }
            else
            {
                var loaderVersion = SelectedLoaderVersion?.Version;
                var installed = await _loaders.InstallLoaderAsync(
                    root, row.Id, SelectedLoader, loaderVersion, progress).ConfigureAwait(true);
                result = installed.ToOperationResult();
            }

            if (!result.IsSuccess)
            {
                _toasts.Error("Установка не удалась", result.Message ?? "Смотрите лог.");
                return;
            }

            // Профиль начинает указывать на установленную версию и лоадер.
            var updated = profile with
            {
                MinecraftVersion = row.Id,
                Loader = SelectedLoader,
                LoaderVersion = SelectedLoader == LoaderKind.Vanilla ? null : SelectedLoaderVersion?.Version
            };
            await _profiles.UpdateAsync(updated).ConfigureAwait(true);
            await _state.RefreshProfilesAsync().ConfigureAwait(true);

            _toasts.Success("Установлено", $"{row.Id} · {SelectedLoader}");
        }
        finally
        {
            IsBusy = false;
            InstallStage = string.Empty;
        }
    }

    [RelayCommand]
    private async Task LoadLoaderVersionsAsync(VersionRowViewModel? row)
    {
        if (row is null || SelectedLoader == LoaderKind.Vanilla)
        {
            return;
        }

        var options = await _loaders.GetLoaderVersionsAsync(SelectedLoader, row.Id).ConfigureAwait(true);
        LoaderVersions.Clear();
        if (!options.IsSuccess)
        {
            _toasts.Warning("Лоадеры недоступны", options.Message ?? "Для этой версии нет сборок.");
            return;
        }

        foreach (var option in options.Value!)
        {
            LoaderVersions.Add(option);
        }

        SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await OnNavigatedToAsync();
}

public sealed class VersionRowViewModel(VersionSummary summary)
{
    public string Id => summary.Id;

    public string Type => summary.Type;

    public string TypeLabel => summary.Type switch
    {
        "release" => "Релиз",
        "snapshot" => "Снапшот",
        "old_beta" => "Бета",
        "old_alpha" => "Альфа",
        _ => summary.Type
    };

    public string DateLabel => summary.ReleaseTime.ToLocalTime().ToString("dd MMM yyyy");

    public string RelativeLabel
    {
        get
        {
            var days = (DateTimeOffset.UtcNow - summary.ReleaseTime).TotalDays;
            return days switch
            {
                < 1 => "сегодня",
                < 30 => $"{(int)days} дн. назад",
                < 365 => $"{(int)(days / 30)} мес. назад",
                _ => $"{(int)(days / 365)} г. назад"
            };
        }
    }

    public bool IsLatest { get; set; }

    public VersionSummary Summary => summary;
}
