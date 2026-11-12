using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Storage;

namespace EHOT.Launcher.Auth;

public sealed record LocalProfileRequest(string Username, SkinModel SkinModel = SkinModel.Classic);

/// <summary>Результат создания профиля: аккаунт плюс честное описание, откуда взялись UUID и скин.</summary>
public sealed record LocalProfileOutcome(AccountInfo Account, bool CopiedOfficialSkin, string Note);

/// <summary>
/// Локальные (оффлайн) профили — основа пиратского режима, как в TLauncher:
/// никакой лицензии не требуется, играть можно на своём сервере и в одиночке.
/// <para>
/// Если ник занят официальным аккаунтом, лаунчер берёт у него <b>публичный</b> UUID и копию скина —
/// так игрок выглядит как привычный персонаж с этим ником. Это не даёт доступа к чужим данным:
/// UUID и текстура скина публичны, а вход на серверы с online-mode по-прежнему требует лицензии.
/// Если такого ника в официальном сервисе нет — UUID считается оффлайн-способом (как у ванильного
/// сервера), а скин можно загрузить свой: его увидит только владелец профиля.
/// </para>
/// </summary>
public interface ILocalProfileService
{
    Task<OperationResult<LocalProfileOutcome>> CreateAsync(LocalProfileRequest request, CancellationToken ct = default);

    Task<OperationResult> SetSkinAsync(string accountId, string pngPath, CancellationToken ct = default);

    /// <summary>Сохраняет скин из памяти (например, копию скина официального игрока).</summary>
    Task<OperationResult> SaveSkinFromBytesAsync(string accountId, byte[] png, bool slim, string source,
        CancellationToken ct = default);

    Task<OperationResult> DeleteAsync(string accountId, CancellationToken ct = default);
}

public sealed class LocalProfileService(
    IAccountRepository accounts,
    IProfileLookupService lookup,
    Core.Platform.IAppPaths paths,
    Core.Diagnostics.ILogService log) : ILocalProfileService
{
    public async Task<OperationResult<LocalProfileOutcome>> CreateAsync(LocalProfileRequest request, CancellationToken ct = default)
    {
        var name = request.Username?.Trim() ?? string.Empty;
        if (!PathGuard.IsValidMinecraftUsername(name))
        {
            return OperationResult<LocalProfileOutcome>.Fail(ErrorCode.Validation,
                "Ник: 3–16 символов, только латинские буквы, цифры и подчёркивание.");
        }

        var existing = await accounts.GetAllAsync(ct).ConfigureAwait(false);
        if (existing.Any(a => string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase) && a.Kind == AccountKind.Local))
        {
            return OperationResult<LocalProfileOutcome>.Fail(ErrorCode.Conflict, "Локальный профиль с таким ником уже существует.");
        }

        // 1. Ищем официальный профиль с этим ником: у него берём UUID и скин.
        var uuid = PathGuard.OfflineUuidFor(name);
        var model = request.SkinModel;
        string? skinPath = null;
        string? skinSource = null;
        var uuidFromOfficial = false;
        var copiedOfficialSkin = false;
        var note = string.Empty;

        var remote = await lookup.FetchSkinAsync(name, ct).ConfigureAwait(false);
        if (remote.IsSuccess && remote.Value is not null)
        {
            var skin = remote.Value;

            if (Guid.TryParse(skin.Uuid, out var officialUuid))
            {
                uuid = officialUuid;
                uuidFromOfficial = true;
            }

            model = skin.IsSlim ? SkinModel.Slim : SkinModel.Classic;
            skinPath = await SaveSkinFileAsync(name, Guid.NewGuid().ToString("N")[..12], skin.Png, ct).ConfigureAwait(false);
            skinSource = "mojang";
            copiedOfficialSkin = true;

            note = $"Ник занят официальным аккаунтом: взят его публичный UUID и копия скина " +
                   $"({(model == SkinModel.Slim ? "slim" : "classic")}). Вход на серверы с online-mode " +
                   "всё равно требует лицензии — это ограничение сервера, а не лаунчера.";
        }
        else
        {
            note = remote.IsSuccess
                ? "Такого ника в официальном сервисе нет: UUID посчитан в оффлайн-режиме (как у ванильного сервера). " +
                  "Загрузите свой скин — его будет видеть только этот профиль."
                : $"Скин официального игрока получить не удалось ({remote.Message}). UUID посчитан оффлайн. " +
                  "Можно загрузить свой скин — его будет видеть только этот профиль.";
        }

        var account = new AccountInfo
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Username = name,
            Uuid = uuid,
            Kind = AccountKind.Local,
            SkinModel = model,
            LocalSkinPath = skinPath,
            SkinSource = skinSource,
            UuidFromOfficialProfile = uuidFromOfficial,
            IsVerified = false,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUsedAt = DateTimeOffset.UtcNow
        };

        await accounts.SaveAsync(account, ct).ConfigureAwait(false);
        log.Info("Auth", $"Создан локальный профиль {name} (UUID {(uuidFromOfficial ? "официальный" : "оффлайн")}, скин {(copiedOfficialSkin ? "скопирован" : "не задан")})");
        return OperationResult<LocalProfileOutcome>.Ok(new LocalProfileOutcome(account, copiedOfficialSkin, note));
    }

    /// <summary>Сохраняет PNG-скин в каталог лаунчера и возвращает путь (или null, если это не скин).</summary>
    private async Task<string?> SaveSkinFileAsync(string username, string accountId, byte[] png, CancellationToken ct)
    {
        if (!PathGuard.LooksLikeMinecraftSkin(png))
        {
            log.Warn("Auth", $"Скин для {username} отклонён: не PNG 64×64 / 64×32.");
            return null;
        }

        var skinsDir = Path.Combine(paths.AppDataDirectory, "skins");
        Directory.CreateDirectory(skinsDir);
        var target = Path.Combine(skinsDir, $"{PathGuard.ToSafeFileName(username, "skin")}-{accountId}.png");
        await File.WriteAllBytesAsync(target, png, ct).ConfigureAwait(false);
        return target;
    }

    public async Task<OperationResult> SetSkinAsync(string accountId, string pngPath, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return OperationResult.Fail(ErrorCode.NotFound, "Профиль не найден.");
        }

        if (!File.Exists(pngPath))
        {
            return OperationResult.Fail(ErrorCode.NotFound, "Файл скина не найден.");
        }

        var info = new FileInfo(pngPath);
        if (info.Length > 512 * 1024)
        {
            return OperationResult.Fail(ErrorCode.Validation, "Скин больше 512 КБ — вероятно, это не PNG-скин 64×64.");
        }

        // Проверяем PNG-сигнатуру и размеры (64×64 или 64×32).
        var header = new byte[24];
        await using (var fs = File.OpenRead(pngPath))
        {
            var read = await fs.ReadAsync(header, ct).ConfigureAwait(false);
            if (read < 24)
            {
                return OperationResult.Fail(ErrorCode.Validation, "Файл повреждён.");
            }
        }

        if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
        {
            return OperationResult.Fail(ErrorCode.Validation, "Скин должен быть в формате PNG.");
        }

        var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        if (width != 64 || (height != 64 && height != 32))
        {
            return OperationResult.Fail(ErrorCode.Validation,
                $"Ожидается PNG 64×64 или 64×32, получено {width}×{height}.");
        }

        var skinsDir = Path.Combine(paths.AppDataDirectory, "skins");
        Directory.CreateDirectory(skinsDir);
        var target = Path.Combine(skinsDir, $"{PathGuard.ToSafeFileName(account.Username, "skin")}-{account.Id}.png");
        await using (var src = File.OpenRead(pngPath))
        await using (var dst = File.Create(target))
        {
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }

        await accounts.SaveAsync(account with
        {
            LocalSkinPath = target,
            SkinSource = "local",
            LastUsedAt = DateTimeOffset.UtcNow
        }, ct).ConfigureAwait(false);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> SaveSkinFromBytesAsync(string accountId, byte[] png, bool slim, string source,
        CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return OperationResult.Fail(ErrorCode.NotFound, "Профиль не найден.");
        }

        var path = await SaveSkinFileAsync(account.Username, account.Id, png, ct).ConfigureAwait(false);
        if (path is null)
        {
            return OperationResult.Fail(ErrorCode.Validation, "Файл не похож на скин Minecraft (нужен PNG 64×64 или 64×32).");
        }

        await accounts.SaveAsync(account with
        {
            LocalSkinPath = path,
            SkinModel = slim ? SkinModel.Slim : SkinModel.Classic,
            SkinSource = source,
            LastUsedAt = DateTimeOffset.UtcNow
        }, ct).ConfigureAwait(false);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> DeleteAsync(string accountId, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return OperationResult.Fail(ErrorCode.NotFound, "Профиль не найден.");
        }

        await accounts.DeleteAsync(accountId, ct).ConfigureAwait(false);
        return OperationResult.Ok();
    }
}
