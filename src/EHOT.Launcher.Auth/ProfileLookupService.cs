using System.Text.Json.Serialization;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Results;

namespace EHOT.Launcher.Auth;

public sealed record OfficialProfile(string Username, string Uuid, string? SkinUrl);

/// <summary>
/// Проверка существования ника в официальном сервисе Mojang.
/// Мы НЕ создаём и не подменяем официальные UUID: если профиль найден — показываем реальные данные
/// владельцу аккаунта для информации; создать лицензионный аккаунт по чужому нику невозможно.
/// </summary>
public interface IProfileLookupService
{
    Task<OperationResult<OfficialProfile?>> LookupAsync(string username, CancellationToken ct = default);
}

public sealed class ProfileLookupService(IHttpService http, ILogService log) : IProfileLookupService
{
    public async Task<OperationResult<OfficialProfile?>> LookupAsync(string username, CancellationToken ct = default)
    {
        if (!Core.Security.PathGuard.IsValidMinecraftUsername(username))
        {
            return OperationResult<OfficialProfile?>.Fail(ErrorCode.Validation,
                "Ник должен содержать 3–16 символов: латинские буквы, цифры, подчёркивание.");
        }

        try
        {
            var url = $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(username)}";
            var response = await http.GetStringAsync(new Uri(url), allowAnyHost: true, ct: ct).ConfigureAwait(false);

            if ((int)response.StatusCode == 404 || string.IsNullOrWhiteSpace(response.Content) || response.Content == "null")
            {
                return OperationResult<OfficialProfile?>.Ok(null);
            }

            if (!response.IsSuccess)
            {
                return OperationResult<OfficialProfile?>.Fail(ErrorCode.Network,
                    $"Сервис Mojang вернул {(int)response.StatusCode}. Попробуйте позже.");
            }

            var profile = System.Text.Json.JsonSerializer.Deserialize<MojangProfile>(response.Content, JsonDefaults.External);
            if (profile?.Id is null || profile.Name is null)
            {
                return OperationResult<OfficialProfile?>.Ok(null);
            }

            var dashed = profile.Id.Length == 32
                ? $"{profile.Id[..8]}-{profile.Id[8..12]}-{profile.Id[12..16]}-{profile.Id[16..20]}-{profile.Id[20..]}"
                : profile.Id;

            return OperationResult<OfficialProfile?>.Ok(new OfficialProfile(
                profile.Name,
                dashed,
                $"https://crafatar.com/skins/{dashed}"));
        }
        catch (Exception ex)
        {
            log.Warn("Auth", $"Проверка ника не удалась: {ex.Message}");
            return OperationResult<OfficialProfile?>.FromException(ex, ErrorCode.Network);
        }
    }

    private sealed class MojangProfile
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
