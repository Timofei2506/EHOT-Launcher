using System.Text.Json;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;

namespace EHOT.Launcher.Game.Minecraft;

/// <summary>Резолвнутая (плоская) версия: базовый version.json + все наследованные (Fabric/Forge/Quilt).</summary>
public sealed record ResolvedVersion
{
    public required string Id { get; init; }
    public required string BaseVersionId { get; init; }
    public required string MainClass { get; init; }
    public required IReadOnlyList<LibraryDto> Libraries { get; init; }
    public required IReadOnlyList<string> JvmArguments { get; init; }
    public required IReadOnlyList<string> GameArguments { get; init; }
    public required string AssetsId { get; init; }
    public AssetIndexRef? AssetIndex { get; init; }
    public DownloadInfo? Client { get; init; }
    public LoggingClient? Logging { get; init; }
    public int JavaMajorVersion { get; init; }
    public string? JavaComponent { get; init; }
    public required string Type { get; init; }
    public required string VersionJsonPath { get; init; }

    /// <summary>Идентификатор версии, чей version.json содержит assets/client (наследуемая база).</summary>
    public required VersionDto BaseVersion { get; init; }
}

public interface IMinecraftVersionService
{
    Task<OperationResult<VersionManifestDto>> GetManifestAsync(bool forceRefresh = false, CancellationToken ct = default);

    Task<OperationResult<VersionSummary>> FindVersionAsync(string id, CancellationToken ct = default);

    /// <summary>Разворачивает цепочку inheritsFrom, применяя правила ОС к нативным библиотекам.</summary>
    Task<OperationResult<ResolvedVersion>> ResolveAsync(string versionId, string versionsDirectory, CancellationToken ct = default);

    Task<OperationResult<VersionDto>> LoadVersionJsonAsync(string versionId, string versionsDirectory, CancellationToken ct = default);
}

public sealed class MinecraftVersionService(
    IHttpService http,
    IPlatformInfo platform,
    ILogService log) : IMinecraftVersionService
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private static readonly TimeSpan ManifestTtl = TimeSpan.FromHours(6);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static VersionManifestDto? _cached;
    private static DateTimeOffset _cachedAt;

    public async Task<OperationResult<VersionManifestDto>> GetManifestAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cached is not null && DateTimeOffset.UtcNow - _cachedAt < ManifestTtl)
            {
                return OperationResult<VersionManifestDto>.Ok(_cached);
            }

            var manifest = await http.GetJsonAsync<VersionManifestDto>(new Uri(ManifestUrl), ct: ct).ConfigureAwait(false);
            if (manifest is null || manifest.Versions.Count == 0)
            {
                return _cached is not null
                    ? OperationResult<VersionManifestDto>.Ok(_cached) // используем устаревший кэш вместо ошибки
                    : OperationResult<VersionManifestDto>.Fail(ErrorCode.Network, "Не удалось получить список версий Minecraft.");
            }

            _cached = manifest;
            _cachedAt = DateTimeOffset.UtcNow;
            log.Info("Minecraft", $"Манифест версий: {manifest.Versions.Count} версий, release={manifest.Latest?.Release}");
            return OperationResult<VersionManifestDto>.Ok(manifest);
        }
        catch (Exception ex)
        {
            if (_cached is not null)
            {
                log.Warn("Minecraft", "Манифест недоступен, используется кэш.", ex);
                return OperationResult<VersionManifestDto>.Ok(_cached);
            }

            return OperationResult<VersionManifestDto>.FromException(ex, ErrorCode.Network);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<OperationResult<VersionSummary>> FindVersionAsync(string id, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(ct: ct).ConfigureAwait(false);
        if (!manifest.IsSuccess)
        {
            return OperationResult<VersionSummary>.Fail(manifest.Code, manifest.Message ?? "Манифест недоступен.");
        }

        var found = manifest.Value!.Versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
        return found is null
            ? OperationResult<VersionSummary>.Fail(ErrorCode.NotFound, $"Версия Minecraft '{id}' не найдена.")
            : OperationResult<VersionSummary>.Ok(found);
    }

    public async Task<OperationResult<VersionDto>> LoadVersionJsonAsync(string versionId, string versionsDirectory, CancellationToken ct = default)
    {
        var localPath = Path.Combine(versionsDirectory, versionId, $"{versionId}.json");
        if (File.Exists(localPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(localPath, ct).ConfigureAwait(false);
                var dto = JsonSerializer.Deserialize<VersionDto>(json, JsonDefaults.External);
                if (dto is not null)
                {
                    return OperationResult<VersionDto>.Ok(dto);
                }
            }
            catch (JsonException ex)
            {
                log.Warn("Minecraft", $"Повреждён {localPath}, перезагружаем с сервера.", ex);
            }
        }

        var summary = await FindVersionAsync(versionId, ct).ConfigureAwait(false);
        if (!summary.IsSuccess)
        {
            return OperationResult<VersionDto>.Fail(summary.Code, summary.Message ?? string.Empty);
        }

        try
        {
            var dto = await http.GetJsonAsync<VersionDto>(new Uri(summary.Value!.Url), ct: ct).ConfigureAwait(false);
            if (dto is null)
            {
                return OperationResult<VersionDto>.Fail(ErrorCode.Network, $"Не удалось скачать version.json для {versionId}.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllTextAsync(localPath, JsonSerializer.Serialize(dto, JsonDefaults.External), ct).ConfigureAwait(false);
            return OperationResult<VersionDto>.Ok(dto);
        }
        catch (Exception ex)
        {
            return OperationResult<VersionDto>.FromException(ex, ErrorCode.Network);
        }
    }

    public async Task<OperationResult<ResolvedVersion>> ResolveAsync(string versionId, string versionsDirectory, CancellationToken ct = default)
    {
        var context = RuleContext.FromPlatform(platform);

        // Загружаем цепочку inheritsFrom.
        var chain = new List<VersionDto>();
        var currentId = versionId;
        for (var depth = 0; depth < 8; depth++)
        {
            var loaded = await LoadVersionJsonAsync(currentId, versionsDirectory, ct).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return OperationResult<ResolvedVersion>.Fail(loaded.Code, loaded.Message ?? string.Empty);
            }

            chain.Add(loaded.Value!);
            if (string.IsNullOrEmpty(loaded.Value!.InheritsFrom))
            {
                break;
            }

            currentId = loaded.Value.InheritsFrom;

            if (depth == 7)
            {
                return OperationResult<ResolvedVersion>.Fail(ErrorCode.Validation, "Слишком глубокая цепочка inheritsFrom в version.json.");
            }
        }

        var root = chain[0];
        var baseVersion = chain[^1];

        var libraries = new List<LibraryDto>();
        var jvmArgs = new List<string>();
        var gameArgs = new List<string>();
        string? mainClass = null;

        // Применяем от базы к вершине, чтобы лоадер мог переопределить mainClass.
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var v = chain[i];
            foreach (var lib in v.Libraries)
            {
                if (RuleEvaluator.IsAllowed(lib.Rules, context))
                {
                    libraries.Add(lib);
                }
            }

            if (v.Arguments is not null)
            {
                jvmArgs.AddRange(ExpandArguments(v.Arguments.Jvm, context));
                gameArgs.AddRange(ExpandArguments(v.Arguments.Game, context));
            }

            if (!string.IsNullOrEmpty(v.LegacyMinecraftArguments) && gameArgs.Count == 0)
            {
                gameArgs.AddRange(v.LegacyMinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }

            if (!string.IsNullOrEmpty(v.MainClass))
            {
                mainClass = v.MainClass;
            }
        }

        var resolved = new ResolvedVersion
        {
            Id = versionId,
            BaseVersionId = baseVersion.Id,
            MainClass = mainClass ?? baseVersion.MainClass ?? "net.minecraft.client.main.Main",
            Libraries = libraries,
            JvmArguments = jvmArgs,
            GameArguments = gameArgs,
            AssetsId = baseVersion.AssetIndex?.Id ?? baseVersion.Assets ?? "legacy",
            AssetIndex = baseVersion.AssetIndex,
            Client = baseVersion.Downloads?.GetValueOrDefault("client"),
            Logging = baseVersion.Logging?.Client,
            JavaMajorVersion = baseVersion.JavaVersion?.MajorVersion ?? 8,
            JavaComponent = baseVersion.JavaVersion?.Component,
            Type = root.Type,
            VersionJsonPath = Path.Combine(versionsDirectory, versionId, $"{versionId}.json"),
            BaseVersion = baseVersion
        };

        return OperationResult<ResolvedVersion>.Ok(resolved);
    }

    private static IEnumerable<string> ExpandArguments(IEnumerable<ArgumentEntry> entries, RuleContext context)
    {
        foreach (var entry in entries)
        {
            if (!RuleEvaluator.IsAllowed(entry.Rules, context))
            {
                continue;
            }

            foreach (var value in entry.Expand())
            {
                yield return value;
            }
        }
    }
}
