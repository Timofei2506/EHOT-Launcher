using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace EHOT.Launcher.UI.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Стаггерированное появление карточек с задержкой 60 мс между элементами
        // — создаёт мягкий волновой эффект при загрузке страницы.
        var reveals = this.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c.Classes.Contains("reveal"))
            .ToList();

        foreach (var reveal in reveals)
        {
            reveal.Opacity = 0; // гарантируем стартовое значение
        }

        var delayMs = 0;
        foreach (var reveal in reveals)
        {
            await Task.Delay(delayMs);
            reveal.Opacity = 1;
            delayMs += 60;
        }
    }
}
