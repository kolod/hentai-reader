using System.Text.Json.Nodes;
using HentaiReader.App.Controls;
using HentaiReader.App.Images;
using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HentaiReader.App.Pages;

/// <summary>Gallery details, clickable tags grouped by namespace, page thumbnails (US3).</summary>
public sealed partial class GalleryPage : Page
{
    public sealed record Args(SourceEntry Source, JsonObject Summary);

    private Args? _args;
    private string? _webUrl;
    private JsonObject? _gallery;
    private CancellationTokenSource _cts = new();

    /// <summary>Source and gallery shown and fully loaded; going back to it reloads nothing (US2).</summary>
    private string? _loadedKey;

    public GalleryPage()
    {
        InitializeComponent();
        OpenInBrowser.Content = Strings.Get("Gallery_OpenInBrowser");
        ReadButton.Content = Strings.Get("Gallery_Read");
        ContinueButton.Content = Strings.Get("Gallery_Continue");
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not Args args) return;
        var key = $"{args.Source.Id}|{args.Summary["id"]!.GetValue<string>()}";
        if (e.NavigationMode == NavigationMode.Back && key == _loadedKey)
        {
            ShowProgress();
            return;
        }

        // The cached page is reused for every gallery: start from a clean state.
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loadedKey = null;
        _gallery = null;
        ReadButton.IsEnabled = false;
        ContinueButton.Visibility = Visibility.Collapsed;
        ProgressText.Text = "";
        ErrorBar.IsOpen = false;
        Progress.IsActive = true;
        Cover.Source = null;
        OriginalTitleText.Visibility = Visibility.Collapsed;
        MetaText.Text = "";
        TagGroups.Children.Clear();
        PagesGrid.ItemsSource = null;

        _args = args;
        TitleText.Text = args.Summary["title"]?.GetValue<string>() ?? "";
        var session = args.Source.Session!;
        Cover.Source = await ImageLoader.Shared.LoadAsync(session, args.Summary["cover"], (int)(200 * (XamlRoot?.RasterizationScale ?? 1)), token);
        if (token.IsCancellationRequested) return;

        var res = await args.Source.CallAsync("getGallery", [JsonValue.Create(args.Summary["id"]!.GetValue<string>())], token);
        if (token.IsCancellationRequested) return;
        Progress.IsActive = false;
        if (!res.Ok)
        {
            var kind = res.Error!.Type == "SourceError" ? res.Error.Kind! : "ScriptError";
            ErrorBar.Title = Strings.ErrorTitle(kind);
            ErrorBar.Message = Strings.ErrorBody(kind);
            ErrorBar.IsOpen = true;
            return;
        }
        Render(res.Value!.AsObject());
        _loadedKey = key;
        ShowProgress();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _cts.Cancel();

    private void Render(JsonObject g)
    {
        _gallery = g;
        ReadButton.IsEnabled = (g["pages"] as JsonArray)?.Count > 0;
        TitleText.Text = g["title"]?.GetValue<string>() ?? "";
        OriginalTitleText.Text = g["titleOriginal"]?.GetValue<string>() ?? "";
        OriginalTitleText.Visibility = OriginalTitleText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _webUrl = g["webUrl"]?.GetValue<string>();

        var meta = new List<string>();
        if (g["language"]?.GetValue<string>() is { } lang) meta.Add($"{Strings.Get("Gallery_Language")}: {Strings.LanguageName(lang)}");
        if (g["type"]?.GetValue<string>() is { } type) meta.Add($"{Strings.Get("Gallery_Type")}: {Strings.TypeLabel(type)}");
        meta.Add(Strings.Plural("Gallery_PageCount", (long)(g["pageCount"]?.GetValue<double>() ?? 0)));
        if (g["uploadedAt"]?.GetValue<string>() is { } date && DateTimeOffset.TryParse(date, out var at)) meta.Add($"{Strings.Get("Gallery_Uploaded")}: {Strings.Date(at)}");
        if (g["rating"] is JsonValue r) meta.Add($"{Strings.Get("Gallery_Rating")}: {Strings.Number(r.GetValue<double>(), "0.##")}");
        if (g["uploader"]?.GetValue<string>() is { } uploader) meta.Add($"{Strings.Get("Gallery_Uploader")}: {uploader}");
        MetaText.Text = string.Join(" · ", meta);

        TagGroups.Children.Clear();
        foreach (var group in (g["tags"] as JsonArray ?? []).GroupBy(t => t!["namespace"]?.GetValue<string>()))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = Strings.NamespaceLabel(group.Key) + ":", Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) });
            var tags = new WrapPanel();
            foreach (var name in group.Select(t => t!["name"]!.GetValue<string>()))
            {
                var button = new Button { Content = name, Padding = new Thickness(8, 2, 8, 2) };
                button.Click += (_, _) => SearchTag(group.Key, name);
                tags.Children.Add(button);
            }
            Grid.SetColumn(tags, 1);
            row.Children.Add(tags);
            TagGroups.Children.Add(row);
        }
        PagesGrid.ItemsSource = (g["pages"] as JsonArray ?? []).Select(p => p!.AsObject()).ToList();
    }

    /// <summary>Tag click → search in the same source with the `namespace:name_with_underscores` convention.</summary>
    private void SearchTag(string? ns, string name)
    {
        var term = name.Replace(' ', '_');
        var query = string.IsNullOrEmpty(ns) ? term : $"{ns}:{term}";
        Frame.Navigate(typeof(SourcePage), new SourcePage.Args(_args!.Source, query));
    }

    private void PagesGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not JsonObject page) return;
        if (args.ItemContainer.ContentTemplateRoot is not SpriteThumbnail thumb) return;
        thumb.Show(_args!.Source.Session!, page);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer,
            page["fileName"]?.GetValue<string>() ?? $"{(page["index"]?.GetValue<int>() ?? 0) + 1}");
        args.Handled = true;
    }

    /// <summary>Saved progress: "Page 40 of 120" with Continue, or Finished (FR-019).</summary>
    private void ShowProgress()
    {
        if (_args is null || _gallery is null) return;
        var progress = AppServices.Progress.Get(_args.Source.Id, _gallery["id"]!.GetValue<string>());
        var resumable = progress is { Read: false } && (progress.Page > 0 || progress.Offset > 0);
        ContinueButton.Visibility = resumable ? Visibility.Visible : Visibility.Collapsed;
        ReadButton.Content = Strings.Get(resumable ? "Gallery_ReadFromStart" : "Gallery_Read");
        ReadButton.Style = (Style)Application.Current.Resources[resumable ? "DefaultButtonStyle" : "AccentButtonStyle"];
        ProgressText.Text = progress switch
        {
            { Read: true } => Strings.Get("Gallery_Finished"),
            not null when resumable => Strings.Format("Gallery_Progress", progress.Page + 1, progress.PageCount),
            _ => "",
        };
    }

    /// <summary>The reader replaces this page in the same window (FR-001).</summary>
    private void ReadButton_Click(object sender, RoutedEventArgs e) => OpenReader(0, 0);

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_args is null || _gallery is null) return;
        var progress = AppServices.Progress.Get(_args.Source.Id, _gallery["id"]!.GetValue<string>());
        OpenReader(progress?.Page ?? 0, progress?.Offset ?? 0);
    }

    private void OpenReader(int page, double offset)
    {
        if (_gallery is not null && _args is not null) Frame.Navigate(typeof(ReaderPage), new ReaderPage.Args(_args.Source, _gallery, page, offset));
    }

    private async void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_webUrl is not null) await Windows.System.Launcher.LaunchUriAsync(new Uri(_webUrl));
    }
}
