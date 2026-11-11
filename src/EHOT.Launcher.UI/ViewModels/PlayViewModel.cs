using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Auth;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Game.Install;
using EHOT.Launcher.Game.Launch;
using EHOT.Launcher.Game.Minecraft;
using EHOT.Launcher.Game.Profiles;
using EHOT.Launcher.Game.Runtimes;
using EHOT.Launcher.Platform.Shared;
using EHOT.Launcher.UI.Services;
using EHOT.Launcher.Storage;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Профили: создание, выбор, RAM/Java/аргументы, установка версии и запуск.</summary>
public sealed partial class PlayViewModel : ViewModelBase
{
    private readonly IAppState _state;
    private readonly IProfileManager _profiles;
    private readonly ILoaderInstaller _loaders;
    private readonly IGameLauncher _launcher;
    private readonly IAccountSessionProvider _sessions;
    private readonly JavaRuntimeManager _java;
    private readonly IToastService _toasts;
    private readonly INavigationService _navigation;
    private readonly IAccountRepository _accounts;
    private readonly IMinecraftVersionService _versions;

    public PlayViewModel(
        IAppState state,
        IProfileManager profiles,
        ILoaderInstaller loaders,
        IGameLauncher launcher,
        IAccountSessionProvider sessions,
        JavaRuntimeManager java,
        IToastService toasts,
        INavigationService navigation,
        IAccountRepository accounts,
        IMinecraftVersionService versions)
    {
        _state = state;
        _profiles = profiles;
        _loaders = loaders;
        _launcher = launcher;
        _sessions = sessions;
        _java = java;
        _toasts = toasts;
        _navigation = navigation;
        _accounts = accounts;
        _versions = versions;

        _state.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _launcher.SessionEnded += (_, outcome) => Dispatcher.UIThread.Post(() =>
        {
            IsRunning = false;
            if (outcome.Status == LaunchStatus.Failed)
            {
                _toasts.Error("Ошибка запуска", outcome.Error ?? "Смотрите лог.");
            }
        });

        Refresh();
    }

    public ObservableCollection<GameProfile> Profiles { get; } = [];

    public ObservableCollection<string> MinecraftVersions { get; } = [];

    public ObservableCollection<LoaderVersionOption> LoaderVersions { get; } = [];

    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = [];

    public LoaderKind[] AvailableLoaders { get; } = [LoaderKind.Vanilla, LoaderKind.Fabric, LoaderKind.NeoForge, LoaderKind.Forge, LoaderKind.Quilt];

    [ObservableProperty]
    private GameProfile? _selectedProfile;

    [ObservableProperty]
    private string _newProfileName = "Мой профиль";

    [ObservableProperty]
    private string? _newProfileVersion;

    [ObservableProperty]
    private LoaderKind _newProfileLoader = LoaderKind.Vanilla;

    [ObservableProperty]
    private LoaderVersionOption? _selectedLoaderVersion;

    [ObservableProperty]
    private int _memoryMb = 4096;

    [ObservableProperty]
    private JavaRuntime? _selectedJava;

    [ObservableProperty]
    private string _jvmArgs = string.Empty;

    [ObservableProperty]
    private string _installStatusText = "Выберите профиль";

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusLine = string.Empty;

    [ObservableProperty]
    private AccountInfo? _selectedAccount;

    public static int MaxMemoryMb => Math.Max(2048, (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024) - 2048);

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            Refresh();
            await LoadVersionsAsync(ct).ConfigureAwait(true);
            await LoadJavaAsync(ct).ConfigureAwait(true);
            await LoadAccountsAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Refresh()
    {
        Profiles.Clear();
        foreach (var profile in _state.Profiles)
        {
            Profiles.Add(profile);
        }

        SelectedProfile ??= _state.CurrentProfile ?? Profiles.FirstOrDefault();
        IsRunning = SelectedProfile is not null && _launcher.IsRunning(SelectedProfile.Id);
    }

    private async Task LoadAccountsAsync(CancellationToken ct)
    {
        var accounts = await _accounts.GetAllAsync(ct).ConfigureAwait(true);
        SelectedAccount = _state.CurrentAccount ?? (accounts.Count > 0 ? accounts[0] : null);
    }

    private async Task LoadVersionsAsync(CancellationToken ct)
    {
        var versions = await _versions.GetManifestAsync(false, ct).ConfigureAwait(true);
        if (!versions.IsSuccess)
        {
            ErrorMessage = versions.Message;
            return;
        }

        MinecraftVersions.Clear();
        foreach (var v in versions.Value!.Versions.Where(v => v.Type is "release" or "snapshot").Take(80))
        {
            MinecraftVersions.Add(v.Id);
        }

        NewProfileVersion ??= versions.Value.Latest?.Release ?? MinecraftVersions.FirstOrDefault();
    }

    private async Task LoadJavaAsync(CancellationToken ct)
    {
        var runtimes = await _java.DiscoverAsync(ct).ConfigureAwait(true);
        JavaRuntimes.Clear();
        foreach (var runtime in runtimes)
        {
            JavaRuntimes.Add(runtime);
        }

        if (SelectedJava is null && runtimes.Count > 0)
        {
            SelectedJava = runtimes[0];
        }
    }

    [RelayCommand]
    private async Task CreateProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProfileName))
        {
            _toasts.Warning("Название", "Введите название профиля.");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewProfileVersion))
        {
            _toasts.Warning("Версия", "Выберите версию Minecraft.");
            return;
        }

        IsBusy = true;
        try
        {
            var created = await _profiles.CreateAsync(new CreateProfileRequest
            {
                Name = NewProfileName.Trim(),
                MinecraftVersion = NewProfileVersion,
                Loader = NewProfileLoader,
                LoaderVersion = SelectedLoaderVersion?.Version,
                AccountId = SelectedAccount?.Id,
                MemoryMb = MemoryMb
            }).ConfigureAwait(true);

            if (!created.IsSuccess)
            {
                _toasts.Error("Профиль не создан", created.Message ?? "Ошибка.");
                return;
            }

            await _state.RefreshProfilesAsync().ConfigureAwait(true);
            await _state.SetProfileAsync(created.Value).ConfigureAwait(true);
            SelectedProfile = created.Value;
            _toasts.Success("Профиль создан", $"{created.Value!.Name} · {created.Value.DisplayVersion}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallVersionAsync()
    {
        if (SelectedProfile is null)
        {
            _toasts.Warning("Профиль", "Выберите профиль.");
            return;
        }

        IsInstalling = true;
        InstallStatusText = "Подготовка…";
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                InstallProgress = p.Fraction;
                InstallStatusText = p.Detail is null ? p.Stage : $"{p.Stage} · {p.Detail}";
            });

            var instanceDir = SelectedProfile.InstanceDirectory;
            OperationResult result = SelectedProfile.Loader == LoaderKind.Vanilla
                ? (await new GameInstallerProxy(_loaders).InstallVanillaAsync(instanceDir, SelectedProfile.MinecraftVersion, progress).ConfigureAwait(true))
                : (await _loaders.InstallLoaderAsync(
                    instanceDir,
                    SelectedProfile.MinecraftVersion,
                    SelectedProfile.Loader,
                    SelectedProfile.LoaderVersion,
                    progress).ConfigureAwait(true)).ToOperationResult();

            InstallStatusText = result.IsSuccess ? "Установлено" : result.Message ?? "Ошибка установки";
            if (result.IsSuccess)
            {
                InstallProgress = 1;
                _toasts.Success("Версия установлена", SelectedProfile.DisplayVersion);
            }
            else
            {
                _toasts.Error("Установка не удалась", result.Message ?? "Смотрите лог.");
            }
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var updated = SelectedProfile with
        {
            AllocatedMemoryMb = MemoryMb,
            JavaPath = SelectedJava?.Path is null ? null : Path.Combine(SelectedJava.Path, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"),
            ExtraJvmArgs = JvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AccountId = SelectedAccount?.Id ?? SelectedProfile.AccountId
        };

        var saved = await _profiles.UpdateAsync(updated).ConfigureAwait(true);
        if (!saved.IsSuccess)
        {
            _toasts.Error("Не сохранено", saved.Message ?? "Ошибка.");
            return;
        }

        await _state.RefreshProfilesAsync().ConfigureAwait(true);
        _toasts.Success("Настройки сохранены", $"{updated.AllocatedMemoryMb} МБ, Java: {(SelectedJava?.Version ?? "авто")}");
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (SelectedProfile is null)
        {
            _toasts.Warning("Профиль", "Выберите профиль.");
            return;
        }

        if (SelectedAccount is null)
        {
            _toasts.Warning("Аккаунт", "Добавьте аккаунт в разделе «Аккаунты».");
            await _navigation.NavigateAsync("accounts").ConfigureAwait(true);
            return;
        }

        IsBusy = true;
        try
        {
            await _state.SetProfileAsync(SelectedProfile).ConfigureAwait(true);
            await _state.SetAccountAsync(SelectedAccount).ConfigureAwait(true);

            var session = await _sessions.GetSessionAsync(SelectedAccount).ConfigureAwait(true);
            if (!session.IsSuccess)
            {
                _toasts.Error("Аккаунт недоступен", session.Message ?? "Войдите заново.");
                return;
            }

            var result = await _launcher.LaunchAsync(SelectedProfile, SelectedAccount, session.Value!.AccessToken).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                _toasts.Error("Запуск не удался", result.Message ?? "Смотрите лог.");
                return;
            }

            IsRunning = true;
            _toasts.Success("Minecraft запущен", $"PID {result.Value?.ProcessId}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        await _launcher.StopAsync(SelectedProfile.Id).ConfigureAwait(true);
        IsRunning = false;
    }

    [RelayCommand]
    private async Task SelectProfileAsync(GameProfile profile)
    {
        SelectedProfile = profile;
        MemoryMb = profile.AllocatedMemoryMb;
        await _state.SetProfileAsync(profile).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CloneProfileAsync(GameProfile profile)
    {
        var result = await _profiles.CloneAsync(profile.Id, profile.Name + " (копия)").ConfigureAwait(true);
        if (result.IsSuccess)
        {
            await _state.RefreshProfilesAsync().ConfigureAwait(true);
            _toasts.Success("Профиль клонирован", result.Value!.Name);
        }
        else
        {
            _toasts.Error("Клонирование не удалось", result.Message ?? "Ошибка.");
        }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(GameProfile profile)
    {
        var result = await _profiles.DeleteAsync(profile.Id, deleteFiles: false).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            await _state.RefreshProfilesAsync().ConfigureAwait(true);
            _toasts.Show("Профиль удалён", "Папка с файлами сохранена на диске.", ToastKind.Info);
        }
        else
        {
            _toasts.Error("Удаление не удалось", result.Message ?? "Ошибка.");
        }
    }
}

/// <summary>Тонкая обёртка: установка ванильной версии через игровой установщик (без дублирования кода).</summary>
internal sealed class GameInstallerProxy(ILoaderInstaller loaders)
{
    public async Task<OperationResult> InstallVanillaAsync(string instanceDirectory, string version, IProgress<InstallProgress>? progress)
        => (await loaders.InstallLoaderAsync(instanceDirectory, version, LoaderKind.Vanilla, null, progress).ConfigureAwait(false)).ToOperationResult();
}
