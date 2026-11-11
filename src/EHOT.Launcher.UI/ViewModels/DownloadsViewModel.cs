using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Core.Downloads;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Менеджер загрузок: очередь, скорость, пауза/возобновление, история.</summary>
public sealed partial class DownloadsViewModel : ViewModelBase
{
    private readonly IDownloadManager _downloads;
    private readonly IDownloadHistoryStore _history;
    private readonly IToastService _toasts;
    private readonly DispatcherTimer _timer;

    public DownloadsViewModel(IDownloadManager downloads, IDownloadHistoryStore history, IToastService toasts)
    {
        _downloads = downloads;
        _history = history;
        _toasts = toasts;

        _downloads.Updated += _ => Refresh();
        _downloads.Finished += snapshot =>
        {
            if (snapshot.State == DownloadState.Completed)
            {
                _toasts.Success("Загрузка завершена", snapshot.DisplayName);
            }
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
    }

    public ObservableCollection<DownloadSnapshot> Active { get; } = [];

    public ObservableCollection<DownloadHistoryEntry> History { get; } = [];

    [ObservableProperty]
    private string _summary = "Нет активных загрузок";

    [ObservableProperty]
    private double _totalSpeed;

    public override async Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        _timer.Start();
        Refresh();
        await Task.CompletedTask;
    }

    public override Task OnNavigatedFromAsync()
    {
        _timer.Stop();
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        var snapshot = _downloads.Snapshot();
        Active.Clear();
        foreach (var item in snapshot)
        {
            Active.Add(item);
        }

        var running = snapshot.Count(s => s.State == DownloadState.Running);
        var queued = snapshot.Count(s => s.State == DownloadState.Queued);
        TotalSpeed = snapshot.Where(s => s.State == DownloadState.Running).Sum(s => s.BytesPerSecond);
        Summary = running == 0 && queued == 0
            ? "Нет активных загрузок"
            : $"Активных: {running} · В очереди: {queued} · {Converters.BytesToHumanConverter.Format((long)TotalSpeed)}/с";
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        var items = await _history.GetRecentAsync(50).ConfigureAwait(true);
        History.Clear();
        foreach (var item in items)
        {
            History.Add(item);
        }
    }

    [RelayCommand]
    private void Pause(DownloadSnapshot snapshot) => _downloads.Pause(snapshot.Id);

    [RelayCommand]
    private void Resume(DownloadSnapshot snapshot) => _downloads.Resume(snapshot.Id);

    [RelayCommand]
    private void Cancel(DownloadSnapshot snapshot) => _downloads.Cancel(snapshot.Id);
}
