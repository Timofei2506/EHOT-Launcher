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
        // Свой контейнер на каждый раздел: состояние навигации не должно переезжать между тестами.
        var services = AppBootstrap.BuildServiceProvider();
        var main = services.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = main };
        window.Show();

        // Ждём полного завершения старта оболочки (инициализация состояния + переход на «Главную»):
        // пока старт в полёте, его собственный переход может перезаписать открытый раздел.
        Assert.True(
            WaitUntil(() => !main.IsBusy && CurrentKey(services) == "home", TimeSpan.FromSeconds(30)),
            "Оболочка не завершила стартовую навигацию на «Главную».");

        main.NavigateCommand.Execute(key);

        // Ждём именно смену страницы: NavigationService выставляет CurrentEntry до того,
        // как завершится OnNavigatedToAsync, и только потом уведомляет оболочку.
        var navigation = services.GetRequiredService<INavigationService>();
        Assert.True(
            WaitUntil(() => CurrentKey(services) == key
                            && navigation.Current is not null
                            && ReferenceEquals(main.CurrentPage, navigation.Current),
                TimeSpan.FromSeconds(20)),
            $"Раздел «{key}» не открылся: {Describe(window, main, services)}");

        // Дать представлению построиться и уложиться (шрифты, элементы списков).
        Pump(window, TimeSpan.FromSeconds(1.5));

        var page = main.CurrentPage;
        Assert.NotNull(page);

        // Имя View, которое построил ViewLocator: ищем в визуальном дереве именно
        // то представление, чей DataContext — открытая страница (страницы меняются
        // через переход, поэтому старое дерево какое-то время ещё висит в окне).
        var section = FindSection(window, page!);

        Assert.True(
            section is not null,
            $"Раздел «{key}» не показал содержимое (ViewModel {page!.GetType().Name}).");
        Assert.True(
            section!.GetType().Name == expectedViewName,
            $"Раздел «{key}»: ожидалось {expectedViewName}, получено {section.GetType().Name}. {Describe(window, main, services)}");

        // Заглушка ViewLocator — это TextBlock с текстом «Представление не найдено».
        var placeholder = FindText(window, "Представление не найдено");
        Assert.True(placeholder is null, $"Раздел «{key}» показал заглушку вместо View.");

        SaveScreen(window, key);
        window.Close();
    }

    private static UserControl? FindSection(Visual root, ViewModelBase page)
        => root.GetVisualDescendants()
            .OfType<UserControl>()
            // Обход идёт сверху вниз: первый совпавший — сам раздел, следующие — его дочерние View.
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, page));

    private static TextBlock? FindText(Visual root, string value) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.Text?.Contains(value, StringComparison.Ordinal) == true);

    /// <summary>Ключ раздела, открытого навигацией (её состояние — источник истины).</summary>
    private static string? CurrentKey(IServiceProvider services)
        => services.GetRequiredService<INavigationService>().CurrentEntry?.Key;

    /// <summary>Краткое состояние навигации — попадает в текст падения теста.</summary>
    private static string Describe(Window window, MainWindowViewModel main, IServiceProvider services)
    {
        var views = window.GetVisualDescendants()
            .OfType<UserControl>()
            .Select(v => $"{v.GetType().Name}({v.DataContext?.GetType().Name},{v.Bounds.Width:0}x{v.Bounds.Height:0})");
        return $"IsBusy={main.IsBusy}, ключ={CurrentKey(services)}, страница={main.CurrentPage?.GetType().Name}, View=[{string.Join("; ", views)}]";
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        Dispatcher.UIThread.RunJobs();
        return condition();
    }

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
