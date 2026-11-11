using EHOT.Launcher.Content.Models;
using EHOT.Launcher.Content.Resolution;
using EHOT.Launcher.Core.Models;
using Xunit;

namespace EHOT.Launcher.Tests.Content;

/// <summary>Проверка совместимости версии контента с версией игры и загрузчиком.</summary>
public sealed class DependencyResolverTests
{
    private static ContentVersion Version(string[] gameVersions, string[] loaders) => new()
    {
        ProviderId = "modrinth",
        Id = "ver-1",
        ProjectId = "proj-1",
        ProjectTitle = "Example Mod",
        Name = "1.0.0",
        VersionNumber = "1.0.0",
        GameVersions = gameVersions,
        Loaders = loaders,
        Type = ContentType.Mod,
        Files = [new ContentFile { Url = "https://example.invalid/x.jar", FileName = "x.jar", Primary = true }]
    };

    [Fact]
    public void IsCompatible_MatchingGameVersionAndLoader()
    {
        var version = Version(["1.21.4"], ["fabric"]);

        Assert.True(DependencyResolver.IsCompatible(version, "1.21.4", LoaderKind.Fabric));
    }

    [Fact]
    public void IsCompatible_RejectsDifferentGameVersion()
    {
        var version = Version(["1.20.1"], ["fabric"]);

        Assert.False(DependencyResolver.IsCompatible(version, "1.21.4", LoaderKind.Fabric));
    }

    [Fact]
    public void IsCompatible_RejectsDifferentLoader()
    {
        var version = Version(["1.21.4"], ["forge"]);

        Assert.False(DependencyResolver.IsCompatible(version, "1.21.4", LoaderKind.Fabric));
    }

    [Fact]
    public void IsCompatible_EmptyMetadataIsTreatedAsUniversal()
    {
        var version = Version([], []);

        Assert.True(DependencyResolver.IsCompatible(version, "1.21.4", LoaderKind.NeoForge));
    }

    [Fact]
    public void IsCompatible_GameVersionComparisonIgnoresCase()
    {
        var version = Version(["1.21.4"], ["fabric"]);

        Assert.True(DependencyResolver.IsCompatible(version, "1.21.4", LoaderKind.Fabric));
        Assert.False(DependencyResolver.IsCompatible(version, "1.21.5", LoaderKind.Fabric));
    }
}
