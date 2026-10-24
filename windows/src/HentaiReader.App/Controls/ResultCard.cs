using System.Text.Json.Nodes;
using HentaiReader.App.Images;
using HentaiReader.App.Localization;
using HentaiReader.Host.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HentaiReader.App.Controls;

/// <summary>
/// One search result: cover (loaded through the source session), title, language, type, page count
/// and a few tags. Fixed size, so the grid never reflows while covers arrive.
/// </summary>
public sealed class ResultCard : Grid
{
    public const double CardWidth = 200;
    public const double CoverHeight = 280;

    private readonly Image _cover = new() { Stretch = Stretch.UniformToFill, Height = CoverHeight, Width = CardWidth };
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _meta = new() { Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _tags = new() { Opacity = 0.6, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private CancellationTokenSource? _loading;

    public ResultCard()
    {
        Width = CardWidth;
        RowSpacing = 4;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(CoverHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var frame = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = _cover,
        };
        Children.Add(frame);
        SetRow(_title, 1);
        SetRow(_meta, 2);
        SetRow(_tags, 3);
        Children.Add(_title);
        Children.Add(_meta);
        Children.Add(_tags);
    }

    public async void Show(SourceSession session, JsonObject item)
    {
        _loading?.Cancel();
        _loading = new CancellationTokenSource();
        var token = _loading.Token;
        var title = item["title"]?.GetValue<string>() ?? "";
        _title.Text = title;
        ToolTipService.SetToolTip(_title, title);
        var parts = new List<string>();
        if (item["language"]?.GetValue<string>() is { } lang) parts.Add(Strings.LanguageName(lang));
        if (item["type"]?.GetValue<string>() is { } type) parts.Add(Strings.TypeLabel(type));
        parts.Add(Strings.Plural("Gallery_PageCount", (long)(item["pageCount"]?.GetValue<double>() ?? 0)));
        _meta.Text = string.Join(" · ", parts);
        ToolTipService.SetToolTip(_meta, _meta.Text);
        _tags.Text = string.Join(", ", (item["tags"] as JsonArray ?? []).Take(5).Select(t => t!["name"]!.GetValue<string>()));
        _cover.Source = null;
        var image = await ImageLoader.Shared.LoadAsync(session, item["cover"], (int)(CardWidth * XamlRootScale()), token);
        if (!token.IsCancellationRequested) _cover.Source = image;
    }

    private double XamlRootScale() => XamlRoot?.RasterizationScale ?? 1.0;
}
