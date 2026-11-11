using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Storage;

namespace EHOT.Launcher.Auth;

public sealed record LocalProfileRequest(string Username, SkinModel SkinModel = SkinModel.Classic);

/// <summary>
/// Локальные (оффлайн) профили. Это НЕ лицензионные аккаунты:
/// они не проходят проверку на серверах Mojang и не дают доступа к официальным серверам.
/// UUID детерминирован от ника и помечен как локальный, чтобы не выдавать себя за официальный.
/// </summary>
public interface ILocalProfileService
{
    Task<OperationResult<AccountInfo>> CreateAsync(LocalProfileRequest request, CancellationToken ct = default);

    Task<OperationResult> SetSkinAsync(string accountId, string pngPath, CancellationToken ct = default);

    Task<OperationResult> DeleteAsync(string accountId, CancellationToken ct = default);
}

public sealed class LocalProfileService(
    IAccountRepository accounts,
    Core.Platform.IAppPaths paths,
    Core.Diagnostics.ILogService log) : ILocalProfileService
{
    public async Task<OperationResult<AccountInfo>> CreateAsync(LocalProfileRequest request, CancellationToken ct = default)
    {
        var name = request.Username?.Trim() ?? string.Empty;
        if (!PathGuard.IsValidMinecraftUsername(name))
        {
            return OperationResult<AccountInfo>.Fail(ErrorCode.Validation,
                "Ник: 3–16 символов, только латинские буквы, цифры и подчёркивание.");
        }

        var existing = await accounts.GetAllAsync(ct).ConfigureAwait(false);
        if (existing.Any(a => string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase) && a.Kind == AccountKind.Local))
        {
            return OperationResult<AccountInfo>.Fail(ErrorCode.Conflict, "Локальный профиль с таким ником уже существует.");
        }

        var account = new AccountInfo
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Username = name,
            Uuid = PathGuard.OfflineUuidFor(name),
            Kind = AccountKind.Local,
            SkinModel = request.SkinModel,
            IsVerified = false,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUsedAt = DateTimeOffset.UtcNow
        };

        await accounts.SaveAsync(account, ct).ConfigureAwait(false);
        log.Info("Auth", $"Создан локальный профиль {name} (не лицензионный)");
        return OperationResult<AccountInfo>.Ok(account);
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

        await accounts.SaveAsync(account with { LocalSkinPath = target, LastUsedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
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
