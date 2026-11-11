using EHOT.Launcher.Core.Security;
using Xunit;

namespace EHOT.Launcher.Tests.Core;

/// <summary>
/// Безопасность путей: любые внешние имена файлов (архивы, API, пользовательский ввод)
/// не должны выводить за пределы корневой папки.
/// </summary>
public sealed class PathGuardTests : IDisposable
{
    private readonly string _root;

    public PathGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ehot-pathguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // временная папка — не критично для теста
        }
    }

    [Theory]
    [InlineData("mods/fabric-api.jar")]
    [InlineData("assets/../assets/icon.png")]
    [InlineData("./saves/world/level.dat")]
    public void EnsureInside_AllowsPathsBelowRoot(string candidate)
    {
        var resolved = PathGuard.EnsureInside(_root, candidate);

        Assert.StartsWith(Path.GetFullPath(_root), resolved, StringComparison.Ordinal);
        Assert.True(PathGuard.IsInside(_root, candidate));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("mods/../../escape.jar")]
    public void EnsureInside_RejectsTraversal(string candidate)
    {
        Assert.Throws<SecurityException>(() => PathGuard.EnsureInside(_root, candidate));
        Assert.False(PathGuard.IsInside(_root, candidate));
    }

    [Fact]
    public void EnsureInside_RejectsAbsolutePathOutsideRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ehot-other-" + Guid.NewGuid().ToString("N"), "file.txt");

        Assert.Throws<SecurityException>(() => PathGuard.EnsureInside(_root, outside));
    }

    [Fact]
    public void EnsureInside_RejectsSiblingDirectoryWithSamePrefix()
    {
        // Классическая ловушка: "/tmp/ehot-x" является префиксом для "/tmp/ehot-x-evil".
        var sibling = _root + "-evil" + Path.DirectorySeparatorChar + "file.txt";

        Assert.Throws<SecurityException>(() => PathGuard.EnsureInside(_root, sibling));
    }

    [Fact]
    public void EnsureInside_EmptyRootThrows()
    {
        Assert.ThrowsAny<ArgumentException>(() => PathGuard.EnsureInside(" ", "file.txt"));
    }
}
