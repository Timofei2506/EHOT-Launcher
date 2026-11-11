using System.Collections.Concurrent;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Game.Minecraft;
using EHOT.Launcher.Storage;

namespace EHOT.Launcher.Game.Launch;

public sealed record LaunchSession(
    string SessionId,
    string ProfileId,
    int ProcessId,
    DateTimeOffset StartedAt,
    string LogPath);

public sealed record LaunchOutcome(LaunchStatus Status, int? ExitCode, string? Error, TimeSpan Duration);

public interface IGameLauncher
{
    Task<OperationResult<LaunchSession>> LaunchAsync(
        GameProfile profile,
        AccountInfo account,
        string accessToken,
        IReadOnlyDictionary<string, string>? extraUserProperties = null,
        CancellationToken ct = default);

    bool IsRunning(string profileId);

    event Action<LaunchSession>? SessionStarted;

    event Action<string, LaunchOutcome>? SessionEnded;

    event Action<string, ProcessOutputLine>? OutputReceived;

    Task StopAsync(string profileId);

    IReadOnlyList<LaunchSession> ActiveSessions { get; }
}

/// <summary>
/// Запуск Minecraft: подготовка аргументов, classpath, логирование вывода в файл,
/// отслеживание завершения и человекочитаемая диагностика ошибок.
/// </summary>
public sealed class GameLauncher(
    IMinecraftVersionService versions,
    ILaunchArgumentBuilder argumentBuilder,
    IProcessRunner processRunner,
    ILaunchHistoryRepository history,
    IProfileRepository profiles,
    ILogService log) : IGameLauncher
{
    private readonly ConcurrentDictionary<string, RunningGame> _running = new(StringComparer.Ordinal);

    public event Action<LaunchSession>? SessionStarted;

    public event Action<string, LaunchOutcome>? SessionEnded;

    public event Action<string, ProcessOutputLine>? OutputReceived;

    public IReadOnlyList<LaunchSession> ActiveSessions => _running.Values.Select(r => r.Session).ToArray();

    public bool IsRunning(string profileId) => _running.ContainsKey(profileId);

    public async Task<OperationResult<LaunchSession>> LaunchAsync(
        GameProfile profile,
        AccountInfo account,
        string accessToken,
        IReadOnlyDictionary<string, string>? extraUserProperties = null,
        CancellationToken ct = default)
    {
        if (_running.ContainsKey(profile.Id))
        {
            return OperationResult<LaunchSession>.Fail(ErrorCode.Conflict, "Этот профиль уже запущен.");
        }

        var instanceDir = profile.InstanceDirectory;
        var versionsDir = Path.Combine(instanceDir, "versions");
        var versionId = string.IsNullOrEmpty(profile.LoaderVersion) || profile.Loader == LoaderKind.Vanilla
            ? profile.MinecraftVersion
            : ResolveLoaderVersionId(profile);

        var resolved = await versions.ResolveAsync(versionId, versionsDir, ct).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return OperationResult<LaunchSession>.Fail(resolved.Code,
                resolved.Message ?? $"Версия {versionId} не установлена. Установите её на странице «Версии».");
        }

        var version = resolved.Value!;

        // Проверяем наличие клиентского jar и библиотек — иначе даём понятную ошибку, а не «класс не найден».
        var (classpath, missing) = argumentBuilder.BuildClasspath(version, Path.Combine(instanceDir, "libraries"));
        if (missing.Count > 0)
        {
            return OperationResult<LaunchSession>.Fail(ErrorCode.InstallIncomplete,
                $"Установка не завершена: отсутствует {missing.Count} файл(ов). Повторите установку версии {profile.MinecraftVersion}.");
        }

        // Java.
        var javaPath = profile.JavaPath;
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
        {
            return OperationResult<LaunchSession>.Fail(ErrorCode.JavaNotFound,
                "Java не выбрана. Укажите путь к Java в настройках профиля.");
        }

        var logDir = Path.Combine(instanceDir, "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"launch-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");

        var assetsDir = Path.Combine(instanceDir, "assets");
        var nativesDir = Path.Combine(instanceDir, "versions", versionId, "natives");
        var gameDir = Path.Combine(instanceDir, "game");
        Directory.CreateDirectory(gameDir);

        var userType = account.Kind == AccountKind.Microsoft ? "msa" : "legacy";
        var clientId = "ehot-launcher";

        var context = new LaunchContext
        {
            MinecraftVersion = versionId,
            VersionType = version.Type,
            GameDirectory = gameDir,
            AssetsDirectory = assetsDir,
            AssetsIndex = version.AssetsId,
            LibraryDirectory = Path.Combine(instanceDir, "libraries"),
            NativesDirectory = nativesDir,
            Classpath = classpath,
            MainClass = version.MainClass,
            Username = account.Username,
            Uuid = account.Uuid.ToString("N"),
            AccessToken = accessToken,
            UserType = userType,
            ClientId = clientId,
            XboxUid = account.XboxUserId ?? string.Empty,
            GameAssetsDirectory = Path.Combine(assetsDir, "virtual", version.AssetsId),
            LauncherWidth = 925,
            LauncherHeight = 530,
            UserProperties = "{}",
            ProfileName = profile.Name,
            LauncherName = "EHOTLauncher",
            LauncherVersion = "0.1.0",
            MemoryMb = profile.AllocatedMemoryMb,
            LoggingArgument = BuildLoggingArgument(version, assetsDir)
        };

        var jvmArgs = argumentBuilder.BuildJvmArguments(version, context).ToList();
        jvmArgs.AddRange(profile.ExtraJvmArgs);
        var gameArgs = argumentBuilder.BuildGameArguments(version, context).ToList();
        gameArgs.AddRange(profile.ExtraGameArgs);

        var request = new ProcessRequest(javaPath, [.. jvmArgs, .. gameArgs], gameDir);

        var sessionId = Guid.NewGuid().ToString("N");
        var record = new LaunchRecord
        {
            Id = sessionId,
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            MinecraftVersion = profile.MinecraftVersion,
            AccountName = account.Username,
            Status = LaunchStatus.Started,
            StartedAt = DateTimeOffset.UtcNow,
            LogPath = logPath
        };

        try
        {
            await history.AddAsync(record, ct).ConfigureAwait(false);

            var handle = processRunner.Start(request);
            var session = new LaunchSession(sessionId, profile.Id, handle.ProcessId, DateTimeOffset.UtcNow, logPath);
            var running = new RunningGame(session, handle, logPath, record);
            _running[profile.Id] = running;

            handle.OutputReceived += line =>
            {
                running.Append(line);
                OutputReceived?.Invoke(profile.Id, line);
            };

            handle.Exited += exitCode => _ = OnExitedAsync(running, exitCode);

            await profiles.MarkLaunchedAsync(profile.Id, ct).ConfigureAwait(false);
            SessionStarted?.Invoke(session);

            log.Info("Launch", $"Minecraft запущен: профиль '{profile.Name}', PID {handle.ProcessId}, лог {logPath}");
            return OperationResult<LaunchSession>.Ok(session);
        }
        catch (Exception ex)
        {
            log.Error("Launch", "Не удалось запустить Minecraft", ex);
            record = record with { Status = LaunchStatus.Failed, Error = ex.Message, EndedAt = DateTimeOffset.UtcNow };
            await history.UpdateAsync(record, CancellationToken.None).ConfigureAwait(false);
            return OperationResult<LaunchSession>.FromException(ex, ErrorCode.GameCrash);
        }
    }

    public async Task StopAsync(string profileId)
    {
        if (_running.TryGetValue(profileId, out var running))
        {
            log.Info("Launch", $"Остановка Minecraft для профиля {profileId}");
            running.Handle.Kill();
            await running.Handle.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private async Task OnExitedAsync(RunningGame running, int exitCode)
    {
        var outcome = new LaunchOutcome(
            exitCode == 0 ? LaunchStatus.Exited : LaunchStatus.Failed,
            exitCode,
            exitCode == 0 ? null : DescribeExitCode(exitCode, running.TailLines),
            DateTimeOffset.UtcNow - running.Session.StartedAt);

        _running.TryRemove(running.Session.ProfileId, out _);
        await running.DisposeAsync().ConfigureAwait(false);

        var record = running.Record with
        {
            Status = outcome.Status,
            ExitCode = exitCode,
            Error = outcome.Error,
            EndedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await history.UpdateAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn("Launch", "Не удалось сохранить историю запуска", ex);
        }

        log.Info("Launch", $"Minecraft завершился: код {exitCode}, длительность {outcome.Duration:hh\\:mm\\:ss}");
        SessionEnded?.Invoke(running.Session.ProfileId, outcome);
    }

    /// <summary>Человекочитаемая диагностика по коду возврата и последним строкам лога.</summary>
    public static string DescribeExitCode(int exitCode, IReadOnlyList<string> tail)
    {
        var text = tail.Count > 0 ? string.Join('\n', tail) : string.Empty;

        if (text.Contains("UnsupportedClassVersionError", StringComparison.Ordinal))
        {
            return "Версия Java не подходит для этой сборки. Выберите Java нужной версии (обычно 17 или 21) в настройках профиля.";
        }

        if (text.Contains("OutOfMemoryError", StringComparison.Ordinal))
        {
            return "Не хватило памяти. Увеличьте объём RAM в настройках профиля или закройте другие приложения.";
        }

        if (text.Contains("Missing or unsupported mandatory dependencies", StringComparison.Ordinal)
            || text.Contains("Missing or unsupported mandatory", StringComparison.Ordinal))
        {
            return "Не хватает зависимых модов. Откройте «Сборки» и проверьте зависимости.";
        }

        if (text.Contains("java.lang.NoClassDefFoundError", StringComparison.Ordinal))
        {
            return "Повреждена установка: отсутствует класс из библиотеки. Переустановите версию.";
        }

        return exitCode switch
        {
            0 => string.Empty,
            1 => "Игра завершилась с ошибкой (код 1). Подробности — в логе запуска.",
            -1073740791 => "Игра аварийно завершилась (0xC0000409). Часто причина — конфликт модов или несовместимая Java.",
            -1073741819 => "Нарушение доступа (0xC0000005). Обновите драйвер видеокарты.",
            134 or 139 => "Игра завершилась аварийно (сигнал). Проверьте совместимость модов и версию Java.",
            _ => $"Игра завершилась с кодом {exitCode}."
        };
    }

    private static string ResolveLoaderVersionId(GameProfile profile) => profile.Loader switch
    {
        LoaderKind.Fabric => $"fabric-loader-{profile.LoaderVersion}-{profile.MinecraftVersion}",
        LoaderKind.Quilt => $"quilt-loader-{profile.LoaderVersion}-{profile.MinecraftVersion}",
        LoaderKind.Forge => $"{profile.MinecraftVersion}-forge-{profile.LoaderVersion}",
        LoaderKind.NeoForge => $"neoforge-{profile.LoaderVersion}",
        _ => profile.MinecraftVersion
    };

    private static string? BuildLoggingArgument(ResolvedVersion version, string assetsDir)
    {
        if (version.Logging is null || string.IsNullOrEmpty(version.Logging.Argument))
        {
            return null;
        }

        var logConfig = Path.Combine(assetsDir, "log_configs", version.Logging.File?.Id ?? "client-1.12.xml");
        return version.Logging.Argument.Replace("${path}", logConfig, StringComparison.Ordinal);
    }

    private sealed class RunningGame(LaunchSession session, IProcessHandle handle, string logPath, LaunchRecord record) : IAsyncDisposable
    {
        private readonly StreamWriter _writer = CreateWriter(logPath);
        private readonly Lock _gate = new();
        private readonly Queue<string> _tail = new();

        public LaunchSession Session { get; } = session;

        public IProcessHandle Handle { get; } = handle;

        public LaunchRecord Record { get; } = record;

        public IReadOnlyList<string> TailLines
        {
            get
            {
                lock (_gate)
                {
                    return _tail.ToArray();
                }
            }
        }

        public void Append(ProcessOutputLine line)
        {
            lock (_gate)
            {
                _writer.WriteLine($"[{line.Timestamp:HH:mm:ss}] {(line.IsError ? "[ERR]" : "[OUT]")} {Core.Diagnostics.SecretRedactor.Redact(line.Text)}");
                _tail.Enqueue(line.Text);
                while (_tail.Count > 40)
                {
                    _tail.Dequeue();
                }
            }
        }

        private static StreamWriter CreateWriter(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };
        }

        public async ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _writer.Dispose();
            }

            await Handle.DisposeAsync().ConfigureAwait(false);
        }
    }
}

