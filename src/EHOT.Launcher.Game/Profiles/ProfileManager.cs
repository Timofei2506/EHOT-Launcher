using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Storage;

namespace EHOT.Launcher.Game.Profiles;

public sealed record CreateProfileRequest
{
    public required string Name { get; init; }
    public required string MinecraftVersion { get; init; }
    public LoaderKind Loader { get; init; }
    public string? LoaderVersion { get; init; }
    public string? AccountId { get; init; }
    public int MemoryMb { get; init; } = 4096;
    public string? ModpackId { get; init; }
    public string? BaseDirectory { get; init; }
}

/// <summary>Управление игровыми профилями: независимые папки, клонирование, удаление с подтверждением.</summary>
public interface IProfileManager
{
    Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken ct = default);

    Task<GameProfile?> GetAsync(string id, CancellationToken ct = default);

    Task<OperationResult<GameProfile>> CreateAsync(CreateProfileRequest request, CancellationToken ct = default);

    Task<OperationResult<GameProfile>> UpdateAsync(GameProfile profile, CancellationToken ct = default);

    Task<OperationResult<GameProfile>> CloneAsync(string id, string newName, CancellationToken ct = default);

    /// <summary>Удаляет профиль. deleteFiles = false по умолчанию (данные пользователя не трогаем без согласия).</summary>
    Task<OperationResult> DeleteAsync(string id, bool deleteFiles = false, CancellationToken ct = default);

    GameProfile? Current { get; }

    Task SetCurrentAsync(string id, CancellationToken ct = default);
}

public sealed class ProfileManager(
    IProfileRepository repository,
    IAppPaths paths,
    ISettingsRepository settings) : IProfileManager
{
    private GameProfile? _current;

    public GameProfile? Current => _current;

    public Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken ct = default) => repository.GetAllAsync(ct);

    public Task<GameProfile?> GetAsync(string id, CancellationToken ct = default) => repository.GetAsync(id, ct);

    public async Task<OperationResult<GameProfile>> CreateAsync(CreateProfileRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return OperationResult<GameProfile>.Fail(ErrorCode.Validation, "Введите название профиля.");
        }

        if (!PathGuard.IsValidMinecraftUsername(request.MinecraftVersion.Replace('.', '_')) && string.IsNullOrWhiteSpace(request.MinecraftVersion))
        {
            return OperationResult<GameProfile>.Fail(ErrorCode.Validation, "Выберите версию Minecraft.");
        }

        var cfg = await settings.LoadAsync(ct).ConfigureAwait(false);
        var root = request.BaseDirectory ?? cfg.MinecraftDirectory ?? paths.DefaultMinecraftDirectory;
        var id = Guid.NewGuid().ToString("N")[..12];
        var folder = PathGuard.ToSafeFileName($"{request.Name}-{id}", $"instance-{id}");
        var instanceDir = Path.Combine(root, "instances", folder);

        Directory.CreateDirectory(instanceDir);
        foreach (var sub in new[] { "versions", "libraries", "assets", "mods", "resourcepacks", "shaderpacks", "saves", "logs", "game" })
        {
            Directory.CreateDirectory(Path.Combine(instanceDir, sub));
        }

        var profile = new GameProfile
        {
            Id = id,
            Name = request.Name.Trim(),
            MinecraftVersion = request.MinecraftVersion,
            Loader = request.Loader,
            LoaderVersion = request.LoaderVersion,
            InstanceDirectory = instanceDir,
            AllocatedMemoryMb = request.MemoryMb,
            AccountId = request.AccountId,
            ModpackId = request.ModpackId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await repository.SaveAsync(profile, ct).ConfigureAwait(false);
        _current ??= profile;
        return OperationResult<GameProfile>.Ok(profile);
    }

    public async Task<OperationResult<GameProfile>> UpdateAsync(GameProfile profile, CancellationToken ct = default)
    {
        if (profile.AllocatedMemoryMb < 512)
        {
            return OperationResult<GameProfile>.Fail(ErrorCode.Validation, "Минимум 512 МБ RAM.");
        }

        await repository.SaveAsync(profile, ct).ConfigureAwait(false);
        if (_current?.Id == profile.Id)
        {
            _current = profile;
        }

        return OperationResult<GameProfile>.Ok(profile);
    }

    public async Task<OperationResult<GameProfile>> CloneAsync(string id, string newName, CancellationToken ct = default)
    {
        var source = await repository.GetAsync(id, ct).ConfigureAwait(false);
        if (source is null)
        {
            return OperationResult<GameProfile>.Fail(ErrorCode.NotFound, "Профиль не найден.");
        }

        var newId = Guid.NewGuid().ToString("N")[..12];
        var parent = Path.GetDirectoryName(source.InstanceDirectory)!;
        var target = Path.Combine(parent, PathGuard.ToSafeFileName($"{newName}-{newId}", $"instance-{newId}"));

        // Копируем только конфигурацию и моды, не дублируя гигабайты библиотек/ассетов.
        await Task.Run(() =>
        {
            Directory.CreateDirectory(target);
            foreach (var sub in new[] { "mods", "resourcepacks", "shaderpacks", "game", "saves" })
            {
                var from = Path.Combine(source.InstanceDirectory, sub);
                if (Directory.Exists(from))
                {
                    CopyDirectory(from, Path.Combine(target, sub));
                }
            }
        }, ct).ConfigureAwait(false);

        var clone = source with
        {
            Id = newId,
            Name = newName.Trim(),
            InstanceDirectory = target,
            CreatedAt = DateTimeOffset.UtcNow,
            LastLaunchedAt = null
        };

        await repository.SaveAsync(clone, ct).ConfigureAwait(false);
        return OperationResult<GameProfile>.Ok(clone);
    }

    public async Task<OperationResult> DeleteAsync(string id, bool deleteFiles = false, CancellationToken ct = default)
    {
        var profile = await repository.GetAsync(id, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult.Fail(ErrorCode.NotFound, "Профиль не найден.");
        }

        await repository.DeleteAsync(id, ct).ConfigureAwait(false);

        if (deleteFiles && Directory.Exists(profile.InstanceDirectory))
        {
            try
            {
                Directory.Delete(profile.InstanceDirectory, recursive: true);
            }
            catch (IOException ex)
            {
                return OperationResult.Fail(ErrorCode.Io,
                    "Профиль удалён из списка, но папку не удалось удалить (возможно, файл занят игрой).", ex);
            }
        }

        if (_current?.Id == id)
        {
            var rest = await repository.GetAllAsync(ct).ConfigureAwait(false);
            _current = rest.Count > 0 ? rest[0] : null;
        }

        return OperationResult.Ok();
    }

    public async Task SetCurrentAsync(string id, CancellationToken ct = default)
    {
        var profile = await repository.GetAsync(id, ct).ConfigureAwait(false);
        if (profile is not null)
        {
            _current = profile;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name is "libraries" or "assets" or "versions")
            {
                continue;
            }

            CopyDirectory(dir, Path.Combine(target, name));
        }
    }
}
