using System.Collections.ObjectModel;
using System.Net;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EHOT.Launcher.Content;
using EHOT.Launcher.Core.Models;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Официальные новости Minecraft Java Edition + подробная статья в приложении.</summary>
public sealed partial class NewsViewModel : ViewModelBase
{
    private readonly IMinecraftNewsService _news;
    private IReadOnlyList<NewsCardViewModel> _all = [];

    public NewsViewModel(IMinecraftNewsService news) => _news = news;

    public ObservableCollection<NewsCardViewModel> Items { get; } = [];
    public ObservableCollection<string> Tabs { get; } = ["Все", "Обновления", "Снапшоты"];

    [ObservableProperty]
    private string _selectedTab = "Все";

    [ObservableProperty]
    private NewsCardViewModel? _selectedArticle;

    [ObservableProperty]
    private bool _isArticleOpen;

    [ObservableProperty]
    private bool _isArticleLoading;

    [ObservableProperty]
    private string _articleTitle = string.Empty;

    [ObservableProperty]
    private string _articleMeta = string.Empty;

    [ObservableProperty]
    private string _articleImageUrl = string.Empty;

    [ObservableProperty]
    private string _articleBody = string.Empty;

    [ObservableProperty]
    private string _articleLink = string.Empty;

    [ObservableProperty]
    private bool _hasItems;

    public override async Task OnNavigatedToAsync(CancellationToken ct = default) => await RefreshAsync(ct).ConfigureAwait(true);

    partial void OnSelectedTabChanged(string value)
    {
        _ = value;
        ApplyFilter();
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var items = await _news.GetLatestAsync(30, ct).ConfigureAwait(true);
            _all = items.Select(i => new NewsCardViewModel(i)).ToArray();
            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        IEnumerable<NewsCardViewModel> filtered = SelectedTab switch
        {
            "Обновления" => _all.Where(i => i.Kind == NewsKind.Release),
            "Снапшоты" => _all.Where(i => i.Kind == NewsKind.Snapshot),
            _ => _all
        };

        foreach (var item in filtered)
        {
            Items.Add(item);
        }

        HasItems = Items.Count > 0;
    }

    [RelayCommand]
    private void SetTab(string? tab)
    {
        if (tab is not null)
        {
            SelectedTab = tab;
        }
    }

    [RelayCommand]
    private async Task OpenArticleAsync(NewsCardViewModel? item)
    {
        item ??= SelectedArticle;
        if (item is null)
        {
            return;
        }

        SelectedArticle = item;
        IsArticleOpen = true;
        IsArticleLoading = true;
        ArticleTitle = item.Title;
        ArticleMeta = item.Meta;
        ArticleImageUrl = item.ImageUrl;
        ArticleBody = "Загружаю официальный текст статьи…";
        ArticleLink = $"https://www.minecraft.net/en-us/article/{item.Id}";

        try
        {
            var article = await _news.GetArticleAsync(item.ContentPath).ConfigureAwait(true);
            ArticleBody = article is null
                ? "Официальный текст статьи не загрузился. Откройте оригинал по кнопке ниже."
                : StripHtml(article.BodyHtml);
        }
        finally
        {
            IsArticleLoading = false;
        }
    }

    [RelayCommand]
    private void CloseArticle() => IsArticleOpen = false;

    [RelayCommand]
    private void OpenOriginal()
    {
        if (string.IsNullOrWhiteSpace(ArticleLink))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ArticleLink) { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // Ссылка показана на экране — пользователь сможет открыть её вручную.
        }
    }

    private static string StripHtml(string html)
    {
        var text = html
            .Replace("</li>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<li>", "• ", StringComparison.OrdinalIgnoreCase)
            .Replace("</p>", "\n\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</h1>", "\n\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</h2>", "\n\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase);

        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }
}

public enum NewsKind
{
    Release,
    Snapshot,
    Other
}

public sealed class NewsCardViewModel(NewsItem item)
{
    public NewsItem Item { get; } = item;
    public string Id => Item.Id;
    public string Title => Item.Title;
    public string Version => Item.Version;
    public string ContentPath => Item.ContentPath;
    public string ImageUrl => Item.ImageUrl;
    public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);
    public string DateLabel => Item.Date.ToLocalTime().ToString("dd.MM.yyyy");
    public NewsKind Kind => string.Equals(Item.Type, "snapshot", StringComparison.OrdinalIgnoreCase) ? NewsKind.Snapshot
        : string.Equals(Item.Type, "release", StringComparison.OrdinalIgnoreCase) ? NewsKind.Release
        : NewsKind.Other;
    public string TypeLabel => Kind switch
    {
        NewsKind.Snapshot => "Снапшот",
        NewsKind.Release => "Обновление",
        _ => Item.Type
    };
    public string Meta => $"{TypeLabel} · {DateLabel}";
}
