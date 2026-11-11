using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EHOT.Launcher.UI.Services;
using EHOT.Launcher.UI.ViewModels;
using EHOT.Launcher.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.UI;

public partial class App : Application
{
    private IServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: все зависимости собираются здесь.
            _services = AppBootstrap.BuildServiceProvider();
            var logProvider = _services.GetRequiredService<ILoggerFactory>();

            var window = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>()
            };

            desktop.MainWindow = window;

            // --- Ссылки ehot://… и файлы .mrpack -----------------------
            // Аргументы приходят из установщика/браузера/ярлыка. Если лаунчер уже запущен,
            // второй экземпляр передаёт ссылку первому и завершается (одно окно — один лаунчер).
            var router = _services.GetRequiredService<DeepLinkRouter>();
            var args = DeepLinkRouter.StartupArguments;

            var alreadyRunning = args.Count > 0
                                 && DeepLinkRouter.TryForwardToRunningInstanceAsync(args[0]).GetAwaiter().GetResult();
            if (alreadyRunning)
            {
                logProvider.CreateLogger("UI").LogInformation("Ссылка передана запущенному экземпляру, выход");
                desktop.Shutdown();
                return;
            }

            router.StartListening();
            foreach (var argument in args)
            {
                _ = router.HandleAsync(argument);
            }

            desktop.Exit += (_, _) => router.Dispose();
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) => (_services as IDisposable)?.Dispose();

            logProvider.CreateLogger("UI").LogInformation("EHOT Launcher запущен");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
