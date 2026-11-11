namespace EHOT.Launcher.Core.Platform;

/// <summary>Уведомления ОС. Реализация может быть no-op, если ОС их не поддерживает.</summary>
public interface INotificationService
{
    /// <summary>Поддерживаются ли системные уведомления на этой платформе.</summary>
    bool IsSupported { get; }

    Task NotifyAsync(string title, string message, CancellationToken ct = default);
}

/// <summary>Запуск внешних процессов без привязки к ОС-API.</summary>
public interface IProcessRunner
{
    /// <summary>Запустить процесс и дождаться завершения, собирая stdout/stderr построчно.</summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default);

    /// <summary>Запустить долгоживущий процесс (например, Minecraft) и отдавать поток вывода.</summary>
    IProcessHandle Start(ProcessRequest request);
}

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IProcessHandle : IAsyncDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    int ExitCode { get; }

    /// <summary>Событие строки вывода (stdout и stderr объединены, с префиксом потока).</summary>
    event Action<ProcessOutputLine>? OutputReceived;

    event Action<int>? Exited;

    Task<int> WaitForExitAsync(CancellationToken ct = default);

    void Kill();
}

public readonly record struct ProcessOutputLine(DateTimeOffset Timestamp, bool IsError, string Text);

/// <summary>Проверка и повышение сетевых привилегий (нужно для TUN-адаптера).</summary>
public interface INetworkPrivileges
{
    /// <summary>Есть ли уже необходимые права.</summary>
    bool HasTunnelPrivileges { get; }

    /// <summary>Может ли лаунчер запросить повышение прав (UAC / авторизация / установка хелпера).</summary>
    bool CanRequestElevation { get; }

    /// <summary>Человекочитаемое объяснение текущего состояния для UI.</summary>
    string Describe(TunnelPrivilegeState state);
}

public enum TunnelPrivilegeState
{
    Granted = 0,
    ElevationRequired = 1,
    HelperNotInstalled = 2,
    NotSupported = 3
}
