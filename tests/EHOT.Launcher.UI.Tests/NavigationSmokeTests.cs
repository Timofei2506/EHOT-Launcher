using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EHOT.Launcher.UI.Services;
using EHOT.Launcher.UI.ViewModels;
using EHOT.Launcher.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EHOT.Launcher.UI.Tests;

/// <summary>
/// Проверки «каждый раздел действительно открывается».
/// Ловят две реальные регрессии: (1) ViewLocator не находит View по имени ViewModel,
/// (2) страница падает при загрузке XAML / при инициализации ViewModel.
/// Дополнительно сохраняет скриншоты всех разделов в build/ui-screens для визуальной проверки.
/// </summary>
public sealed class NavigationSmokeTests
{
    private static string ScreenDirectory
    {
        get
        {
            // Скриншоты — только в build/ (эта папка исключена из снапшота воркспейса).
            var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build", "ui-screens"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static readonly Lazy<IServiceProvider> SharedServices = new(AppBootstrap.BuildServiceProvider);

    /// <summary>Все разделы бокового меню: ключ навигации и ожидаемый тип View.</summary>
    public static TheoryData<string, string> Sections => new()
    {
        { "home", "HomeView" },
        { "play", "PlayView" },
        { "versions", "VersionsView" },
        { "mods", "BrowseModsView" },
        { "modpacks", "BrowseModpacksView" },
        { "resourcepacks", "BrowseResourcePacksView" },
        { "shaders", "BrowseShadersView" },
        { "worlds", "BrowseWorldsView" },
        { "network", "GameNetworkView" },
        { "accounts", "AccountsView" },
        { "downloads", "DownloadsView" },
        { "settings", "SettingsView" },
        { "about", "AboutView" }
    };

    [AvaloniaTheory]
    [MemberData(nameof(Sections))]
    public void Section_OpensAndRendersWithoutPlaceholder(string key, string expectedViewName)
    {
        // Провайдер общий на все разделы: иначе создание и прогрев страниц выполняются
        // одновременно в нескольких прогонах @AvaloniaTheory (диспетчер общий, зависание).
        var services = SharedServices.Value;
        var main = services.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = main };
        window.Show();

        // Ждём, пока оболочка выполнит стартовую навигацию.
        Pump(window, TimeSpan.FromSeconds(2));

        main.NavigateCommand.Execute(key);
        Pump(window, TimeSpan.FromSeconds(2));

        var page = main.CurrentPage;
        Assert.NotNull(page);

        // Имя View, которое построил ViewLocator: ищем в визуальном дереве.
        var section = FindSection(window);
        Assert.True(
            section is not null,
            $"Раздел «{key}» не показал содержимое (ViewModel {page!.GetType().Name}).");
        Assert.Equal(expectedViewName, section!.GetType().Name);

        // Заглушка ViewLocator — это TextBlock с текстом «Представление не найдено».
        var placeholder = FindText(window, "Представление не найдено");
        Assert.True(placeholder is null, $"Раздел «{key}» показал заглушку вместо View.");

        SaveScreen(window, key);
        window.Close();
    }

    private static UserControl? FindSection(Visual root)
    {
        foreach (var child in root.GetVisualDescendants())
        {
            if (child is ContentControl { Content: ViewModelBase } presenter)
            {
                // Presentation-контейнер страницы: ищем его визуального ребёнка-View.
                foreach (var view in presenter.GetVisualDescendants())
                {
                    if (view is UserControl userControl)
                    {
                        return userControl;
                    }
                }
            }
        }

        return null;
    }

    private static TextBlock? FindText(Visual root, string value) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.Text?.Contains(value, StringComparison.Ordinal) == true);

    private static void Pump(Window window, TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void SaveScreen(Window window, string key)
    {
        try
        {
            var frame = window.CaptureRenderedFrame();
            if (frame is null) { Console.WriteLine($"[screen] {key}: кадр не получен"); return; }
            frame.Save(Path.Combine(ScreenDirectory, $"{key}.png"), PngBitmapEncoderOptions.Default);
        }
        catch (Exception ex)
        {
            // Скриншот — вспомогательный артефакт, но причину видно в выводе теста.
            Console.WriteLine($"[screen] {key}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
