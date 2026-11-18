using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Game.Import;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Импорт пользовательского контента из каталога другого лаунчера (<c>.minecraft</c>).</summary>
public sealed partial class ImportViewModel : ViewModelBase
{
    private readonly IForeignLauncherImportService _import;
    private readonly INavigationService _navigation;
    private readonly IAppPaths _paths;
    private readonly ILogService _log;
    private readonly IToastService _toasts;

    public ImportViewModel(
        IForeignLauncherImportService import,
        INavigationService navigation,
        IAppPaths paths,
        ILogService log,
        IToastService toasts)
    {
        _import = import;
        _navigation = navigation;
        _paths = paths;
        _log = log;
        _toasts = toasts;

        InstanceName = string.Empty;
        MinecraftVersion = string.Empty;
    }

    [ObservableProperty]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private ImportScanResult? _scanResult;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _instanceName;

    [ObservableProperty]
    private string _minecraftVersion;

    [ObservableProperty]
    private ImportSelection _selection = new();

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private ImportResult? _importResult;

    public static string DefaultPathHint
    {
        get
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appData))
            {
                return $"{appData}\\.minecraft (Windows) · ~/Library/Application Support/minecraft (macOS) · ~/.minecraft (Linux)";
            }

            return "~/.minecraft — укажите путь вручную";
        }
    }

    public string AppDataRoot => _paths.AppDataDirectory;

    [RelayCommand]
    private async Task ScanAsync(CancellationToken ct = default)
    {
        IsScanning = true;
        ImportResult = null;
        try
        {
            ScanResult = await _import.ScanAsync(SourcePath, ct).ConfigureAwait(true);
            if (ScanResult is { DetectedVersion: { Length: > 0 } })
            {
                MinecraftVersion = ScanResult.DetectedVersion!;
            }

            _log.Info("ImportViewModel", $"Сканирование завершено: {ScanResult.Summary}");
        }
        catch (Exception ex)
        {
            _log.Error("ImportViewModel", $"Сканирование не удалось: {ex.Message}", ex);
            _toasts.Error("Сканирование не удалось", ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task ImportAsync(CancellationToken ct = default)
    {
        if (ScanResult is null || !ScanResult.CanImport)
        {
            _toasts.Show("Импорт", "Нет подходящего контента для импорта. Сначала выберите папку .minecraft и нажмите Сканировать.", ToastKind.Info);
            return;
        }

        IsImporting = true;
        ImportResult = null;
        try
        {
            var result = await _import.ImportAsync(
                SourcePath,
                InstanceName,
                MinecraftVersion,
                Selection,
                ct).ConfigureAwait(true);

            ImportResult = result;
            if (result.Success)
            {
                _toasts.Success("Импорт завершён", result.Summary);
                await _navigation.NavigateAsync("play", null, ct).ConfigureAwait(true);
            }
            else
            {
                _toasts.Error("Импорт не удался", result.Error ?? "Неизвестная ошибка.");
            }
        }
        catch (Exception ex)
        {
            _log.Error("ImportViewModel", $"Импорт не удался: {ex.Message}", ex);
            _toasts.Error("Импорт не удался", ex.Message);
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    private void ClearResult() => ImportResult = null;

    public override Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        ImportResult = null;
        ScanResult = null;
        return Task.CompletedTask;
    }
}
