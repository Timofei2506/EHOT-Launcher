using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;

namespace EHOT.Launcher.UI.ViewModels;

public sealed partial class AboutViewModel(IPlatformInfo platform, ICapabilityRegistry capabilities) : ViewModelBase
{
    /// <summary>Версия берётся из сборки (Directory.Build.props), а не дублируется вручную.</summary>
    public static string Version
    {
        get
        {
            var version = typeof(AboutViewModel).Assembly.GetName().Version;
            return version is null ? "версия не определена" : $"{version.Major}.{version.Minor}.{version.Build} (beta)";
        }
    }

    public string Runtime => $"Avalonia 12.1.6 · .NET {platform.RuntimeVersion}";

    public string Platform => $"{platform.OsName} {platform.OsVersion} · {platform.Architecture}";

    public ObservableCollection<Capability> FeatureMatrix { get; } = new(capabilities.All);

    public static string LegalNotice =>
        "EHOT Launcher не связан с Mojang Studios и Microsoft. Minecraft — торговая марка Mojang AB. " +
        "Лаунчер не обходит лицензионную защиту: для официальных серверов требуется приобретённая копия игры " +
        "и вход через Microsoft. Контент загружается из официальных API (Modrinth, CurseForge при наличии ключа) с " +
        "соблюдением их условий использования.";

    public static string Credits =>
        "Иконки — собственные SVG-контуры EHOT. Шрифт Inter (SIL Open Font License). " +
        "Спасибо сообществу Fabric, NeoForge, Modrinth за открытые форматы и API.";

    [ObservableProperty]
    private string _buildInfo = string.Empty;

    public override Task OnNavigatedToAsync(CancellationToken ct = default)
    {
        BuildInfo = $"Сборка: {typeof(AboutViewModel).Assembly.GetName().Version} · " +
                    $"Конфигурация: {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}";
        return Task.CompletedTask;
    }
}
