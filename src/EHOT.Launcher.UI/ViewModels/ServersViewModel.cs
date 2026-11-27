using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Game.Servers;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Свои серверы: ping по Minecraft Server List Ping, избранное, быстрый copy адреса.</summary>
public sealed partial class ServersViewModel : ViewModelBase
{
    private readonly IServerCatalogService _servers;
    private readonly IServerPinger _pinger;
    private readonly IToastService _toasts;
    private readonly IClipboardService _clipboard;
    private IReadOnlyList<ServerEntry> _all = [];

    public ServersViewModel(IServerCatalogService servers, IServerPinger pinger, IToastService toasts, IClipboardService clipboard)
    {
        _servers = servers;
        _pinger = pinger;
        _toasts = toasts;
        _clipboard = clipboard;
    }

    public ObservableCollection<ServerViewItem> Items { get; } = [];
    public ObservableCollection<string> Tabs { get; } = ["Мои серверы", "Избранные"];

    [ObservableProperty]
    private string _selectedTab = "Мои серверы";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isEditorOpen;

    [ObservableProperty]
    private string? _editingId;

    [ObservableProperty]
    private string _editorName = string.Empty;

    [ObservableProperty]
    private string _editorHost = string.Empty;

    [ObservableProperty]
    private string _editorPort = "25565";

    [ObservableProperty]
    private string _editorDescription = string.Empty;

    [ObservableProperty]
    private ServerViewItem? _itemPendingDelete;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _hasItems;

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        Load();
        await PingAllAsync(ct).ConfigureAwait(true);
    }

    partial void OnSelectedTabChanged(string value)
    {
        _ = value;
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        ApplyFilter();
    }

    private void Load()
    {
        _all = _servers.List();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        IEnumerable<ServerEntry> filtered = _all;

        if (string.Equals(SelectedTab, "Избранные", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(s => s.IsFavorite);
        }

        if (query.Length > 0)
        {
            filtered = filtered.Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Host.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        Items.Clear();
        foreach (var entry in filtered.OrderByDescending(s => s.IsFavorite).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            Items.Add(new ServerViewItem(entry));
        }

        HasItems = Items.Count > 0;
    }

    [RelayCommand]
    private void SetTab(string? tab)
    {
        if (tab is not null)
        {
            SelectedTab = tab;
        }
    }

    [RelayCommand]
    private void OpenAddEditor()
    {
        EditingId = null;
        EditorName = string.Empty;
        EditorHost = string.Empty;
        EditorPort = "25565";
        EditorDescription = string.Empty;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void OpenEditEditor(ServerViewItem? item)
    {
        if (item is null)
        {
            return;
        }

        EditingId = item.Id;
        EditorName = item.Name;
        EditorHost = item.Host;
        EditorPort = item.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EditorDescription = item.Description;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CloseEditor() => IsEditorOpen = false;

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        var name = EditorName.Trim();
        var host = EditorHost.Trim();
        if (name.Length == 0 || host.Length == 0)
        {
            _toasts.Warning("Сервер", "Укажите название и адрес сервера.");
            return;
        }

        if (!int.TryParse(EditorPort, out var port) || port is < 1 or > 65535)
        {
            _toasts.Warning("Порт", "Порт должен быть числом от 1 до 65535.");
            return;
        }

        if (EditingId is null)
        {
            await _servers.AddAsync(name, host, port, EditorDescription).ConfigureAwait(true);
            _toasts.Success("Сервер добавлен", $"{name} · {host}:{port}");
        }
        else
        {
            var current = _all.FirstOrDefault(s => s.Id == EditingId);
            if (current is not null)
            {
                await _servers.UpdateAsync(current with
                {
                    Name = name,
                    Host = host,
                    Port = port,
                    Description = EditorDescription.Trim()
                }).ConfigureAwait(true);
                _toasts.Success("Сервер сохранён", $"{name} · {host}:{port}");
            }
        }

        IsEditorOpen = false;
        Load();
        await PingAllAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PingAllAsync(CancellationToken ct = default)
    {
        if (Items.Count == 0)
        {
            return;
        }

        IsChecking = true;
        try
        {
            using var gate = new SemaphoreSlim(6, 6);
            var tasks = Items.Select(async item =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var status = await _pinger.PingAsync(item.Host, item.Port, ct).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() => item.ApplyStatus(status));
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task PingOneAsync(ServerViewItem? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsChecking = true;
        try
        {
            var status = await _pinger.PingAsync(item.Host, item.Port).ConfigureAwait(true);
            item.ApplyStatus(status);
        }
        finally
        {
            item.IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(ServerViewItem? item)
    {
        if (item is null)
        {
            return;
        }

        var updated = item.ToEntry() with { IsFavorite = !item.IsFavorite };
        await _servers.UpdateAsync(updated).ConfigureAwait(true);
        Load();
    }

    [RelayCommand]
    private async Task CopyAddressAsync(ServerViewItem? item)
    {
        if (item is null)
        {
            return;
        }

        var copied = await _clipboard.SetTextAsync(item.Address).ConfigureAwait(true);
        if (copied)
        {
            _toasts.Success("Адрес скопирован", item.Address);
        }
        else
        {
            _toasts.Warning("Не удалось скопировать", item.Address);
        }
    }

    [RelayCommand]
    private void AskDelete(ServerViewItem? item)
    {
        if (item is not null)
        {
            ItemPendingDelete = item;
        }
    }

    [RelayCommand]
    private void CancelDelete() => ItemPendingDelete = null;

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
            await _servers.RemoveAsync(item.Id).ConfigureAwait(true);
            _toasts.Show("Сервер удалён", item.Address, ToastKind.Info);
            Load();
        }
        finally
        {
            ItemPendingDelete = null;
        }
    }
}

public sealed partial class ServerViewItem : ObservableObject
{
    public ServerViewItem(ServerEntry entry)
    {
        Id = entry.Id;
        Name = entry.Name;
        Host = entry.Host;
        Port = entry.Port;
        Description = entry.Description;
        IsFavorite = entry.IsFavorite;
        CreatedAt = entry.CreatedAt;
        StatusLabel = "Проверяю…";
        StatusDetail = string.Empty;
        IsOnline = false;
        IsChecking = true;
    }

    public string Id { get; }
    public string Name { get; }
    public string Host { get; }
    public int Port { get; }
    public string Description { get; }
    public DateTimeOffset CreatedAt { get; }

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string _statusLabel;

    [ObservableProperty]
    private string _statusDetail;

    public string Address => $"{Host}:{Port}";

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    public ServerEntry ToEntry() => new()
    {
        Id = Id,
        Name = Name,
        Host = Host,
        Port = Port,
        Description = Description,
        IsFavorite = IsFavorite,
        CreatedAt = CreatedAt
    };

    public void ApplyStatus(ServerStatus status)
    {
        IsOnline = status.Online;
        IsChecking = false;
        if (status.Online)
        {
            StatusLabel = status.PlayersOnline is { } online && status.PlayersMax is { } max
                ? $"{online} / {max} игроков"
                : "Онлайн";
            StatusDetail = string.IsNullOrWhiteSpace(status.Version)
                ? $"Отклик {status.LatencyMs} мс"
                : $"{status.Version} · {status.LatencyMs} мс";
            if (!string.IsNullOrWhiteSpace(status.Description))
            {
                ServerMotd = status.Description;
            }
        }
        else
        {
            StatusLabel = "Недоступен";
            StatusDetail = status.Error ?? "нет ответа";
        }
    }

    [ObservableProperty]
    private string? _serverMotd;
}
