using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Headless.XUnit;
using EHOT.Launcher.UI.Converters;
using Xunit;

namespace EHOT.Launcher.UI.Tests;

/// <summary>
/// Headless-проверки UI-слоя: конвертеры, форматирование и загрузка XAML-ресурсов дизайн-системы
/// без открытия окна. Именно здесь ловятся регрессии вида «шаблон не скомпилировался».
/// </summary>
public sealed class UiSmokeTests
{
    [AvaloniaTheory]
    [InlineData(0, "0 Б")]
    [InlineData(999, "999 Б")]
    [InlineData(1024, "1 КБ")]
    [InlineData(1536, "1.5 КБ")]
    [InlineData(1024 * 1024, "1 МБ")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 ГБ")]
    public void BytesToHumanConverter_FormatsCommonSizes(long bytes, string expected)
    {
        Assert.Equal(expected, BytesToHumanConverter.Format(bytes), StringComparer.Ordinal);
    }

    [AvaloniaFact]
    public void Application_LoadsDesignSystemWithoutErrors()
    {
        // App.axaml подключает Tokens.axaml и Controls.axaml; исключение здесь означает,
        // что дизайн-система (ControlTheme, стили, кисти) не загрузилась.
        Assert.NotNull(Avalonia.Application.Current);
        Assert.NotEmpty(Avalonia.Application.Current!.Styles);
        Assert.NotEmpty(Avalonia.Application.Current.Resources.MergedDictionaries);
    }

    [AvaloniaFact]
    public void PrimaryButton_UsesControlThemeAndRenders()
    {
        var button = new Button
        {
            Theme = (ControlTheme)Avalonia.Application.Current!.FindResource("EhotPrimaryButton")!,
            Content = "Играть"
        };

        var window = new Window { Content = button };
        window.Show();

        Assert.NotNull(button.Theme);
        Assert.NotNull(button.Background);

        window.Close();
    }
}
