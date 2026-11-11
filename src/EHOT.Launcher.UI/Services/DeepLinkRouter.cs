using System.IO.Pipes;
using Avalonia.Controls;
using Avalonia.Threading;
using EHOT.Launcher.UI.Services;
using EHOT.Launcher.UI.ViewModels;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.UI;

/// <summary>
/// Приём ссылок `ehot://…` (из установщика, браузера, ярлыка) и файлов .mrpack.
/// Один экземпляр приложения: если лаунчер уже запущен, второй экземпляр передаёт ссылку первому
/// через именованный канал и завершается. Так двойной клик по приглашению не открывает две копии.
/// </summary>
public sealed class DeepLinkRouter : IDisposable
{
    // Именованный канал (Windows) / Unix-сокет (Linux, macOS). Одно имя на пользователя.
    private const string PipeName = "ehot-launcher-deeplink";
    private const int ListenBufferBytes = 4096;

    private readonly ILogger _logger;
    private readonly INavigationService _navigation;
    private readonly Func<Window?> _mainWindow;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _activeListener;

    public DeepLinkRouter(ILogger logger, INavigationService navigation, Func<Window?> mainWindow)
    {
        _logger = logger;
        _navigation = navigation;
        _mainWindow = mainWindow;
    }

    /// <summary>Аргументы командной строки, которые ОС передала приложению (включая код приглашения).</summary>
    public static IReadOnlyList<string> StartupArguments { get; set; } = [];

    /// <summary>
    /// Пытается передать ссылку уже запущенному экземпляру.
    /// Возвращает true, если передача удалась (значит, этот процесс должен завершиться).
    /// </summary>
    public static async Task<bool> TryForwardToRunningInstanceAsync(string argument, CancellationToken ct = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await client.ConnectAsync(1000, ct).ConfigureAwait(false);
            await using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteAsync(argument.AsMemory(), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            // Никто не слушает — значит, этот экземпляр первый и должен сам всё обработать.
            return false;
        }
    }

    /// <summary>Начинает слушать передачи от последующих экземпляров.</summary>
    public void StartListening()
    {
        _activeListener?.Cancel();
        _activeListener = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var token = _activeListener.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server);
                    var buffer = new char[ListenBufferBytes];
                    var read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    if (read > 0)
                    {
                        var payload = new string(buffer, 0, read).Trim('\0', '\r', '\n', ' ');
                        if (payload.Length > 0)
                        {
                            await Dispatcher.UIThread.InvokeAsync(() => Handle(payload));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Канал диплинков: {Message}", ex.Message);
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    /// <summary>Обрабатывает ссылку/файл: поднимает окно и ведёт в нужный экран.</summary>
    public async Task HandleAsync(string argument)
    {
        try
        {
            var link = DeepLinkParser.Parse(argument)
                       ?? (File.Exists(argument) && argument.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
                           ? new DeepLink(DeepLinkKind.Install, FilePath: argument)
                           : null);

            if (link is null)
            {
                _logger.LogDebug("Ссылка не распознана: {Argument}", argument);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var window = _mainWindow();
                if (window is not null)
                {
                    if (window.WindowState == WindowState.Minimized)
                    {
                        window.WindowState = WindowState.Normal;
                    }

                    window.Activate();
                }
            });

            switch (link.Kind)
            {
                case DeepLinkKind.Join:
                case DeepLinkKind.Room:
                    await _navigation.NavigateAsync("network", link).ConfigureAwait(false);
                    break;

                case DeepLinkKind.Install:
                    await _navigation.NavigateAsync("modpacks", link).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось обработать ссылку {Argument}", argument);
        }
    }

    private void Handle(string argument) => _ = HandleAsync(argument);

    public void Dispose()
    {
        _cts.Cancel();
        _activeListener?.Cancel();
        _cts.Dispose();
        _activeListener?.Dispose();
    }
}
