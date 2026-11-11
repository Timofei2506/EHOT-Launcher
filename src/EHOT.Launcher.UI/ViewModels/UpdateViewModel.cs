using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.UI.Services;
using EHOT.Launcher.Update;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>
/// Баннер обновления в шапке: проверка при старте, показ «что нового», скачивание и установка.
/// Ничего не устанавливается без нажатия пользователя; при выключенной авто-проверке не лезем в сеть.
/// </summary>
public sealed partial class UpdateViewModel : ViewModelBase
{
    private readonly IUpdateService _updates;
    private readonly IToastService _toasts;
    private readonly ILogService _log;

    public UpdateViewModel(IUpdateService updates, IToastService toasts, ILogService log)
    {
        _updates = updates;
        _toasts = toasts;
        _log = log;

        VersionLabel = $"v{_updates.CurrentVersion}";
    }

    public string VersionLabel { get; }

    public bool IsSupported => _updates.IsConfigured;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _availableVersion;

    [ObservableProperty]
    private string? _releaseNotes;

    [ObservableProperty]
    private string? _errorText;

    private UpdateCheckResult? _lastCheck;
    private UpdatePlan? _plan;

    public bool HasUpdate => _lastCheck?.HasUpdate == true;

    public bool IsVisible => HasUpdate || IsReady || IsDownloading;

    /// <summary>Проверка при запуске и повторно по кнопке. cancelToken позволяет отменить «вручную».</summary>
    [RelayCommand]
    private async Task CheckAsync(CancellationToken ct)
    {
        if (!_updates.IsConfigured)
        {
            StatusText = "Канал обновлений не настроен в этой сборке.";
            return;
        }

        IsChecking = true;
        ErrorText = null;
        try
        {
            var result = await _updates.CheckAsync(includePrerelease: true, ct).ConfigureAwait(true);
            _lastCheck = result;

            switch (result.Status)
            {
                case UpdateAvailability.Available:
                    AvailableVersion = $"v{result.Release!.Version}";
                    ReleaseNotes = TrimNotes(result.Release.Notes);
                    StatusText = $"Доступно обновление {AvailableVersion}";
                    _toasts.Show("Обновление", $"Вышла версия {AvailableVersion}.", ToastKind.Info);
                    break;

                case UpdateAvailability.UpToDate:
                    StatusText = $"Установлена последняя версия ({VersionLabel})";
                    AvailableVersion = null;
                    ReleaseNotes = null;
                    break;

                case UpdateAvailability.Failed:
                    ErrorText = result.Message;
                    StatusText = "Не удалось проверить обновления";
                    _log.Warn("Update", result.Message ?? "проверка не удалась");
                    break;

                default:
                    StatusText = result.Message ?? "Канал обновлений не настроен.";
                    break;
            }

            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(IsVisible));
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Скачивание файла обновления с прогрессом.</summary>
    [RelayCommand]
    private async Task DownloadAsync(CancellationToken ct)
    {
        if (_lastCheck?.Release is not { } release)
        {
            return;
        }

        IsDownloading = true;
        ErrorText = null;
        try
        {
            var progress = new Progress<UpdateProgress>(p => Dispatcher.UIThread.Post(() =>
            {
                Progress = p.Fraction;
                StatusText = p.Message;
            }));

            _plan = await _updates.DownloadAsync(release, progress, ct).ConfigureAwait(true);
            IsReady = true;
            StatusText = "Обновление скачано, готово к установке";
            _toasts.Success("Обновление готово", "Нажмите «Установить и перезапустить».");
            OnPropertyChanged(nameof(IsVisible));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            ErrorText = ex.Message;
            StatusText = "Скачивание не удалось";
            _log.Warn("Update", $"Скачивание обновления: {ex.Message}");
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Установка: подмена файлов после выхода и перезапуск (на Windows), иначе — честный отказ.</summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        if (_plan is null)
        {
            return;
        }

        try
        {
            await _updates.ApplyAsync(_plan).ConfigureAwait(true);
            _toasts.Show("Установка", "Лаунчер закроется, обновится и запустится снова.", ToastKind.Info);

            // Даём тосту показаться и выходим: файлы нельзя менять у работающего процесса.
            await Task.Delay(1200).ConfigureAwait(true);
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch (PlatformNotSupportedException ex)
        {
            _toasts.Warning("Установка вручную", ex.Message);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            _log.Error("Update", "Установка обновления не удалась", ex);
            _toasts.Error("Не удалось установить", ex.Message);
        }
    }

    /// <summary>Открыть страницу релиза в браузере (например, если авто-установка недоступна).</summary>
    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = _lastCheck?.Release?.HtmlUrl;
        if (url is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.ToString())
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _toasts.Warning("Не удалось открыть браузер", url.ToString());
            _log.Warn("Update", ex.Message);
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        _lastCheck = null;
        AvailableVersion = null;
        ReleaseNotes = null;
        _plan = null;
        IsReady = false;
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>Заметки релиза бывают длинными — в баннере показываем первые строки.</summary>
    private static string? TrimNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var lines = notes.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var text = string.Join('\n', lines.Take(6));
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}
