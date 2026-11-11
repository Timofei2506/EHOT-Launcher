using EHOT.Launcher.API.Services;
using Xunit;

namespace EHOT.Launcher.Tests.API;

/// <summary>
/// Коды приглашений и виртуальные адреса комнат: проверка хэширования, стойкости сравнения,
/// различимости кодов и корректного распределения адресов в подсети.
/// </summary>
public sealed class InviteServiceTests
{
    private static readonly Guid RoomId = Guid.Parse("0f8f9a2e-1f2b-4d3c-9a51-2b1f7e0d1a11");

    private static InviteService CreateService() => new(new ApiOptions
    {
        InviteSecret = new string('s', 48),
        JwtSecret = new string('j', 48)
    });

    [Fact]
    public void GenerateCode_HasRequestedLengthAndAllowedAlphabet()
    {
        var service = CreateService();
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        for (var i = 0; i < 50; i++)
        {
            var code = service.GenerateCode();

            Assert.Equal(8, code.Length);
            Assert.All(code, ch => Assert.True(alphabet.Contains(ch, StringComparison.Ordinal), $"Недопустимый символ '{ch}' в коде."));
        }
    }

    [Fact]
    public void GenerateCode_IsNotRepeating()
    {
        var service = CreateService();
        var codes = Enumerable.Range(0, 200).Select(_ => service.GenerateCode()).ToHashSet(StringComparer.Ordinal);

        Assert.True(codes.Count >= 195, $"Получено {codes.Count} уникальных кодов из 200 — генератор слишком предсказуем.");
    }

    [Fact]
    public void Hash_IsDeterministicAndCaseInsensitive()
    {
        var service = CreateService();

        var hash = service.Hash("ABCD2345", RoomId);

        Assert.Equal(hash, service.Hash("ABCD2345", RoomId));
        Assert.Equal(hash, service.Hash("abcd2345 ", RoomId));
        Assert.Equal(64, hash.Length); // hex от SHA-256
    }

    [Fact]
    public void Hash_DependsOnRoomAndSecret()
    {
        var service = CreateService();
        var otherRoomSecret = new InviteService(new ApiOptions
        {
            InviteSecret = new string('t', 48),
            JwtSecret = new string('j', 48)
        });

        Assert.NotEqual(service.Hash("ABCD2345", RoomId), service.Hash("ABCD2345", Guid.NewGuid()));
        Assert.NotEqual(service.Hash("ABCD2345", RoomId), otherRoomSecret.Hash("ABCD2345", RoomId));
    }

    [Fact]
    public void Verify_AcceptsOnlyMatchingCode()
    {
        var service = CreateService();
        var hash = service.Hash("ABCD2345", RoomId);

        Assert.True(service.Verify("ABCD2345", RoomId, hash));
        Assert.True(service.Verify("abcd2345", RoomId, hash));
        Assert.False(service.Verify("ZZZZ9999", RoomId, hash));
    }

    [Fact]
    public void ApiOptions_RequiresLongSecrets()
    {
        Assert.False(new ApiOptions { JwtSecret = "short", InviteSecret = "short" }.IsConfigured);
        Assert.True(new ApiOptions { JwtSecret = new string('a', 32), InviteSecret = new string('b', 32) }.IsConfigured);
    }

    [Fact]
    public void BuildSubnet_StaysWithinAllocatedRange()
    {
        for (var seed = 0; seed < 64; seed++)
        {
            var subnet = IpAllocator.BuildSubnet(seed);
            var second = int.Parse(subnet.Split('.')[1]);

            Assert.InRange(second, 64, 79);
            Assert.EndsWith(".0.0/24", subnet, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Allocate_SkipsUsedAddressesAndNeverReturnsNetworkOrBroadcast()
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { "10.64.0.2", "10.64.0.3" };

        var address = IpAllocator.Allocate(used, "10.64.0.0/24");

        Assert.Equal("10.64.0.4", address);

        // Диапазон раздачи — .2 … .253 (адрес сети .0, широковещательный .255 не используются).
        var nearlyFull = new HashSet<string>(Enumerable.Range(2, 251).Select(i => $"10.64.0.{i}"), StringComparer.Ordinal);
        Assert.Equal("10.64.0.253", IpAllocator.Allocate(nearlyFull, "10.64.0.0/24"));

        var full = new HashSet<string>(Enumerable.Range(2, 252).Select(i => $"10.64.0.{i}"), StringComparer.Ordinal);
        Assert.Throws<InvalidOperationException>(() => IpAllocator.Allocate(full, "10.64.0.0/24"));
    }
}
