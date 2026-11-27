using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Галерея скриншотов из реальных папок игры (общей и инстансов).</summary>
public sealed partial class GalleryViewModel : ViewModelBase
{
    private readonly IGalleryService _gallery;
    private readonly IToastService _toasts;
    private readonly IClipboardService _clipboard;
    private IReadOnlyList<GalleryItem> _all = [];

    public GalleryViewModel(IGalleryService gallery, IToastService toasts, IClipboardService clipboard)
    {
        _gallery = gallery;
        _toasts = toasts;
        _clipboard = clipboard;
    }

    public ObservableCollection<GalleryItem> Items { get; } = [];
    public ObservableCollection<string> SourceFilters { get; } = ["Все папки"];

    [ObservableProperty]
    private GalleryItem? _selectedItem;

    [ObservableProperty]
    private GalleryItem? _itemPendingDelete;

    [ObservableProperty]
    private bool _isFullscreenOpen;

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private string _selectedSource = "Все папки";

    public override async Task OnNavigatedToAsync(CancellationToken ct = default) => await RefreshAsync(ct).ConfigureAwait(true);

    partial void OnSelectedSourceChanged(string value)
    {
        _ = value;
        ApplyFilter();
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            _all = await _gallery.ScanAsync(ct).ConfigureAwait(true);

            var current = SelectedSource;
            SourceFilters.Clear();
            SourceFilters.Add("Все папки");
            foreach (var source in _all.Select(i => i.SourceLabel).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s))
            {
                SourceFilters.Add(source);
            }

            SelectedSource = SourceFilters.Contains(current) ? current : "Все папки";
            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        var filtered = string.Equals(SelectedSource, "Все папки", StringComparison.OrdinalIgnoreCase)
            ? _all
            : _all.Where(i => string.Equals(i.SourceLabel, SelectedSource, StringComparison.OrdinalIgnoreCase));

        foreach (var item in filtered)
        {
            Items.Add(item);
        }

        HasItems = Items.Count > 0;
    }

    [RelayCommand]
    private void OpenPreview(GalleryItem? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedItem = item;
        IsFullscreenOpen = true;
    }

    [RelayCommand]
    private void CloseFullscreen() => IsFullscreenOpen = false;

    [RelayCommand]
    private void OpenOriginal(GalleryItem? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        if (!FileSystemActions.OpenFile(item.FullPath))
        {
            _toasts.Error("Файл не открылся", item.FileName);
        }
    }

    [RelayCommand]
    private void OpenFolder(GalleryItem? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        if (!FileSystemActions.RevealFile(item.FullPath))
        {
            _toasts.Error("Папка не открылась", Path.GetDirectoryName(item.FullPath) ?? item.FullPath);
        }
    }

    [RelayCommand]
    private async Task CopyImageAsync(GalleryItem? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        var copied = await _clipboard.SetBitmapAsync(item.FullPath).ConfigureAwait(true);
        if (copied)
        {
            _toasts.Success("Скриншот скопирован", item.FileName);
        }
        else
        {
            _toasts.Warning("Не удалось скопировать", "Система не приняла изображение в буфер обмена.");
        }
    }

    [RelayCommand]
    private void AskDelete(GalleryItem? item)
    {
        item ??= SelectedItem;
        if (item is not null)
        {
            ItemPendingDelete = item;
        }
    }

    [RelayCommand]
    private void CancelDelete() => ItemPendingDelete = null;

    [RelayCommand]
    private void ConfirmDelete()
    {
        var item = ItemPendingDelete;
        if (item is null)
        {
            return;
        }

        try
        {
            File.Delete(item.FullPath);
            _all = _all.Where(i => !string.Equals(i.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            Items.Remove(item);
            HasItems = Items.Count > 0;
            if (SelectedItem?.FullPath == item.FullPath)
            {
                SelectedItem = null;
                IsFullscreenOpen = false;
            }

            _toasts.Show("Скриншот удалён", item.FileName, ToastKind.Info);
        }
        catch (Exception ex)
        {
            _toasts.Error("Не удалось удалить", ex.Message);
        }
        finally
        {
            ItemPendingDelete = null;
        }
    }
}
