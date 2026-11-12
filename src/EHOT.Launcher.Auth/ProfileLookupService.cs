using System.Text.Json.Serialization;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Core.Security;

namespace EHOT.Launcher.Auth;

public sealed record OfficialProfile(string Username, string Uuid, string? SkinUrl);

/// <summary>Скин, снятый с официального профиля: сам PNG и модель (classic/slim).</summary>
public sealed record RemoteSkin(string Username, string Uuid, string SkinUrl, byte[] Png, bool IsSlim);

/// <summary>
/// Проверка ника в официальном сервисе Mojang и получение его скина.
/// Ник официального игрока — это <b>чужой</b> аккаунт: лаунчер не пытается выдать себя за него,
/// он берёт только публичные данные (UUID и текстуру скина), которые Mojang отдаёт всем желающим.
/// </summary>
public interface IProfileLookupService
{
    Task<OperationResult<OfficialProfile?>> LookupAsync(string username, CancellationToken ct = default);

    /// <summary>Скин официального игрока с таким ником, если он существует.</summary>
    Task<OperationResult<RemoteSkin?>> FetchSkinAsync(string username, CancellationToken ct = default);
}

public sealed class ProfileLookupService(IHttpService http, ILogService log) : IProfileLookupService
{
    public async Task<OperationResult<OfficialProfile?>> LookupAsync(string username, CancellationToken ct = default)
    {
        if (!PathGuard.IsValidMinecraftUsername(username))
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

            var dashed = ToDashed(profile.Id);
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

    public async Task<OperationResult<RemoteSkin?>> FetchSkinAsync(string username, CancellationToken ct = default)
    {
        var lookup = await LookupAsync(username, ct).ConfigureAwait(false);
        if (!lookup.IsSuccess)
        {
            return OperationResult<RemoteSkin?>.Fail(lookup.Code, lookup.Message ?? "Проверка ника недоступна.");
        }

        if (lookup.Value is null)
        {
            // Ника в официальном сервисе нет — значит и скина нет: это локальный игрок.
            return OperationResult<RemoteSkin?>.Ok(null);
        }

        var uuid = lookup.Value.Uuid;

        // 1. Официальный источник текстуры: sessionserver отдаёт ссылку на PNG и модель.
        var (textureUrl, slim) = await TryGetTextureAsync(uuid, ct).ConfigureAwait(false);

        // sessionserver отдаёт ссылку вида http://textures.minecraft.net/... — принудительно https.
        if (textureUrl is not null && textureUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            textureUrl = "https://" + textureUrl["http://".Length..];
        }

        // 2. Запасной вариант — crafatar (тот же публичный скин, отрендеренный из официальной текстуры).
        var candidates = new List<string?>
        {
            textureUrl,
            $"https://crafatar.com/skins/{uuid}"
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                await using var stream = await http.OpenReadAsync(new Uri(candidate), ct: ct).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                var png = buffer.ToArray();

                if (!PathGuard.LooksLikeMinecraftSkin(png))
                {
                    log.Warn("Auth", $"Текстура {candidate} не похожа на скин Minecraft — пропускаю.");
                    continue;
                }

                return OperationResult<RemoteSkin?>.Ok(new RemoteSkin(lookup.Value.Username, uuid, candidate, png, slim));
            }
            catch (Exception ex)
            {
                log.Warn("Auth", $"Скин с {candidate} не скачался: {ex.Message}");
            }
        }

        return OperationResult<RemoteSkin?>.Fail(ErrorCode.Network,
            "Профиль существует, но скин скачать не удалось. Можно загрузить свой файл.");
    }

    private async Task<(string? Url, bool Slim)> TryGetTextureAsync(string uuid, CancellationToken ct)
    {
        try
        {
            var url = $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid.Replace("-", string.Empty)}";
            var response = await http.GetStringAsync(new Uri(url), allowAnyHost: true, ct: ct).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return (null, false);
            }

            var profile = System.Text.Json.JsonSerializer.Deserialize<MojangSessionProfile>(response.Content, JsonDefaults.External);
            var property = profile?.Properties?.FirstOrDefault(p => p.Name == "textures");
            if (property?.Value is null)
            {
                return (null, false);
            }

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(property.Value));
            var textures = System.Text.Json.JsonSerializer.Deserialize<TexturePayload>(json, JsonDefaults.External);
            var skin = textures?.Textures?.Skin;
            return skin?.Url is null
                ? (null, false)
                : (skin.Url, string.Equals(skin.Metadata?.Model, "slim", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            log.Warn("Auth", $"Текстуры {uuid} недоступны: {ex.Message}");
            return (null, false);
        }
    }

    private static string ToDashed(string id) => id.Length == 32
        ? $"{id[..8]}-{id[8..12]}-{id[12..16]}-{id[16..20]}-{id[20..]}"
        : id;

    private sealed class MojangProfile
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class MojangSessionProfile
    {
        [JsonPropertyName("properties")]
        public List<MojangProperty>? Properties { get; set; }
    }

    private sealed class MojangProperty
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    private sealed class TexturePayload
    {
        [JsonPropertyName("textures")]
        public TextureSet? Textures { get; set; }
    }

    private sealed class TextureSet
    {
        [JsonPropertyName("SKIN")]
        public TextureRef? Skin { get; set; }
    }

    private sealed class TextureRef
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("metadata")]
        public TextureMetadata? Metadata { get; set; }
    }

    private sealed class TextureMetadata
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }
    }
}
