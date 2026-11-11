using EHOT.Launcher.Update;
using Xunit;

namespace EHOT.Launcher.Tests.Update;

/// <summary>
/// Логика автообновления, которую опасно проверять «на живом релизе»:
/// разбор версий из тегов, выбор файла под платформу, честные статусы при отсутствии канала.
/// </summary>
public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("0.2.3", "0.2.3")]
    [InlineData("v1.0.0-beta.1", "1.0.0")]
    [InlineData("V2.5.1+sha.abcdef", "2.5.1")]
    [InlineData("release-1.2.3", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseVersion_ReadsSemanticTags(string? tag, string? expected)
    {
        var version = GitHubUpdateService.ParseVersion(tag);

        if (expected is null)
        {
            Assert.Null(version);
        }
        else
        {
            Assert.Equal(Version.Parse(expected), version);
        }
    }

    [Fact]
    public void ParseVersion_ComparesNumericallyNotAlphabetically()
    {
        // Классическая ловушка: "0.10.0" новее "0.9.0" только при числовом сравнении.
        var older = GitHubUpdateService.ParseVersion("v0.9.0")!;
        var newer = GitHubUpdateService.ParseVersion("v0.10.0")!;

        Assert.True(newer > older);
    }

    private static ReleaseAsset Asset(string name) =>
        new(name, new Uri("https://example.invalid/" + name), 1000, name.GetHashCode(StringComparison.Ordinal));

    [Fact]
    public void SelectAsset_PicksInstallerForCurrentOs()
    {
        var assets = new List<ReleaseAsset>
        {
            Asset("EHOT-Launcher-0.2.0-win-x64-setup.exe"),
            Asset("EHOT-Launcher-0.2.0-osx-arm64.dmg"),
            Asset("EHOT-Launcher-0.2.0-osx-x64.dmg"),
            Asset("EHOT-Launcher-0.2.0-linux-x64.tar.gz"),
            Asset("EHOT-Launcher-0.2.0-sources.tar.gz"),
            Asset("checksums.sha256")
        };

        var selected = GitHubUpdateService.SelectAsset(assets);

        Assert.NotNull(selected);
        var expectedSuffix = OperatingSystem.IsWindows() ? ".exe"
            : OperatingSystem.IsMacOS() ? ".dmg"
            : ".tar.gz";
        Assert.EndsWith(expectedSuffix, selected!.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectAsset_PrefersExactArchitecture()
    {
        // На arm64-машине не должен утягиваться x64-файл: это привело бы к «не запускается».
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                   System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        var other = arch == "arm64" ? "x64" : "arm64";
        var prefix = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";

        var assets = new List<ReleaseAsset>
        {
            Asset($"EHOT-0.2.0-{prefix}-{other}.zip"),
            Asset($"EHOT-0.2.0-{prefix}-{arch}.zip")
        };

        var selected = GitHubUpdateService.SelectAsset(assets);

        Assert.NotNull(selected);
        Assert.Contains(arch, selected!.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectAsset_ReturnsNullWhenNothingMatches()
    {
        var assets = new List<ReleaseAsset> { Asset("docs.pdf"), Asset("checksums.sha256") };

        Assert.Null(GitHubUpdateService.SelectAsset(assets));
    }

    [Fact]
    public async Task CheckAsync_WithoutRepository_ReportsNotConfigured()
    {
        var options = new UpdateOptions { Repository = string.Empty, StagingRoot = string.Empty };
        using var log = new FakeLog();
        var service = new GitHubUpdateService(options, log, new FakePlatform());

        var result = await service.CheckAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.NotConfigured, result.Status);
        Assert.False(service.IsConfigured);
        Assert.False(result.HasUpdate);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void UpdateOptions_RequiresRepositoryAndStaging()
    {
        Assert.False(new UpdateOptions { Repository = "owner/repo" }.IsConfigured);
        Assert.False(new UpdateOptions { Repository = "repo-without-owner", StagingRoot = "/tmp" }.IsConfigured);
        Assert.True(new UpdateOptions { Repository = "owner/repo", StagingRoot = "/tmp" }.IsConfigured);
    }

    [Fact]
    public void UpdateCheckResult_HasUpdateOnlyWhenAvailable()
    {
        var release = new ReleaseInfo(
            new Version(1, 0, 0), "v1.0.0", "Релиз", null,
            new Uri("https://example.invalid"), DateTimeOffset.UnixEpoch, false, []);

        Assert.True(new UpdateCheckResult(UpdateAvailability.Available, release, new Version(0, 9, 0)).HasUpdate);
        Assert.False(new UpdateCheckResult(UpdateAvailability.UpToDate, release, new Version(1, 0, 0)).HasUpdate);
        Assert.False(new UpdateCheckResult(UpdateAvailability.Failed).HasUpdate);
    }

    private sealed class FakeLog : EHOT.Launcher.Core.Diagnostics.ILogService, IDisposable
    {
        public string CurrentLogFile => string.Empty;

        public IReadOnlyList<EHOT.Launcher.Core.Diagnostics.LogEntry> Recent => [];

        public event Action<EHOT.Launcher.Core.Diagnostics.LogEntry>? EntryAdded;

        public void Write(EHOT.Launcher.Core.Diagnostics.LogLevel2 level, string category, string message, Exception? ex = null)
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

        public Task FlushAsync() => Task.CompletedTask;

        public void Dispose()
        {
            _ = EntryAdded;
            GC.SuppressFinalize(this);
        }
    }

    private sealed class FakePlatform : EHOT.Launcher.Core.Platform.IPlatformInfo
    {
        public string OsName => "Test";

        public string OsVersion => "1.0";

        public string Architecture => "x64";

        public string RuntimeVersion => "10.0";

        public string RuntimeIdentifier => "linux-x64";

        public bool IsWindows => false;

        public bool IsMacOS => false;

        public bool IsLinux => true;

        public long TotalMemoryBytes => 8L * 1024 * 1024 * 1024;

        public int LogicalProcessorCount => 4;
    }
}
