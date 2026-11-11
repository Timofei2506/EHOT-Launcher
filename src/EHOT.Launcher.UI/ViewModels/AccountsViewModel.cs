using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Auth;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Storage;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>
/// Аккаунты: локальные профили, вход Microsoft (device code), проверка ника, скины.
/// Честно разделяет лицензионные и локальные профили.
/// </summary>
public sealed partial class AccountsViewModel : ViewModelBase
{
    private readonly IAppState _state;
    private readonly ILocalProfileService _local;
    private readonly IMicrosoftAuthService _microsoft;
    private readonly IProfileLookupService _lookup;
    private readonly IAccountRepository _accounts;
    private readonly IToastService _toasts;

    public AccountsViewModel(
        IAppState state,
        ILocalProfileService local,
        IMicrosoftAuthService microsoft,
        IProfileLookupService lookup,
        IAccountRepository accounts,
        IToastService toasts)
    {
        _state = state;
        _local = local;
        _microsoft = microsoft;
        _lookup = lookup;
        _accounts = accounts;
        _toasts = toasts;

        MicrosoftConfigured = microsoft.IsConfigured;
        MicrosoftHint = microsoft.ConfigurationHint ?? string.Empty;
    }

    public ObservableCollection<AccountInfo> Accounts { get; } = [];

    public bool MicrosoftConfigured { get; }

    public string MicrosoftHint { get; }

    [ObservableProperty]
    private string _newUsername = string.Empty;

    [ObservableProperty]
    private AccountInfo? _selectedAccount;

    [ObservableProperty]
    private string _deviceCode = string.Empty;

    [ObservableProperty]
    private string _deviceVerificationUri = string.Empty;

    [ObservableProperty]
    private string _loginStatus = string.Empty;

    [ObservableProperty]
    private bool _isLoggingIn;

    [ObservableProperty]
    private string _lookupResult = string.Empty;

    [ObservableProperty]
    private string _skinPath = string.Empty;

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        await ReloadAsync(ct).ConfigureAwait(true);
    }

    private async Task ReloadAsync(CancellationToken ct = default)
    {
        var items = await _accounts.GetAllAsync(ct).ConfigureAwait(true);
        Accounts.Clear();
        foreach (var item in items)
        {
            Accounts.Add(item);
        }

        SelectedAccount ??= _state.CurrentAccount ?? Accounts.FirstOrDefault();
    }

    [RelayCommand]
    private async Task CreateLocalAsync()
    {
        var result = await _local.CreateAsync(new LocalProfileRequest(NewUsername)).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            _toasts.Error("Профиль не создан", result.Message ?? "Проверьте ник.");
            return;
        }

        await _state.RefreshAccountsAsync().ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);

        _toasts.Warning("Локальный профиль",
            "Это оффлайн-профиль: вход на официальные серверы и загрузка скина на Mojang недоступны.");
        NewUsername = string.Empty;
    }

    [RelayCommand]
    private async Task CheckUsernameAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUsername))
        {
            return;
        }

        var lookup = await _lookup.LookupAsync(NewUsername.Trim()).ConfigureAwait(true);
        if (!lookup.IsSuccess)
        {
            LookupResult = lookup.Message ?? "Проверка недоступна.";
            return;
        }

        LookupResult = lookup.Value is null
            ? "Ник свободен в официальном сервисе Mojang: можно создать локальный профиль или войти своим аккаунтом Microsoft."
            : $"Ник занят официальным аккаунтом (UUID {lookup.Value.Uuid}). Войти под чужим ником невозможно — используйте свой Microsoft-аккаунт.";
    }

    [RelayCommand]
    private async Task SignInMicrosoftAsync()
    {
        if (!MicrosoftConfigured)
        {
            _toasts.Warning("Требуется настройка", MicrosoftHint);
            return;
        }

        IsLoggingIn = true;
        LoginStatus = "Запрашиваем код у Microsoft…";
        try
        {
            var challenge = await _microsoft.StartDeviceCodeAsync().ConfigureAwait(true);
            if (!challenge.IsSuccess)
            {
                _toasts.Error("Microsoft недоступен", challenge.Message ?? "Ошибка.");
                return;
            }

            DeviceCode = challenge.Value!.UserCode;
            DeviceVerificationUri = challenge.Value.VerificationUri;
            LoginStatus = $"Откройте {DeviceVerificationUri} и введите код {DeviceCode}";

            var session = await _microsoft.WaitForAuthenticationAsync(challenge.Value,
                new Progress<string>(s => LoginStatus = s)).ConfigureAwait(true);

            if (!session.IsSuccess)
            {
                _toasts.Error("Вход не выполнен", session.Message ?? "Ошибка авторизации.");
                LoginStatus = session.Message ?? string.Empty;
                return;
            }

            await _state.RefreshAccountsAsync().ConfigureAwait(true);
            await _state.SetAccountAsync(session.Value!.Account).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);

            LoginStatus = $"Вошли как {session.Value.Account.Username}";
            _toasts.Success("Вход выполнен", session.Value.Account.Username);
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    [RelayCommand]
    private async Task UseAccountAsync(AccountInfo account)
    {
        await _state.SetAccountAsync(account).ConfigureAwait(true);
        SelectedAccount = account;
        _toasts.Success("Аккаунт выбран", $"{account.Username} · {account.DisplayKind}");
    }

    [RelayCommand]
    private async Task DeleteAccountAsync(AccountInfo account)
    {
        var result = account.Kind == AccountKind.Microsoft
            ? await _local.DeleteAsync(account.Id).ConfigureAwait(true)
            : await _local.DeleteAsync(account.Id).ConfigureAwait(true);

        if (result.IsSuccess)
        {
            if (account.Kind == AccountKind.Microsoft)
            {
                await _microsoft.SignOutAsync(account.Id).ConfigureAwait(true);
            }

            await _state.RefreshAccountsAsync().ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            _toasts.Show("Аккаунт удалён", account.Username, ToastKind.Info);
        }
        else
        {
            _toasts.Error("Не удалось удалить", result.Message ?? "Ошибка.");
        }
    }

    [RelayCommand]
    private async Task ApplySkinAsync()
    {
        if (SelectedAccount is null)
        {
            _toasts.Warning("Аккаунт", "Выберите профиль.");
            return;
        }

        if (string.IsNullOrWhiteSpace(SkinPath))
        {
            _toasts.Warning("Файл", "Укажите путь к PNG-скину 64×64.");
            return;
        }

        var result = await _local.SetSkinAsync(SelectedAccount.Id, SkinPath).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            await ReloadAsync().ConfigureAwait(true);
            _toasts.Success("Скин применён", "Файл сохранён локально.");
        }
        else
        {
            _toasts.Error("Скин не применён", result.Message ?? "Проверьте формат PNG.");
        }
    }
}
