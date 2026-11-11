using EHOT.Launcher.Content.Models;
using EHOT.Launcher.Core.Models;

namespace EHOT.Launcher.Content.Resolution;

public enum ResolveStatus
{
    Resolved = 0,
    MissingRequired = 1,
    Conflict = 2,
    Incompatible = 3,
    CycleDetected = 4
}

public sealed record ResolveIssue(string Kind, string Message, string? ProjectId = null);

public sealed record DependencyNode(ContentVersion Version, int Depth, bool IsRoot, IReadOnlyList<string> RequiredBy);

public sealed record ResolveResult(
    ResolveStatus Status,
    IReadOnlyList<DependencyNode> InstallOrder,
    IReadOnlyList<ResolveIssue> Issues)
{
    public bool IsSuccess => Status == ResolveStatus.Resolved;

    /// <summary>Все версии, которые нужно скачать (в порядке установки).</summary>
    public IReadOnlyList<ContentVersion> Versions => InstallOrder.Select(n => n.Version).ToArray();
}

public interface IDependencyResolver
{
    Task<ResolveResult> ResolveAsync(
        IContentProvider provider,
        ContentVersion root,
        string minecraftVersion,
        LoaderKind loader,
        CancellationToken ct = default);
}

/// <summary>
/// Разрешение зависимостей модов: обход required-зависимостей, выбор совместимой версии,
/// обнаружение циклов, конфликтов (incompatible) и несовместимости по версии игры/лоадеру.
/// </summary>
public sealed class DependencyResolver : IDependencyResolver
{
    private const int MaxDepth = 12;
    private const int MaxNodes = 200;

    public async Task<ResolveResult> ResolveAsync(
        IContentProvider provider,
        ContentVersion root,
        string minecraftVersion,
        LoaderKind loader,
        CancellationToken ct = default)
    {
        var issues = new List<ResolveIssue>();
        var nodes = new Dictionary<string, DependencyNode>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<DependencyNode>();

        if (!IsCompatible(root, minecraftVersion, loader))
        {
            issues.Add(new ResolveIssue("incompatible",
                $"«{root.ProjectTitle}» не поддерживает Minecraft {minecraftVersion} с загрузчиком {loader}."));
            return new ResolveResult(ResolveStatus.Incompatible, [], issues);
        }

        var rootNode = new DependencyNode(root, 0, true, []);
        nodes[root.Id] = rootNode;

        await VisitAsync(root, 0, root.ProjectTitle).ConfigureAwait(false);

        return issues.Any(i => i.Kind == "conflict")
            ? new ResolveResult(ResolveStatus.Conflict, order, issues)
            : issues.Any(i => i.Kind == "missing")
                ? new ResolveResult(ResolveStatus.MissingRequired, order, issues)
                : new ResolveResult(ResolveStatus.Resolved, order, issues);

        async Task VisitAsync(ContentVersion version, int depth, string requiredBy)
        {
            ct.ThrowIfCancellationRequested();

            if (depth > MaxDepth || nodes.Count > MaxNodes)
            {
                issues.Add(new ResolveIssue("limit", "Слишком глубокая или большая цепочка зависимостей.", version.ProjectId));
                return;
            }

            if (visiting.Contains(version.ProjectId))
            {
                issues.Add(new ResolveIssue("cycle", $"Циклическая зависимость: {version.ProjectTitle}.", version.ProjectId));
                return;
            }

            visiting.Add(version.ProjectId);

            foreach (var dep in version.Dependencies)
            {
                if (dep.Kind == "incompatible")
                {
                    issues.Add(new ResolveIssue("conflict",
                        $"«{version.ProjectTitle}» несовместим с «{dep.FileName ?? dep.ProjectId}».", dep.ProjectId));
                    continue;
                }

                if (dep.Kind is not ("required" or "embedded"))
                {
                    continue;
                }

                if (dep.Kind == "embedded")
                {
                    continue; // встроенная зависимость поставляется внутри файла
                }

                var resolved = await ResolveDependencyAsync(dep, minecraftVersion, loader, ct).ConfigureAwait(false);
                if (resolved is null)
                {
                    if (dep.Kind == "required")
                    {
                        issues.Add(new ResolveIssue("missing",
                            $"Не найдена обязательная зависимость для «{version.ProjectTitle}» ({(dep.FileName ?? dep.ProjectId ?? dep.VersionId)}).",
                            dep.ProjectId));
                    }

                    continue;
                }

                if (nodes.TryGetValue(resolved.Id, out var existing))
                {
                    if (existing.Version.Id != resolved.Id)
                    {
                        issues.Add(new ResolveIssue("conflict",
                            $"Конфликт версий для «{resolved.ProjectTitle}»: выбрана {existing.Version.VersionNumber}, требуется {resolved.VersionNumber}.",
                            dep.ProjectId));
                    }

                    continue;
                }

                if (!IsCompatible(resolved, minecraftVersion, loader))
                {
                    issues.Add(new ResolveIssue("incompatible",
                        $"Зависимость «{resolved.ProjectTitle}» {resolved.VersionNumber} не поддерживает {minecraftVersion}/{loader}.",
                        resolved.ProjectId));
                    continue;
                }

                var node = new DependencyNode(resolved, depth + 1, false, [requiredBy]);
                nodes[resolved.Id] = node;
                order.Add(node);
                await VisitAsync(resolved, depth + 1, resolved.ProjectTitle).ConfigureAwait(false);
            }

            visiting.Remove(version.ProjectId);
        }

        async Task<ContentVersion?> ResolveDependencyAsync(ContentDependencyRef dep, string mc, LoaderKind l, CancellationToken t)
        {
            try
            {
                if (!string.IsNullOrEmpty(dep.VersionId))
                {
                    return await provider.GetVersionAsync(dep.VersionId, t).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(dep.ProjectId))
                {
                    var versions = await provider.GetVersionsAsync(dep.ProjectId, mc, l, t).ConfigureAwait(false);
                    if (versions.Count == 0)
                    {
                        return null;
                    }

                    for (var i = 0; i < versions.Count; i++)
                    {
                        if (IsCompatible(versions[i], mc, l))
                        {
                            return versions[i];
                        }
                    }

                    return versions[0];
                }
            }
            catch (Exception)
            {
                // сетевые ошибки не должны «ломать» результат — сообщаем как missing
            }

            return null;
        }
    }

    public static bool IsCompatible(ContentVersion version, string minecraftVersion, LoaderKind loader)
    {
        if (version.GameVersions.Count > 0 && !version.GameVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (version.Loaders.Count > 0 && loader != LoaderKind.Vanilla)
        {
            var expected = Modrinth.ModrinthProvider.LoaderToCategory(loader);
            if (!version.Loaders.Contains(expected, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
