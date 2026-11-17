using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Game.Install;
using EHOT.Launcher.Game.Minecraft;
using EHOT.Launcher.Game.Launch;
using EHOT.Launcher.Game.Profiles;
using EHOT.Launcher.Auth;
using EHOT.Launcher.Storage;
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
    private readonly IAccountRepository _accounts;
    private readonly IAccountSessionProvider _sessions;
    private readonly IGameLauncher _launcher;
    private readonly Core.Platform.IAppPaths _paths;
    private readonly IInstallResumeStore _resume;
    private readonly OptiFineInstaller _optifine;

    public VersionsViewModel(
        IMinecraftVersionService versions,
        IGameInstaller installer,
        ILoaderInstaller loaders,
        IProfileManager profiles,
        IAppState state,
        IToastService toasts,
        IAccountRepository accounts,
        IAccountSessionProvider sessions,
        IGameLauncher launcher,
        Core.Platform.IAppPaths paths,
        IInstallResumeStore resume,
        OptiFineInstaller optifine)
    {
        _versions = versions;
        _installer = installer;
        _loaders = loaders;
        _profiles = profiles;
        _state = state;
        _toasts = toasts;
        _accounts = accounts;
        _sessions = sessions;
        _launcher = launcher;
        _paths = paths;
        _resume = resume;
        _optifine = optifine;
    }

    /// <summary>
    /// Установленная версия — это самодостаточная единица: папка в versions/ плюс общие
    /// libraries/assets. Профиль для установки больше не нужен, он создаётся автоматически
    /// и только тогда, когда версию нужно запустить.
    /// </summary>
    public sealed record InstalledVersion(string Id, string Type, string Label, string LoaderLabel);

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

    /// <summary>Каталог, куда ставим версии (общий для всех профилей).</summary>
    public string VersionsRoot => _state.MinecraftRoot;

    public ObservableCollection<InstalledVersion> InstalledVersions { get; } = [];

    [ObservableProperty]
    private InstalledVersion? _selectedInstalled;

    [ObservableProperty]
    private bool _hasInstalledVersions;

    // --- OptiFine ---------------------------------------------------------
    public ObservableCollection<OptiFineVersion> OptiFineVersions { get; } = [];

    [ObservableProperty]
    private OptiFineVersion? _selectedOptiFine;

    [ObservableProperty]
    private bool _isOptiFineBusy;

    [ObservableProperty]
    private string _optiFineStatus = string.Empty;

    [ObservableProperty]
    private bool _hasOptiFineVersions;

    /// <summary>Установка, прерванная закрытием лаунчера: предлагаем продолжить.</summary>
    [ObservableProperty]
    private PendingInstall? _pendingInstall;

    [ObservableProperty]
    private bool _hasPendingInstall;

    [ObservableProperty]
    private string _pendingInstallText = string.Empty;

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

            await LoadInstalledAsync().ConfigureAwait(true);
            await LoadPendingAsync().ConfigureAwait(true);

            await LoadOptiFineAsync().ConfigureAwait(true);

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

        // Профиль для установки не нужен: файлы версии общие для всего лаунчера.
        IsBusy = true;
        // Отмечаем установку как незавершённую: если лаунчер закроют, при следующем
        // запуске предложим продолжить, а не начинать заново.
        await _resume.SaveAsync(new PendingInstall(
            row.Id,
            SelectedLoader,
            SelectedLoader == LoaderKind.Vanilla ? null : SelectedLoaderVersion?.Version,
            VersionsRoot,
            DateTimeOffset.UtcNow)).ConfigureAwait(true);

        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                InstallProgress = p.Fraction;
                InstallStage = p.Stage;
            });

            var root = VersionsRoot;
            OperationResult result;
            string? installedVersionId = null;

            if (SelectedLoader == LoaderKind.Vanilla)
            {
                if (await _installer.IsInstalledAsync(root, row.Id).ConfigureAwait(true))
                {
                    _toasts.Show("Уже установлено", $"{row.Id} есть на диске — запускать можно сразу.", ToastKind.Info);
                    await LoadInstalledAsync().ConfigureAwait(true);
                    return;
                }

                var installed = await _installer.InstallAsync(root, row.Id, new InstallOptions(), progress).ConfigureAwait(true);
                result = installed.ToOperationResult();
                installedVersionId = row.Id;
            }
            else
            {
                var loaderVersion = SelectedLoaderVersion?.Version;
                var installed = await _loaders.InstallLoaderAsync(
                    root, row.Id, SelectedLoader, loaderVersion, progress).ConfigureAwait(true);
                result = installed.ToOperationResult();
                if (result.IsSuccess)
                {
                    installedVersionId = installed.Value;
                }
            }

            if (!result.IsSuccess)
            {
                _toasts.Error("Установка не удалась", result.Message ?? "Смотрите лог.");
                return;
            }

            InstallProgress = 1;
            InstallStage = "Установлено";
            await _resume.ClearAsync().ConfigureAwait(true);
            HasPendingInstall = false;
            PendingInstall = null;
            await LoadInstalledAsync().ConfigureAwait(true);
            SelectedInstalled = InstalledVersions.FirstOrDefault(v => v.Id == installedVersionId);

            _toasts.Success("Установлено", $"{row.Id} · {SelectedLoader}. Профиль создастся автоматически при запуске.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Список версий OptiFine с зеркала. Честно сообщаем, если список недоступен:
    /// без него кнопка установки не должна выглядеть рабочей.
    /// </summary>
    private async Task LoadOptiFineAsync()
    {
        IsOptiFineBusy = true;
        try
        {
            var list = await _optifine.GetVersionsAsync().ConfigureAwait(true);
            OptiFineVersions.Clear();
            if (!list.IsSuccess || list.Value is null)
            {
                OptiFineStatus = list.Message ?? "Список OptiFine недоступен.";
                HasOptiFineVersions = false;
                return;
            }

            foreach (var version in list.Value)
            {
                OptiFineVersions.Add(version);
            }

            HasOptiFineVersions = OptiFineVersions.Count > 0;
            SelectedOptiFine = OptiFineVersions.FirstOrDefault();
            OptiFineStatus = HasOptiFineVersions
                ? $"Доступно {OptiFineVersions.Count} сборок. Версия Minecraft должна быть установлена заранее."
                : "Список пуст.";
        }
        finally
        {
            IsOptiFineBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshOptiFineAsync() => await LoadOptiFineAsync().ConfigureAwait(true);

    /// <summary>
    /// Ставит OptiFine патчем поверх client.jar выбранной версии Minecraft.
    /// Если ванильная версия не установлена — сначала доустанавливаем её.
    /// </summary>
    [RelayCommand]
    private async Task InstallOptiFineAsync()
    {
        var version = SelectedOptiFine;
        if (version is null)
        {
            _toasts.Warning("OptiFine", "Выберите сборку OptiFine.");
            return;
        }

        IsOptiFineBusy = true;
        var root = VersionsRoot;

        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                InstallProgress = p.Fraction;
                InstallStage = p.Stage;
            });

            if (!await _installer.IsInstalledAsync(root, version.MinecraftVersion).ConfigureAwait(true))
            {
                OptiFineStatus = $"Сначала ставим Minecraft {version.MinecraftVersion}…";
                var baseInstall = await _installer
                    .InstallAsync(root, version.MinecraftVersion, new InstallOptions(), progress)
                    .ConfigureAwait(true);
                if (!baseInstall.IsSuccess)
                {
                    _toasts.Error("Minecraft не установился", baseInstall.Message ?? "Смотрите лог.");
                    return;
                }
            }

            OptiFineStatus = "Применяем патч OptiFine…";
            var installed = await _optifine.InstallAsync(root, version, progress).ConfigureAwait(true);
            if (!installed.IsSuccess)
            {
                OptiFineStatus = installed.Message ?? "OptiFine не установился.";
                _toasts.Error("OptiFine не установился", installed.Message ?? "Смотрите лог.");
                return;
            }

            await LoadInstalledAsync().ConfigureAwait(true);
            SelectedInstalled = InstalledVersions.FirstOrDefault(v => v.Id == installed.Value);
            OptiFineStatus = $"Готово: {version.Label} для {version.MinecraftVersion}.";
            _toasts.Success("OptiFine установлен", $"{version.Label} · {version.MinecraftVersion}");
        }
        finally
        {
            IsOptiFineBusy = false;
        }
    }

    /// <summary>
    /// Запуск установленной версии. Профиль создаётся здесь и только один раз:
    /// пользователю не нужно заранее придумывать название и версию в другом разделе.
    /// </summary>
    [RelayCommand]
    private async Task LaunchInstalledAsync(InstalledVersion? version)
    {
        version ??= SelectedInstalled;
        if (version is null)
        {
            _toasts.Warning("Версия", "Выберите установленную версию.");
            return;
        }

        var account = _state.CurrentAccount;
        if (account is null)
        {
            var accounts = await _accounts.GetAllAsync().ConfigureAwait(true);
            account = accounts.Count > 0 ? accounts[0] : null;
        }

        if (account is null)
        {
            _toasts.Warning("Аккаунт", "Сначала создайте профиль в разделе «Аккаунты» — нужен ник.");
            return;
        }

        IsBusy = true;
        try
        {
            // Ищем профиль, который уже указывает на эту версию, иначе создаём новый.
            var existing = _state.Profiles.FirstOrDefault(p =>
                string.Equals(p.MinecraftVersion, version.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Loader.ToString(), version.LoaderLabel, StringComparison.OrdinalIgnoreCase));
            var profile = existing;

            if (profile is null)
            {
                var created = await _profiles.CreateAsync(new CreateProfileRequest
                {
                    Name = version.Id,
                    MinecraftVersion = version.Id,
                    Loader = Enum.TryParse<LoaderKind>(version.LoaderLabel, true, out var loader) ? loader : LoaderKind.Vanilla,
                    AccountId = account.Id
                }).ConfigureAwait(true);

                if (!created.IsSuccess)
                {
                    _toasts.Error("Профиль не создан", created.Message ?? "Ошибка.");
                    return;
                }

                profile = created.Value!;
                await _state.RefreshProfilesAsync().ConfigureAwait(true);
            }

            await _state.SetProfileAsync(profile).ConfigureAwait(true);
            await _state.SetAccountAsync(account).ConfigureAwait(true);

            var session = await _sessions.GetSessionAsync(account).ConfigureAwait(true);
            if (!session.IsSuccess)
            {
                _toasts.Error("Аккаунт недоступен", session.Message ?? "Войдите заново.");
                return;
            }

            var launched = await _launcher.LaunchAsync(profile, account, session.Value!.AccessToken).ConfigureAwait(true);
            if (!launched.IsSuccess)
            {
                _toasts.Error("Запуск не удался", launched.Message ?? "Смотрите лог.");
                return;
            }

            _toasts.Success("Minecraft запущен", $"{version.Id} · {account.Username}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Читает папку versions/ и показывает, что уже скачано (для раздела «Играть» и списка версий).</summary>
    private Task LoadInstalledAsync()
    {
        InstalledVersions.Clear();
        var versionsDir = Path.Combine(VersionsRoot, "versions");
        if (Directory.Exists(versionsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(versionsDir))
            {
                var id = Path.GetFileName(dir);
                if (!File.Exists(Path.Combine(dir, $"{id}.json")))
                {
                    continue;
                }

                var loader = id.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ? "NeoForge"
                    : id.Contains("forge", StringComparison.OrdinalIgnoreCase) ? "Forge"
                    : id.Contains("quilt", StringComparison.OrdinalIgnoreCase) ? "Quilt"
                    : id.Contains("fabric", StringComparison.OrdinalIgnoreCase) ? "Fabric"
                    : "Vanilla";

                var baseId = loader == "Vanilla" ? id : (id.Split('-')[0]);
                InstalledVersions.Add(new InstalledVersion(id, "release", baseId, loader));
            }
        }

        HasInstalledVersions = InstalledVersions.Count > 0;
        SelectedInstalled ??= InstalledVersions.FirstOrDefault();
        return Task.CompletedTask;
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

    private async Task LoadPendingAsync()
    {
        PendingInstall = await _resume.LoadAsync().ConfigureAwait(true);
        HasPendingInstall = PendingInstall is not null;
        PendingInstallText = PendingInstall is null
            ? string.Empty
            : $"Установка {PendingInstall.MinecraftVersion}" +
              (PendingInstall.Loader == LoaderKind.Vanilla ? string.Empty : $" · {PendingInstall.Loader}") +
              " была прервана. Скачанное сохранено — можно продолжить с того же места.";
    }

    /// <summary>Продолжает прерванную установку: файлы на диске и .part-файлы переиспользуются.</summary>
    [RelayCommand]
    private async Task ResumePendingAsync()
    {
        if (PendingInstall is not { } pending)
        {
            return;
        }

        var row = Releases.FirstOrDefault(r => r.Id == pending.MinecraftVersion);
        if (row is null)
        {
            _toasts.Warning("Версия недоступна", "Список версий ещё не загружен — попробуйте через минуту.");
            return;
        }

        SelectedLoader = pending.Loader;
        if (pending.Loader != LoaderKind.Vanilla)
        {
            await LoadLoaderVersionsAsync(row).ConfigureAwait(true);
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(o => o.Version == pending.LoaderVersion)
                                   ?? SelectedLoaderVersion;
        }

        await InstallAsync(row).ConfigureAwait(true);
        if (!IsBusy)
        {
            // Установка завершилась успешно — состояние больше не нужно.
            await _resume.ClearAsync().ConfigureAwait(true);
            PendingInstall = null;
            HasPendingInstall = false;
        }
    }

    [RelayCommand]
    private async Task DismissPendingAsync()
    {
        await _resume.ClearAsync().ConfigureAwait(true);
        PendingInstall = null;
        HasPendingInstall = false;
        PendingInstallText = string.Empty;
    }

    [RelayCommand]
    private void OpenVersionsFolder()
    {
        var dir = Path.Combine(VersionsRoot, "versions");
        try
        {
            Directory.CreateDirectory(dir);
            var info = new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true };
            System.Diagnostics.Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            _toasts.Error("Папка не открылась", ex.Message);
        }
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
