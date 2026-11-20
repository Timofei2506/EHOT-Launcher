using CommunityToolkit.Mvvm.ComponentModel;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Storage;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>
/// Общее состояние приложения: текущие настройки, аккаунт и профиль.
/// Единственный источник истины для оболочки и главного экрана.
/// </summary>
public interface IAppState
{
    LauncherSettings Settings { get; }

    AccountInfo? CurrentAccount { get; }

    GameProfile? CurrentProfile { get; }

    IReadOnlyList<AccountInfo> Accounts { get; }

    IReadOnlyList<GameProfile> Profiles { get; }

    bool IsInitialized { get; }

    /// <summary>Общий каталог игры (.minecraft): версии, библиотеки, ассеты.</summary>
    string MinecraftRoot { get; }

    event Action? Changed;

    Task InitializeAsync(CancellationToken ct = default);

    Task RefreshAccountsAsync(CancellationToken ct = default);

    Task RefreshProfilesAsync(CancellationToken ct = default);

    Task SetAccountAsync(AccountInfo? account, CancellationToken ct = default);

    Task SetProfileAsync(GameProfile? profile, CancellationToken ct = default);

    Task SaveSettingsAsync(LauncherSettings settings, CancellationToken ct = default);
}

public sealed class AppState(
    ISettingsRepository settingsRepository,
    IAccountRepository accountRepository,
    IProfileRepository profileRepository,
    IAppPaths paths,
    IPlatformInfo platform) : ObservableObject, IAppState
{
    private LauncherSettings _settings = LauncherSettings.Default;
    private AccountInfo? _currentAccount;
    private GameProfile? _currentProfile;
    private IReadOnlyList<AccountInfo> _accounts = [];
    private IReadOnlyList<GameProfile> _profiles = [];
    private bool _initialized;

    public LauncherSettings Settings => _settings;

    public AccountInfo? CurrentAccount => _currentAccount;

    public GameProfile? CurrentProfile => _currentProfile;

    public IReadOnlyList<AccountInfo> Accounts => _accounts;

    public IReadOnlyList<GameProfile> Profiles => _profiles;

    public bool IsInitialized => _initialized;

    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
        {
            return;
        }

        _settings = await settingsRepository.LoadAsync(ct).ConfigureAwait(true);
        _accounts = await accountRepository.GetAllAsync(ct).ConfigureAwait(true);
        _profiles = await profileRepository.GetAllAsync(ct).ConfigureAwait(true);

        _currentAccount = PickLastUsed(_accounts, static a => a.LastUsedAt);

        _currentProfile = PickLastUsed(_profiles, static p => p.LastLaunchedAt);

        _initialized = true;
        Changed?.Invoke();
    }

    public async Task RefreshAccountsAsync(CancellationToken ct = default)
    {
        _accounts = await accountRepository.GetAllAsync(ct).ConfigureAwait(true);
        _currentAccount = PickSameOrFirst(_accounts, _currentAccount, static a => a.Id);
        Changed?.Invoke();
    }

    public async Task RefreshProfilesAsync(CancellationToken ct = default)
    {
        _profiles = await profileRepository.GetAllAsync(ct).ConfigureAwait(true);
        _currentProfile = PickSameOrFirst(_profiles, _currentProfile, static p => p.Id);
        Changed?.Invoke();
    }

    public async Task SetAccountAsync(AccountInfo? account, CancellationToken ct = default)
    {
        _currentAccount = account;
        if (account is not null)
        {
            await accountRepository.TouchAsync(account.Id, ct).ConfigureAwait(true);
        }

        Changed?.Invoke();
    }

    public async Task SetProfileAsync(GameProfile? profile, CancellationToken ct = default)
    {
        _currentProfile = profile;
        if (profile is not null && !string.IsNullOrEmpty(profile.AccountId))
        {
            var account = _accounts.FirstOrDefault(a => a.Id == profile.AccountId);
            if (account is not null)
            {
                _currentAccount = account;
            }
        }

        Changed?.Invoke();
        await Task.CompletedTask;
    }

    public async Task SaveSettingsAsync(LauncherSettings settings, CancellationToken ct = default)
    {
        _settings = settings;
        await settingsRepository.SaveAsync(settings, ct).ConfigureAwait(true);
        Changed?.Invoke();
    }

    public string MinecraftRoot => _settings.MinecraftDirectory ?? paths.DefaultMinecraftDirectory;

    public string PlatformSummary => $"{platform.OsName} · {platform.Architecture} · .NET {platform.RuntimeVersion}";

    /// <summary>Последний использованный элемент, иначе первый; без LINQ по индексируемому списку.</summary>
    private static T? PickLastUsed<T>(IReadOnlyList<T> items, Func<T, DateTimeOffset?> stamp)
        where T : class
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (stamp(items[i]) is not null)
            {
                return items[i];
            }
        }

        return items.Count > 0 ? items[0] : null;
    }

    /// <summary>Элемент с тем же идентификатором, иначе первый (список мог измениться).</summary>
    private static T? PickSameOrFirst<T>(IReadOnlyList<T> items, T? current, Func<T, string> id)
        where T : class
    {
        if (current is not null)
        {
            var currentId = id(current);
            for (var i = 0; i < items.Count; i++)
            {
                if (string.Equals(id(items[i]), currentId, StringComparison.Ordinal))
                {
                    return items[i];
                }
            }
        }

        return items.Count > 0 ? items[0] : null;
    }
}
