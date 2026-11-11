using System.Security.Cryptography;
using System.Text;

namespace EHOT.Launcher.API.Services;

/// <summary>
/// Коды приглашений: 8 символов base32 (без похожих 0/O/1/I), HMAC-SHA256 от кода хранится в БД.
/// Код нельзя восстановить из хэша; TTL и лимит использований проверяются при входе.
/// </summary>
public sealed class InviteService(ApiOptions options)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string GenerateCode(int length = 8)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        var sb = new StringBuilder(length);
        foreach (var b in bytes)
        {
            sb.Append(Alphabet[b % Alphabet.Length]);
        }

        return sb.ToString();
    }

    public string Hash(string code, Guid roomId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.InviteSecret));
        var payload = Encoding.UTF8.GetBytes($"{roomId:N}:{code.Trim().ToUpperInvariant()}");
        return Convert.ToHexString(hmac.ComputeHash(payload));
    }

    public bool Verify(string code, Guid roomId, string storedHash)
    {
        var computed = Hash(code, roomId);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(storedHash));
    }
}

/// <summary>Распределение виртуальных адресов внутри подсети комнаты.</summary>
public static class IpAllocator
{
    public static string Allocate(HashSet<string> used, string subnet = "10.64.0.0/24")
    {
        var prefix = subnet.Split('/')[0];
        var octets = prefix.Split('.');
        var basePrefix = $"{octets[0]}.{octets[1]}.{octets[2]}";

        for (var host = 2; host < 254; host++)
        {
            var candidate = $"{basePrefix}.{host}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("В подсети комнаты закончились адреса.");
    }

    public static string BuildSubnet(int roomSeed) => $"10.{64 + (roomSeed % 16)}.0.0/24";
}
