using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Game.Profiles;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>«Сборки»: профили-инстансы с операциями, которые реально выполняются (выбор, клон, избранное, удаление).</summary>
public sealed partial class BuildsViewModel : ViewModelBase
{
    private readonly IAppState _state;
    private readonly IProfileManager _profiles;
    private readonly IToastService _toasts;
    private readonly INavigationService _navigation;

    public BuildsViewModel(IAppState state, IProfileManager profiles, IToastService toasts, INavigationService navigation)
    {
        _state = state;
        _profiles = profiles;
        _toasts = toasts;
        _navigation = navigation;
    }

    public ObservableCollection<BuildCardViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private string? _currentProfileId;

    [ObservableProperty]
    private BuildCardViewModel? _itemPendingDelete;

    [ObservableProperty]
    private bool _isCloneEditorOpen;

    [ObservableProperty]
    private string _cloneName = string.Empty;

    private string? _cloneSourceId;

    public override async Task OnNavigatedToAsync(CancellationToken ct = default) => await ReloadAsync(ct).ConfigureAwait(true);

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync(CancellationToken.None).ConfigureAwait(true);

    private async Task ReloadAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await _state.RefreshProfilesAsync(ct).ConfigureAwait(true);
            ReloadFromState();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Не удалось прочитать профили: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReloadFromState()
    {
        CurrentProfileId = _state.CurrentProfile?.Id;
        var accounts = _state.Accounts;

        Items.Clear();
        foreach (var profile in _state.Profiles
                     .OrderByDescending(p => p.IsFavorite)
                     .ThenByDescending(p => p.LastLaunchedAt ?? DateTimeOffset.MinValue)
                     .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var accountName = accounts.FirstOrDefault(a => a.Id == profile.AccountId)?.Username;
            Items.Add(new BuildCardViewModel(profile, accountName, profile.Id == CurrentProfileId));
        }

        HasItems = Items.Count > 0;
    }

    [RelayCommand]
    private async Task UseAsync(BuildCardViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var profile = _state.Profiles.FirstOrDefault(p => p.Id == item.Id);
        if (profile is null)
        {
            return;
        }

        await _state.SetProfileAsync(profile).ConfigureAwait(true);
        _toasts.Success("Сборка выбрана", profile.Name);
        ReloadFromState();
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(BuildCardViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var profile = _state.Profiles.FirstOrDefault(p => p.Id == item.Id);
        if (profile is null)
        {
            return;
        }

        var updated = await _profiles.UpdateAsync(profile with { IsFavorite = !profile.IsFavorite }).ConfigureAwait(true);
        if (!updated.IsSuccess)
        {
            _toasts.Error("Не сохранилось", updated.Message ?? "Ошибка.");
            return;
        }

        await _state.RefreshProfilesAsync().ConfigureAwait(true);
        ReloadFromState();
    }

    [RelayCommand]
    private void OpenCloneEditor(BuildCardViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _cloneSourceId = item.Id;
        CloneName = $"{item.Name} (копия)";
        IsCloneEditorOpen = true;
    }

    [RelayCommand]
    private void CloseCloneEditor() => IsCloneEditorOpen = false;

    [RelayCommand]
    private async Task ConfirmCloneAsync()
    {
        var name = CloneName.Trim();
        if (string.IsNullOrEmpty(name) || _cloneSourceId is null)
        {
            _toasts.Warning("Клон", "Введите название сборки.");
            return;
        }

        var created = await _profiles.CloneAsync(_cloneSourceId, name).ConfigureAwait(true);
        if (!created.IsSuccess)
        {
            _toasts.Error("Не удалось клонировать", created.Message ?? "Ошибка.");
            return;
        }

        IsCloneEditorOpen = false;
        await _state.RefreshProfilesAsync().ConfigureAwait(true);
        ReloadFromState();
        _toasts.Success("Сборка склонирована", name);
    }

    [RelayCommand]
    private void AskDelete(BuildCardViewModel? item)
    {
        if (item is not null)
        {
            ItemPendingDelete = item;
        }
    }

    [RelayCommand]
    private void CancelDelete() => ItemPendingDelete = null;

    /// <summary>Удаляет только запись профиля: файлы инстанса остаются на диске.</summary>
    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        var item = ItemPendingDelete;
        if (item is null)
        {
            return;
        }

        try
        {
            var result = await _profiles.DeleteAsync(item.Id, deleteFiles: false).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                _toasts.Error("Не удалось удалить", result.Message ?? "Ошибка.");
                return;
            }

            await _state.RefreshProfilesAsync().ConfigureAwait(true);
            ReloadFromState();
            _toasts.Show("Сборка удалена", $"{item.Name} · файлы инстанса сохранены", ToastKind.Info);
        }
        finally
        {
            ItemPendingDelete = null;
        }
    }

    [RelayCommand]
    private void OpenFolder(BuildCardViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!FileSystemActions.OpenFolder(item.InstanceDirectory))
        {
            _toasts.Error("Папка не открылась", item.InstanceDirectory);
        }
    }

    [RelayCommand]
    private async Task OpenHomeAsync() => await _navigation.NavigateAsync("home").ConfigureAwait(true);
}

/// <summary>Карточка сборки: снимок данных профиля на момент отрисовки списка.</summary>
public sealed class BuildCardViewModel
{
    public BuildCardViewModel(GameProfile profile, string? accountName, bool isCurrent)
    {
        Id = profile.Id;
        Name = profile.Name;
        DisplayVersion = profile.DisplayVersion;
        InstanceDirectory = profile.InstanceDirectory;
        AllocatedMemoryMb = profile.AllocatedMemoryMb;
        IsFavorite = profile.IsFavorite;
        IsModpack = !string.IsNullOrEmpty(profile.ModpackId);
        AccountName = accountName ?? "Аккаунт не привязан";
        IsCurrent = isCurrent;
        LastLaunchLabel = profile.LastLaunchedAt is { } launched
            ? $"Запускалась {launched.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "Ещё не запускалась";
        Initial = string.IsNullOrEmpty(profile.Name) ? "?" : profile.Name[..1].ToUpperInvariant();
    }

    public string Id { get; }
    public string Name { get; }
    public string DisplayVersion { get; }
    public string InstanceDirectory { get; }
    public int AllocatedMemoryMb { get; }
    public bool IsFavorite { get; }
    public bool IsModpack { get; }
    public string AccountName { get; }
    public bool IsCurrent { get; }
    public string LastLaunchLabel { get; }
    public string Initial { get; }
}
