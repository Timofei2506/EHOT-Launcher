using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Storage;

namespace EHOT.Launcher.Auth;

/// <summary>
/// Настройки Microsoft OAuth. Клиентский идентификатор НЕ является секретом и должен быть
/// зарегистрирован в Azure как публичное приложение; ключ (client secret) лаунчеру не нужен.
/// Подставляется через конфигурацию/переменную окружения EHOT_AZURE_CLIENT_ID.
/// </summary>
public sealed record MicrosoftAuthOptions
{
    public string? ClientId { get; init; }
    public string Scope { get; init; } = "XboxLive.signin offline_access";
    public string DeviceCodeEndpoint { get; init; } = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    public string TokenEndpoint { get; init; } = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public static MicrosoftAuthOptions FromEnvironment() => new()
    {
        ClientId = Environment.GetEnvironmentVariable("EHOT_AZURE_CLIENT_ID")
    };
}

public sealed record DeviceCodeChallenge(
    string UserCode,
    string VerificationUri,
    string DeviceCode,
    DateTimeOffset ExpiresAt,
    int IntervalSeconds);

public sealed record MicrosoftAuthSession(AccountInfo Account, string AccessToken, DateTimeOffset ExpiresAt);

public interface IMicrosoftAuthService
{
    bool IsConfigured { get; }

    string? ConfigurationHint { get; }

    Task<OperationResult<DeviceCodeChallenge>> StartDeviceCodeAsync(CancellationToken ct = default);

    /// <summary>Ожидает подтверждения пользователя, опрашивая token endpoint.</summary>
    Task<OperationResult<MicrosoftAuthSession>> WaitForAuthenticationAsync(
        DeviceCodeChallenge challenge,
        IProgress<string>? status = null,
        CancellationToken ct = default);

    /// <summary>Пытается восстановить сессию по refresh token из защищённого хранилища.</summary>
    Task<OperationResult<MicrosoftAuthSession>> TryRestoreAsync(string accountId, CancellationToken ct = default);

    Task SignOutAsync(string accountId, CancellationToken ct = default);
}

/// <summary>
/// Легальная авторизация Microsoft → Xbox Live → Minecraft Services.
/// Никаких обходов лицензионной проверки: если у аккаунта нет Minecraft, вход не завершается.
/// </summary>
public sealed class MicrosoftAuthService(
    IHttpService http,
    ISecretStore secrets,
    IAccountRepository accounts,
    MicrosoftAuthOptions options,
    ILogService log) : IMicrosoftAuthService
{
    private const string XboxLiveUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    public bool IsConfigured => options.IsConfigured;

    public string? ConfigurationHint => IsConfigured
        ? null
        : "Вход через Microsoft требует зарегистрированного в Azure клиентского идентификатора " +
          "(публичное приложение с включённым device code flow и разрешением на Xbox Live). " +
          "Задайте переменную окружения EHOT_AZURE_CLIENT_ID или параметр Auth:Microsoft:ClientId. " +
          "Лаунчер не содержит чужих ключей и не обходит проверку лицензии.";

    public async Task<OperationResult<DeviceCodeChallenge>> StartDeviceCodeAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return OperationResult<DeviceCodeChallenge>.Fail(ErrorCode.Configuration, ConfigurationHint!);
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId!,
            ["scope"] = options.Scope
        };

        var response = await PostFormAsync<DeviceCodeResponse>(options.DeviceCodeEndpoint, form, ct).ConfigureAwait(false);
        if (response is null)
        {
            return OperationResult<DeviceCodeChallenge>.Fail(ErrorCode.Network, "Microsoft не вернул код устройства.");
        }

        return OperationResult<DeviceCodeChallenge>.Ok(new DeviceCodeChallenge(
            response.UserCode,
            response.VerificationUri,
            response.DeviceCode,
            DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn),
            Math.Max(2, response.Interval)));
    }

    public async Task<OperationResult<MicrosoftAuthSession>> WaitForAuthenticationAsync(
        DeviceCodeChallenge challenge,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = options.ClientId!,
            ["device_code"] = challenge.DeviceCode
        };

        var interval = challenge.IntervalSeconds;
        while (DateTimeOffset.UtcNow < challenge.ExpiresAt)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report("Ожидаем подтверждение входа в браузере…");

            var (response, error) = await PostFormRawAsync<TokenResponse>(options.TokenEndpoint, form, ct).ConfigureAwait(false);
            if (response?.AccessToken is not null)
            {
                return await CompleteSignInAsync(response, ct).ConfigureAwait(false);
            }

            switch (error)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += 5;
                    break;
                case "authorization_declined":
                    return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthDenied, "Вход отклонён пользователем.");
                case "expired_token":
                    return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthPending, "Код устройства истёк. Начните вход заново.");
                default:
                    if (!string.IsNullOrEmpty(error))
                    {
                        log.Warn("Auth", $"Device code error: {error}");
                    }

                    break;
            }

            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
        }

        return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthPending, "Время ожидания входа истекло.");
    }

    public async Task<OperationResult<MicrosoftAuthSession>> TryRestoreAsync(string accountId, CancellationToken ct = default)
    {
        var refreshToken = await secrets.GetAsync(SecretKeys.RefreshToken(accountId), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(refreshToken))
        {
            return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthRequired, "Сессия не найдена — требуется вход.");
        }

        if (!IsConfigured)
        {
            return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.Configuration, ConfigurationHint!);
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = options.ClientId!,
            ["refresh_token"] = refreshToken,
            ["scope"] = options.Scope
        };

        var (response, error) = await PostFormRawAsync<TokenResponse>(options.TokenEndpoint, form, ct).ConfigureAwait(false);
        if (response?.AccessToken is null)
        {
            log.Warn("Auth", $"Не удалось обновить токен: {error}");
            return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthRequired, "Сессия истекла — войдите снова.");
        }

        var result = await CompleteSignInAsync(response, ct, existingAccountId: accountId).ConfigureAwait(false);
        return result;
    }

    public async Task SignOutAsync(string accountId, CancellationToken ct = default)
    {
        await secrets.RemoveAsync(SecretKeys.RefreshToken(accountId), ct).ConfigureAwait(false);
        await secrets.RemoveAsync(SecretKeys.MinecraftAccessToken(accountId), ct).ConfigureAwait(false);
        log.Info("Auth", "Сессия Microsoft удалена из хранилища секретов.");
    }

    private async Task<OperationResult<MicrosoftAuthSession>> CompleteSignInAsync(
        TokenResponse token, CancellationToken ct, string? existingAccountId = null)
    {
        try
        {
            // 1. Xbox Live user token.
            var xbl = await PostJsonAsync<XblResponse>(XboxLiveUrl, new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = "d=" + token.AccessToken
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT"
            }, ct).ConfigureAwait(false);

            if (xbl?.Token is null)
            {
                return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthDenied,
                    "Xbox Live отклонил вход. Проверьте, что аккаунт Microsoft имеет доступ к Xbox Live.");
            }

            // 2. XSTS.
            var xsts = await PostJsonAsync<XstsResponse>(XstsUrl, new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xbl.Token } },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT"
            }, ct).ConfigureAwait(false);

            if (xsts?.Token is null)
            {
                if (xsts?.XErr is not null)
                {
                    var message = xsts.XErr switch
                    {
                        2148916233L => "У аккаунта Microsoft нет профиля Xbox. Создайте его на xbox.com и повторите.",
                        2148916235L => "Xbox Live недоступен в регионе аккаунта.",
                        2148916238L => "Аккаунт несовершеннолетний и должен быть добавлен в семейную группу.",
                        _ => $"Xbox Live вернул ошибку {xsts.XErr}."
                    };

                    return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthDenied, message);
                }

                return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthDenied, "Не удалось получить XSTS-токен.");
            }

            var userHash = xsts.DisplayClaims?.Xui?.FirstOrDefault()?.Uhs;
            if (string.IsNullOrEmpty(userHash))
            {
                return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthDenied, "XSTS не вернул идентификатор пользователя.");
            }

            // 3. Minecraft access token.
            var mc = await PostJsonAsync<MinecraftAuthResponse>(MinecraftLoginUrl, new
            {
                identityToken = $"XBL3.0 x={userHash};{xsts.Token}"
            }, ct).ConfigureAwait(false);

            if (mc?.AccessToken is null)
            {
                return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthDenied,
                    "Minecraft Services не выдал токен. У аккаунта должна быть приобретена Minecraft Java Edition.");
            }

            // 4. Профиль Minecraft.
            var profileData = await http.GetStringAsync(new Uri(MinecraftProfileUrl), bearerToken: mc.AccessToken, allowAnyHost: true, ct: ct).ConfigureAwait(false);
            if (!profileData.IsSuccess)
            {
                if ((int)profileData.StatusCode == 404)
                {
                    return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthDenied,
                        "У аккаунта нет профиля Minecraft Java Edition (игра не приобретена).");
                }

                return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthDenied,
                    $"Minecraft Services вернул {(int)profileData.StatusCode} при запросе профиля.");
            }

            var profile = JsonSerializer.Deserialize<MinecraftProfileResponse>(profileData.Content, JsonDefaults.External);
            if (profile?.Id is null || profile.Name is null)
            {
                return OperationResult<MicrosoftAuthSession>.Fail(ErrorCode.AuthDenied, "Профиль Minecraft пуст.");
            }

            var accountId = existingAccountId ?? Guid.NewGuid().ToString("N")[..12];
            var account = new AccountInfo
            {
                Id = accountId,
                Username = profile.Name,
                Uuid = Guid.Parse(AddDashes(profile.Id)),
                Kind = AccountKind.Microsoft,
                XboxUserId = userHash,
                IsVerified = true,
                CreatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = DateTimeOffset.UtcNow
            };

            await accounts.SaveAsync(account, ct).ConfigureAwait(false);

            // Токены — только в защищённом хранилище.
            if (token.RefreshToken is not null)
            {
                await secrets.SetAsync(SecretKeys.RefreshToken(accountId), token.RefreshToken, ct).ConfigureAwait(false);
            }

            await secrets.SetAsync(SecretKeys.MinecraftAccessToken(accountId), mc.AccessToken, ct).ConfigureAwait(false);

            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(mc.ExpiresIn > 0 ? mc.ExpiresIn : 86_400);
            log.Info("Auth", $"Вход выполнен: {profile.Name} ({account.Uuid})");

            return OperationResult<MicrosoftAuthSession>.Ok(new MicrosoftAuthSession(account, mc.AccessToken, expiresAt));
        }
        catch (Exception ex)
        {
            log.Error("Auth", "Ошибка завершения входа Microsoft", ex);
            return OperationResult<MicrosoftAuthSession>.FromException(ex, ErrorCode.AuthDenied);
        }
    }

    private async Task<T?> PostJsonAsync<T>(string url, object body, CancellationToken ct) where T : class
    {
        var data = await http.SendJsonAsync(HttpMethod.Post, new Uri(url), body, allowAnyHost: true, ct: ct).ConfigureAwait(false);
        if (!data.IsSuccess)
        {
            log.Warn("Auth", $"{url} → {(int)data.StatusCode}: {Truncate(data.Content)}");
            return null;
        }

        return JsonSerializer.Deserialize<T>(data.Content, JsonDefaults.External);
    }

    private static async Task<T?> PostFormAsync<T>(string url, Dictionary<string, string> form, CancellationToken ct) where T : class
    {
        var (response, _) = await PostFormRawAsync<T>(url, form, ct).ConfigureAwait(false);
        return response;
    }

    private static async Task<(T? Response, string? Error)> PostFormRawAsync<T>(string url, Dictionary<string, string> form, CancellationToken ct)
        where T : class
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(new Uri(url), content, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
                return (null, error ?? $"HTTP {(int)response.StatusCode}");
            }
            catch (JsonException)
            {
                return (null, $"HTTP {(int)response.StatusCode}");
            }
        }

        return (JsonSerializer.Deserialize<T>(json, JsonDefaults.External), null);
    }

    private static string AddDashes(string uuid)
        => uuid.Length == 32
            ? $"{uuid[..8]}-{uuid[8..12]}-{uuid[12..16]}-{uuid[16..20]}-{uuid[20..]}"
            : uuid;

    private static string Truncate(string value) => value.Length > 300 ? value[..300] + "…" : value;

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("user_code")]
        public string UserCode { get; set; } = string.Empty;

        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; } = string.Empty;

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; } = 5;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class XblResponse
    {
        [JsonPropertyName("Token")]
        public string? Token { get; set; }
    }

    private sealed class XstsResponse
    {
        [JsonPropertyName("Token")]
        public string? Token { get; set; }

        [JsonPropertyName("XErr")]
        public long? XErr { get; set; }

        [JsonPropertyName("DisplayClaims")]
        public XstsClaims? DisplayClaims { get; set; }
    }

    private sealed class XstsClaims
    {
        [JsonPropertyName("xui")]
        public List<XuiClaim>? Xui { get; set; }
    }

    private sealed class XuiClaim
    {
        [JsonPropertyName("uhs")]
        public string? Uhs { get; set; }
    }

    private sealed class MinecraftAuthResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class MinecraftProfileResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}

public static class SecretKeys
{
    public static string RefreshToken(string accountId) => $"account:{accountId}:refresh_token";

    public static string MinecraftAccessToken(string accountId) => $"account:{accountId}:minecraft_token";
}
