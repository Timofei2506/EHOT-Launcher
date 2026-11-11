using EHOT.Launcher.Content.Models;
using EHOT.Launcher.Content.Modrinth;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Net;

namespace EHOT.Launcher.Content;

/// <summary>
/// Реестр провайдеров каталога. Modrinth — основной (без ключа),
/// CurseForge подключается только при наличии ключа в настройках.
/// </summary>
public sealed class ContentProviderRegistry : IContentProviderRegistry
{
    public ContentProviderRegistry(IHttpService http, ILogService log, string? curseForgeApiKey)
    {
        var modrinth = new ModrinthProvider(http, log);
        Providers = new IContentProvider[]
        {
            modrinth,
            new CurseForgeProvider(http, log, curseForgeApiKey)
        };

        Primary = modrinth;
    }

    public IReadOnlyList<IContentProvider> Providers { get; }

    public IContentProvider Primary { get; }

    public IContentProvider? Get(string id)
        => Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
