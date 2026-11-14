using EHOT.Launcher.Auth;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Storage;
using Xunit;

namespace EHOT.Launcher.Tests.Core;

/// <summary>
/// Хранение скина и UUID профиля: именно здесь проверяется требование «свой скин виден только
/// владельцу» и «UUID официального игрока не подменяется оффлайн-расчётом».
/// </summary>
public sealed class LocalProfileSkinTests
{
    private static byte[] Skin(int w = 64, int h = 64)
    {
        var bytes = new byte[64];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        bytes[16] = (byte)(w >> 24); bytes[17] = (byte)(w >> 16); bytes[18] = (byte)(w >> 8); bytes[19] = (byte)w;
        bytes[20] = (byte)(h >> 24); bytes[21] = (byte)(h >> 16); bytes[22] = (byte)(h >> 8); bytes[23] = (byte)h;
        return bytes;
    }

    [Fact]
    public async Task SaveSkinFromBytes_MarksSkinAsLocal_AndStoresFile()
    {
        var accounts = new MemoryAccounts();
        var service = new LocalProfileService(accounts, new NoLookup(), new TempAppPaths(), new NullLog());
        var account = await Seed(accounts, "SkinOwner");

        var result = await service.SaveSkinFromBytesAsync(account.Id, Skin(), slim: false, source: "local",
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var saved = await accounts.GetAsync(account.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(saved!.LocalSkinPath);
        Assert.True(File.Exists(saved.LocalSkinPath));
        Assert.Equal(64, ReadSkinSize(saved.LocalSkinPath!));
        // Свой скин не должен выглядеть как официальный: иначе UI соврёт про источник.
        Assert.Equal("local", saved.SkinSource);
        Assert.False(saved.UuidFromOfficialProfile);
    }

    [Fact]
    public async Task SaveSkinFromBytes_RejectsNonSkinFile()
    {
        var accounts = new MemoryAccounts();
        var service = new LocalProfileService(accounts, new NoLookup(), new TempAppPaths(), new NullLog());
        var account = await Seed(accounts, "SkinOwner2");

        var result = await service.SaveSkinFromBytesAsync(account.Id, [1, 2, 3, 4], slim: false, source: "local",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var saved = await accounts.GetAsync(account.Id, TestContext.Current.CancellationToken);
        Assert.Null(saved!.LocalSkinPath);
    }

    [Fact]
    public async Task CreateAsync_UsesOfficialUuidAndCopiesSkin_WhenNickExists()
    {
        var accounts = new MemoryAccounts();
        var lookup = new StubLookup(new RemoteSkin("notch", "069a79f4-44e9-4726-a5be-fca90e38aaf5",
            "https://textures.minecraft.net/texture/x", Skin(64, 32), IsSlim: false));
        var service = new LocalProfileService(accounts, lookup, new TempAppPaths(), new NullLog());

        var result = await service.CreateAsync(new LocalProfileRequest("notch"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.CopiedOfficialSkin);
        Assert.Equal(Guid.Parse("069a79f4-44e9-4726-a5be-fca90e38aaf5"), result.Value.Account.Uuid);
        Assert.True(result.Value.Account.UuidFromOfficialProfile);
        Assert.Equal("mojang", result.Value.Account.SkinSource);
        Assert.NotNull(result.Value.Account.LocalSkinPath);
    }

    [Fact]
    public async Task CreateAsync_FallsBackToOfflineUuid_WhenNickIsFree()
    {
        var accounts = new MemoryAccounts();
        var service = new LocalProfileService(accounts, new StubLookup(null), new TempAppPaths(), new NullLog());

        var result = await service.CreateAsync(new LocalProfileRequest("FreeNick"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.CopiedOfficialSkin);
        Assert.False(result.Value.Account.UuidFromOfficialProfile);
        Assert.Null(result.Value.Account.LocalSkinPath);
        Assert.Equal(EHOT.Launcher.Core.Security.PathGuard.OfflineUuidFor("FreeNick"), result.Value.Account.Uuid);
    }

    [Fact]
    public async Task CreateAsync_KeepsOfficialUuid_EvenWhenSkinFails()
    {
        var accounts = new MemoryAccounts();
        // Ник существует (UUID есть), но текстура скина недоступна — типичный сбой CDN.
        var lookup = new StubLookup(new RemoteSkin("notch", "069a79f4-44e9-4726-a5be-fca90e38aaf5",
            "https://textures.minecraft.net/texture/x", [], IsSlim: false));
        var service = new LocalProfileService(accounts, lookup, new TempAppPaths(), new NullLog());

        var result = await service.CreateAsync(new LocalProfileRequest("notch"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Account.UuidFromOfficialProfile);
        Assert.Equal(Guid.Parse("069a79f4-44e9-4726-a5be-fca90e38aaf5"), result.Value.Account.Uuid);
        Assert.False(result.Value.CopiedOfficialSkin);
        Assert.Null(result.Value.Account.LocalSkinPath);
    }

    private static int ReadSkinSize(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[24];
        stream.ReadExactly(header);
        // PNG хранит размеры big-endian в IHDR: байты 16..19 — ширина, 20..23 — высота.
        return (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
    }

    private static async Task<AccountInfo> Seed(MemoryAccounts accounts, string name)
    {
        var account = new AccountInfo
        {
            Id = "acc" + name.Length,
            Username = name,
            Uuid = EHOT.Launcher.Core.Security.PathGuard.OfflineUuidFor(name),
            Kind = AccountKind.Local,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await accounts.SaveAsync(account, TestContext.Current.CancellationToken);
        return account;
    }

    private sealed class MemoryAccounts : IAccountRepository
    {
        private readonly Dictionary<string, AccountInfo> _items = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<AccountInfo>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AccountInfo>>(_items.Values.ToArray());

        public Task<AccountInfo?> GetAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_items.GetValueOrDefault(id));

        public Task SaveAsync(AccountInfo account, CancellationToken ct = default)
        {
            _items[account.Id] = account;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            _items.Remove(id);
            return Task.CompletedTask;
        }

        public Task TouchAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubLookup(RemoteSkin? skin) : IProfileLookupService
    {
        public Task<OperationResult<OfficialProfile?>> ResolveOfficialAsync(string username, CancellationToken ct = default)
            => LookupAsync(username, ct);

        public Task<OperationResult<OfficialProfile?>> LookupAsync(string username, CancellationToken ct = default)
            => Task.FromResult(OperationResult<OfficialProfile?>.Ok(skin is null
                ? null
                : new OfficialProfile(skin.Username, skin.Uuid, skin.SkinUrl)));

        public Task<OperationResult<RemoteSkin?>> FetchSkinAsync(string username, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RemoteSkin?>.Ok(skin));
    }

    private sealed class NoLookup : IProfileLookupService
    {
        public Task<OperationResult<OfficialProfile?>> ResolveOfficialAsync(string username, CancellationToken ct = default)
            => LookupAsync(username, ct);

        public Task<OperationResult<OfficialProfile?>> LookupAsync(string username, CancellationToken ct = default)
            => Task.FromResult(OperationResult<OfficialProfile?>.Ok(null));

        public Task<OperationResult<RemoteSkin?>> FetchSkinAsync(string username, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RemoteSkin?>.Ok(null));
    }

    private sealed class TempAppPaths : EHOT.Launcher.Core.Platform.IAppPaths
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ehot-test-" + Guid.NewGuid().ToString("N")[..8]);

        public string AppDataDirectory => _root;

        public string DatabasePath => Path.Combine(_root, "ehot.db");

        public string LogsDirectory => Path.Combine(_root, "logs");

        public string InstancesDirectory => Path.Combine(_root, "instances");

        public string DownloadsDirectory => Path.Combine(_root, "downloads");

        public string CacheDirectory => Path.Combine(_root, "cache");

        public string SecretsPath => Path.Combine(_root, "secrets.bin");

        public string MinecraftRoot => Path.Combine(_root, "minecraft");

        public string DefaultMinecraftDirectory => Path.Combine(_root, "minecraft");

        public string DefaultDownloadsDirectory => DownloadsDirectory;

        public void EnsureCreated()
        {
            foreach (var dir in new[] { _root, LogsDirectory, InstancesDirectory, DownloadsDirectory, CacheDirectory })
            {
                Directory.CreateDirectory(dir);
            }
        }
    }

    private sealed class NullLog : ILogService
    {
        public IReadOnlyList<LogEntry> Recent => [];

        public event Action<LogEntry>? EntryAdded { add { } remove { } }

        public void Write(LogLevel2 level, string category, string message, Exception? ex = null)
        {
        }

        public void Info(string category, string message)
        {
        }

        public void Warn(string category, string message, Exception? ex = null)
        {
        }

        public void Error(string category, string message, Exception? ex = null)
        {
        }

        public string CurrentLogFile => string.Empty;

        public Task FlushAsync() => Task.CompletedTask;
    }
}
