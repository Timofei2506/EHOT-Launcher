using EHOT.Launcher.Content.Modpacks;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Game.Install;

namespace EHOT.Launcher.Content;

/// <summary>
/// Адаптер между модулем контента и игровым модулем.
/// Позволяет Content устанавливать версию игры для сборки, не дублируя логику установки.
/// </summary>
public sealed class GameInstallerBridge(ILoaderInstaller loaderInstaller, IGameInstaller gameInstaller) : IGameInstallerBridge
{
    public async Task<OperationResult> InstallVersionAsync(
        string instanceDirectory,
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion,
        IProgress<InstallProgressBridge>? progress,
        CancellationToken ct = default)
    {
        if (loader == LoaderKind.Vanilla)
        {
            var progressAdapter = progress is null
                ? null
                : new Progress<InstallProgress>(p => progress.Report(new InstallProgressBridge(p.Stage, p.Fraction, p.Detail)));

            var result = await gameInstaller.InstallAsync(instanceDirectory, minecraftVersion, new InstallOptions(), progressAdapter, ct).ConfigureAwait(false);
            return result.ToOperationResult();
        }

        var adapter = progress is null
            ? null
            : new Progress<InstallProgress>(p => progress.Report(new InstallProgressBridge(p.Stage, p.Fraction, p.Detail)));

        var install = await loaderInstaller.InstallLoaderAsync(instanceDirectory, minecraftVersion, loader, loaderVersion, adapter, ct).ConfigureAwait(false);
        return install.ToOperationResult();
    }
}
