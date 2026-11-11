using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace EHOT.Launcher.Core.Downloads;

public enum DownloadState
{
    Queued,
    Running,
    Verifying,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public sealed record DownloadRequest
{
    public required string Id { get; init; }
    public required Uri Url { get; init; }
    public required string DestinationPath { get; init; }
    public string? DisplayName { get; init; }
    public long? ExpectedSize { get; init; }
    public string? ExpectedSha1 { get; init; }
    public string? ExpectedSha512 { get; init; }

    /// <summary>Группа (например, "minecraft-1.21.4"), для отмены пачкой.</summary>
    public string? Group { get; init; }

    public int MaxRetries { get; init; } = 3;
    public bool OverwriteExisting { get; init; } = false;

    /// <summary>Если файл уже существует и хэш совпадает — не скачивать.</summary>
    public bool SkipIfVerified { get; init; } = true;
}

public sealed record DownloadSnapshot(
    string Id,
    string DisplayName,
    string Group,
    DownloadState State,
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond,
    TimeSpan? Eta,
    string? Error,
    string DestinationPath)
{
    public double Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : 0;
}

/// <summary>История загрузок (реализуется в Storage, чтобы Core не зависел от БД).</summary>
public interface IDownloadHistoryStore
{
    Task RecordCompletedAsync(DownloadRequest request, long bytes, TimeSpan duration, CancellationToken ct = default);

    Task RecordFailedAsync(DownloadRequest request, string error, CancellationToken ct = default);

    Task<IReadOnlyList<DownloadHistoryEntry>> GetRecentAsync(int limit, CancellationToken ct = default);
}

public sealed record DownloadHistoryEntry(string Id, string DisplayName, string Url, string Destination, long Bytes, DateTimeOffset CompletedAt, string? Error, TimeSpan Duration);

/// <summary>Внутреннее состояние задачи.</summary>
public sealed class DownloadTaskItem
{
    private long _bytesReceived;
    private long _speedWindowBytes;
    private long _speedWindowStartTicks;

    public DownloadTaskItem(DownloadRequest request)
    {
        Request = request;
        State = DownloadState.Queued;
        StartedAt = Stopwatch.GetTimestamp();
    }

    public DownloadRequest Request { get; }
    public DownloadState State { get; internal set; }
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public long? TotalBytes { get; internal set; }
    public string? Error { get; internal set; }
    public double BytesPerSecond { get; internal set; }
    public long StartedAt { get; }
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(StartedAt);

    internal CancellationTokenSource? Cts { get; set; }
    internal bool PauseRequested { get; set; }
    internal int Attempt { get; set; }

    internal void ResetBytes() => Interlocked.Exchange(ref _bytesReceived, 0);

    public void AddBytes(long count)
    {
        Interlocked.Add(ref _bytesReceived, count);
        Interlocked.Add(ref _speedWindowBytes, count);
    }

    internal void ResetSpeedWindow()
    {
        Interlocked.Exchange(ref _speedWindowBytes, 0);
        Volatile.Write(ref _speedWindowStartTicks, Stopwatch.GetTimestamp());
    }

    internal double ComputeSpeed()
    {
        var start = Volatile.Read(ref _speedWindowStartTicks);
        if (start == 0)
        {
            return 0;
        }

        var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
        if (elapsed < 0.5)
        {
            return BytesPerSecond;
        }

        var bytes = Interlocked.Exchange(ref _speedWindowBytes, 0);
        Volatile.Write(ref _speedWindowStartTicks, Stopwatch.GetTimestamp());

        var instant = bytes / elapsed;
        BytesPerSecond = BytesPerSecond <= 0 ? instant : (BytesPerSecond * 0.7) + (instant * 0.3);
        return BytesPerSecond;
    }

    public DownloadSnapshot ToSnapshot() => new(
        Request.Id,
        Request.DisplayName ?? Path.GetFileName(Request.DestinationPath),
        Request.Group ?? string.Empty,
        State,
        BytesReceived,
        TotalBytes,
        BytesPerSecond,
        TotalBytes is > 0 && BytesPerSecond > 1
            ? TimeSpan.FromSeconds(Math.Max(0, (TotalBytes.Value - BytesReceived) / BytesPerSecond))
            : null,
        Error,
        Request.DestinationPath);
}

public sealed class DownloadManagerOptions
{
    public int MaxParallelDownloads { get; set; } = 4;
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Менеджер загрузок: очередь, параллелизм, докачка через Range, пауза/возобновление,
/// проверка хэшей, атомарная запись и история.
/// </summary>
public interface IDownloadManager
{
    IReadOnlyList<DownloadSnapshot> Snapshot();

    /// <summary>Ставит задачу в очередь и возвращает её как awaitable-результат.</summary>
    Task<bool> EnqueueAsync(DownloadRequest request, CancellationToken ct = default);

    /// <summary>Ставит пачку и ждёт завершения всех.</summary>
    Task<bool> EnqueueManyAsync(IEnumerable<DownloadRequest> requests, IProgress<(int Completed, int Total)>? progress = null, CancellationToken ct = default);

    void Pause(string id);

    void Resume(string id);

    void Cancel(string id);

    void CancelGroup(string group);

    DownloadSnapshot? Get(string id);

    event Action<DownloadSnapshot>? Updated;

    event Action<DownloadSnapshot>? Finished;
}

public sealed class DownloadManager(
    Net.IHttpService http,
    IDownloadHistoryStore history,
    DownloadManagerOptions options,
    Diagnostics.ILogService log) : IDownloadManager, IDisposable
{
    private const int BufferSize = 128 * 1024;
    private const string PartSuffix = ".ehotpart";

    private readonly ConcurrentDictionary<string, DownloadTaskItem> _tasks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _workerSlots = new(Math.Max(1, options.MaxParallelDownloads));
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _completions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);
    private CancellationTokenSource _lifetime = new();
    private Timer? _progressTimer;
    private bool _disposed;

    public event Action<DownloadSnapshot>? Updated;

    public event Action<DownloadSnapshot>? Finished;

    public IReadOnlyList<DownloadSnapshot> Snapshot()
        => _tasks.Values
            .OrderByDescending(t => t.StartedAt)
            .Select(t => t.ToSnapshot())
            .ToArray();

    public DownloadSnapshot? Get(string id)
        => _tasks.TryGetValue(id, out var task) ? task.ToSnapshot() : null;

    public async Task<bool> EnqueueAsync(DownloadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_tasks.TryGetValue(request.Id, out var existing))
        {
            if (existing.State is DownloadState.Running or DownloadState.Verifying)
            {
                return await WaitAsync(request.Id, ct).ConfigureAwait(false);
            }

            if (existing.State == DownloadState.Completed)
            {
                return true;
            }
        }

        var item = new DownloadTaskItem(request);
        item.ResetSpeedWindow();
        _tasks[request.Id] = item;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completions[request.Id] = tcs;

        EnsureProgressTimer();
        Publish(item);

        _ = Task.Run(() => RunQueuedAsync(item), CancellationToken.None);

        return await WaitAsync(request.Id, ct).ConfigureAwait(false);
    }

    public async Task<bool> EnqueueManyAsync(IEnumerable<DownloadRequest> requests, IProgress<(int Completed, int Total)>? progress = null, CancellationToken ct = default)
    {
        var list = requests.ToList();
        var done = 0;
        var failures = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);

        var tasks = list.Select(async request =>
        {
            var ok = await EnqueueAsync(request, linked.Token).ConfigureAwait(false);
            if (!ok)
            {
                Interlocked.Increment(ref failures);
            }

            var c = Interlocked.Increment(ref done);
            progress?.Report((c, list.Count));
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return failures == 0;
    }

    public void Pause(string id)
    {
        if (!_tasks.TryGetValue(id, out var item))
        {
            return;
        }

        item.PauseRequested = true;
        item.Cts?.Cancel();
        item.State = DownloadState.Paused;
        Publish(item);
    }

    public void Resume(string id)
    {
        if (!_tasks.TryGetValue(id, out var item) || item.State is not (DownloadState.Paused or DownloadState.Failed))
        {
            return;
        }

        item.PauseRequested = false;
        item.Error = null;
        item.Attempt = 0;
        item.State = DownloadState.Queued;
        _completions.TryAdd(id, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        Publish(item);
        _ = Task.Run(() => RunQueuedAsync(item), CancellationToken.None);
    }

    public void Cancel(string id)
    {
        if (!_tasks.TryGetValue(id, out var item))
        {
            return;
        }

        item.PauseRequested = false;
        item.Cts?.Cancel();
        item.State = DownloadState.Cancelled;
        Publish(item);
        Complete(id, false);
        TryDelete(PartPath(item.Request));
    }

    public void CancelGroup(string group)
    {
        foreach (var item in _tasks.Values.Where(t => string.Equals(t.Request.Group, group, StringComparison.Ordinal)))
        {
            Cancel(item.Request.Id);
        }
    }

    private async Task RunQueuedAsync(DownloadTaskItem item)
    {
        if (!_running.TryAdd(item.Request.Id, 0))
        {
            return;
        }

        try
        {
            await _workerSlots.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (item.State is DownloadState.Cancelled)
                {
                    return;
                }

                item.State = DownloadState.Running;
                item.ResetSpeedWindow();
                Publish(item);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                item.Cts = cts;

                var ok = await DownloadCoreAsync(item, cts.Token).ConfigureAwait(false);

                if (item.PauseRequested)
                {
                    item.State = DownloadState.Paused;
                    Publish(item);
                    Complete(item.Request.Id, false);
                    return;
                }

                if (!ok)
                {
                    item.State = DownloadState.Failed;
                    Publish(item);
                    Complete(item.Request.Id, false);
                    return;
                }

                item.State = DownloadState.Completed;
                item.BytesPerSecond = 0;
                Publish(item);
                Complete(item.Request.Id, true);
                await history.RecordCompletedAsync(item.Request, item.BytesReceived, item.Elapsed).ConfigureAwait(false);
            }
            finally
            {
                _workerSlots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            if (item.State is not (DownloadState.Paused or DownloadState.Cancelled))
            {
                item.State = DownloadState.Cancelled;
                Publish(item);
            }

            Complete(item.Request.Id, false);
        }
        catch (Exception ex)
        {
            item.Error = ex.Message;
            item.State = DownloadState.Failed;
            log.Error("Download", $"Загрузка {item.Request.Id} не удалась: {ex.Message}", ex);
            Publish(item);
            Complete(item.Request.Id, false);
            await history.RecordFailedAsync(item.Request, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(item.Request.Id, out _);
        }
    }

    private async Task<bool> DownloadCoreAsync(DownloadTaskItem item, CancellationToken ct)
    {
        var request = item.Request;
        var destination = request.DestinationPath;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (File.Exists(destination) && request.SkipIfVerified && !request.OverwriteExisting && await VerifyFileIfPossibleAsync(destination, request, ct).ConfigureAwait(false))
        {
            item.TotalBytes = new FileInfo(destination).Length;
            log.Info("Download", $"Файл уже на месте и прошёл проверку: {destination}");
            return true;
        }

        var partPath = PartPath(request);
        var existingLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        for (var attempt = 0; attempt <= request.MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var hasher = request.ExpectedSha512 is not null
                    ? IncrementalHash.CreateHash(HashAlgorithmName.SHA512)
                    : request.ExpectedSha1 is not null
                        ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1)
                        : null;

                // Если докачиваем, хэш уже посчитанной части восстановить нельзя —
                // поэтому докачка используется только когда хэш не задан, иначе качаем с нуля.
                var resumeOffset = hasher is null && existingLength > 0 ? existingLength : 0;
                if (resumeOffset == 0 && File.Exists(partPath))
                {
                    File.Delete(partPath);
                }

                using var response = await http.OpenReadAsync(request.Url, resumeOffset > 0 ? resumeOffset : null, ct).ConfigureAwait(false);

                var fileMode = resumeOffset > 0 ? FileMode.Append : FileMode.Create;
                item.TotalBytes = request.ExpectedSize;
                if (resumeOffset == 0)
                {
                    item.ResetBytes();
                }

                await using var fileStream = new FileStream(partPath, fileMode, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                var buffer = new byte[BufferSize];
                long processed = resumeOffset;
                int read;

                while ((read = await response.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hasher?.AppendData(buffer, 0, read);
                    item.AddBytes(read);
                    processed += read;

                }

                item.State = DownloadState.Verifying;
                Publish(item);

                if (request.ExpectedSize is > 0 && processed != request.ExpectedSize)
                {
                    throw new IOException($"Размер файла не совпал: ожидалось {request.ExpectedSize}, получено {processed}.");
                }

                if (hasher is not null)
                {
                    var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    var expected = request.ExpectedSha512 ?? request.ExpectedSha1!;
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(partPath);
                        throw new InvalidDataException($"Хэш не совпал для {request.Id}: ожидался {expected}, получен {actual}.");
                    }
                }

                fileStream.Close();
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                File.Move(partPath, destination);
                log.Info("Download", $"Загружено: {destination} ({processed} байт)");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < request.MaxRetries)
            {
                log.Warn("Download", $"Попытка {attempt + 1} для {request.Id} не удалась: {ex.Message}");
                item.Error = ex.Message;
                existingLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static async Task<bool> VerifyFileIfPossibleAsync(string path, DownloadRequest request, CancellationToken ct)
    {
        if (request.ExpectedSha1 is null && request.ExpectedSha512 is null)
        {
            return false;
        }

        var info = new FileInfo(path);
        if (request.ExpectedSize is > 0 && info.Length != request.ExpectedSize)
        {
            return false;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = request.ExpectedSha512 is not null
            ? await SHA512.HashDataAsync(stream, ct).ConfigureAwait(false)
            : await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);

        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        var expected = request.ExpectedSha512 ?? request.ExpectedSha1!;
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string PartPath(DownloadRequest request) => request.DestinationPath + PartSuffix;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private void Complete(string id, bool success)
    {
        if (_completions.TryRemove(id, out var tcs))
        {
            tcs.TrySetResult(success);
        }

        if (_tasks.TryGetValue(id, out var item))
        {
            Finished?.Invoke(item.ToSnapshot());
        }
    }

    private async Task<bool> WaitAsync(string id, CancellationToken ct)
    {
        if (!_completions.TryGetValue(id, out var tcs))
        {
            var snap = Get(id);
            return snap?.State == DownloadState.Completed;
        }

        await using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    private void Publish(DownloadTaskItem item) => Updated?.Invoke(item.ToSnapshot());

    private void EnsureProgressTimer()
    {
        if (_progressTimer is not null)
        {
            return;
        }

        _progressTimer = new Timer(_ =>
        {
            foreach (var item in _tasks.Values)
            {
                if (item.State != DownloadState.Running)
                {
                    continue;
                }

                item.ComputeSpeed();
                Publish(item);
            }
        }, null, options.ProgressInterval, options.ProgressInterval);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _progressTimer?.Dispose();
        _workerSlots.Dispose();
        _lifetime.Dispose();
    }
}
