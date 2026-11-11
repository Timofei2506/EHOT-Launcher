using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EHOT.Launcher.Auth;

/// <summary>Единая точка получения токена для запуска игры.</summary>
public interface IAccountSessionProvider
{
    /// <summary>
    /// Возвращает данные для входа. Для локального профиля токен — фиктивная строка "0" (как у официального
    /// offline-режима), и это явно помечается статусом <see cref="AccountSession.IsOffline"/>.
    /// </summary>
    Task<OperationResult<AccountSession>> GetSessionAsync(AccountInfo account, CancellationToken ct = default);
}

public sealed record AccountSession(AccountInfo Account, string AccessToken, bool IsOffline, DateTimeOffset? ExpiresAt);

public sealed class AccountSessionProvider(
    ISecretStore secrets,
    IMicrosoftAuthService microsoft) : IAccountSessionProvider
{
    public async Task<OperationResult<AccountSession>> GetSessionAsync(AccountInfo account, CancellationToken ct = default)
    {
        if (account.Kind == AccountKind.Local)
        {
            return OperationResult<AccountSession>.Ok(new AccountSession(account, "0", true, null));
        }

        var cached = await secrets.GetAsync(SecretKeys.MinecraftAccessToken(account.Id), ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(cached))
        {
            return OperationResult<AccountSession>.Ok(new AccountSession(account, cached, false, null));
        }

        var restored = await microsoft.TryRestoreAsync(account.Id, ct).ConfigureAwait(false);
        return restored.IsSuccess
            ? OperationResult<AccountSession>.Ok(new AccountSession(restored.Value!.Account, restored.Value.AccessToken, false, restored.Value.ExpiresAt))
            : OperationResult<AccountSession>.Fail(restored.Code, restored.Message ?? "Требуется вход в Microsoft.");
    }
}

public static class AuthModule
{
    public static IServiceCollection AddEhotAuth(this IServiceCollection services, MicrosoftAuthOptions? options = null)
    {
        services.AddSingleton(options ?? MicrosoftAuthOptions.FromEnvironment());
        services.AddSingleton<ILocalProfileService, LocalProfileService>();
        services.AddSingleton<IProfileLookupService, ProfileLookupService>();
        services.AddSingleton<IMicrosoftAuthService, MicrosoftAuthService>();
        services.AddSingleton<IAccountSessionProvider, AccountSessionProvider>();
        return services;
    }
}
