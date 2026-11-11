using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EHOT.Launcher.UI.Services;

public enum ToastKind
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3
}

public sealed partial class Toast : ObservableObject
{
    public Toast(string title, string message, ToastKind kind, TimeSpan? duration = null)
    {
        Title = title;
        Message = message;
        Kind = kind;
        Duration = duration ?? (kind == ToastKind.Error ? TimeSpan.FromSeconds(9) : TimeSpan.FromSeconds(5));
    }

    public string Title { get; }

    public string Message { get; }

    public ToastKind Kind { get; }

    public TimeSpan Duration { get; }

    [ObservableProperty]
    private bool _isVisible = true;

    public string Accent => Kind switch
    {
        ToastKind.Success => "#34D399",
        ToastKind.Warning => "#FBBF24",
        ToastKind.Error => "#F87171",
        _ => "#38BDF8"
    };
}

public interface IToastService
{
    ObservableCollection<Toast> Items { get; }

    void Show(string title, string message, ToastKind kind = ToastKind.Info);

    void Success(string title, string message);

    void Error(string title, string message);

    void Warning(string title, string message);
}

public sealed class ToastService : IToastService
{
    private const int MaxVisible = 4;

    public ObservableCollection<Toast> Items { get; } = [];

    public void Show(string title, string message, ToastKind kind = ToastKind.Info)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(title, message, kind));
            return;
        }

        var toast = new Toast(title, message, kind);
        Items.Insert(0, toast);
        while (Items.Count > MaxVisible)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        _ = DismissAfterAsync(toast);
    }

    public void Success(string title, string message) => Show(title, message, ToastKind.Success);

    public void Error(string title, string message) => Show(title, message, ToastKind.Error);

    public void Warning(string title, string message) => Show(title, message, ToastKind.Warning);

    private async Task DismissAfterAsync(Toast toast)
    {
        await Task.Delay(toast.Duration).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => Items.Remove(toast));
    }
}
