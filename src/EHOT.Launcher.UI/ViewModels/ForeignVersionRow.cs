using CommunityToolkit.Mvvm.ComponentModel;
using EHOT.Launcher.Game.Import;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Строка списка найденных версий: обёртка над <see cref="ForeignVersionInfo"/> с флагом выбора.</summary>
public sealed partial class ForeignVersionRow : ObservableObject
{
    public ForeignVersionRow(ForeignVersionInfo info)
    {
        Info = info;
        _isSelected = !info.AlreadyImported;
    }

    public ForeignVersionInfo Info { get; }

    public string VersionId => Info.VersionId;

    public string SourceLabel => Info.SourceLabel;

    public string SizeLabel => Info.SizeLabel;

    public string DataLabel => Info.DataLabel;

    public bool AlreadyImported => Info.AlreadyImported;

    public bool CanImport => !Info.AlreadyImported;

    [ObservableProperty]
    private bool _isSelected;
}
