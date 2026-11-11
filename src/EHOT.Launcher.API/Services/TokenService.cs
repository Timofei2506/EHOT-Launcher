using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace EHOT.Launcher.API.Services;

public sealed record ApiOptions
{
    /// <summary>Секрет подписи JWT. Обязателен в production, задаётся через env/secrets.</summary>
    public string JwtSecret { get; init; } = string.Empty;

    /// <summary>Секрет HMAC для кодов приглашений.</summary>
    public string InviteSecret { get; init; } = string.Empty;

    public string Issuer { get; init; } = "ehot-control-plane";

    public string Audience { get; init; } = "ehot-launcher";

    public int TokenLifetimeDays { get; init; } = 30;

    /// <summary>Максимальное число комнат на одну identity.</summary>
    public int MaxRoomsPerIdentity { get; init; } = 3;

    public bool IsConfigured => JwtSecret.Length >= 32 && InviteSecret.Length >= 32;
}

public sealed class TokenService(ApiOptions options)
{
    public string CreateToken(Guid identityId, string displayName)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, identityId.ToString()),
            new Claim("name", displayName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new Claim(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            expires: DateTimeOffset.UtcNow.AddDays(options.TokenLifetimeDays).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
