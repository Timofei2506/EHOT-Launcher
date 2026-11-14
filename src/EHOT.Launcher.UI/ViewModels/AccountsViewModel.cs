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
    private readonly ISkinFilePicker _picker;

    public AccountsViewModel(
        IAppState state,
        ILocalProfileService local,
        IMicrosoftAuthService microsoft,
        IProfileLookupService lookup,
        IAccountRepository accounts,
        IToastService toasts,
        ISkinFilePicker picker)
    {
        _state = state;
        _local = local;
        _microsoft = microsoft;
        _lookup = lookup;
        _accounts = accounts;
        _toasts = toasts;
        _picker = picker;

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

    /// <summary>Что известно про введённый ник: официальный аккаунт или свободный (оффлайн) ник.</summary>
    [ObservableProperty]
    private string _usernameStatus = string.Empty;

    [ObservableProperty]
    private bool _hasUsernameStatus;

    /// <summary>true — ник занят официальным аккаунтом, скин и UUID будут скопированы у него.</summary>
    [ObservableProperty]
    private bool _nickIsOfficial;

    /// <summary>Путь к скину выбранного профиля — для превью.</summary>
    [ObservableProperty]
    private string? _selectedSkinPath;

    [ObservableProperty]
    private string _selectedSkinHint = "Скин не задан: на сервере будет стандартный персонаж.";

    partial void OnNewUsernameChanged(string value)
    {
        // Любое изменение ника сбрасывает прошлый вердикт: данные не должны «прилипать».
        UsernameStatus = string.Empty;
        HasUsernameStatus = false;
        NickIsOfficial = false;
    }

    partial void OnSelectedAccountChanged(AccountInfo? value)
    {
        SelectedSkinPath = value?.LocalSkinPath;
        SelectedSkinHint = value switch
        {
            null => "Выберите профиль, чтобы увидеть его скин.",
            { SkinSource: "mojang" } => "Скин скопирован у официального игрока с этим ником — его видят все, у кого есть такой скин.",
            { SkinSource: "local" } => "Свой скин: его видите только вы (на серверах с online-mode чужие игроки его не получат).",
            _ => "Скин не задан: на сервере будет стандартный персонаж."
        };
    }

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

        var outcome = result.Value!;
        await _state.RefreshAccountsAsync().ConfigureAwait(true);
        await _state.SetAccountAsync(outcome.Account).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == outcome.Account.Id) ?? SelectedAccount;

        _toasts.Show(
            outcome.CopiedOfficialSkin ? "Скин скопирован у официального игрока" : "Профиль создан",
            outcome.Note,
            outcome.CopiedOfficialSkin ? ToastKind.Success : ToastKind.Info);

        LookupResult = outcome.Note;
        NewUsername = string.Empty;
    }

    /// <summary>Автоматически переносит скин официального игрока на уже созданный профиль.</summary>
    [RelayCommand]
    private async Task CopyOfficialSkinAsync(AccountInfo? account)
    {
        account ??= SelectedAccount;
        if (account is null)
        {
            _toasts.Warning("Профиль", "Выберите профиль.");
            return;
        }

        // UUID официального игрока — отдельно от скина: он нужен даже если текстура не скачается.
        var official = await _lookup.ResolveOfficialAsync(account.Username).ConfigureAwait(true);
        if (official.IsSuccess && official.Value is not null && Guid.TryParse(official.Value.Uuid, out var officialUuid))
        {
            await _local.ApplyOfficialUuidAsync(account.Id, officialUuid).ConfigureAwait(true);
        }

        var remote = await _lookup.FetchSkinAsync(account.Username).ConfigureAwait(true);
        if (!remote.IsSuccess)
        {
            _toasts.Error("Скин не получен", remote.Message ?? "Сервис Mojang недоступен.");
            return;
        }

        if (remote.Value is null)
        {
            _toasts.Warning("Ника нет в официальном сервисе",
                "Такого игрока нет — можно загрузить свой скин: его увидит только этот профиль.");
            return;
        }

        var result = await _local.SaveSkinFromBytesAsync(account.Id, remote.Value.Png,
            remote.Value.IsSlim, source: "mojang").ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            _toasts.Error("Скин не сохранён", result.Message ?? "Ошибка.");
            return;
        }

        await _state.RefreshAccountsAsync().ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == account.Id) ?? SelectedAccount;
        _toasts.Success("Скин скопирован", $"Ник {remote.Value.Username}: скин официального игрока применён.");
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
            UsernameStatus = string.Empty;
            HasUsernameStatus = false;
            NickIsOfficial = false;
            return;
        }

        HasUsernameStatus = true;
        if (lookup.Value is null)
        {
            NickIsOfficial = false;
            UsernameStatus = "Ника нет в официальном сервисе — это свободный ник";
            LookupResult = "При создании профиля UUID будет посчитан в оффлайн-режиме (как у ванильного сервера). " +
                           "Скин можно загрузить свой: его будет видеть только этот профиль.";
            return;
        }

        NickIsOfficial = true;
        UsernameStatus = $"Официальный аккаунт · UUID {lookup.Value.Uuid}";
        LookupResult = "Это ник существующего официального игрока: при создании профиля лаунчер возьмёт его " +
                       "публичный UUID и копию скина, чтобы персонаж выглядел привычно. Вход на серверы с " +
                       "online-mode по-прежнему требует лицензии — это правило сервера.";
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

    /// <summary>Открывает системный диалог: путь больше не нужно вбивать руками.</summary>
    [RelayCommand]
    private async Task PickSkinFileAsync()
    {
        string? path;
        try
        {
            path = await _picker.PickSkinAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _toasts.Error("Диалог не открылся", ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            // Отмена — это нормальный исход, ругаться не нужно.
            return;
        }

        SkinPath = path;
        await ApplySkinAsync().ConfigureAwait(true);
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
