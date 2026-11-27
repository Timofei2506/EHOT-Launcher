using System.Text.Json;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Platform;

namespace EHOT.Launcher.UI.Services;

public sealed record ServerEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 25565;
    public string Description { get; init; } = string.Empty;
    public bool IsFavorite { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IServerCatalogService
{
    IReadOnlyList<ServerEntry> List();
    Task<ServerEntry> AddAsync(string name, string host, int port, string description, CancellationToken ct = default);
    Task UpdateAsync(ServerEntry entry, CancellationToken ct = default);
    Task<bool> RemoveAsync(string id, CancellationToken ct = default);
}

/// <summary>Пользовательские серверы: JSON в данных лаунчера, без подмены статуса.</summary>
public sealed class ServerCatalogService(IAppPaths paths, ILogService log) : IServerCatalogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storagePath = Path.Combine(paths.AppDataDirectory, "servers.json");
    private List<ServerEntry>? _cache;

    public IReadOnlyList<ServerEntry> List() => Load().ToArray();

    public async Task<ServerEntry> AddAsync(string name, string host, int port, string description, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var servers = Load();
            var entry = new ServerEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Trim(),
                Host = host.Trim(),
                Port = port,
                Description = string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            servers.Add(entry);
            await SaveAsync(servers, ct).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(ServerEntry entry, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var servers = Load();
            var index = servers.FindIndex(s => s.Id == entry.Id);
            if (index >= 0)
            {
                servers[index] = entry;
                await SaveAsync(servers, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var servers = Load();
            var removed = servers.RemoveAll(s => s.Id == id) > 0;
            if (removed)
            {
                await SaveAsync(servers, ct).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<ServerEntry> Load()
    {
        if (_cache is not null)
        {
            return [.. _cache];
        }

        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                _cache = JsonSerializer.Deserialize<List<ServerEntry>>(json, JsonOptions) ?? [];
            }
            else
            {
                _cache = [];
            }
        }
        catch (Exception ex)
        {
            log.Warn("Servers", $"Не удалось прочитать servers.json: {ex.Message}");
            _cache = [];
        }

        return [.. _cache];
    }

    private async Task SaveAsync(List<ServerEntry> servers, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.AppDataDirectory);
        var temp = _storagePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(servers, JsonOptions), ct).ConfigureAwait(false);
        File.Move(temp, _storagePath, overwrite: true);
        _cache = [.. servers];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
