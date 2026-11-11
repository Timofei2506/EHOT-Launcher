using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Network;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>
/// Game Network: создание/вход в комнату, участники, адрес для Minecraft, диагностика.
/// Все значения берутся из реального состояния сессии; при отсутствии сервера — честное сообщение.
/// </summary>
public sealed partial class GameNetworkViewModel : ViewModelBase
{
    private readonly IRoomSession _session;
    private readonly IToastService _toasts;

    public GameNetworkViewModel(IRoomSession session, IToastService toasts, INetworkAdapter adapter)
    {
        _session = session;
        _toasts = toasts;

        _session.SnapshotChanged += snapshot => Dispatcher.UIThread.Post(() => Apply(snapshot));

        TunnelAvailability = adapter.Availability;
        Apply(_session.Snapshot);
    }

    public ObservableCollection<RoomPeer> Peers { get; } = [];

    [ObservableProperty]
    private string _roomName = "Игровая комната";

    [ObservableProperty]
    private string _joinCode = string.Empty;

    [ObservableProperty]
    private string _displayName = "Игрок";

    [ObservableProperty]
    private RoomState _state;

    [ObservableProperty]
    private string _stateLabel = "Не подключено";

    [ObservableProperty]
    private string _stateDetail = "Создайте комнату или введите код приглашения.";

    [ObservableProperty]
    private string _minecraftAddress = "—";

    [ObservableProperty]
    private string _latencyLabel = "—";

    [ObservableProperty]
    private string _trafficLabel = "—";

    [ObservableProperty]
    private string _transportLabel = "—";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusyNetwork;

    [ObservableProperty]
    private string _inviteCode = "—";

    [ObservableProperty]
    private string _tunnelStatus = string.Empty;

    [ObservableProperty]
    private bool _tunnelAvailable;

    public TunAvailability TunnelAvailability { get; }

    private void Apply(RoomSnapshot snapshot)
    {
        State = snapshot.State;
        IsConnected = snapshot.State is RoomState.Connected or RoomState.WaitingForPeers;

        StateLabel = snapshot.State switch
        {
            RoomState.Offline => "Не подключено",
            RoomState.Creating => "Подключение…",
            RoomState.WaitingForPeers => "Ожидание игроков",
            RoomState.Connected => "Подключено",
            RoomState.Reconnecting => "Переподключение…",
            RoomState.Closed => "Комната закрыта",
            RoomState.Error => "Ошибка",
            _ => "—"
        };

        StateDetail = snapshot.Error
            ?? (snapshot.State == RoomState.WaitingForPeers
                ? "Поделитесь кодом приглашения с друзьями."
                : snapshot.State == RoomState.Connected
                    ? "Скопируйте адрес и подключитесь в Minecraft: Сетевая игра → Прямое подключение."
                    : "Создайте комнату или введите код приглашения.");

        Peers.Clear();
        foreach (var peer in snapshot.Peers)
        {
            Peers.Add(peer);
        }

        MinecraftAddress = snapshot.MinecraftAddress ?? "—";
        InviteCode = snapshot.Room?.JoinCode ?? "—";
        LatencyLabel = snapshot.LatencyMs > 0 ? $"{snapshot.LatencyMs} мс" : "—";
        TrafficLabel = $"{Converters.BytesToHumanConverter.Format(snapshot.BytesSent)} ↑ / {Converters.BytesToHumanConverter.Format(snapshot.BytesReceived)} ↓";
        TransportLabel = snapshot.Transport switch
        {
            TransportKind.Direct => "Прямое соединение",
            TransportKind.NatTraversal => "NAT traversal",
            TransportKind.Relay => "Relay",
            _ => "—"
        };

        TunnelAvailable = TunnelAvailability.IsAvailable;
        TunnelStatus = snapshot.TunnelStatusDetail ?? TunnelAvailability.Detail;
    }

    [RelayCommand]
    private async Task CreateRoomAsync()
    {
        IsBusyNetwork = true;
        try
        {
            var result = await _session.CreateRoomAsync(RoomName, DisplayName).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                _toasts.Error("Комната не создана", result.Message ?? "Сервер недоступен.");
                return;
            }

            _toasts.Success("Комната создана", $"Код приглашения: {result.Value!.Room?.JoinCode}");
        }
        finally
        {
            IsBusyNetwork = false;
        }
    }

    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        if (string.IsNullOrWhiteSpace(JoinCode))
        {
            _toasts.Warning("Код", "Введите код приглашения.");
            return;
        }

        IsBusyNetwork = true;
        try
        {
            var result = await _session.JoinAsync(JoinCode, DisplayName).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                _toasts.Error("Не удалось подключиться", result.Message ?? "Проверьте код.");
                return;
            }

            _toasts.Success("Подключено", $"Адрес для Minecraft: {result.Value!.MinecraftAddress}");
        }
        finally
        {
            IsBusyNetwork = false;
        }
    }

    [RelayCommand]
    private async Task LeaveAsync()
    {
        var result = await _session.LeaveAsync().ConfigureAwait(true);
        if (result.IsSuccess)
        {
            _toasts.Show("Вы вышли из комнаты", "Доступ к сети комнаты отозван.", ToastKind.Info);
        }
        else
        {
            _toasts.Error("Ошибка выхода", result.Message ?? "Попробуйте ещё раз.");
        }
    }

    [RelayCommand]
    private async Task KickAsync(RoomPeer peer)
    {
        var result = await _session.KickMemberAsync(peer.MemberId).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            _toasts.Show("Участник удалён", peer.DisplayName, ToastKind.Info);
        }
        else
        {
            _toasts.Error("Не удалось удалить", result.Message ?? "Только владелец может удалять участников.");
        }
    }

    [RelayCommand]
    private async Task CopyAddressAsync()
    {
        var address = await _session.CopyAddressAsync().ConfigureAwait(true);
        if (!address.IsSuccess)
        {
            _toasts.Warning("Адрес недоступен", address.Message ?? "Нет подключения.");
        }
        else
        {
            _toasts.Success("Адрес", address.Value ?? "Адрес скопирован.");
        }
    }

    /// <summary>Код приглашения всегда вводится в верхнем регистре (в Avalonia 12 нет CharacterCasing).</summary>
    partial void OnJoinCodeChanged(string value)
    {
        var normalized = value.ToUpperInvariant();
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            JoinCode = normalized;
        }
    }
}
