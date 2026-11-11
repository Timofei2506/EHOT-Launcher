using Avalonia;

namespace EHOT.Launcher.UI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
