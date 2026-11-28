using CommunityToolkit.Mvvm.ComponentModel;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Вкладка левого списка в настройках (концепт).</summary>
public sealed partial class SettingsTab : ObservableObject
{
    public SettingsTab(string key, string label, string iconKey)
    {
        Key = key;
        Label = label;
        IconKey = iconKey;
    }

    public string Key { get; }

    public string Label { get; }

    public string IconKey { get; }

    [ObservableProperty]
    private bool _isSelected;
}
