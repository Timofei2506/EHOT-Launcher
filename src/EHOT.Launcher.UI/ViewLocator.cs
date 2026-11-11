using Avalonia.Controls;
using Avalonia.Controls.Templates;
using EHOT.Launcher.UI.ViewModels;

namespace EHOT.Launcher.UI;

/// <summary>ViewModel → View по соглашению об именах (XxxViewModel ↔ XxxView).</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        var name = param.GetType().FullName!
            .Replace("ViewModels", "Views", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);

        var type = Type.GetType(name);
        if (type is null)
        {
            return new TextBlock { Text = $"Представление не найдено: {name}", Margin = new Avalonia.Thickness(16) };
        }

        return (Control)Activator.CreateInstance(type)!;
    }

    public bool Match(object? data) => data is ViewModelBase;
}
