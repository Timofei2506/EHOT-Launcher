using Avalonia.Controls;
using Avalonia.Controls.Templates;
using EHOT.Launcher.UI.ViewModels;

namespace EHOT.Launcher.UI;

/// <summary>
/// Подбор представления под ViewModel.
/// По соглашению: <c>XxxViewModel</c> → <c>Views.XxxView</c>. Если такого типа в сборке нет,
/// ищем «однофамильца» (например, <c>ModsViewModel</c> → <c>BrowseModsView</c>), чтобы
/// каталог контента с общим базовым классом не превращался в заглушку.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        var type = ResolveViewType(param.GetType());
        if (type is null)
        {
            // Заглушки быть не должно: это видно и в тестах, и пользователю.
            return new TextBlock
            {
                Text = $"Представление не найдено: {param.GetType().FullName}",
                Margin = new Avalonia.Thickness(16)
            };
        }

        return (Control)Activator.CreateInstance(type)!;
    }

    public bool Match(object? data) => data is ViewModelBase;

    /// <summary>Ищет тип представления для типа ViewModel. Отдельно — чтобы покрыть тестом.</summary>
    public static Type? ResolveViewType(Type viewModelType)
    {
        var assembly = viewModelType.Assembly;
        var ns = viewModelType.Namespace ?? "EHOT.Launcher.UI.ViewModels";
        var viewsNamespace = ns.Replace("ViewModels", "Views", StringComparison.Ordinal);
        var shortName = viewModelType.Name;

        // 1. Прямое совпадение по конвенции: ModsViewModel → ModsView.
        var exactName = $"{viewsNamespace}.{shortName.Replace("ViewModel", "View", StringComparison.Ordinal)}";
        var exact = assembly.GetType(exactName, throwOnError: false);
        if (exact is not null)
        {
            return exact;
        }

        // 2. Однофамилец: ModsViewModel → BrowseModsView, ShadersViewModel → BrowseShadersView.
        if (shortName.EndsWith("ViewModel", StringComparison.Ordinal))
        {
            var stem = shortName[..^"ViewModel".Length];
            var candidates = assembly.GetTypes()
                .Where(t => t.Namespace == viewsNamespace
                            && typeof(Control).IsAssignableFrom(t)
                            && t.Name.EndsWith(stem + "View", StringComparison.Ordinal))
                .OrderBy(t => t.Name.Length)
                .ToList();

            if (candidates.Count > 0)
            {
                return candidates[0];
            }
        }

        return null;
    }
}
