using System.IO.Compression;
using System.Text;
using EHOT.Launcher.Core.Archives;
using EHOT.Launcher.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EHOT.Launcher.Tests.Core;

/// <summary>
/// Распаковка архивов: zip-slip, симлинки, zip-бомбы и лимиты должны приводить к ошибке,
/// а не к «тихому» пропуску. Проверяется на реальных ZIP-файлах, без моков.
/// </summary>
public sealed class SafeZipExtractorTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string _dir;
    private readonly SafeZipExtractor _extractor = new(NullLogger<SafeZipExtractor>.Instance);

    public SafeZipExtractorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ehot-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // временная папка — не критично для теста
        }
    }

    private static string CreateZip(string dir, string name, params (string Entry, string Content)[] entries)
    {
        var zipPath = Path.Combine(dir, name);
        using var fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            var e = zip.CreateEntry(entry);
            using var s = e.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes, 0, bytes.Length);
        }

        return zipPath;
    }

    [Fact]
    public async Task ExtractZip_ExtractsNormalArchive()
    {
        var zip = CreateZip(_dir, "normal.zip", ("mods/example.jar", "binary"), ("config/settings.json", "{}"));
        var dest = Path.Combine(_dir, "out");

        var report = await _extractor.ExtractZipAsync(zip, dest, ExtractionLimits.ContentPack, ct: Token);

        Assert.Equal(2, report.Entries);
        Assert.True(File.Exists(Path.Combine(dest, "mods", "example.jar")));
        Assert.True(File.Exists(Path.Combine(dest, "config", "settings.json")));
    }

    [Fact]
    public async Task ExtractZip_RejectsZipSlip()
    {
        var zip = CreateZip(_dir, "slip.zip", ("../../escaped.txt", "pwned"));
        var dest = Path.Combine(_dir, "out-slip");

        await Assert.ThrowsAnyAsync<SecurityException>(
            () => _extractor.ExtractZipAsync(zip, dest, ExtractionLimits.ContentPack, ct: Token));

        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }

    [Fact]
    public async Task ExtractZip_RejectsSymlinkEntry()
    {
        var zipPath = Path.Combine(_dir, "symlink.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("link");
            e.ExternalAttributes = 0xA1FF << 16; // S_IFLNK | 0777
            await using var s = e.Open();
            s.Write("some/target"u8);
        }

        await Assert.ThrowsAnyAsync<SecurityException>(
            () => _extractor.ExtractZipAsync(zipPath, Path.Combine(_dir, "out-link"), ExtractionLimits.ContentPack, ct: Token));
    }

    [Fact]
    public async Task ExtractZip_RejectsTooManyEntries()
    {
        var entries = Enumerable.Range(0, 40).Select(i => ($"f{i}.txt", "x")).ToArray();
        var zip = CreateZip(_dir, "many.zip", entries);
        var limits = new ExtractionLimits { MaxEntries = 10 };

        await Assert.ThrowsAnyAsync<SecurityException>(
            () => _extractor.ExtractZipAsync(zip, Path.Combine(_dir, "out-many"), limits, ct: Token));
    }

    [Fact]
    public async Task ExtractZip_RejectsOversizedEntry()
    {
        var big = new string('a', 200_000);
        var zip = CreateZip(_dir, "big.zip", ("big.txt", big));
        var limits = new ExtractionLimits { MaxEntryBytes = 1000 };

        await Assert.ThrowsAnyAsync<SecurityException>(
            () => _extractor.ExtractZipAsync(zip, Path.Combine(_dir, "out-big"), limits, ct: Token));
    }

    [Fact]
    public async Task ExtractZip_RespectVersionCancellation()
    {
        var zip = CreateZip(_dir, "cancel.zip", ("a.txt", "a"), ("b.txt", "b"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _extractor.ExtractZipAsync(zip, Path.Combine(_dir, "out-cancel"), ExtractionLimits.ContentPack, ct: cts.Token));
    }

    [Fact]
    public async Task ReadTextEntry_ReturnsContentAndNullForMissing()
    {
        var zip = CreateZip(_dir, "index.zip", ("modrinth.index.json", "{\"formatVersion\":1}"));

        var json = await _extractor.ReadTextEntryAsync(zip, "modrinth.index.json", ct: Token);
        var missing = await _extractor.ReadTextEntryAsync(zip, "absent.json", ct: Token);

        Assert.Equal("{\"formatVersion\":1}", json);
        Assert.Null(missing);
    }
}
