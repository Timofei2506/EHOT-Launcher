using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EHOT.Launcher.UI.ViewModels;

namespace EHOT.Launcher.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Перед закрытием останавливаем активные загрузки, сохранив .part-файлы:
    /// при следующем запуске установка продолжится с того же места.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        (DataContext as MainWindowViewModel)?.PrepareForExit();
        base.OnClosing(e);
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl/Cmd + K — глобальный поиск (как в современных приложениях).
        if (e.Key == Key.K && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ToggleSearchCommand.Execute(null);
                e.Handled = true;
            }
        }

        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm2 && vm2.IsSearchOpen)
        {
            vm2.ToggleSearchCommand.Execute(null);
            e.Handled = true;
        }

        await Task.CompletedTask;
    }
}
