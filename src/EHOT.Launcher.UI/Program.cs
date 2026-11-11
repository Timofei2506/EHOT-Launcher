using Avalonia;

namespace EHOT.Launcher.UI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Внешние ссылки (ehot://join/CODE, путь к .mrpack) приходят аргументами командной строки.
        // Запоминаем их здесь: App сможет обработать их после того, как поднимется окно.
        DeepLinkRouter.StartupArguments = args
            .Select(a => a.Trim())
            .Where(a => a.Length > 0 && !a.StartsWith('-'))
            .ToArray();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Последний рубеж: не молчим, а сообщаем пользователю текстом, даже если UI не поднялся.
            Console.Error.WriteLine("EHOT Launcher не смог запуститься: " + ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
