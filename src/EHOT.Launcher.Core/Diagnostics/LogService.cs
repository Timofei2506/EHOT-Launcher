using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.Core.Diagnostics;

public enum LogLevel2
{
    Trace,
    Debug,
    Info,
    Warn,
    Error
}

public readonly record struct LogEntry(DateTimeOffset Timestamp, LogLevel2 Level, string Category, string Message, string? Exception = null);

/// <summary>
/// Логирование лаунчера: файл с ротацией + кольцевой буфер для экрана «Диагностика».
/// Все строки проходят через редакцию секретов.
/// </summary>
public interface ILogService
{
    IReadOnlyList<LogEntry> Recent { get; }

    event Action<LogEntry>? EntryAdded;

    void Write(LogLevel2 level, string category, string message, Exception? ex = null);

    void Info(string category, string message);
    void Warn(string category, string message, Exception? ex = null);
    void Error(string category, string message, Exception? ex = null);

    /// <summary>Путь к текущему файлу лога (для кнопки «Открыть папку логов»).</summary>
    string CurrentLogFile { get; }

    Task FlushAsync();
}

public sealed class LogService : ILogService
{
    private const int BufferSize = 2000;
    private static readonly TimeSpan MaxFileSize = TimeSpan.FromDays(1);

    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly Lock _fileGate = new();
    private readonly string _logDirectory;
    private readonly string _currentFile;
    private StreamWriter? _writer;
    private DateTime _currentDay = DateTime.UtcNow.Date;
    private bool _disposed;

    public LogService(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
        _currentFile = Path.Combine(_logDirectory, $"ehot-{DateTime.UtcNow:yyyyMMdd}.log");
        OpenWriter();
    }

    public IReadOnlyList<LogEntry> Recent => _recent.ToArray();

    public string CurrentLogFile => _currentFile;

    public event Action<LogEntry>? EntryAdded;

    public void Write(LogLevel2 level, string category, string message, Exception? ex = null)
    {
        var safeMessage = SecretRedactor.Redact(message);
        var safeException = ex is null ? null : SecretRedactor.Redact(ex.ToString());
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, category, safeMessage, safeException);

        _recent.Enqueue(entry);
        while (_recent.Count > BufferSize && _recent.TryDequeue(out _))
        {
        }

        EntryAdded?.Invoke(entry);

        if (level == LogLevel2.Trace)
        {
            return; // в файл пишем от Debug и выше
        }

        lock (_fileGate)
        {
            RotateIfNeeded();
            _writer?.WriteLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {category}: {safeMessage}");
            if (safeException is not null)
            {
                _writer?.WriteLine("    " + safeException.Replace("\n", "\n    "));
            }
        }
    }

    public void Info(string category, string message) => Write(LogLevel2.Info, category, message);

    public void Warn(string category, string message, Exception? ex = null) => Write(LogLevel2.Warn, category, message, ex);

    public void Error(string category, string message, Exception? ex = null) => Write(LogLevel2.Error, category, message, ex);

    public Task FlushAsync()
    {
        lock (_fileGate)
        {
            _writer?.Flush();
        }

        return Task.CompletedTask;
    }

    private void OpenWriter()
    {
        var stream = new FileStream(_currentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 8192, useAsync: false);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
    }

    private void RotateIfNeeded()
    {
        if (_currentDay == DateTime.UtcNow.Date)
        {
            return;
        }

        _writer?.Dispose();
        _currentDay = DateTime.UtcNow.Date;
        OpenWriter();

        // Чистим логи старше 14 дней.
        try
        {
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "ehot-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
            // чистка логов не должна ломать приложение
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_fileGate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>Удаление секретов из строк перед записью в лог/отправкой в поддержку.</summary>
public static partial class SecretRedactor
{
    private static readonly string[] SensitiveKeys =
    [
        "access_token", "refresh_token", "accessToken", "refreshToken", "authorization",
        "identityToken", "client_secret", "clientSecret", "password", "session",
        "minecraft_access_token", "XBL3.0", "userHash", "Authorization"
    ];

    [GeneratedRegex(@"(?i)(access_token|refresh_token|client_secret|password|session)\s*[=:]\s*""?[^""\s,&}]+")]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9._\-]{16,}")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"XBL3\.0\s+x=[^;\s]+")]
    private static partial Regex XboxRegex();

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var result = KeyValueSecretRegex().Replace(input, m => $"{m.Groups[1].Value}=***");
        result = JwtRegex().Replace(result, "***jwt***");
        result = BearerRegex().Replace(result, "Bearer ***");
        result = XboxRegex().Replace(result, "XBL3.0 x=***");
        return result;
    }

    public static bool ContainsSensitiveKey(string text)
        => SensitiveKeys.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Адаптер Microsoft.Extensions.Logging → ILogService (чтобы библиотеки писали в ту же трубу).</summary>
public sealed class LogServiceLoggerProvider(ILogService logService) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Adapter(logService, categoryName);

    public void Dispose()
    {
    }

    private sealed class Adapter(ILogService service, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var level = logLevel switch
            {
                LogLevel.Trace => LogLevel2.Trace,
                LogLevel.Debug => LogLevel2.Debug,
                LogLevel.Information => LogLevel2.Info,
                LogLevel.Warning => LogLevel2.Warn,
                _ => LogLevel2.Error
            };

            service.Write(level, category, formatter(state, exception), exception);
        }
    }
}
