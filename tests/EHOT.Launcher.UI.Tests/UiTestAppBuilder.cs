using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(EHOT.Launcher.UI.Tests.UiTestAppBuilder))]

namespace EHOT.Launcher.UI.Tests;

/// <summary>
/// Headless-хост для UI-тестов: собирает приложение так же, как боевой <c>Program.BuildAvaloniaApp</c>,
/// поэтому в тестах реально загружаются дизайн-система и тема Fluent.
/// </summary>
public static class UiTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<EHOT.Launcher.UI.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
